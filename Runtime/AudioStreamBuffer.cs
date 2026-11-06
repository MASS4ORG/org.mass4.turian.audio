using System.Threading;

namespace Turian.Audio;

/// <summary>
/// Preallocated single-producer single-consumer PCM ring: the service's decoder thread writes frames and one mixer
/// voice consumes them. Mixer methods neither allocate nor block, so they are safe on the audio callback.
/// </summary>
public unsafe struct AudioStreamBuffer
{
    internal const int Free = 0, Opening = 1, Ready = 2, Failed = 3;

    internal float* samples;
    internal int capacity;
    internal int channels;
    internal int sampleRate;
    internal long written;
    internal long read;
    internal int state;
    internal int ended;
    internal int released;
    internal long underruns;

    /// <summary>Whether the decoder has published its format and first frames.</summary>
    public readonly bool IsReady => Volatile.Read(in state) == Ready;
    /// <summary>Whether the decoder could not open or read the source; the voice should end.</summary>
    public readonly bool IsFailed => Volatile.Read(in state) == Failed;
    /// <summary>Whether no further frames will be written beyond those available.</summary>
    public readonly bool IsEnded => Volatile.Read(in ended) != 0;
    /// <summary>Interleaved channel count of the stream; valid once ready.</summary>
    public readonly int Channels => channels;
    /// <summary>Source frame rate; valid once ready.</summary>
    public readonly int SampleRate => sampleRate;
    /// <summary>Frames written but not yet consumed.</summary>
    public readonly int Available => (int)(Volatile.Read(in written) - read);

    /// <summary>Reads one sample at a frame offset from the consumer position; the offset must be below Available.</summary>
    public readonly float Sample(int frame, int channel) =>
        samples[(int)((read + frame) & (capacity - 1)) * channels + channel];

    /// <summary>Returns consumed frames to the decoder thread.</summary>
    public void Consume(int frames) => Volatile.Write(ref read, read + frames);

    /// <summary>Counts a block in which the consumer needed frames that had not been decoded yet.</summary>
    public void ReportUnderrun() => Volatile.Write(ref underruns, underruns + 1);

    /// <summary>Hands the buffer back to the service once its voice has finished, faded out or been discarded.</summary>
    public void Release() => Volatile.Write(ref released, 1);
}
