using System;
using System.Collections.Generic;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Immutable extension lookup used by asset loading before PCM reaches playback.</summary>
public sealed class AudioCodecRegistry
{
    readonly Dictionary<string, IAudioCodec> codecs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a registry from backend codecs, or an empty registry when none are supplied.</summary>
    public AudioCodecRegistry(IEnumerable<IAudioCodec>? codecs = null)
    {
        foreach (var codec in codecs ?? [])
        {
            ArgumentNullException.ThrowIfNull(codec);
            foreach (var extension in codec.Extensions)
            {
                if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.')
                {
                    throw new ArgumentException("Codec extensions must begin with a dot.", nameof(codecs));
                }
                if (!this.codecs.TryAdd(extension, codec))
                {
                    throw new ArgumentException("Multiple audio codecs claim the same extension.", nameof(codecs));
                }
            }
        }
    }

    /// <summary>Opens resolved asset bytes with the codec registered for their source extension.</summary>
    public IAudioStreamSource OpenStream(IAssetFileProvider asset, string extension)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return codecs.TryGetValue(extension, out var codec) ? codec.OpenStream(asset)
            : throw new NotSupportedException($"No audio codec is registered for '{extension}'.");
    }
}
