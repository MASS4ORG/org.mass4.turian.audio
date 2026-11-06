using Microsoft.Extensions.DependencyInjection;
using MASS4.Attributes;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Transform-driven emitter posting named events through the session audio service.</summary>
[TypeId("0a76c35e-2590-470d-8bea-d33ce4bfe8ec")]
[ComponentContextMenu("Audio/Source")]
public sealed class AudioSource : Component
{
    IAudioService? audio;

    /// <summary>Event asset prepared when the component starts.</summary>
    public DataAssetReference<AudioEvent>? Event { get; set; }
    /// <summary>Whether the assigned event is posted on start.</summary>
    public bool PlayOnStart { get; set; }
    /// <summary>Continuous linear amplitude multiplier.</summary>
    public float Volume { get; set; } = 1;
    /// <summary>Continuous playback rate multiplier.</summary>
    public float Pitch { get; set; } = 1;

    /// <inheritdoc/>
    public override void OnStart()
    {
        audio = Services?.GetService<IAudioService>();
        if (audio is not null && Event is { IsEmpty: false } reference)
        {
            var eventName = audio.Prepare(reference);
            if (PlayOnStart)
            {
                Post(eventName, new AudioParameterSet());
            }
        }
    }

    /// <summary>Posts a prepared named event at this emitter's current position.</summary>
    public VoiceHandle Post(string eventName, in AudioParameterSet parameters)
    {
        audio ??= Services?.GetService<IAudioService>();
        var voice = audio?.Post(eventName, Id, parameters with
        {
            Position = Node?.GlobalTransform.Position ?? default,
        }) ?? default;
        OnUpdate(0);
        return voice;
    }

    /// <inheritdoc/>
    public override void OnUpdate(float deltaTime)
    {
        audio?.SetEmitterParameters(Id, new AudioParameterSet
        {
            Position = Node?.GlobalTransform.Position ?? default,
            Volume = Volume,
            Pitch = Pitch,
        });
    }

    /// <summary>Stops this emitter's voices, optionally fading them out.</summary>
    public void StopAll(float fadeOutSeconds = 0)
    {
        audio?.StopAll(Id, fadeOutSeconds);
    }

    /// <summary>Holds or resumes this emitter's voices at their current position.</summary>
    public void Pause(bool paused = true)
    {
        audio?.Pause(Id, paused);
    }

    /// <inheritdoc/>
    public override void OnDisable() => StopAll();
}
