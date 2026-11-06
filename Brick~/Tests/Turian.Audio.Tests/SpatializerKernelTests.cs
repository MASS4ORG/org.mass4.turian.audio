namespace Turian.Audio.Tests;

/// <summary>Checks static spatial dispatch without invoking managed spatializer methods on the mixing path.</summary>
public sealed unsafe class SpatializerKernelTests
{
    sealed class KernelOnlySpatializer : ISpatializer
    {
        public SpatializerKernel Kernel => SpatializerKernels.Get(SpatializerKind.Simple);
        public void ProcessListener(in AudioListenerState listener) => throw new InvalidOperationException();
        public SpatialParams Compute(in VoiceState voice) => throw new InvalidOperationException();
    }

    sealed class ExternalSpatializer : ISpatializer
    {
        public SpatializerKernel Kernel => new(&ComputeStatic, 3);
        public void ProcessListener(in AudioListenerState listener) => throw new InvalidOperationException();
        public SpatialParams Compute(in VoiceState voice) => throw new InvalidOperationException();
        static SpatialParams ComputeStatic(nint context, in AudioListenerState listener, in VoiceState voice)
            => new(context * 0.1f, listener.Position.X * 0.1f, 0);
    }

    /// <summary>The built-in table supplies panning and attenuation even when interface methods cannot run.</summary>
    [Fact]
    public void MixerUsesTheBuiltInKernelWithoutInterfaceDispatch()
    {
        var settings = new AudioSettings { SampleRate = 8000 };
        using var device = new SilentAudioDevice();
        using var mixer = new AudioMixerGraph(settings, new KernelOnlySpatializer());
        using var service = new AudioService(device, mixer, settings);
        service.Prepare(new AudioEvent { EventName = "pan", Loop = true, Spatial = true, MaxDistance = 11 },
            [AudioClip.FromPcm([0.5f], new(1, 8000))]);
        service.Post("pan", Guid.Empty, new AudioParameterSet { Position = new(6, 0, 0) });
        var output = new float[256];
        device.Render(output);
        Assert.Equal(0.25f, output[^2], 5);
        Assert.Equal(0, output[^1]);
        service.SetListener(new(new(5, 0, 0), Quaternion.Identity));
        device.Render(output);
        Assert.Equal(0.5f, output[^2], 5);
        service.Reset();
        Assert.Equal(0, mixer.ActiveVoiceCount);
    }

    /// <summary>An integration supplies its own static kernel and context without extending the built-in enum.</summary>
    [Fact]
    public void MixerPassesContextAndListenerToExternalKernels()
    {
        var settings = new AudioSettings { SampleRate = 8000 };
        using var device = new SilentAudioDevice();
        using var mixer = new AudioMixerGraph(settings, new ExternalSpatializer());
        using var service = new AudioService(device, mixer, settings);
        service.Prepare(new AudioEvent { EventName = "external", Loop = true },
            [AudioClip.FromPcm([0.5f], new(1, 8000))]);
        service.SetListener(new(new(2, 0, 0), Quaternion.Identity));
        service.Post("external", Guid.Empty, new AudioParameterSet());
        var output = new float[256];
        device.Render(output);
        Assert.Equal(0.15f, output[^2], 5);
        Assert.Equal(0.1f, output[^1], 5);
    }

    /// <summary>Unknown built-in kernels fail during setup rather than inside a callback.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void InvalidKernelKindIsRejected(int kind)
        => Assert.Throws<ArgumentOutOfRangeException>(() => SpatializerKernels.Get((SpatializerKind)kind));

    /// <summary>A missing callback fails before voice memory or a native device is allocated.</summary>
    [Fact]
    public void MissingKernelIsRejected()
        => Assert.Throws<ArgumentException>(() => new AudioMixerGraph(new AudioSettings(), Substitute.For<ISpatializer>()));
}