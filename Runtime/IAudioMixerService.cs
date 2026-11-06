using System.Collections.Generic;

namespace Turian.Audio;

/// <summary>Producer-thread mixer controls and lock-free metering, independent of a particular GUI.</summary>
public interface IAudioMixerService
{
    /// <summary>Stable bus routing names in authored order.</summary>
    IReadOnlyList<string> Buses { get; }
    /// <summary>Available snapshot names.</summary>
    IReadOnlyList<string> Snapshots { get; }
    /// <summary>Reads the current requested fader, mute and solo state.</summary>
    AudioBusValues GetBus(string bus);
    /// <summary>Queues a fader change; false means the command queue is full.</summary>
    bool SetBusVolume(string bus, float volume);
    /// <summary>Queues mute without changing the authored or requested fader.</summary>
    bool SetBusMute(string bus, bool mute);
    /// <summary>Queues solo selection without changing other buses' solo flags.</summary>
    bool SetBusSolo(string bus, bool solo);
    /// <summary>Queues one atomic complete snapshot, with a sample-accurate gain transition.</summary>
    bool TransitionTo(string snapshot, float seconds);
    /// <summary>Reads post-effects and post-fader stereo peak/RMS values.</summary>
    AudioBusMeter GetMeter(string bus);
}

/// <summary>Requested bus controls, unaffected by automatic sidechain reduction.</summary>
public readonly record struct AudioBusValues(float Volume, bool Mute, bool Solo);

/// <summary>Post-fader linear amplitudes published once per rendered block.</summary>
public readonly record struct AudioBusMeter(float LeftPeak, float RightPeak, float LeftRms, float RightRms);
