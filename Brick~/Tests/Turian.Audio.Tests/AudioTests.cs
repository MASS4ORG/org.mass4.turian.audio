namespace Turian.Audio.Tests;

/// <summary>Exercises the production audio command and mixer path without opening a device.</summary>
public sealed class AudioTests
{
    sealed class Session : IDisposable
    {
        public readonly AudioSettings Settings;
        public readonly SilentAudioDevice Device = new();
        public readonly AudioMixerGraph Mixer;
        public readonly AudioService Service;
        public readonly Guid Owner = Guid.NewGuid();

        public Session(int voices = 4, int queue = 64)
        {
            Settings = new() { SampleRate = 8000, MaxVirtualVoices = voices, CommandCapacity = queue };
            Mixer = new(Settings, new SimpleSpatializer());
            Service = new(Device, Mixer, Settings);
        }

        public void Prepare(string name = "tone", bool loop = true, bool spatial = false,
            int priority = 128, float value = 0.5f, float volume = 1, float pitch = 1)
        {
            Service.Prepare(new AudioEvent
            {
                EventName = name,
                Loop = loop,
                Spatial = spatial,
                Priority = priority,
                MinDistance = 1,
                MaxDistance = 11,
                Volume = volume,
                Pitch = pitch,
            }, [AudioClip.FromPcm(Enumerable.Repeat(value, 800).ToArray(), new(1, 8000))]);
        }

        public float[] Render(int frames = 128)
        {
            var output = new float[frames * 2];
            Device.Render(output);
            return output;
        }

        public void Dispose() => Service.Dispose();
    }

    /// <summary>Playback preserves event defaults when emitter parameters change and stops by owner.</summary>
    [Fact]
    public void PlaybackRetainsEventGainAndStops()
    {
        using var session = new Session();
        session.Prepare(volume: 0.5f);
        var voice = session.Service.Post("tone", session.Owner, new AudioParameterSet());
        Assert.NotEqual(0, voice.Value);
        Assert.Equal(0.25f, session.Render()[^1], 5);
        Assert.Equal(1, session.Mixer.ActiveVoiceCount);
        session.Service.SetEmitterParameters(session.Owner, new AudioParameterSet { Volume = 0.5f });
        Assert.Equal(0.125f, session.Render()[^1], 5);
        session.Service.SetParameters(voice, new AudioParameterSet { Volume = 0.25f });
        Assert.Equal(0.0625f, session.Render()[^1], 5);
        session.Service.StopAll(session.Owner);
        Assert.All(session.Render(), static sample => Assert.Equal(0, sample));
        Assert.Equal(0, session.Mixer.ActiveVoiceCount);
    }

    /// <summary>Every overlapping voice follows its emitter while other emitters keep playing.</summary>
    [Fact]
    public void EmitterParametersApplyToAllOwnedVoices()
    {
        using var session = new Session();
        session.Prepare(value: 0.1f);
        session.Service.Post("tone", session.Owner, new AudioParameterSet());
        session.Service.Post("tone", session.Owner, new AudioParameterSet { Volume = 0.5f });
        session.Service.Post("tone", Guid.NewGuid(), new AudioParameterSet());
        session.Service.SetEmitterParameters(session.Owner, new AudioParameterSet { Volume = 0.5f });
        Assert.Equal(0.175f, session.Render()[^1], 5);
        session.Service.StopAll(session.Owner);
        Assert.Equal(0.1f, session.Render()[^1], 5);
    }

    /// <summary>Higher priority events steal with a short fade, while low priority posts are rejected.</summary>
    [Fact]
    public void VoicePoolHonorsPriorityAndFadesStolenVoice()
    {
        using var session = new Session(voices: 1);
        session.Prepare("low", priority: 1, value: 0.2f);
        session.Prepare("high", priority: 2, value: 0.4f);
        session.Service.Post("low", session.Owner, new AudioParameterSet());
        Assert.Equal(0.2f, session.Render()[^1], 5);
        session.Service.Post("high", session.Owner, new AudioParameterSet());
        var transition = session.Render();
        Assert.InRange(transition[0], 0.19f, 0.22f);
        Assert.Equal(0.4f, transition[^1], 5);
        session.Service.Post("low", session.Owner, new AudioParameterSet());
        Assert.Equal(0.4f, session.Render()[^1], 5);
        Assert.Equal(1, session.Mixer.ActiveVoiceCount);
    }

    /// <summary>Equally prioritized nearby voices displace farther ones.</summary>
    [Fact]
    public void VoiceStealingUsesListenerDistance()
    {
        using var session = new Session(voices: 1);
        session.Prepare(spatial: true);
        session.Service.Post("tone", session.Owner, new AudioParameterSet { Position = new(10, 0, 0) });
        session.Render();
        session.Service.Post("tone", session.Owner, new AudioParameterSet { Position = new(1, 0, 0) });
        Assert.Equal(0.5f, session.Render()[^2], 5);
        session.Service.Post("tone", session.Owner, new AudioParameterSet { Position = new(10, 0, 0) });
        Assert.Equal(0.5f, session.Render()[^2], 5);
    }

    /// <summary>Pan and attenuation follow listener position and orientation through queued commands.</summary>
    [Fact]
    public void SpatialAudioUsesListenerPose()
    {
        using var session = new Session();
        session.Prepare(spatial: true);
        session.Service.Post("tone", session.Owner, new AudioParameterSet { Position = new(6, 0, 0) });
        var output = session.Render();
        Assert.Equal(0.25f, output[^2], 5);
        Assert.Equal(0, output[^1]);
        session.Service.SetListener(new(new(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI)));
        output = session.Render();
        Assert.InRange(Math.Abs(output[^2]), 0, 0.001f);
        Assert.Equal(0.5f, output[^1], 5);
        session.Service.SetListener(new(new(-10, 0, 0), Quaternion.Identity));
        Assert.All(session.Render(), static sample => Assert.Equal(0, sample));
    }

    /// <summary>Pitch changes duration, looping wraps correctly and stop fades reach silence.</summary>
    [Fact]
    public void PitchLoopAndFadeHaveDeterministicLifetimes()
    {
        using var session = new Session();
        session.Prepare(loop: false, pitch: 2);
        session.Service.Post("tone", session.Owner, new AudioParameterSet());
        session.Render(399);
        Assert.Equal(1, session.Mixer.ActiveVoiceCount);
        session.Render(2);
        Assert.Equal(0, session.Mixer.ActiveVoiceCount);
        session.Prepare("loop");
        session.Service.Post("loop", session.Owner, new AudioParameterSet());
        session.Render(2000);
        Assert.Equal(1, session.Mixer.ActiveVoiceCount);
        session.Service.StopAll(session.Owner, 0.01f);
        var fade = session.Render(128);
        Assert.True(fade[0] > fade[40]);
        Assert.Equal(0, fade[^1]);
        Assert.Equal(0, session.Mixer.ActiveVoiceCount);
    }

    /// <summary>Stereo channels remain separate, rate conversion interpolates, and event variations alternate.</summary>
    [Fact]
    public void StereoResamplingAndVariationsPreservePcm()
    {
        using var session = new Session();
        var samples = Enumerable.Range(0, 100).SelectMany(static _ => new[] { 0.2f, -0.4f }).ToArray();
        var clip = AudioClip.FromPcm(samples, new(2, 4000));
        session.Service.Prepare(new AudioEvent { EventName = "stereo", Loop = true }, [clip]);
        session.Service.Post("stereo", session.Owner, new AudioParameterSet());
        var output = session.Render();
        Assert.Equal(0.2f, output[^2], 5);
        Assert.Equal(-0.4f, output[^1], 5);
        session.Service.StopAll(session.Owner);
        session.Service.Prepare(new AudioEvent { EventName = "vary", Loop = true },
            [AudioClip.FromPcm([0.1f], new(1, 8000)), AudioClip.FromPcm([0.3f], new(1, 8000))]);
        session.Service.Post("vary", session.Owner, new AudioParameterSet());
        Assert.Equal(0.1f, session.Render()[^1], 5);
        session.Service.StopAll(session.Owner);
        session.Service.Post("vary", session.Owner, new AudioParameterSet());
        Assert.Equal(0.3f, session.Render()[^1], 5);
    }

    /// <summary>Queue saturation is reported and queued voices are released on reset.</summary>
    [Fact]
    public void QueueBackpressureAndResetDoNotLeakVoices()
    {
        using var session = new Session(queue: 2);
        session.Prepare();
        Assert.NotEqual(0, session.Service.Post("tone", session.Owner, new AudioParameterSet()).Value);
        Assert.NotEqual(0, session.Service.Post("tone", session.Owner, new AudioParameterSet()).Value);
        Assert.Equal(0, session.Service.Post("tone", session.Owner, new AudioParameterSet()).Value);
        Assert.False(session.Service.StopAll(session.Owner));
        session.Service.Reset();
        Assert.Equal(AudioDeviceState.Stopped, session.Device.State);
        Assert.Equal(0, session.Mixer.ActiveVoiceCount);
        Assert.All(session.Render(), static sample => Assert.Equal(0, sample));
        Assert.Throws<InvalidOperationException>(() => session.Service.Post("tone", session.Owner, new AudioParameterSet()));
        for (var i = 0; i < 20; i++)
        {
            session.Prepare();
            session.Service.Post("tone", session.Owner, new AudioParameterSet());
            Assert.Equal(0.5f, session.Render()[^1], 5);
            session.Service.Reset();
            Assert.Equal(0, session.Mixer.ActiveVoiceCount);
        }
    }

    /// <summary>Posting and mixing warmed-up events allocate no managed memory.</summary>
    [Fact]
    public void PlaybackAndCallbackAllocateNoManagedMemory()
    {
        using var session = new Session();
        session.Prepare();
        var pcm = new float[256];
        void Work()
        {
            var voice = session.Service.Post("tone", session.Owner, new AudioParameterSet());
            session.Service.SetParameters(voice, new AudioParameterSet());
            session.Service.SetEmitterParameters(session.Owner, new AudioParameterSet());
            session.Device.Render(pcm);
            session.Service.StopAll(session.Owner);
            session.Device.Render(pcm);
        }
        for (var i = 0; i < 20; i++)
        {
            Work();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            Work();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Invalid parameters fail on the producer, before entering the callback.</summary>
    [Fact]
    public void InvalidInputIsRejectedBeforePlayback()
    {
        using var session = new Session();
        session.Prepare();
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Service.Post("tone", session.Owner,
            new AudioParameterSet { Pitch = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Service.SetParameters(default,
            new AudioParameterSet { Volume = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Service.StopAll(session.Owner, -1));
        Assert.Throws<ArgumentException>(() => session.Service.SetListener(default));
        Assert.Throws<ArgumentException>(() => session.Service.Prepare(new AudioEvent { EventName = "empty" }, []));
        Assert.Throws<ArgumentException>(() => AudioClip.FromPcm([float.NaN], new(1, 8000)));
        Assert.Throws<ArgumentException>(() => AudioClip.FromPcm([1], new(2, 8000)));
        Assert.Throws<ArgumentException>(() => session.Device.Render(new float[3]));
        Assert.Throws<InvalidOperationException>(() => session.Service.Prepare(new AudioEvent()));
    }

    /// <summary>Callback failures are contained, silence output and can be reset safely.</summary>
    [Fact]
    public void CallbackFailureProducesSilenceAndLogsOnce()
    {
        var settings = new AudioSettings();
        using var device = new SilentAudioDevice();
        var mixer = Substitute.For<IMixerGraph>();
        mixer.When(static m => m.CreateVoice(Arg.Any<VoiceDesc>())).Do(static _ => throw new InvalidOperationException());
        var logger = Substitute.For<ILogger>();
        using var audio = new AudioService(device, mixer, settings, logger: logger);
        audio.Prepare(new AudioEvent { EventName = "fault" }, [AudioClip.FromPcm([1], new(1, 8000))]);
        audio.Post("fault", Guid.Empty, new AudioParameterSet());
        var pcm = new float[32];
        device.Render(pcm);
        Assert.All(pcm, static sample => Assert.Equal(0, sample));
        audio.StopAll(Guid.Empty);
        audio.StopAll(Guid.Empty);
        Assert.Single(logger.ReceivedCalls());
        device.Render(pcm);
        audio.Reset();
        Assert.Equal(AudioDeviceState.Stopped, device.State);
    }

    /// <summary>Headless registration selects a device-free session and permits replacing each seam.</summary>
    [Fact]
    public void ServiceRegistrationAndDisposalAreSessionScoped()
    {
        using var first = new ServiceCollection().AddAudio(new AudioSettings()).BuildServiceProvider();
        using var second = new ServiceCollection().AddAudio(new AudioSettings()).BuildServiceProvider();
        Assert.IsType<SilentAudioDevice>(first.GetRequiredService<IAudioDevice>());
        Assert.NotSame(first.GetRequiredService<IAudioService>(), second.GetRequiredService<IAudioService>());
        first.GetRequiredService<IAudioService>().Dispose();
        first.GetRequiredService<IAudioService>().Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.GetRequiredService<IAudioService>().Reset());
        var replacement = Substitute.For<ISpatializer>();
        using var custom = new ServiceCollection().AddAudio(new AudioSettings())
            .AddSingleton(replacement).BuildServiceProvider();
        Assert.Same(replacement, custom.GetRequiredService<ISpatializer>());
    }

    /// <summary>Scene components prepare events and publish global transforms only through the audio service.</summary>
    [Fact]
    public void ComponentsUseSessionServiceAndGlobalTransforms()
    {
        var audio = Substitute.For<IAudioService>();
        var audioEvent = new AudioEvent { EventName = "component" };
        audio.Prepare(Arg.Any<DataAssetReference<AudioEvent>>()).Returns("component");
        using var services = new ServiceCollection().AddSingleton(audio).BuildServiceProvider();
        var parent = new Node { Position = new(2, 0, 0) };
        var node = new Node { Position = new(3, 0, 0) };
        parent.Children.Add(node);
        var source = new AudioSource { Event = new(audioEvent.Id), PlayOnStart = true, Volume = 0.5f };
        var listener = new AudioListener();
        node.Components.Add(source);
        node.Components.Add(listener);
        parent.Awake(null, services);
        source.EnsureStarted();
        listener.OnLateUpdate(0);
        audio.Received(1).Prepare(Arg.Is<DataAssetReference<AudioEvent>>(r => r.AssetId == audioEvent.Id));
        audio.Received(1).Post("component", source.Id,
            Arg.Is<AudioParameterSet>(static p => p.Position.X == 5));
        audio.Received().SetEmitterParameters(source.Id,
            Arg.Is<AudioParameterSet>(static p => p.Position.X == 5 && p.Volume == 0.5f));
        audio.Received(1).SetListener(Arg.Is<AudioListenerState>(static p => p.Position.X == 5));
        source.OnDisable();
        audio.Received(1).StopAll(source.Id, 0);
    }

    /// <summary>Failed device startup can be retried without leaving an event registered or PCM pinned.</summary>
    [Fact]
    public void FailedPreparationCanBeRetried()
    {
        var settings = new AudioSettings();
        var device = Substitute.For<IAudioDevice>();
        var fail = true;
        device.When(static d => d.Start(Arg.Any<AudioDeviceConfig>()))
            .Do(_ =>
            {
                if (fail)
                {
                    throw new InvalidOperationException("Device unavailable.");
                }
            });
        using var mixer = new AudioMixerGraph(settings, new SimpleSpatializer());
        using var audio = new AudioService(device, mixer, settings);
        var audioEvent = new AudioEvent { EventName = "retry" };
        var clip = AudioClip.FromPcm([0.5f], new(1, 8000));
        Assert.Throws<InvalidOperationException>(() => audio.Prepare(audioEvent, [clip]));
        Assert.Throws<InvalidOperationException>(() => audio.Post("retry", Guid.Empty, new AudioParameterSet()));
        fail = false;
        audio.Prepare(audioEvent, [clip]);
        audio.Prepare(audioEvent, [clip]);
        Assert.Throws<InvalidOperationException>(() => audio.Prepare(new AudioEvent { EventName = "retry" }, [clip]));
    }

    /// <summary>Extreme finite positions attenuate safely instead of introducing NaN PCM.</summary>
    [Fact]
    public void ExtremeSpatialCoordinatesRemainFinite()
    {
        var spatializer = new SimpleSpatializer();
        spatializer.ProcessListener(new(new(-float.MaxValue, 0, 0), Quaternion.Identity));
        var gains = spatializer.Compute(new(new(float.MaxValue, 0, 0), true, 1, 10));
        Assert.Equal(0, gains.Left);
        Assert.Equal(0, gains.Right);
        Assert.True(float.IsFinite(gains.Distance));
    }

    /// <summary>Asset references resolve through the service and prepare clips before posting.</summary>
    [Fact]
    public void ServiceResolvesEventAndClipAssets()
    {
        var clip = AudioClip.FromPcm([0.25f], new(1, 8000));
        var audioEvent = new AudioEvent
        {
            EventName = "asset",
            Loop = true,
            Clips = [new AssetReference<AudioClip>(clip.Id)],
        };
        var loader = Substitute.For<IAssetLoader>();
        loader.LoadContentAsync<AudioEvent>(audioEvent.Id).Returns(Task.FromResult<AudioEvent?>(audioEvent));
        loader.LoadAsync<AudioClip>(clip.Id).Returns(Task.FromResult<AudioClip?>(clip));
        var settings = new AudioSettings();
        using var device = new SilentAudioDevice();
        using var mixer = new AudioMixerGraph(settings, new SimpleSpatializer());
        using var audio = new AudioService(device, mixer, settings, loader);
        Assert.Equal("asset", audio.Prepare(new DataAssetReference<AudioEvent>(audioEvent.Id)));
        audio.Prepare(audioEvent);
        loader.Received(1).LoadAsync<AudioClip>(clip.Id);
        audio.Post("asset", Guid.Empty, new AudioParameterSet());
        var pcm = new float[1024];
        device.Render(pcm);
        Assert.Equal(0.25f, pcm[^1], 5);
        Assert.Throws<InvalidDataException>(() => audio.Prepare(new DataAssetReference<AudioEvent>(Guid.NewGuid())));
    }

    /// <summary>Replacing the mixer retains the session's voice budget and priority policy.</summary>
    [Fact]
    public unsafe void VoicePolicyAppliesAboveReplacementMixer()
    {
        var mixer = Substitute.For<IMixerGraph>();
        mixer.CreateVoice(Arg.Any<VoiceDesc>()).Returns(static call => call.Arg<VoiceDesc>().Handle);
        mixer.IsPlaying(Arg.Any<VoiceHandle>()).Returns(true);
        var manager = new VirtualVoiceManager(1, mixer);
        var low = new VoiceDesc(new(1), Guid.Empty, null, 1, new(1, 8000),
            new AudioParameterSet(), true, false, 1, 10, 1);
        var high = new VoiceDesc(new(2), Guid.Empty, null, 1, new(1, 8000),
            new AudioParameterSet(), true, false, 1, 10, 2);
        Assert.Equal(low.Handle, manager.Play(low));
        Assert.Equal(high.Handle, manager.Play(high));
        mixer.Received(1).StopVoice(low.Handle, 0.005f);
        Assert.Equal(default, manager.Play(low));
        mixer.Received(2).CreateVoice(Arg.Any<VoiceDesc>());
        manager.Reset();
        mixer.Received(1).Reset();
    }

    /// <summary>The ring publishes complete commands in FIFO order while wrapping under concurrent use.</summary>
    [Fact]
    public void CommandRingPreservesOrderingAcrossThreads()
    {
        var queue = new AudioCommandQueue(8);
        Exception? failure = null;
        var deadline = Stopwatch.StartNew();
        var consumer = new Thread(() =>
        {
            try
            {
                for (var i = 1; i <= 10000; i++)
                {
                    var spin = new SpinWait();
                    AudioCommand command;
                    while (!queue.TryRead(out command))
                    {
                        if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                        {
                            throw new TimeoutException("Audio command producer did not complete.");
                        }
                        spin.SpinOnce();
                    }
                    Assert.Equal(i, command.Handle.Value);
                }
            }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };
        consumer.Start();
        for (var i = 1; i <= 10000; i++)
        {
            var spin = new SpinWait();
            while (!queue.TryWrite(new AudioCommand { Handle = new(i) }))
            {
                Assert.Null(Volatile.Read(ref failure));
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10));
                spin.SpinOnce();
            }
        }
        Assert.True(consumer.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }
}
