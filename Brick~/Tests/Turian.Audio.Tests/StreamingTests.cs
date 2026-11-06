namespace Turian.Audio.Tests;

/// <summary>Exercises streamed voices, pausing and master gain through the production service and mixer.</summary>
public sealed class StreamingTests
{
    /// <summary>Deterministic PCM whose sample value encodes the frame index, with disposal tracking.</summary>
    sealed class RampSource(int frames, AudioFormat format, Action? disposed = null) : IAudioStreamSource
    {
        long position;
        public AudioFormat Format { get; } = format;
        public int Reads { get; private set; }

        public int Read(Span<float> buffer)
        {
            Reads++;
            var count = (int)Math.Min(buffer.Length / Format.Channels, frames - position);
            for (var i = 0; i < count; i++)
            {
                for (var c = 0; c < Format.Channels; c++)
                {
                    buffer[i * Format.Channels + c] = Value(position + i);
                }
            }
            position += count;
            return count;
        }

        public void Seek(long frame) => position = frame;
        public void Dispose() => disposed?.Invoke();
        public static float Value(long frame) => 0.25f + (frame % 1000) / 4000f;
    }

    sealed class Session : IDisposable
    {
        public readonly SilentAudioDevice Device = new();
        public readonly AudioMixerGraph Mixer;
        public readonly AudioService Service;
        public readonly ILogger Logger = Substitute.For<ILogger>();
        public readonly Guid Owner = Guid.NewGuid();
        public int Opened;
        public int Disposed;

        public Session(int voices = 4, int streams = 2, int bufferFrames = 4096)
        {
            var settings = new AudioSettings
            {
                SampleRate = 8000, MaxVirtualVoices = voices, MaxStreams = streams, StreamBufferFrames = bufferFrames,
                BufferFrames = 64,
            };
            Mixer = new(settings, new SimpleSpatializer());
            Service = new(Device, Mixer, settings, logger: Logger);
        }

        public void PrepareStream(string name = "music", int frames = 100000, bool loop = false, int rate = 8000,
            int channels = 1, int priority = 128)
        {
            Service.PrepareStream(new AudioEvent { EventName = name, Loop = loop, Priority = priority }, [() =>
            {
                Interlocked.Increment(ref Opened);
                return new RampSource(frames, new(channels, rate), () => Interlocked.Increment(ref Disposed));
            }]);
        }

        public float[] Render(int frames = 128)
        {
            var output = new float[frames * 2];
            Device.Render(output);
            return output;
        }

        // Renders until the decoder thread has published the stream and the voice produces sound.
        public float[] RenderUntilAudible()
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                var output = Render();
                if (output[^1] != 0)
                {
                    return output;
                }
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "The stream never became audible.");
                Thread.Sleep(1);
            }
        }

        public void Dispose() => Service.Dispose();
    }

    static void WaitFor(Func<bool> condition, string message)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), message);
            Thread.Sleep(1);
        }
    }

    /// <summary>Streamed PCM matches its source frame by frame, ends at its length and returns its ring.</summary>
    [Fact]
    public void StreamedVoicePlaysExactPcmAndReleasesItsRingAtTheEnd()
    {
        using var session = new Session();
        session.PrepareStream(frames: 2000);
        Assert.Equal(1, session.Opened);
        Assert.Equal(1, session.Disposed);
        session.Service.Post("music", session.Owner, new AudioParameterSet());
        var first = session.RenderUntilAudible();
        Assert.Equal(1, session.Service.Statistics.ActiveStreams);
        var later = session.Render();
        var consumed = first.Length / 2;
        Assert.Equal(RampSource.Value(consumed + 127), later[^1], 5);
        Assert.Equal(later[^1], later[^2], 5);
        for (var i = 0; i < 20; i++) { session.Render(); }
        Assert.Equal(0, session.Mixer.ActiveVoiceCount);
        WaitFor(() => session.Service.Statistics.ActiveStreams == 0, "The finished stream was not recycled.");
        Assert.Equal(2, session.Disposed);
        Assert.Equal(0, session.Service.Statistics.StreamUnderruns);
    }

    /// <summary>Looping streams rewind on the decoder thread, keeping a continuous ramp without dropouts.</summary>
    [Fact]
    public void LoopingStreamRewindsWithoutDropouts()
    {
        using var session = new Session();
        session.PrepareStream(frames: 300, loop: true, channels: 2);
        session.Service.Post("music", session.Owner, new AudioParameterSet());
        session.RenderUntilAudible();
        for (var block = 0; block < 40; block++)
        {
            var output = session.Render();
            for (var i = 2; i < output.Length; i += 2)
            {
                var step = output[i] - output[i - 2];
                Assert.True(Math.Abs(step) < 0.001f || Math.Abs(step + 299 / 4000f) < 0.001f,
                    $"Unexpected discontinuity {step} in block {block}.");
            }
            Thread.Sleep(1);
        }
        Assert.Equal(0, session.Service.Statistics.StreamUnderruns);
        Assert.Equal(1, session.Mixer.ActiveVoiceCount);
    }

    /// <summary>Pausing fades to silence while holding the stream position, and resuming continues from it.</summary>
    [Fact]
    public void PauseHoldsStreamPositionAndResumes()
    {
        using var session = new Session();
        session.PrepareStream();
        session.Service.Post("music", session.Owner, new AudioParameterSet());
        session.RenderUntilAudible();
        var before = session.Render()[^1];
        session.Service.Pause(session.Owner);
        var fade = session.Render();
        Assert.True(fade[0] > 0);
        Assert.Equal(0, fade[^1]);
        for (var i = 0; i < 10; i++) { Assert.All(session.Render(), static sample => Assert.Equal(0, sample)); }
        Assert.Equal(1, session.Mixer.ActiveVoiceCount);
        session.Service.Pause(session.Owner, paused: false);
        var resumed = session.Render();
        Assert.InRange(resumed[^1] - before, 0, 200 / 4000f);
        Assert.Equal(0, session.Service.Statistics.StreamUnderruns);
    }

    /// <summary>Master volume ramps without steps, mutes without stopping voices and resets with the session.</summary>
    [Fact]
    public void MasterVolumeRampsMutesAndResets()
    {
        using var session = new Session();
        session.Service.Prepare(new AudioEvent { EventName = "tone", Loop = true },
            [AudioClip.FromPcm(Enumerable.Repeat(0.5f, 800).ToArray(), new(1, 8000))]);
        session.Service.Post("tone", session.Owner, new AudioParameterSet());
        Assert.Equal(0.5f, session.Render()[^1], 5);
        session.Service.SetMasterVolume(0);
        var ramp = session.Render(256);
        for (var i = 2; i < ramp.Length; i += 2) { Assert.True(Math.Abs(ramp[i] - ramp[i - 2]) < 0.01f); }
        Assert.Equal(0, ramp[^1]);
        Assert.Equal(1, session.Mixer.ActiveVoiceCount);
        session.Service.SetMasterVolume(0.5f);
        session.Render(256);
        Assert.Equal(0.25f, session.Render()[^1], 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Service.SetMasterVolume(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Service.SetMasterVolume(-1));
        session.Service.Reset();
        session.Service.Prepare(new AudioEvent { EventName = "tone", Loop = true },
            [AudioClip.FromPcm(Enumerable.Repeat(0.5f, 800).ToArray(), new(1, 8000))]);
        session.Service.Post("tone", session.Owner, new AudioParameterSet());
        Assert.Equal(0.5f, session.Render()[^1], 5);
    }

    /// <summary>A consumer that outruns its decoder reports dropouts instead of reading stale frames or failing.</summary>
    [Fact]
    public void ConsumerOutrunningTheRingReportsUnderruns()
    {
        using var session = new Session();
        session.PrepareStream(rate: 48000, frames: int.MaxValue);
        session.Service.Post("music", session.Owner, new AudioParameterSet { Pitch = 16 });
        WaitFor(() =>
        {
            session.Render();
            return session.Service.Statistics.StreamUnderruns > 0;
        }, "No underrun was reported.");
        Assert.Equal(1, session.Mixer.ActiveVoiceCount);
    }

    /// <summary>Rejected, dropped and saturated stream posts never leak rings.</summary>
    [Fact]
    public void RejectedStreamsReturnTheirRings()
    {
        using var session = new Session(voices: 1, streams: 1);
        session.Service.Prepare(new AudioEvent { EventName = "high", Loop = true, Priority = 200 },
            [AudioClip.FromPcm([0.5f], new(1, 8000))]);
        session.PrepareStream(priority: 1);
        session.Service.Post("high", session.Owner, new AudioParameterSet());
        Assert.NotEqual(0, session.Service.Post("music", session.Owner, new AudioParameterSet()).Value);
        Assert.Equal(0, session.Service.Post("music", session.Owner, new AudioParameterSet()).Value);
        session.Render();
        Assert.Equal(1, session.Mixer.ActiveVoiceCount);
        WaitFor(() => session.Service.Statistics.ActiveStreams == 0, "The rejected stream was not recycled.");
        session.Service.StopAll(session.Owner);
        session.Render();
        Assert.NotEqual(0, session.Service.Post("music", session.Owner, new AudioParameterSet()).Value);
        session.RenderUntilAudible();
    }

    /// <summary>Decoder failures end the voice silently and report once; probe failures fail preparation.</summary>
    [Fact]
    public void DecoderFailuresAreContained()
    {
        using var session = new Session();
        var opens = 0;
        session.Service.PrepareStream(new AudioEvent { EventName = "broken" }, [() => ++opens == 1
            ? new RampSource(10, new(1, 8000))
            : throw new IOException("Payload vanished.")]);
        session.Service.Post("broken", session.Owner, new AudioParameterSet());
        WaitFor(() =>
        {
            session.Render();
            return session.Service.Statistics.ActiveStreams == 0 && session.Mixer.ActiveVoiceCount == 0;
        }, "The failed stream was not recycled.");
        Assert.Contains(session.Logger.ReceivedCalls(), static call => call.GetArguments()[0] is LogLevel.Error);
        Assert.Throws<NotSupportedException>(() => session.Service.PrepareStream(new AudioEvent { EventName = "wide" },
            [() => new RampSource(10, new(6, 8000))]));
        Assert.Throws<ArgumentException>(() => session.Service.PrepareStream(new AudioEvent { EventName = "none" }, []));
    }

    /// <summary>Repeated sessions join the decoder thread and dispose every source they opened.</summary>
    [Fact]
    public void ResetReleasesStreamsAcrossSessions()
    {
        using var session = new Session();
        for (var i = 0; i < 3; i++)
        {
            session.PrepareStream(loop: true);
            session.Service.Post("music", session.Owner, new AudioParameterSet());
            session.RenderUntilAudible();
            session.Service.Reset();
            Assert.Equal(0, session.Service.Statistics.ActiveStreams);
            Assert.Equal(session.Opened, session.Disposed);
            Assert.All(session.Render(), static sample => Assert.Equal(0, sample));
        }
    }

    /// <summary>Warmed-up streamed callbacks, pauses and master changes allocate no managed memory.</summary>
    [Fact]
    public void StreamedCallbackAllocatesNoManagedMemory()
    {
        using var session = new Session();
        session.PrepareStream(loop: true);
        session.Service.Post("music", session.Owner, new AudioParameterSet());
        session.RenderUntilAudible();
        var pcm = new float[256];
        void Work()
        {
            session.Service.Pause(session.Owner);
            session.Device.Render(pcm);
            session.Service.Pause(session.Owner, paused: false);
            session.Service.SetMasterVolume(0.5f);
            session.Device.Render(pcm);
        }
        for (var i = 0; i < 20; i++) { Work(); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) { Work(); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>The server service accepts the new controls without decoding or allocating a mixer.</summary>
    [Fact]
    public void SilentServiceAcceptsPlaybackControls()
    {
        var silent = new SilentAudioService();
        Assert.True(silent.Pause(Guid.Empty));
        Assert.True(silent.SetMasterVolume(0));
        Assert.Equal(default, silent.Statistics);
    }
}
