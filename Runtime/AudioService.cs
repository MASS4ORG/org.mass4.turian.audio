using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Session-owned event preparation and single-producer command transport to the audio callback.</summary>
public sealed unsafe partial class AudioService : IAudioService, IAudioMixerService
{
    sealed class PreparedEvent
    {
        public required VoiceDesc[] Variations;
        public Func<IAudioStreamSource>[]? Streams;
        public Guid AssetId;
        public int Next;
    }

    readonly IAudioDevice device;
    readonly IMixerGraph mixer;
    readonly IAssetLoader? assets;
    readonly AudioCodecRegistry codecs;
    readonly AudioSettings settings;
    readonly AudioCommandQueue commands;
    readonly VirtualVoiceManager voices;
    readonly Dictionary<string, PreparedEvent> events = new(StringComparer.Ordinal);
    readonly List<GCHandle> pins = [];
    readonly int producerThread = Environment.CurrentManagedThreadId;
    readonly ILogger? logger;
    AudioStreamPump? streams;
    GCHandle context;
    long nextVoice;
    int callbackFault;
    bool disposed;

    /// <summary>Creates a stopped session; preparing the first event starts its device.</summary>
    public AudioService(IAudioDevice device, IMixerGraph mixer, AudioSettings settings,
        IAssetLoader? assets = null, ILogger? logger = null, AudioCodecRegistry? codecs = null,
        AudioMixer? mixerModel = null, IEnumerable<IAudioEffectFactory>? effectFactories = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        this.device = device;
        this.mixer = mixer;
        this.assets = assets;
        this.codecs = codecs ?? new AudioCodecRegistry();
        this.settings = new AudioSettings
        {
            SampleRate = settings.SampleRate,
            BufferFrames = settings.BufferFrames,
            MaxVirtualVoices = settings.MaxVirtualVoices,
            CommandCapacity = settings.CommandCapacity,
            DeviceName = settings.DeviceName,
            MaxStreams = settings.MaxStreams,
            StreamBufferFrames = settings.StreamBufferFrames,
        };
        this.logger = logger;
        commands = new(settings.CommandCapacity);
        voices = new(settings.MaxVirtualVoices, mixer);
        InitializeMixer(settings, mixerModel, effectFactories);
    }

    /// <inheritdoc/>
    public string Prepare(DataAssetReference<AudioEvent> reference)
    {
        CheckProducer();
        ArgumentNullException.ThrowIfNull(reference);
        var audioEvent = assets?.LoadContentAsync<AudioEvent>(reference.AssetId).GetAwaiter().GetResult()
            ?? throw new InvalidDataException("The audio event asset could not be resolved.");
        Prepare(audioEvent);
        return audioEvent.EventName;
    }

    /// <inheritdoc/>
    public void Prepare(AudioEvent audioEvent)
    {
        CheckProducer();
        ArgumentNullException.ThrowIfNull(audioEvent);
        if (AlreadyPrepared(audioEvent))
        {
            return;
        }
        if (assets is null)
        {
            throw new InvalidOperationException("Preparing asset references requires an asset loader.");
        }
        var clips = new AudioClip[audioEvent.Clips.Count];
        var sources = audioEvent.Stream ? new Func<IAudioStreamSource>[clips.Length] : null;
        for (var i = 0; i < clips.Length; i++)
        {
            clips[i] = assets.LoadAsync<AudioClip>(audioEvent.Clips[i].AssetId).GetAwaiter().GetResult()
                ?? throw new InvalidDataException("An audio event references a missing clip.");
            if (clips[i].Frames != 0 && sources is null)
            {
                continue;
            }
            var provider = clips[i].ContentProvider ?? throw new InvalidDataException("The clip has no resolved payload.");
            var extension = Path.GetExtension(clips[i].RelativePath);
            if (sources is not null)
            {
                sources[i] = () => codecs.OpenStream(provider, extension);
                continue;
            }
            using var source = codecs.OpenStream(provider, extension);
            clips[i].LoadPcm(source);
        }
        if (sources is not null)
        {
            PrepareStream(audioEvent, sources);
            return;
        }
        Prepare(audioEvent, clips);
    }

    /// <summary>
    /// Prepares an event whose variations are decoded while playing. Each factory is opened once here to validate its
    /// format, then once per posted voice on the decoder thread; the service disposes every source it opens.
    /// </summary>
    public void PrepareStream(AudioEvent audioEvent, IReadOnlyList<Func<IAudioStreamSource>> sources)
    {
        CheckProducer();
        ArgumentNullException.ThrowIfNull(audioEvent);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEvent.EventName);
        if (AlreadyPrepared(audioEvent))
        {
            return;
        }
        ValidateEvent(audioEvent, sources.Count);
        var variations = new VoiceDesc[sources.Count];
        var factories = new Func<IAudioStreamSource>[sources.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            factories[i] = sources[i] ?? throw new ArgumentException("Stream factories cannot be null.", nameof(sources));
            AudioFormat format;
            using (var probe = factories[i]())
            {
                format = probe.Format;
            }
            ValidateStreamFormat(format);
            variations[i] = new(default, default, null, 0, format, new AudioParameterSet(), audioEvent.Loop,
                audioEvent.Spatial, audioEvent.MinDistance, audioEvent.MaxDistance, audioEvent.Priority)
            { EventVolume = audioEvent.Volume, EventPitch = audioEvent.Pitch, Bus = EventBus(audioEvent) };
        }
        streams ??= new AudioStreamPump(settings.MaxStreams, Math.Max(settings.StreamBufferFrames, settings.BufferFrames * 4),
            logger);
        StartDevice();
        events.Add(audioEvent.EventName, new() { Variations = variations, Streams = factories, AssetId = audioEvent.Id });
    }

    /// <summary>Prepares an event from PCM clips supplied by tools or procedural generators.</summary>
    public void Prepare(AudioEvent audioEvent, IReadOnlyList<AudioClip> clips)
    {
        CheckProducer();
        ArgumentNullException.ThrowIfNull(audioEvent);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEvent.EventName);
        if (AlreadyPrepared(audioEvent))
        {
            return;
        }
        ValidateEvent(audioEvent, clips.Count);
        var variations = new VoiceDesc[clips.Count];
        for (var i = 0; i < clips.Count; i++)
        {
            if (clips[i] is not { Frames: > 0 })
            {
                throw new ArgumentException("Events cannot use empty PCM clips.", nameof(clips));
            }
        }
        var firstPin = pins.Count;
        try
        {
            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                var pin = GCHandle.Alloc(clip.Samples, GCHandleType.Pinned);
                pins.Add(pin);
                variations[i] = new(default, default, (float*)pin.AddrOfPinnedObject(), clip.Frames, clip.Format,
                    new AudioParameterSet(), audioEvent.Loop, audioEvent.Spatial,
                    audioEvent.MinDistance, audioEvent.MaxDistance, audioEvent.Priority)
                { EventVolume = audioEvent.Volume, EventPitch = audioEvent.Pitch, Bus = EventBus(audioEvent) };
            }
            StartDevice();
            events.Add(audioEvent.EventName, new() { Variations = variations, AssetId = audioEvent.Id });
        }
        catch
        {
            for (var i = pins.Count - 1; i >= firstPin; i--)
            {
                pins[i].Free();
                pins.RemoveAt(i);
            }
            throw;
        }
    }

    static void ValidateStreamFormat(AudioFormat format)
    {
        if (format.Channels is < 1 or > 2 || format.SampleRate <= 0)
        {
            throw new NotSupportedException("Audio streams support mono or stereo PCM.");
        }
    }

    static void ValidateEvent(AudioEvent audioEvent, int variations)
    {
        ValidateParameters(new AudioParameterSet { Volume = audioEvent.Volume, Pitch = audioEvent.Pitch });
        if (!float.IsFinite(audioEvent.MinDistance) || !float.IsFinite(audioEvent.MaxDistance)
            || audioEvent.MinDistance < 0 || audioEvent.MaxDistance <= audioEvent.MinDistance || variations == 0)
        {
            throw new ArgumentException("Events require clips and a finite increasing distance range.", nameof(audioEvent));
        }
    }

    bool AlreadyPrepared(AudioEvent audioEvent)
    {
        if (!events.TryGetValue(audioEvent.EventName, out var prepared))
        {
            return false;
        }
        if (prepared.AssetId != audioEvent.Id)
        {
            throw new InvalidOperationException("Different audio event assets cannot share the same event name.");
        }
        return true;
    }

    /// <inheritdoc/>
    public VoiceHandle Post(string eventName, Guid owner, in AudioParameterSet parameters)
    {
        CheckProducer();
        ValidateParameters(parameters);
        if (!events.TryGetValue(eventName, out var prepared))
        {
            throw new InvalidOperationException($"Audio event '{eventName}' must be prepared before posting.");
        }
        var variation = prepared.Variations[prepared.Next];
        AudioStreamBuffer* stream = null;
        if (prepared.Streams is { } sources)
        {
            stream = (AudioStreamBuffer*)streams!.Acquire(sources[prepared.Next], variation.Loop);
            if (stream == null)
            {
                logger?.LogWarning("Audio event '{Event}' was dropped: all {Count} streams are playing.",
                    eventName, settings.MaxStreams);
                return default;
            }
        }
        var handle = new VoiceHandle(++nextVoice);
        var command = new AudioCommand
        {
            Kind = AudioCommandKind.Play,
            Voice = variation with { Handle = handle, Owner = owner, Parameters = parameters, Stream = stream },
        };
        if (!commands.TryWrite(command))
        {
            if (stream != null)
            {
                stream->Release();
            }
            return default;
        }
        prepared.Next = (prepared.Next + 1) % prepared.Variations.Length;
        return handle;
    }

    /// <inheritdoc/>
    public bool SetEmitterParameters(Guid owner, in AudioParameterSet parameters)
    {
        CheckProducer();
        ValidateParameters(parameters);
        return commands.TryWrite(new AudioCommand
        {
            Kind = AudioCommandKind.Emitter,
            Owner = owner,
            Parameters = parameters,
        });
    }

    /// <inheritdoc/>
    public bool SetParameters(VoiceHandle voice, in AudioParameterSet parameters)
    {
        CheckProducer();
        ValidateParameters(parameters);
        return commands.TryWrite(new AudioCommand
        {
            Kind = AudioCommandKind.Parameters,
            Handle = voice,
            Parameters = parameters,
        });
    }

    /// <inheritdoc/>
    public bool StopAll(Guid owner, float fadeOutSeconds = 0)
    {
        CheckProducer();
        if (!float.IsFinite(fadeOutSeconds) || fadeOutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fadeOutSeconds));
        }
        return commands.TryWrite(new AudioCommand { Kind = AudioCommandKind.Stop, Owner = owner, Value = fadeOutSeconds });
    }

    /// <inheritdoc/>
    public bool Pause(Guid owner, bool paused = true)
    {
        CheckProducer();
        return commands.TryWrite(new AudioCommand { Kind = AudioCommandKind.Pause, Owner = owner, Value = paused ? 1 : 0 });
    }

    /// <inheritdoc/>
    public bool SetMasterVolume(float volume)
    {
        CheckProducer();
        if (!float.IsFinite(volume) || volume is < 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Master volume must be finite and in range.");
        }
        return commands.TryWrite(new AudioCommand { Kind = AudioCommandKind.Master, Value = volume });
    }

    /// <inheritdoc/>
    public AudioStatistics Statistics =>
        new(mixer.ActiveVoiceCount, streams?.ActiveStreams ?? 0, streams?.Underruns ?? 0);

    /// <inheritdoc/>
    public bool SetListener(in AudioListenerState listener)
    {
        CheckProducer();
        if (!Finite(listener.Position) || !float.IsFinite(listener.Orientation.LengthSquared())
            || listener.Orientation.LengthSquared() < 0.00001f)
        {
            throw new ArgumentException("The listener requires a finite position and nonzero orientation.", nameof(listener));
        }
        return commands.TryWrite(new AudioCommand
        {
            Kind = AudioCommandKind.Listener,
            Listener = listener with { Orientation = Quaternion.Normalize(listener.Orientation) },
        });
    }

    void StartDevice()
    {
        if (device.State == AudioDeviceState.Started)
        {
            return;
        }
        context = GCHandle.Alloc(this);
        try
        {
            device.Start(new AudioDeviceConfig(new(2, settings.SampleRate), settings.BufferFrames,
                settings.DeviceName, GCHandle.ToIntPtr(context), &Callback));
        }
        catch
        {
            device.Stop();
            context.Free();
            throw;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Callback(nint context, float* output, uint frames)
    {
        if (output == null || frames > int.MaxValue / 2)
        {
            return;
        }
        var buffer = new Span<float>(output, (int)frames * 2);
        try
        {
            var service = (AudioService)GCHandle.FromIntPtr(context).Target!;
            if (Volatile.Read(ref service.callbackFault) != 0)
            {
                buffer.Clear();
                return;
            }
            try { service.Update(buffer); }
            catch
            {
                Volatile.Write(ref service.callbackFault, 1);
                buffer.Clear();
            }
        }
        catch { buffer.Clear(); }
    }

    // Device callback only; every drain is bounded so a busy producer cannot starve mixing.
    void Update(Span<float> output)
    {
        for (var i = 0; i < settings.CommandCapacity && commands.TryRead(out var command); i++)
        {
            switch (command.Kind)
            {
                case AudioCommandKind.Play: Play(command.Voice); break;
                case AudioCommandKind.Parameters: voices.SetParameters(command.Handle, command.Parameters); break;
                case AudioCommandKind.Emitter: voices.SetEmitterParameters(command.Owner, command.Parameters); break;
                case AudioCommandKind.Stop: voices.Stop(command.Owner, command.Value); break;
                case AudioCommandKind.Listener: voices.SetListener(command.Listener); break;
                case AudioCommandKind.Pause: mixer.Pause(command.Owner, command.Value != 0); break;
                case AudioCommandKind.Master: mixer.SetMasterVolume(command.Value); break;
                case AudioCommandKind.Bus: busMixer!.Set(command.Bus, command.BusValues); break;
                case AudioCommandKind.Snapshot: busMixer!.Snapshot(command.Bus, command.Value); break;
            }
        }
        mixer.Mix(output);
        ValidateOutput(output);
    }

    void ValidateOutput(Span<float> output)
    {
        for (var i = 0; i < output.Length; i++)
        {
            if (float.IsFinite(output[i])) { continue; }
            Volatile.Write(ref callbackFault, 1);
            output.Clear();
            return;
        }
    }

    // A rejected streamed voice never reaches the mixer, so its ring is returned here.
    void Play(in VoiceDesc voice)
    {
        if (voices.Play(voice).Value == 0 && voice.Stream != null)
        {
            voice.Stream->Release();
        }
    }

    void CheckProducer()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != producerThread)
        {
            throw new InvalidOperationException("Audio commands must originate on the session's main thread.");
        }
        if (Interlocked.CompareExchange(ref callbackFault, 2, 1) == 1)
        {
            logger?.LogError("Audio callback failed; output is silent until the session is reset.");
        }
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    static void ValidateParameters(in AudioParameterSet parameters)
    {
        if (!float.IsFinite(parameters.Volume) || parameters.Volume is < 0 or > 16
            || !float.IsFinite(parameters.Pitch) || parameters.Pitch is < 0.01f or > 16 || !Finite(parameters.Position))
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Audio parameters must be finite and in range.");
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        CheckProducer();
        device.Stop();
        if (context.IsAllocated)
        {
            context.Free();
        }
        commands.Reset();
        voices.Reset();
        busMixer?.Defaults.CopyTo(requested, 0);
        streams?.Reset();
        foreach (var pin in pins)
        {
            pin.Free();
        }
        pins.Clear();
        events.Clear();
        Volatile.Write(ref callbackFault, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        Reset();
        device.Dispose();
        mixer.Dispose();
        streams?.Dispose();
        disposed = true;
    }
}
