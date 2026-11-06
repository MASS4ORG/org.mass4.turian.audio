namespace Turian.Audio.Tests;

/// <summary>Checks producer ownership, authored asset loading and custom DSP fault containment.</summary>
public sealed unsafe class MixerSafetyTests
{
    /// <summary>Alternate voice mixers advertise no built-in buses and keep control errors on the producer.</summary>
    [Fact]
    public void ReplacementMixersDoNotAdvertiseUnsupportedControls()
    {
        using var audio = new AudioService(new SilentAudioDevice(), Substitute.For<IMixerGraph>(), new AudioSettings());
        Assert.Empty(audio.Buses);
        Assert.Empty(audio.Snapshots);
        Assert.Throws<NotSupportedException>(() => audio.SetBusVolume("Master", 0.5f));
        Assert.Throws<NotSupportedException>(() => audio.GetMeter("Master"));
    }

    sealed class FaultFactory(bool infinity) : IAudioEffectFactory
    {
        public string Id => "fault";
        public CompiledAudioEffect Compile(AudioEffect settings, int sampleRate)
            => new(new AudioEffectKernel(&Process, infinity ? 1 : 0));
        static void Process(nint context, float* samples, int frames)
        {
            for (var i = 0; i < frames * 2; i++) { samples[i] = context == 0 ? float.NaN : float.PositiveInfinity; }
        }
    }

    /// <summary>Invalid custom output silences the whole callback and subsequent output until reset.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonfiniteCustomDspSilencesOutputAndMeters(bool infinity)
    {
        var model = new AudioMixer();
        model.Buses[2].Effects.Add(new AudioEffect { Kind = AudioEffectKind.Custom, CustomId = "fault" });
        using var session = new MixerTests.Session(model, effects: [new FaultFactory(infinity)]);
        session.Tone("SFX");
        Assert.All(session.Render(), static sample => Assert.Equal(0, sample));
        Assert.All(session.Render(), static sample => Assert.Equal(0, sample));
        Assert.Equal(default, session.Audio.GetMeter("Master"));
        session.Audio.Reset();
        Assert.Equal(default, session.Audio.GetMeter("SFX"));
    }

    /// <summary>Settings load the selected mixer before event preparation and missing assets fail early.</summary>
    [Fact]
    public void SettingsResolveTheMixerAssetAtSessionConstruction()
    {
        var model = new AudioMixer();
        model.Buses[1].Volume = 0.3f;
        var loader = Substitute.For<IAssetLoader>();
        loader.LoadContentAsync<AudioMixer>(model.Id).Returns(Task.FromResult<AudioMixer?>(model));
        var settings = new AudioSettings { Mixer = new DataAssetReference<AudioMixer>(model.Id) };
        using var device = new SilentAudioDevice();
        using var graph = new AudioMixerGraph(settings, new SimpleSpatializer());
        using var audio = new AudioService(device, graph, settings, loader);
        Assert.Equal(0.3f, audio.GetBus("Music").Volume);
        Assert.Equal(AudioDeviceState.Stopped, device.State);
        loader.Received(1).LoadContentAsync<AudioMixer>(model.Id);
        using var missingGraph = new AudioMixerGraph(settings, new SimpleSpatializer());
        Assert.Throws<System.IO.InvalidDataException>(() => new AudioService(device, missingGraph, settings));
    }

    /// <summary>The public mixer API enforces the same single-producer ownership as event commands.</summary>
    [Fact]
    public void ControlsRejectAnotherProducerThread()
    {
        using var session = new MixerTests.Session();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { session.Audio.SetBusVolume("Music", 0.2f); }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, session.Audio.GetBus("Music").Volume);
    }
}
