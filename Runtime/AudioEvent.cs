using System.Collections.Generic;
using MASS4.Attributes;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Named playback event with clip variations and default voice policy.</summary>
[CreateAssetMenu(fileName: "AudioEvent", path: "Audio/Audio Event")]
[TypeId("bbf8bc67-0f8c-4613-a263-5f1ee7cb28f6")]
public sealed class AudioEvent : DataAsset
{
    /// <summary>Name used by AudioSource.Post.</summary>
    public string EventName { get; set; } = string.Empty;
    /// <summary>Destination mixer bus for every variation of this event.</summary>
    public string Bus { get; set; } = "SFX";
    /// <summary>Variations selected in round-robin order when posting.</summary>
    public List<AssetReference<AudioClip>> Clips { get; set; } = [];
    /// <summary>Default linear amplitude.</summary>
    public float Volume { get; set; } = 1;
    /// <summary>Default playback rate.</summary>
    public float Pitch { get; set; } = 1;
    /// <summary>Whether playback wraps at the end of the clip.</summary>
    public bool Loop { get; set; }
    /// <summary>Whether the listener pose affects the voice.</summary>
    public bool Spatial { get; set; }
    /// <summary>Distance within which the source retains full amplitude.</summary>
    public float MinDistance { get; set; } = 1;
    /// <summary>Distance at which the source becomes silent.</summary>
    public float MaxDistance { get; set; } = 100;
    /// <summary>Higher values win voice stealing decisions.</summary>
    public int Priority { get; set; } = 128;
    /// <summary>Whether clips are decoded incrementally while playing instead of fully into memory when prepared.</summary>
    public bool Stream { get; set; }
}
