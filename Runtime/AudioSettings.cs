using System;
using MASS4.Attributes;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Session audio format, callback budget and voice limits.</summary>
[CreateAssetMenu(fileName: "AudioSettings", path: "Settings/Audio Settings")]
[TypeId("0edcd9ad-e28c-4cb5-9174-26d6cbd3b33c")]
public sealed class AudioSettings : ProjectSettingsAsset
{
    /// <summary>Output frames per second.</summary>
    public int SampleRate { get; set; } = 48000;
    /// <summary>Optional authored mixer; an empty reference uses Master, Music, SFX, Voice and UI.</summary>
    public DataAssetReference<AudioMixer>? Mixer { get; set; }
    /// <summary>Requested device period in frames.</summary>
    public int BufferFrames { get; set; } = 512;
    /// <summary>Maximum concurrent voices, excluding their short stealing fades.</summary>
    public int MaxVirtualVoices { get; set; } = 64;
    /// <summary>Maximum queued main-thread commands.</summary>
    public int CommandCapacity { get; set; } = 1024;
    /// <summary>Exact playback device name; empty selects the system default.</summary>
    public string DeviceName { get; set; } = string.Empty;
    /// <summary>Maximum concurrently streamed voices; each owns one preallocated ring.</summary>
    public int MaxStreams { get; set; } = 4;
    /// <summary>Frames buffered ahead per stream, rounded up to a power of two.</summary>
    public int StreamBufferFrames { get; set; } = 32768;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStreams, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxStreams, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(StreamBufferFrames, 4096);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(StreamBufferFrames, 1 << 20);
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SampleRate, 192000);
        ArgumentOutOfRangeException.ThrowIfLessThan(BufferFrames, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BufferFrames, 8192);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxVirtualVoices, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxVirtualVoices, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(CommandCapacity, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CommandCapacity, 65536);
    }
}
