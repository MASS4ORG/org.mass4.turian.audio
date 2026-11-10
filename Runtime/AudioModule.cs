using Microsoft.Extensions.DependencyInjection;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Composes audio independently of the window or rendering backend.</summary>
public sealed class AudioModule : IEngineServiceModule
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
        if (!services.GetEngineSession().Interactive)
        {
            services.AddSingleton<IAudioService, SilentAudioService>();
            services.AddSingleton<IAudioMixerService, SilentAudioMixerService>();
        }
        else
        {
            AudioServices.AddAudio(services, static sp => sp.GetRequiredService<IAppSettings>().Get<AudioSettings>());
        }
        services.AddSingleton<IEngineSessionParticipant, AudioSessionParticipant>();
    }
}

sealed class AudioSessionParticipant(IAudioService audio) : IEngineSessionParticipant, IEngineSessionPauseParticipant
{
    public void Start() { }
    public void Stop() => audio.Reset();
    public void SetPaused(bool paused) => audio.SetApplicationPaused(paused);
}
