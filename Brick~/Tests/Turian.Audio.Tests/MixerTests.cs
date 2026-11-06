namespace Turian.Audio.Tests;

/// <summary>Verifies authored routing, live controls, transitions and sidechains through production PCM output.</summary>
public sealed class MixerTests
{
    internal sealed class Session : IDisposable
    {
        internal readonly SilentAudioDevice Device = new();
        internal readonly AudioMixerGraph Graph;
        internal readonly AudioService Audio;
        internal Session(AudioMixer? model = null, int queue = 64, IEnumerable<IAudioEffectFactory>? effects = null)
        {
            var settings = new AudioSettings { SampleRate = 8000, BufferFrames = 64, CommandCapacity = queue };
            Graph = new(settings, new SimpleSpatializer());
            Audio = new(Device, Graph, settings, mixerModel: model, effectFactories: effects);
        }
        internal Guid Tone(string bus, float sample = 0.2f)
        {
            var owner = Guid.NewGuid();
            var name = owner.ToString();
            Audio.Prepare(new AudioEvent { EventName = name, Bus = bus, Loop = true },
                [AudioClip.FromPcm([sample], new(1, 8000))]);
            Audio.Post(name, owner, new AudioParameterSet());
            return owner;
        }
        internal float[] Render(int frames = 1024)
        {
            var output = new float[frames * 2];
            Device.Render(output);
            return output;
        }
        public void Dispose() => Audio.Dispose();
    }

    /// <summary>Nested buses multiply gains and mute descendants while unrelated buses remain audible.</summary>
    [Fact]
    public void HierarchyRoutesChildBusesBeforeParents()
    {
        var model = new AudioMixer();
        model.Buses[2].Volume = 0.5f;
        model.Buses.Add(new AudioBus { Name = "Combat", Parent = "SFX", Volume = 0.5f });
        using var session = new Session(model);
        session.Tone("Combat");
        session.Tone("Music", 0.1f);
        Assert.Equal(0.15f, session.Render()[^1], 5);
        Assert.True(session.Audio.SetBusMute("SFX", true));
        Assert.Equal(0.1f, session.Render()[^1], 5);
        Assert.True(session.Audio.SetBusMute("SFX", false));
        session.Audio.SetBusVolume("Combat", 0.25f);
        Assert.Equal(0.125f, session.Render()[^1], 5);
        session.Render();
        Assert.Equal(0.05f, session.Audio.GetMeter("Combat").RightPeak, 5);
    }

    /// <summary>Solo selects whole subtrees, with muted buses silent and direct ancestor voices excluded.</summary>
    [Fact]
    public void SoloCombinesSelectedBusesWithoutLeakingAncestorVoices()
    {
        using var session = new Session();
        session.Tone("Master", 0.05f);
        session.Tone("Music", 0.1f);
        session.Tone("SFX", 0.2f);
        session.Tone("Voice", 0.3f);
        Assert.Equal(0.65f, session.Render()[^1], 5);
        session.Audio.SetBusSolo("Music", true);
        Assert.Equal(0.1f, session.Render()[^1], 5);
        session.Audio.SetBusSolo("SFX", true);
        Assert.Equal(0.3f, session.Render()[^1], 5);
        session.Audio.SetBusMute("Music", true);
        Assert.Equal(0.2f, session.Render()[^1], 5);
        session.Audio.SetBusSolo("Master", true);
        Assert.Equal(0.55f, session.Render()[^1], 5);
    }

    /// <summary>Snapshot gains reach exact endpoints at the requested frame count across callback chunks.</summary>
    [Fact]
    public void SnapshotsTransitionFromTheCurrentGainAndRestoreDefaults()
    {
        var model = new AudioMixer();
        model.Buses[1].Volume = 0.2f;
        model.Snapshots.Add(new AudioSnapshot { Name = "Loud", Values = [new() { Bus = "Music", Volume = 0.6f }] });
        model.Snapshots.Add(new AudioSnapshot { Name = "Normal" });
        using var session = new Session(model);
        session.Tone("Music", 0.25f);
        Assert.Equal(0.05f, session.Render()[^1], 5);
        Assert.True(session.Audio.TransitionTo("Loud", 0.1f));
        Assert.Equal(0.1f, session.Render(400)[^1], 4);
        Assert.Equal(0.15f, session.Render(400)[^1], 5);
        session.Audio.TransitionTo("Normal", 0.1f);
        Assert.Equal(0.1f, session.Render(400)[^1], 4);
        session.Audio.TransitionTo("Loud", 0.05f);
        Assert.Equal(0.15f, session.Render(400)[^1], 5);
        Assert.Equal(0.6f, session.Audio.GetBus("Music").Volume);
    }

    /// <summary>A saturated command queue rejects the complete snapshot without changing requested state.</summary>
    [Fact]
    public void SnapshotQueueFailureDoesNotPartiallyApplyState()
    {
        var model = new AudioMixer();
        model.Snapshots.Add(new AudioSnapshot { Name = "Mute", Values = [new() { Bus = "Music", Mute = true }] });
        using var session = new Session(model, queue: 2);
        Assert.True(session.Audio.SetBusVolume("Music", 0.4f));
        Assert.True(session.Audio.SetBusVolume("SFX", 0.3f));
        Assert.False(session.Audio.TransitionTo("Mute", 1));
        Assert.False(session.Audio.SetBusMute("Music", true));
        Assert.Equal(new AudioBusValues(0.4f, false, false), session.Audio.GetBus("Music"));
        session.Tone("Voice");
        session.Render();
        Assert.True(session.Audio.TransitionTo("Mute", 1));
        Assert.True(session.Audio.GetBus("Music").Mute);
    }

    /// <summary>A muted detector bus still ducks music, and stopping its voices releases the reduction.</summary>
    [Fact]
    public void DuckingUsesPreFaderDetectorAndRecoversAfterSpeech()
    {
        var model = new AudioMixer();
        model.Buses[1].Ducking = new AudioDucking { SourceBus = "Voice", Attack = 0.001f, Release = 0.02f };
        using var session = new Session(model);
        session.Tone("Music");
        Assert.Equal(0.2f, session.Render()[^1], 5);
        var voice = session.Tone("Voice");
        session.Audio.SetBusMute("Voice", true);
        Assert.Equal(0.2f * MathF.Pow(10, -12f / 20), session.Render(2048)[^1], 4);
        session.Audio.StopAll(voice);
        Assert.Equal(0.2f, session.Render(4096)[^1], 4);
        Assert.Equal(0.2f, session.Audio.GetMeter("Music").LeftPeak, 4);
    }

    /// <summary>Controls validate bus names and numeric limits before enqueueing callback work.</summary>
    [Fact]
    public void InvalidControlsFailOnTheProducerThread()
    {
        using var session = new Session();
        Assert.Throws<ArgumentException>(() => session.Audio.SetBusMute("unknown", true));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Audio.SetBusVolume("Music", float.NaN));
        Assert.Throws<ArgumentException>(() => session.Audio.TransitionTo("missing", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Audio.TransitionTo("missing", -1));
        Assert.Throws<ArgumentException>(() => session.Tone("unknown"));
    }

    /// <summary>Repeated resets restore controls and clear voices, meters and delay history.</summary>
    [Fact]
    public void ResetRestoresTheAuthoredMixAndClearsDspState()
    {
        var model = new AudioMixer();
        model.Buses[2].Effects.Add(new AudioEffect { Kind = AudioEffectKind.Delay, Seconds = 0.01f });
        using var session = new Session(model);
        for (var i = 0; i < 3; i++)
        {
            session.Tone("SFX");
            session.Render();
            session.Audio.SetBusMute("Music", true);
            session.Audio.Reset();
            Assert.Equal(new AudioBusValues(1, false, false), session.Audio.GetBus("Music"));
            Assert.Equal(default, session.Audio.GetMeter("SFX"));
            Assert.All(session.Render(), static value => Assert.Equal(0, value));
            Assert.Equal(0, session.Graph.ActiveVoiceCount);
        }
    }

    /// <summary>Rendered stereo channels publish independent RMS and peak values.</summary>
    [Fact]
    public void MeterReportsProcessedChannels()
    {
        using var session = new Session();
        session.Audio.Prepare(new AudioEvent { EventName = "stereo", Bus = "UI", Loop = true },
            [AudioClip.FromPcm([0.1f, -0.2f], new(2, 8000))]);
        session.Audio.Post("stereo", Guid.Empty, new AudioParameterSet());
        session.Render();
        session.Render();
        var meter = session.Audio.GetMeter("UI");
        Assert.Equal(0.1f, meter.LeftPeak, 5);
        Assert.Equal(0.2f, meter.RightPeak, 5);
        Assert.Equal(0.1f, meter.LeftRms, 5);
        Assert.Equal(0.2f, meter.RightRms, 5);
    }
}
