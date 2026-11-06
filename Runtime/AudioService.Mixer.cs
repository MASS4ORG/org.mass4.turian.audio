using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Turian.Audio;

public sealed unsafe partial class AudioService
{
    AudioBusMixer? busMixer;
    AudioBusValues[] requested = [];

    void InitializeMixer(AudioSettings source, AudioMixer? model, IEnumerable<IAudioEffectFactory>? effects)
    {
        if (mixer is not AudioMixerGraph graph) { return; }
        if (model is null && source.Mixer is { IsEmpty: false } reference)
        {
            model = assets?.LoadContentAsync<AudioMixer>(reference.AssetId).GetAwaiter().GetResult()
                ?? throw new InvalidDataException("The project's audio mixer asset could not be loaded.");
        }
        if (model is not null) { graph.ConfigureMixer(model, effects); }
        busMixer = graph.BusMixer;
        requested = (AudioBusValues[])busMixer.Defaults.Clone();
    }

    int EventBus(AudioEvent audioEvent) => busMixer is null ? 0 : BusIndex(audioEvent.Bus);

    int BusIndex(string bus)
    {
        if (busMixer is null) { throw new NotSupportedException("This mixer does not provide bus controls."); }
        if (!busMixer.Indices.TryGetValue(bus, out var index))
        {
            throw new ArgumentException($"The mixer has no bus named '{bus}'.", nameof(bus));
        }
        return index;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Buses => busMixer?.Names ?? Array.Empty<string>();

    /// <inheritdoc/>
    public IReadOnlyList<string> Snapshots => busMixer?.SnapshotNames ?? Array.Empty<string>();

    /// <inheritdoc/>
    public AudioBusValues GetBus(string bus)
    {
        CheckProducer();
        return requested[BusIndex(bus)];
    }

    /// <inheritdoc/>
    public bool SetBusVolume(string bus, float volume)
    {
        CheckProducer();
        AudioMixerValidation.Range(volume, 0, 16, nameof(volume));
        var index = BusIndex(bus);
        return QueueBus(index, requested[index] with { Volume = volume });
    }

    /// <inheritdoc/>
    public bool SetBusMute(string bus, bool mute)
    {
        CheckProducer();
        var index = BusIndex(bus);
        return QueueBus(index, requested[index] with { Mute = mute });
    }

    /// <inheritdoc/>
    public bool SetBusSolo(string bus, bool solo)
    {
        CheckProducer();
        var index = BusIndex(bus);
        return QueueBus(index, requested[index] with { Solo = solo });
    }

    bool QueueBus(int index, AudioBusValues state)
    {
        if (!commands.TryWrite(new AudioCommand { Kind = AudioCommandKind.Bus, Bus = index, BusValues = state }))
        {
            return false;
        }
        requested[index] = state;
        return true;
    }

    /// <inheritdoc/>
    public bool TransitionTo(string snapshot, float seconds)
    {
        CheckProducer();
        AudioMixerValidation.Range(seconds, 0, 3600, nameof(seconds));
        var index = -1;
        for (var i = 0; i < Snapshots.Count; i++)
        {
            if (Snapshots[i] == snapshot) { index = i; break; }
        }
        if (index < 0) { throw new ArgumentException($"No mixer snapshot is named '{snapshot}'.", nameof(snapshot)); }
        if (!commands.TryWrite(new AudioCommand { Kind = AudioCommandKind.Snapshot, Bus = index, Value = seconds }))
        {
            return false;
        }
        busMixer!.SnapshotValues[index].CopyTo(requested, 0);
        return true;
    }

    /// <inheritdoc/>
    public AudioBusMeter GetMeter(string bus)
    {
        CheckProducer();
        var index = BusIndex(bus);
        return Volatile.Read(ref callbackFault) == 0 ? busMixer!.Meter(index) : default;
    }
}
