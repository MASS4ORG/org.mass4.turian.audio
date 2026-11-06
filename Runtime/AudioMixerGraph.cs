using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Turian.Audio;

/// <summary>
/// Preallocated stereo voice mixer with rate conversion, looping, streamed voices, pausing, a ramped master gain and
/// short voice-stealing fades.
/// </summary>
public sealed unsafe class AudioMixerGraph : IMixerGraph
{
    struct Voice
    {
        public VoiceDesc Desc;
        public AudioParameterSet Posted;
        // Resident voices: frame position in their PCM. Streamed voices: offset from the ring's read position.
        public double Cursor;
        public float Gain;
        public float FadeStep;
        public int Stopping;
        public int Paused;
        public int Active;
        public int StreamState;
    }

    const float MasterRampSeconds = 0.02f;
    readonly int capacity;
    readonly int sampleRate;
    readonly SpatializerKernel spatializer;
    AudioListenerState listener = new(Vector3.Zero, Quaternion.Identity);
    readonly Dictionary<long, int> handles;
    readonly delegate*<Voice*, float*, int, int, SpatialParams, void> resident = &RenderVoice;
    readonly delegate*<Voice*, float*, int, int, SpatialParams, void> streamed = &RenderStream;
    Voice* voices;
    int activeVoiceCount;
    AudioBusMixer buses;
    internal AudioBusMixer BusMixer => buses;

    internal void ConfigureMixer(AudioMixer model, IEnumerable<IAudioEffectFactory>? effects)
    {
        var next = new AudioBusMixer(model, sampleRate, buses.Frames, effects);
        buses.Dispose();
        buses = next;
    }

    float master = 1;
    float masterTarget = 1;

    /// <summary>Allocates all voice and fade slots before playback.</summary>
    public AudioMixerGraph(AudioSettings settings, ISpatializer spatializer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(spatializer);
        settings.Validate();
        capacity = settings.MaxVirtualVoices;
        sampleRate = settings.SampleRate;
        this.spatializer = spatializer.Kernel;
        if (this.spatializer.Compute == null)
        {
            throw new ArgumentException("The spatializer requires a static processing kernel.", nameof(spatializer));
        }
        buses = new AudioBusMixer(new AudioMixer(), sampleRate, settings.BufferFrames);
        handles = new(capacity);
        voices = (Voice*)NativeMemory.AllocZeroed((nuint)(capacity * 2), (nuint)sizeof(Voice));
    }

    /// <inheritdoc/>
    public int ActiveVoiceCount => Volatile.Read(ref activeVoiceCount);

    /// <inheritdoc/>
    public VoiceHandle CreateVoice(in VoiceDesc desc)
    {
        if (voices is null || !ValidVoice(desc))
        {
            return default;
        }

        var slot = FindAvailableSlot();
        if (slot < 0 || !RetireSlot(slot)) { return default; }
        ref var target = ref voices[slot];
        handles.Remove(target.Desc.Handle.Value);
        target = new Voice { Desc = desc, Posted = desc.Parameters, Active = 1 };
        handles.Add(desc.Handle.Value, slot);
        return desc.Handle;
    }

    bool ValidVoice(in VoiceDesc desc) => desc.Handle.Value != 0 && (uint)desc.Bus < buses.Names.Count
        && (desc.Stream != null || desc.Samples != null && desc.Frames > 0);

    int FindAvailableSlot()
    {
        for (var i = 0; i < capacity; i++)
        {
            if (voices[i].Active == 0 || voices[i].Stopping != 0) { return i; }
        }
        return -1;
    }

    bool RetireSlot(int slot)
    {
        ref var target = ref voices[slot];
        if (target.Active == 0) { return true; }
        if (target.Gain <= 0) { Deactivate(ref target); return true; }
        if (voices[slot + capacity].Active != 0) { return false; }
        voices[slot + capacity] = target;
        BeginStop(ref voices[slot + capacity], 0.005f);
        return true;
    }

    /// <inheritdoc/>
    public bool IsPlaying(VoiceHandle voice) => handles.TryGetValue(voice.Value, out var slot)
        && voices[slot].Active != 0 && voices[slot].Stopping == 0;

    /// <inheritdoc/>
    public void StopVoice(VoiceHandle voice, float fadeOutSeconds)
    {
        if (handles.TryGetValue(voice.Value, out var slot))
        {
            BeginStop(ref voices[slot], fadeOutSeconds);
        }
    }

    /// <inheritdoc/>
    public void SetParameters(VoiceHandle voice, in AudioParameterSet parameters)
    {
        for (var i = 0; i < capacity; i++)
        {
            if (voices[i].Active != 0 && voices[i].Desc.Handle == voice)
            {
                voices[i].Desc = voices[i].Desc with { Parameters = parameters };
                voices[i].Posted = parameters;
                return;
            }
        }
    }

    /// <inheritdoc/>
    public void SetEmitterParameters(Guid owner, in AudioParameterSet parameters)
    {
        for (var i = 0; i < capacity; i++)
        {
            ref var voice = ref voices[i];
            if (voice.Active != 0 && voice.Desc.Owner == owner)
            {
                voice.Desc = voice.Desc with
                {
                    Parameters = parameters with
                    {
                        Volume = voice.Posted.Volume * parameters.Volume,
                        Pitch = voice.Posted.Pitch * parameters.Pitch,
                    },
                };
            }
        }
    }

    /// <inheritdoc/>
    public void Stop(Guid owner, float fadeOutSeconds)
    {
        for (var i = 0; i < capacity * 2; i++)
        {
            if (voices[i].Active != 0 && voices[i].Desc.Owner == owner)
            {
                BeginStop(ref voices[i], fadeOutSeconds);
            }
        }
    }

    void BeginStop(ref Voice voice, float seconds)
    {
        if (seconds <= 0 || voice.Gain <= 0)
        {
            Deactivate(ref voice);
            return;
        }
        voice.Stopping = 1;
        voice.FadeStep = voice.Gain / Math.Max(1, seconds * sampleRate);
    }

    // Every path that ends a voice goes through here so streamed rings always return to the decoder thread.
    static void Deactivate(ref Voice voice)
    {
        voice.Active = 0;
        if (voice.Desc.Stream != null)
        {
            voice.Desc.Stream->Release();
        }
    }

    /// <inheritdoc/>
    public void Pause(Guid owner, bool paused)
    {
        for (var i = 0; i < capacity * 2; i++)
        {
            if (voices[i].Active != 0 && voices[i].Desc.Owner == owner)
            {
                voices[i].Paused = paused ? 1 : 0;
            }
        }
    }

    /// <inheritdoc/>
    public void SetMasterVolume(float volume) => masterTarget = volume;

    /// <inheritdoc/>
    public void SetListener(in AudioListenerState listener) => this.listener = listener;

    SpatialParams Spatial(in VoiceDesc desc) => spatializer.Compute(spatializer.Context, listener,
        new VoiceState(desc.Parameters.Position, desc.Spatial, desc.MinDistance, desc.MaxDistance));

    /// <inheritdoc/>
    public int Mix(Span<float> interleavedOut)
    {
        interleavedOut.Clear();
        if (voices is null) { return 0; }
        var frames = interleavedOut.Length / 2;
        CaptureStreamStates();
        buses.Begin();
        for (var first = 0; first < frames; first += buses.Frames)
        {
            var count = Math.Min(buses.Frames, frames - first);
            Array.Clear(buses.Samples);
            RenderBusVoices(count);
            ExcludeSoloVoices(count);
            buses.Process(count);
            buses.Samples.AsSpan(0, count * 2).CopyTo(interleavedOut.Slice(first * 2, count * 2));
        }
        ApplyOutputGain(interleavedOut);
        var active = 0;
        for (var i = 0; i < capacity; i++) { if (voices[i].Active != 0) { active++; } }
        buses.Publish(frames);
        Volatile.Write(ref activeVoiceCount, active);
        return frames;
    }

    // Worker publication cannot start a pending stream halfway through a callback's bus chunks.
    void CaptureStreamStates()
    {
        for (var i = 0; i < capacity * 2; i++)
        {
            var stream = voices[i].Desc.Stream;
            if (voices[i].Active == 0 || stream == null) { continue; }
            voices[i].StreamState = stream->IsReady ? 1 : stream->IsFailed ? 2 : 0;
        }
    }

    void RenderBusVoices(int frames)
    {
        fixed (float* buffers = buses.Samples)
        {
            for (var i = 0; i < capacity * 2; i++)
            {
                if (voices[i].Active == 0) { continue; }
                var bus = voices[i].Desc.Bus;
                var render = voices[i].Desc.Stream != null ? streamed : resident;
                render(voices + i, buffers + bus * buses.Frames * 2, frames, sampleRate, Spatial(voices[i].Desc));
                if (i < capacity && voices[i].Active == 0) { handles.Remove(voices[i].Desc.Handle.Value); }
            }
        }
    }

    // Solo-excluded voices keep their cursors advancing without contributing detector or output signal.
    void ExcludeSoloVoices(int frames)
    {
        for (var bus = 0; bus < buses.VoiceEnabled.Length; bus++)
        {
            if (!buses.VoiceEnabled[bus]) { Array.Clear(buses.Samples, bus * buses.Frames * 2, frames * 2); }
        }
    }

    void ApplyOutputGain(Span<float> interleavedOut)
    {
        var ramp = 1f / (sampleRate * MasterRampSeconds);
        for (var i = 0; i + 1 < interleavedOut.Length; i += 2)
        {
            master += Math.Clamp(masterTarget - master, -ramp, ramp);
            interleavedOut[i] = ClampFinite(interleavedOut[i] * master);
            interleavedOut[i + 1] = ClampFinite(interleavedOut[i + 1] * master);
        }
    }

    // Preserve invalid samples so the service's callback boundary can silence and report the faulty processor.
    static float ClampFinite(float sample) => float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : sample;

    // Advances one frame of fade-in, pause or stop envelope; false ends the voice's block.
    static bool Envelope(Voice* voice, int outputRate)
    {
        if (voice->Stopping != 0)
        {
            voice->Gain = Math.Max(0, voice->Gain - voice->FadeStep);
            if (voice->Gain > 0)
            {
                return true;
            }
            Deactivate(ref *voice);
            return false;
        }
        var ramp = 1f / (outputRate * 0.005f);
        if (voice->Paused != 0)
        {
            voice->Gain = Math.Max(0, voice->Gain - ramp);
            return voice->Gain > 0;
        }
        voice->Gain = Math.Min(1, voice->Gain + ramp);
        return true;
    }

    static void Accumulate(Voice* voice, float* output, int index, float left, float right, SpatialParams spatial)
    {
        var desc = voice->Desc;
        if (desc.Spatial)
        {
            left = right = (left + right) * 0.5f;
        }
        var gain = voice->Gain * desc.Parameters.Volume * desc.EventVolume;
        output[index * 2] += left * gain * spatial.Left;
        output[index * 2 + 1] += right * gain * spatial.Right;
    }

    static void RenderVoice(Voice* voice, float* output, int frames, int outputRate, SpatialParams spatial)
    {
        var desc = voice->Desc;
        var step = desc.Format.SampleRate * (double)desc.Parameters.Pitch * desc.EventPitch / outputRate;
        var channels = desc.Format.Channels;
        var rightChannel = channels == 1 ? 0 : 1;
        for (var i = 0; i < frames; i++)
        {
            if (voice->Cursor >= desc.Frames)
            {
                if (!desc.Loop)
                {
                    Deactivate(ref *voice);
                    break;
                }
                voice->Cursor %= desc.Frames;
            }
            if (!Envelope(voice, outputRate))
            {
                break;
            }
            var frame = (int)voice->Cursor;
            var next = frame + 1 < desc.Frames ? frame + 1 : desc.Loop ? 0 : frame;
            var blend = (float)(voice->Cursor - frame);
            var left = desc.Samples[frame * channels];
            var right = desc.Samples[frame * channels + rightChannel];
            left += (desc.Samples[next * channels] - left) * blend;
            right += (desc.Samples[next * channels + rightChannel] - right) * blend;
            Accumulate(voice, output, i, left, right, spatial);
            voice->Cursor += step;
        }
    }

    static void RenderStream(Voice* voice, float* output, int frames, int outputRate, SpatialParams spatial)
    {
        var desc = voice->Desc;
        var stream = desc.Stream;
        if (!StreamCanRender(voice)) { return; }
        // Reading the end flag before the frame count guarantees the count already includes the final frames.
        var ended = stream->IsEnded;
        var available = stream->Available;
        var step = stream->SampleRate * (double)desc.Parameters.Pitch * desc.EventPitch / outputRate;
        var rightChannel = stream->Channels == 1 ? 0 : 1;
        for (var i = 0; i < frames; i++)
        {
            var frame = (int)voice->Cursor;
            if (!StreamFrameAvailable(voice, frame, available, ended)) { break; }
            if (!Envelope(voice, outputRate))
            {
                break;
            }
            var next = frame + 1 < available ? frame + 1 : frame;
            var blend = (float)(voice->Cursor - frame);
            var left = stream->Sample(frame, 0);
            var right = stream->Sample(frame, rightChannel);
            left += (stream->Sample(next, 0) - left) * blend;
            right += (stream->Sample(next, rightChannel) - right) * blend;
            Accumulate(voice, output, i, left, right, spatial);
            voice->Cursor += step;
        }
        var consumed = Math.Min((int)voice->Cursor, available);
        if (consumed > 0)
        {
            stream->Consume(consumed);
            voice->Cursor -= consumed;
        }
    }

    static bool StreamCanRender(Voice* voice)
    {
        if (voice->Paused != 0 && voice->Gain <= 0) { return false; }
        if (voice->StreamState == 1) { return true; }
        // An opening stream remains a silent pre-roll; failure ends it without counting a dropout.
        if (voice->StreamState == 2) { Deactivate(ref *voice); }
        return false;
    }

    static bool StreamFrameAvailable(Voice* voice, int frame, int available, bool ended)
    {
        if (frame + 1 < available) { return true; }
        if (!ended) { voice->Desc.Stream->ReportUnderrun(); return false; }
        if (frame < available) { return true; }
        Deactivate(ref *voice);
        return false;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        listener = new(Vector3.Zero, Quaternion.Identity);
        master = masterTarget = 1;
        buses.Reset();
        if (voices is not null)
        {
            NativeMemory.Clear(voices, (nuint)(capacity * 2 * sizeof(Voice)));
        }
        Volatile.Write(ref activeVoiceCount, 0);
        handles.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        buses.Dispose();
        NativeMemory.Free(voices);
        voices = null;
        Volatile.Write(ref activeVoiceCount, 0);
        handles.Clear();
    }
}
