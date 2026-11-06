using System;

namespace Turian.Audio;

/// <summary>Built-in spatializer kernels available without reflection or runtime registration.</summary>
public enum SpatializerKind
{
    /// <summary>Equal-power panning and linear distance attenuation.</summary>
    Simple,
}

/// <summary>Immutable audio-thread dispatch; its context must remain valid until the device has stopped.</summary>
public readonly unsafe struct SpatializerKernel(
    delegate*<nint, in AudioListenerState, in VoiceState, SpatialParams> compute, nint context = 0)
{
    /// <summary>Preallocated implementation state, owned by the spatializer.</summary>
    public nint Context { get; } = context;
    /// <summary>Static processing entry point; implementations must not allocate or throw.</summary>
    public delegate*<nint, in AudioListenerState, in VoiceState, SpatialParams> Compute { get; } = compute;
}

/// <summary>Static dispatch table for built-in spatializers; integration bricks may supply their own kernels.</summary>
public static unsafe class SpatializerKernels
{
    static readonly SpatializerKernel[] Table = [new(&SimpleSpatializer.ComputeKernel)];

    /// <summary>Resolves a built-in kernel during session setup.</summary>
    public static SpatializerKernel Get(SpatializerKind kind)
    {
        var index = (int)kind;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Table.Length);
        return Table[index];
    }
}