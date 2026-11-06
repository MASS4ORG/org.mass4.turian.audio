using System;
using System.Collections.Generic;
using System.Numerics;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Interleaved floating-point PCM layout; one frame contains one sample per channel.</summary>
public readonly record struct AudioFormat(int Channels, int SampleRate);

/// <summary>Sequential PCM provider, independent of storage and codec.</summary>
public interface IAudioStreamSource : IDisposable
{
    /// <summary>The layout returned by Read.</summary>
    AudioFormat Format { get; }
    /// <summary>Fills complete frames and returns their count; zero means end of stream.</summary>
    int Read(Span<float> buffer);
    /// <summary>Moves to a frame, or throws NotSupportedException for a non-seekable source.</summary>
    void Seek(long frame);
}

/// <summary>Opens asset content as PCM without exposing paths to playback.</summary>
public interface IAudioCodec
{
    /// <summary>Recognized filename extensions, including the leading dot.</summary>
    IReadOnlyList<string> Extensions { get; }
    /// <summary>Opens a decoder; the caller owns the returned source.</summary>
    IAudioStreamSource OpenStream(IAssetFileProvider asset);
}

/// <summary>Device lifecycle state.</summary>
public enum AudioDeviceState
{
    /// <summary>No callback is running.</summary>
    Stopped,
    /// <summary>The device may pull PCM.</summary>
    Started,
}

/// <summary>Callback configuration; the context and callback remain valid until Stop returns.</summary>
public readonly unsafe struct AudioDeviceConfig(
    AudioFormat format, int bufferFrames, string deviceName, nint context,
    delegate* unmanaged[Cdecl]<nint, float*, uint, void> callback)
{
    /// <summary>Requested PCM layout.</summary>
    public AudioFormat Format { get; } = format;
    /// <summary>Requested callback period.</summary>
    public int BufferFrames { get; } = bufferFrames;
    /// <summary>Exact device name, or empty for the default.</summary>
    public string DeviceName { get; } = deviceName;
    /// <summary>Opaque callback context.</summary>
    public nint Context { get; } = context;
    /// <summary>Unmanaged PCM fill entry point.</summary>
    public delegate* unmanaged[Cdecl]<nint, float*, uint, void> Callback { get; } = callback;
}

/// <summary>Owns a playback device. Stop joins callbacks before returning.</summary>
public interface IAudioDevice : IDisposable
{
    /// <summary>Starts pulling PCM through the configured callback.</summary>
    void Start(in AudioDeviceConfig config);
    /// <summary>Stops and joins callbacks; repeated calls are harmless.</summary>
    void Stop();
    /// <summary>Current device lifecycle state.</summary>
    AudioDeviceState State { get; }
}

/// <summary>Session-local playback identity; zero is invalid.</summary>
public readonly record struct VoiceHandle(long Value);

/// <summary>Playback parameters sent by value through the audio command queue.</summary>
public readonly record struct AudioParameterSet
{
    /// <summary>Creates default unity gain and pitch.</summary>
    public AudioParameterSet() { }
    /// <summary>Linear amplitude multiplier.</summary>
    public float Volume { get; init; } = 1;
    /// <summary>Playback rate multiplier.</summary>
    public float Pitch { get; init; } = 1;
    /// <summary>World position of the emitter.</summary>
    public Vector3 Position { get; init; }
}

/// <summary>Listener pose in world space; the unrotated listening basis has +Z forward and -X screen-right.</summary>
public readonly record struct AudioListenerState(Vector3 Position, Quaternion Orientation);

/// <summary>Spatial state used once per voice block.</summary>
public readonly record struct VoiceState(Vector3 Position, bool Spatial, float MinDistance, float MaxDistance);

/// <summary>Stereo gains and distance used for voice selection.</summary>
public readonly record struct SpatialParams(float Left, float Right, float Distance);

/// <summary>Per-voice spatial processing, independent of the device and mixer.</summary>
public interface ISpatializer
{
    /// <summary>Captures static processing and preallocated state before the device starts.</summary>
    SpatializerKernel Kernel { get; }
    /// <summary>Sets the audio thread's listener pose.</summary>
    void ProcessListener(in AudioListenerState listener);
    /// <summary>Computes the voice's stereo coefficients.</summary>
    SpatialParams Compute(in VoiceState voice);
}

/// <summary>Immutable PCM and initial playback state. PCM must outlive every voice referencing it.</summary>
public readonly unsafe struct VoiceDesc(
    VoiceHandle handle, Guid owner, float* samples, int frames, AudioFormat format,
    AudioParameterSet parameters, bool loop, bool spatial, float minDistance, float maxDistance, int priority)
{
    /// <summary>Playback identity.</summary>
    public VoiceHandle Handle { get; init; } = handle;
    /// <summary>Emitter identity.</summary>
    public Guid Owner { get; init; } = owner;
    /// <summary>Stable pointer to interleaved PCM.</summary>
    public float* Samples { get; } = samples;
    /// <summary>PCM frame count.</summary>
    public int Frames { get; } = frames;
    /// <summary>PCM layout.</summary>
    public AudioFormat Format { get; } = format;
    /// <summary>Current continuous playback parameters.</summary>
    public AudioParameterSet Parameters { get; init; } = parameters;
    readonly byte loopFlag = loop ? (byte)1 : (byte)0;
    /// <summary>Whether the voice wraps its PCM cursor.</summary>
    public bool Loop => loopFlag != 0;
    readonly byte spatialFlag = spatial ? (byte)1 : (byte)0;
    /// <summary>Whether the spatializer affects the voice.</summary>
    public bool Spatial => spatialFlag != 0;
    /// <summary>Inner attenuation radius.</summary>
    public float MinDistance { get; } = minDistance;
    /// <summary>Outer attenuation radius.</summary>
    public float MaxDistance { get; } = maxDistance;
    /// <summary>Higher priorities win voice stealing decisions.</summary>
    public int Priority { get; } = priority;
    /// <summary>Event asset's default gain, independent of continuous emitter parameters.</summary>
    public float EventVolume { get; init; } = 1;
    /// <summary>Event asset's default pitch, independent of continuous emitter parameters.</summary>
    public float EventPitch { get; init; } = 1;
    /// <summary>Compiled destination bus index; zero is Master for direct mixer clients.</summary>
    public int Bus { get; init; }
    /// <summary>Streamed PCM ring used instead of resident samples; null for resident voices.</summary>
    public AudioStreamBuffer* Stream { get; init; }
}

/// <summary>Session counters for diagnosing dropouts and resource leaks.</summary>
public readonly record struct AudioStatistics(int ActiveVoices, int ActiveStreams, long StreamUnderruns);

/// <summary>Audio-thread-owned voice executor; future graph implementations replace this seam.</summary>
public interface IMixerGraph : IDisposable
{
    /// <summary>Number of active voices.</summary>
    int ActiveVoiceCount { get; }
    /// <summary>Acquires a pooled voice after the service's voice manager has selected capacity.</summary>
    VoiceHandle CreateVoice(in VoiceDesc desc);
    /// <summary>Whether a handle still owns a playing voice, excluding voices already fading out.</summary>
    bool IsPlaying(VoiceHandle voice);
    /// <summary>Releases one logical voice with an optional completion fade.</summary>
    void StopVoice(VoiceHandle voice, float fadeOutSeconds);
    /// <summary>Changes the continuous parameters of a voice.</summary>
    void SetParameters(VoiceHandle voice, in AudioParameterSet parameters);
    /// <summary>Modulates every voice owned by an emitter while retaining its posted parameters.</summary>
    void SetEmitterParameters(Guid owner, in AudioParameterSet parameters);
    /// <summary>Stops all voices belonging to an emitter with an optional fade.</summary>
    void Stop(Guid owner, float fadeOutSeconds);
    /// <summary>Holds or resumes every voice owned by an emitter at its current position, with short fades.</summary>
    void Pause(Guid owner, bool paused);
    /// <summary>Sets the linear output gain applied after all voices, ramped to avoid clicks.</summary>
    void SetMasterVolume(float volume);
    /// <summary>Updates the listener before mixing.</summary>
    void SetListener(in AudioListenerState listener);
    /// <summary>Fills interleaved stereo output, returning the number of frames.</summary>
    int Mix(Span<float> interleavedOut);
    /// <summary>Returns all voices to the pool after the device has stopped.</summary>
    void Reset();
}

/// <summary>Main-thread event API; implementations own devices, PCM and command queues for one session.</summary>
public interface IAudioService : IDisposable
{
    /// <summary>Prepares an event and its PCM outside the audio callback.</summary>
    void Prepare(AudioEvent audioEvent);
    /// <summary>Resolves and prepares an event asset, returning its registered name.</summary>
    string Prepare(DataAssetReference<AudioEvent> reference);
    /// <summary>Posts a prepared named event; returns invalid when the queue is full.</summary>
    VoiceHandle Post(string eventName, Guid owner, in AudioParameterSet parameters);
    /// <summary>Queues continuous parameters for a voice.</summary>
    bool SetParameters(VoiceHandle voice, in AudioParameterSet parameters);
    /// <summary>Queues position, gain and pitch modulation for every voice owned by an emitter.</summary>
    bool SetEmitterParameters(Guid owner, in AudioParameterSet parameters);
    /// <summary>Queues a stop for every voice owned by an emitter.</summary>
    bool StopAll(Guid owner, float fadeOutSeconds = 0);
    /// <summary>Queues holding or resuming every voice owned by an emitter.</summary>
    bool Pause(Guid owner, bool paused = true);
    /// <summary>Queues the session's linear output gain; zero mutes without stopping voices.</summary>
    bool SetMasterVolume(float volume);
    /// <summary>Queues the listener pose.</summary>
    bool SetListener(in AudioListenerState listener);
    /// <summary>Current voice, stream and dropout counters.</summary>
    AudioStatistics Statistics { get; }
    /// <summary>Stops callbacks and releases all voices and prepared event data.</summary>
    void Reset();
}
