namespace Turian.Audio.Tests;

/// <summary>Checks decoder publication at device callback boundaries while the bus graph renders smaller chunks.</summary>
public sealed unsafe class MixerStreamTests
{
    struct Publication
    {
        public AudioStreamBuffer* Stream;
        public int Calls;
    }

    sealed class PublishingSpatializer(nint context) : ISpatializer
    {
        public SpatializerKernel Kernel => new(&ComputeStatic, context);
        public void ProcessListener(in AudioListenerState listener) => throw new InvalidOperationException();
        public SpatialParams Compute(in VoiceState voice) => throw new InvalidOperationException();
        static SpatialParams ComputeStatic(nint context, in AudioListenerState listener, in VoiceState voice)
        {
            var publication = (Publication*)context;
            if (++publication->Calls == 2) { publication->Stream->state = AudioStreamBuffer.Ready; }
            return new(1, 1, 0);
        }
    }

    /// <summary>A stream published between bus chunks starts at the next device callback, preserving frame alignment.</summary>
    [Fact]
    public void PendingStreamsDoNotStartHalfwayThroughACallback()
    {
        var samples = stackalloc float[256];
        new Span<float>(samples, 256).Fill(0.25f);
        var stream = new AudioStreamBuffer
        {
            samples = samples, capacity = 256, channels = 1, sampleRate = 8000,
            written = 256, ended = 1, state = AudioStreamBuffer.Opening,
        };
        var publication = new Publication { Stream = &stream };
        using var graph = new AudioMixerGraph(new AudioSettings { SampleRate = 8000, BufferFrames = 64 },
            new PublishingSpatializer((nint)(&publication)));
        var voice = new VoiceDesc(new(1), Guid.Empty, null, 0, new(1, 8000), new AudioParameterSet(),
            false, false, 1, 10, 128) { Stream = &stream };
        Assert.Equal(1, graph.CreateVoice(voice).Value);
        var output = new float[256];
        graph.Mix(output);
        Assert.All(output, static value => Assert.Equal(0, value));
        Assert.Equal(0, stream.read);
        graph.Mix(output);
        Assert.Equal(0.25f, output[^1]);
        Assert.Equal(128, stream.read);
        Assert.Equal(0, stream.underruns);
    }
}
