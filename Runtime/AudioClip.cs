using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using MASS4.Attributes;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Audio metadata with decoded PCM prepared outside playback.</summary>
[TypeId("61f1fcd3-747a-4402-a9ba-31b12d4cb8d9")]
public sealed class AudioClip : Asset
{
    float[] samples = [];

    /// <summary>Decoded PCM format.</summary>
    [JsonIgnore]
    public AudioFormat Format { get; private set; }
    /// <summary>Number of decoded frames.</summary>
    [JsonIgnore]
    public int Frames => Format.Channels > 0 ? samples.Length / Format.Channels : 0;

    internal float[] Samples => samples;

    /// <summary>Creates an independent PCM clip, copying samples before playback.</summary>
    public static AudioClip FromPcm(ReadOnlySpan<float> samples, AudioFormat format)
    {
        if (format.Channels is < 1 or > 2 || format.SampleRate <= 0 || samples.Length % format.Channels != 0)
        {
            throw new ArgumentException("Audio clips require complete mono or stereo frames.", nameof(format));
        }
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                throw new ArgumentException("PCM must contain finite samples.", nameof(samples));
            }
        }
        return new AudioClip { samples = samples.ToArray(), Format = format };
    }

    /// <summary>Decodes a provider completely off the audio thread; ownership stays with the caller.</summary>
    public static AudioClip Decode(IAudioStreamSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var format = source.Format;
        if (format.Channels is < 1 or > 2 || format.SampleRate <= 0)
        {
            throw new NotSupportedException("Audio clips support mono or stereo PCM.");
        }
        var buffer = new float[4096 * format.Channels];
        var pcm = new List<float>();
        int frames;
        while ((frames = source.Read(buffer)) != 0)
        {
            if (frames < 0 || frames > buffer.Length / format.Channels)
            {
                throw new InvalidDataException("The PCM provider returned an invalid frame count.");
            }
            pcm.AddRange(buffer.AsSpan(0, frames * format.Channels));
        }
        return FromPcm(CollectionsMarshal.AsSpan(pcm), format);
    }

    internal void LoadPcm(IAudioStreamSource source)
    {
        var decoded = Decode(source);
        samples = decoded.samples;
        Format = decoded.Format;
    }
}
