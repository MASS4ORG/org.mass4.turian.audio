using System;

namespace Turian.Audio;

/// <summary>Available statically dispatched bus processors.</summary>
public enum AudioEffectKind
{
    /// <summary>One-pole low-pass filter.</summary>
    LowPass,
    /// <summary>One-pole high-pass filter.</summary>
    HighPass,
    /// <summary>Four parallel feedback combs with stereo decorrelation.</summary>
    Reverb,
    /// <summary>Stereo feedback delay.</summary>
    Delay,
    /// <summary>Linked stereo peak compressor.</summary>
    Compressor,
    /// <summary>Linked peak limiting with a final output ceiling.</summary>
    Limiter,
    /// <summary>Normalized soft saturation.</summary>
    Distortion,
    /// <summary>Static kernel supplied by a registered effect factory.</summary>
    Custom,
}

/// <summary>Serializable processor settings; compilation validates bounds and allocates delay state before playback.</summary>
public sealed class AudioEffect
{
    /// <summary>Processor implementation.</summary>
    public AudioEffectKind Kind { get; set; }
    /// <summary>Wet proportion in the range zero through one.</summary>
    public float Wet { get; set; } = 1;
    /// <summary>Filter cutoff in hertz.</summary>
    public float Cutoff { get; set; } = 1200;
    /// <summary>Delay length in seconds.</summary>
    public float Seconds { get; set; } = 0.2f;
    /// <summary>Delay or reverb feedback in the range zero through 0.98.</summary>
    public float Feedback { get; set; } = 0.5f;
    /// <summary>Compressor or limiter threshold in decibels relative to full scale.</summary>
    public float ThresholdDb { get; set; } = -12;
    /// <summary>Compressor ratio, at least one.</summary>
    public float Ratio { get; set; } = 4;
    /// <summary>Envelope attack in seconds.</summary>
    public float Attack { get; set; } = 0.005f;
    /// <summary>Envelope release in seconds.</summary>
    public float Release { get; set; } = 0.1f;
    /// <summary>Compressor makeup gain in decibels.</summary>
    public float MakeupDb { get; set; }
    /// <summary>Saturation drive, at least one.</summary>
    public float Drive { get; set; } = 2;
    /// <summary>Registered factory id when Kind is Custom.</summary>
    public string CustomId { get; set; } = "";
}

/// <summary>Captures a processor on the producer thread; Process runs only on the audio thread.</summary>
public readonly unsafe struct AudioEffectKernel(
    delegate*<nint, float*, int, void> process, nint context = 0, delegate*<nint, void> reset = null)
{
    /// <summary>In-place interleaved stereo processor receiving a frame count.</summary>
    public delegate*<nint, float*, int, void> Process { get; } = process;
    /// <summary>Stable processor state, valid until the session stops.</summary>
    public nint Context { get; } = context;
    /// <summary>Optional state reset, invoked after device callbacks have stopped.</summary>
    public delegate*<nint, void> Reset { get; } = reset;
}

/// <summary>A captured kernel with resources disposed after callbacks stop.</summary>
public readonly record struct CompiledAudioEffect(AudioEffectKernel Kernel, IDisposable? Owner = null);

/// <summary>Custom effect compilation runs outside the callback and must return a static, allocation-free kernel.</summary>
public interface IAudioEffectFactory
{
    /// <summary>Unique id referenced by authored CustomId values.</summary>
    string Id { get; }
    /// <summary>Allocates state for an interleaved stereo processor at the session's sample rate.</summary>
    CompiledAudioEffect Compile(AudioEffect settings, int sampleRate);
}
