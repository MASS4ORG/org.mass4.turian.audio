using System;
using System.Collections.Generic;

namespace Turian.Audio;

/// <summary>Accepts mixer controls on servers without loading mixer assets or allocating DSP state.</summary>
public sealed class SilentAudioMixerService : IAudioMixerService
{
    static readonly IReadOnlyList<string> Names = Array.AsReadOnly<string>(["Master", "Music", "SFX", "Voice", "UI"]);
    /// <inheritdoc/>
    public IReadOnlyList<string> Buses => Names;
    /// <inheritdoc/>
    public IReadOnlyList<string> Snapshots => Array.Empty<string>();
    /// <inheritdoc/>
    public AudioBusValues GetBus(string bus) => new(1, false, false);
    /// <inheritdoc/>
    public bool SetBusVolume(string bus, float volume) => true;
    /// <inheritdoc/>
    public bool SetBusMute(string bus, bool mute) => true;
    /// <inheritdoc/>
    public bool SetBusSolo(string bus, bool solo) => true;
    /// <inheritdoc/>
    public bool TransitionTo(string snapshot, float seconds) => true;
    /// <inheritdoc/>
    public AudioBusMeter GetMeter(string bus) => default;
}
