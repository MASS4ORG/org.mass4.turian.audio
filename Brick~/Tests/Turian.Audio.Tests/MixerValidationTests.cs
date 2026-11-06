namespace Turian.Audio.Tests;

/// <summary>Rejects malformed authoring before callbacks or native DSP allocations become active.</summary>
public sealed class MixerValidationTests
{
    /// <summary>Hierarchy, snapshot, sidechain and processor errors produce setup-time diagnostics.</summary>
    [Theory]
    [InlineData("root")]
    [InlineData("duplicate")]
    [InlineData("parent")]
    [InlineData("cycle")]
    [InlineData("volume")]
    [InlineData("snapshot")]
    [InlineData("snapshot-bus")]
    [InlineData("snapshot-wet")]
    [InlineData("duck")]
    [InlineData("feedback")]
    [InlineData("factory")]
    public void InvalidAuthoringIsRejectedBeforePlayback(string scenario)
    {
        var model = new AudioMixer();
        switch (scenario)
        {
            case "root": model.Buses[0].Parent = "Music"; break;
            case "duplicate": model.Buses[1].Name = "SFX"; break;
            case "parent": model.Buses[1].Parent = "missing"; break;
            case "cycle": model.Buses[1].Parent = "SFX"; model.Buses[2].Parent = "Music"; break;
            case "volume": model.Buses[1].Volume = float.NaN; break;
            case "snapshot": model.Snapshots = [new() { Name = "x" }, new() { Name = "x" }]; break;
            case "snapshot-bus": model.Snapshots = [new() { Values = [new() { Bus = "missing" }] }]; break;
            case "snapshot-wet": model.Snapshots = [new() { Values = [new() { Bus = "SFX", EffectWet = [1] }] }]; break;
            case "duck": model.Buses[1].Ducking = new AudioDucking { SourceBus = "Music" }; break;
            case "feedback": model.Buses[1].Effects.Add(new() { Kind = AudioEffectKind.Delay, Feedback = 1 }); break;
            case "factory": model.Buses[1].Effects.Add(new() { Kind = AudioEffectKind.Custom, CustomId = "missing" }); break;
        }
        var settings = new AudioSettings { SampleRate = 8000 };
        using var device = new SilentAudioDevice();
        using var graph = new AudioMixerGraph(settings, new SimpleSpatializer());
        Assert.ThrowsAny<ArgumentException>(() => new AudioService(device, graph, settings, mixerModel: model));
        Assert.Equal(AudioDeviceState.Stopped, device.State);
    }

    /// <summary>Serialized mixer settings preserve bus graphs, effect chains, snapshots and project selection.</summary>
    [Fact]
    public void MixerAssetsRoundTripThroughEngineSerialization()
    {
        TypeRegistry.ScanAssembly(typeof(AudioMixer).Assembly);
        var mixer = new AudioMixer();
        mixer.Buses[1].Ducking = new AudioDucking { SourceBus = "Voice", ReductionDb = 18 };
        mixer.Buses[1].Effects.Add(new AudioEffect { Kind = AudioEffectKind.LowPass, Cutoff = 700, Wet = 0 });
        mixer.Snapshots.Add(new AudioSnapshot
        {
            Name = "Underwater", Values = [new() { Bus = "Music", Volume = 0.5f, EffectWet = [1] }],
        });
        var copy = Assert.IsType<AudioMixer>(Serializer.LoadData<DataAsset>(Serializer.Serialize<DataAsset>(mixer)));
        Assert.Equal(mixer.Id, copy.Id);
        Assert.Equal(18, copy.Buses[1].Ducking!.ReductionDb);
        Assert.Equal(700, copy.Buses[1].Effects[0].Cutoff);
        Assert.Equal(1, Assert.Single(copy.Snapshots[0].Values[0].EffectWet));
        var settings = new AudioSettings { Mixer = new DataAssetReference<AudioMixer>(mixer.Id) };
        var restored = Assert.IsType<AudioSettings>(Serializer.LoadData<DataAsset>(Serializer.Serialize<DataAsset>(settings)));
        Assert.Equal(mixer.Id, restored.Mixer!.AssetId);
    }
}
