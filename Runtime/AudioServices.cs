using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Registers one audio session, with device-free output by default.</summary>
public static class AudioServices
{
    /// <summary>Adds replaceable device, mixer and spatializer seams before application modules run.</summary>
    public static IServiceCollection AddAudio(this IServiceCollection services, AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return AddAudio(services, _ => settings);
    }

    internal static IServiceCollection AddAudio(IServiceCollection services, Func<IServiceProvider, AudioSettings> settings)
    {
        services.AddSingleton<ISpatializer, SimpleSpatializer>();
        services.AddSingleton<IAudioDevice, SilentAudioDevice>();
        services.AddSingleton(static sp => new AudioCodecRegistry(sp.GetServices<IAudioCodec>()));
        services.AddSingleton<IMixerGraph>(sp => new AudioMixerGraph(settings(sp), sp.GetRequiredService<ISpatializer>()));
        services.AddSingleton<IAudioService>(sp => new AudioService(sp.GetRequiredService<IAudioDevice>(),
            sp.GetRequiredService<IMixerGraph>(), settings(sp), sp.GetService<IAssetLoader>(),
            sp.GetService<ILogger>(), sp.GetRequiredService<AudioCodecRegistry>(),
            effectFactories: sp.GetServices<IAudioEffectFactory>()));
        services.AddSingleton<IAudioMixerService>(sp => (IAudioMixerService)sp.GetRequiredService<IAudioService>());
        return services;
    }
}
