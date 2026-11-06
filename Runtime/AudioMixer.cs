using System.Collections.Generic;
using MASS4.Attributes;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Authored bus hierarchy, effect chains, sidechains and named snapshots for one audio session.</summary>
[CreateAssetMenu(fileName: "AudioMixer", path: "Audio/Audio Mixer")]
[TypeId("07125d85-afd4-55c0-b543-1ce96ed39462")]
public sealed class AudioMixer : DataAsset
{
    /// <summary>Named buses with exactly one root named Master.</summary>
    public List<AudioBus> Buses { get; set; } =
    [
        new() { Name = "Master", Parent = "" },
        new() { Name = "Music" }, new() { Name = "SFX" }, new() { Name = "Voice" }, new() { Name = "UI" },
    ];
    /// <summary>Named target states used for timed runtime transitions.</summary>
    public List<AudioSnapshot> Snapshots { get; set; } = [];
}

/// <summary>One bus sends its processed stereo output to its parent.</summary>
public sealed class AudioBus
{
    /// <summary>Unique case-sensitive routing name.</summary>
    public string Name { get; set; } = "Bus";
    /// <summary>Parent routing name; only Master has an empty parent.</summary>
    public string Parent { get; set; } = "Master";
    /// <summary>Linear fader gain in the range zero through sixteen.</summary>
    public float Volume { get; set; } = 1;
    /// <summary>Whether this bus and its descendants are inaudible.</summary>
    public bool Mute { get; set; }
    /// <summary>Whether this bus and its subtree participate in the solo selection.</summary>
    public bool Solo { get; set; }
    /// <summary>Effects run in authored order before the fader and parent send.</summary>
    public List<AudioEffect> Effects { get; set; } = [];
    /// <summary>Optional detector bus controlling gain reduction.</summary>
    public AudioDucking? Ducking { get; set; }
}

/// <summary>Controls a bus from another bus's pre-fader signal, including while that detector is muted.</summary>
public sealed class AudioDucking
{
    /// <summary>Source bus and its descendants provide the detector signal.</summary>
    public string SourceBus { get; set; } = "Voice";
    /// <summary>Detector threshold in decibels relative to full scale.</summary>
    public float ThresholdDb { get; set; } = -30;
    /// <summary>Gain reduction in decibels while the detector exceeds the threshold.</summary>
    public float ReductionDb { get; set; } = 12;
    /// <summary>Seconds to approach the reduced gain.</summary>
    public float Attack { get; set; } = 0.01f;
    /// <summary>Seconds to recover after the detector becomes quiet.</summary>
    public float Release { get; set; } = 0.25f;
}

/// <summary>A named complete target state; buses omitted from Values return to their authored state.</summary>
public sealed class AudioSnapshot
{
    /// <summary>Unique transition name.</summary>
    public string Name { get; set; } = "Snapshot";
    /// <summary>Overrides for named buses.</summary>
    public List<AudioBusState> Values { get; set; } = [];
}

/// <summary>Serializable target fader, mute and solo values for a named bus.</summary>
public sealed class AudioBusState
{
    /// <summary>Case-sensitive bus name.</summary>
    public string Bus { get; set; } = "Master";
    /// <summary>Linear fader gain.</summary>
    public float Volume { get; set; } = 1;
    /// <summary>Whether the bus is muted.</summary>
    public bool Mute { get; set; }
    /// <summary>Whether the bus is selected for solo playback.</summary>
    public bool Solo { get; set; }
    /// <summary>Optional wet targets, one per authored effect; an empty list uses the authored amounts.</summary>
    public List<float> EffectWet { get; set; } = [];
}
