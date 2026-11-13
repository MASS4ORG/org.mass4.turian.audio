namespace Turian.Audio.Tests;

/// <summary>Verifies device-free operation without the native backend.</summary>
public sealed class HeadlessModuleTests
{
    /// <summary>A server session registers no decoder, device or mixer and does not load event assets.</summary>
    [Fact]
    public void HeadlessAudioDoesNotCreatePlaybackResources()
    {
        var loader = Substitute.For<IAssetLoader>();
        var services = new ServiceCollection().AddSingleton(loader)
            .AddSingleton(new ApplicationContext(ApplicationKind.Headless));
        new AudioModule().ConfigureServices(services);
        using var provider = services.BuildServiceProvider();
        var audio = Assert.IsType<SilentAudioService>(provider.GetRequiredService<IAudioService>());
        using var lifetime = new ApplicationLifetime(provider.GetServices<IApplicationParticipant>());
        lifetime.Start();
        audio.Prepare(new DataAssetReference<AudioEvent>(Guid.NewGuid()));
        audio.Post("ambient", Guid.Empty, new AudioParameterSet());
        audio.SetEmitterParameters(Guid.Empty, new AudioParameterSet());
        audio.StopAll(Guid.Empty);
        var mixer = Assert.IsType<SilentAudioMixerService>(provider.GetRequiredService<IAudioMixerService>());
        Assert.True(mixer.SetBusVolume("Music", 0.5f));
        Assert.True(mixer.TransitionTo("Underwater", 1));
        Assert.Equal(default, mixer.GetMeter("Master"));
        Assert.Empty(loader.ReceivedCalls());
        Assert.Null(provider.GetService<IAudioDevice>());
        Assert.Null(provider.GetService<IMixerGraph>());
        Assert.Empty(provider.GetServices<IAudioCodec>());
    }

    /// <summary>The front-end assembly has no managed dependency on its optional native backend.</summary>
    [Fact]
    public void FrontEndDoesNotReferenceMiniaudioBackend()
    {
        Assert.DoesNotContain(typeof(AudioModule).Assembly.GetReferencedAssemblies(),
            static reference => reference.Name?.Contains("Miniaudio", StringComparison.Ordinal) == true);
    }
}
