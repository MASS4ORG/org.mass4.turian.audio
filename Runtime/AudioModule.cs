using Microsoft.Extensions.DependencyInjection;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Composes audio independently of the window or rendering backend.</summary>
public sealed class AudioModule : IEngineServiceModule
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
        if (!services.GetApplicationContext().Interactive)
        {
            services.AddSingleton<IAudioService, SilentAudioService>();
            services.AddSingleton<IAudioMixerService, SilentAudioMixerService>();
        }
        else
        {
            AudioServices.AddAudio(services, static sp => sp.GetRequiredService<IAppSettings>().Get<AudioSettings>());
        }
        services.AddSingleton<IApplicationParticipant, AudioApplicationParticipant>();
    }
}

sealed class AudioApplicationParticipant(IAudioService audio) : IApplicationParticipant, IApplicationPauseParticipant
{
    public void Start() { }
    public void Stop() => audio.Reset();
    public void SetPaused(bool paused) => audio.SetApplicationPaused(paused);
}
