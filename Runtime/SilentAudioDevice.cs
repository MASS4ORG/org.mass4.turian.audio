using System;
using System.Threading;

namespace Turian.Audio;

/// <summary>Device-free output driven explicitly by offline tests or tools.</summary>
public sealed unsafe class SilentAudioDevice : IAudioDevice
{
    AudioDeviceConfig config;
    int started;
    int rendering;

    /// <inheritdoc/>
    public AudioDeviceState State => Volatile.Read(ref started) == 0
        ? AudioDeviceState.Stopped : AudioDeviceState.Started;

    /// <inheritdoc/>
    public void Start(in AudioDeviceConfig config)
    {
        Stop();
        if (config.Callback == null || config.Format.Channels != 2)
        {
            throw new ArgumentException("The silent device requires stereo PCM and a callback.", nameof(config));
        }
        this.config = config;
        Volatile.Write(ref started, 1);
    }

    /// <summary>Pulls a deterministic stereo block through the production command and mixer path.</summary>
    public void Render(Span<float> output)
    {
        if (output.Length % 2 != 0)
        {
            throw new ArgumentException("Output must contain complete stereo frames.", nameof(output));
        }
        output.Clear();
        if (Interlocked.CompareExchange(ref rendering, 1, 0) != 0)
        {
            throw new InvalidOperationException("Only one audio consumer can render at a time.");
        }
        try
        {
            if (Volatile.Read(ref started) != 0)
            {
                fixed (float* buffer = output)
                {
                    config.Callback(config.Context, buffer, (uint)(output.Length / 2));
                }
            }
        }
        finally { Volatile.Write(ref rendering, 0); }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        Volatile.Write(ref started, 0);
        var wait = new SpinWait();
        while (Volatile.Read(ref rendering) != 0)
        {
            wait.SpinOnce();
        }
        config = default;
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}
