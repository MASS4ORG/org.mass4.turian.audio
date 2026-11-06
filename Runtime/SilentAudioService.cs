using System;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Server implementation that performs no asset decoding, device initialization or mixing.</summary>
public sealed class SilentAudioService : IAudioService
{
    long sequence;

    /// <inheritdoc/>
    public void Prepare(AudioEvent audioEvent) { }
    /// <summary>Accepts an event reference without loading its payload.</summary>
    public string Prepare(DataAssetReference<AudioEvent> reference) => string.Empty;
    /// <inheritdoc/>
    public VoiceHandle Post(string eventName, Guid owner, in AudioParameterSet parameters) => new(++sequence);
    /// <inheritdoc/>
    public bool SetParameters(VoiceHandle voice, in AudioParameterSet parameters) => true;
    /// <inheritdoc/>
    public bool SetEmitterParameters(Guid owner, in AudioParameterSet parameters) => true;
    /// <inheritdoc/>
    public bool StopAll(Guid owner, float fadeOutSeconds = 0) => true;
    /// <inheritdoc/>
    public bool Pause(Guid owner, bool paused = true) => true;
    /// <inheritdoc/>
    public bool SetMasterVolume(float volume) => true;
    /// <inheritdoc/>
    public bool SetListener(in AudioListenerState listener) => true;
    /// <inheritdoc/>
    public AudioStatistics Statistics => default;
    /// <inheritdoc/>
    public void Reset() { }
    /// <inheritdoc/>
    public void Dispose() { }
}
