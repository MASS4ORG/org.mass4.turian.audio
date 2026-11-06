using System;
using System.Numerics;

namespace Turian.Audio;

/// <summary>Equal-power stereo panning with linear distance attenuation.</summary>
public sealed class SimpleSpatializer : ISpatializer
{
    AudioListenerState listener = new(Vector3.Zero, Quaternion.Identity);

    /// <inheritdoc/>
    public SpatializerKernel Kernel => SpatializerKernels.Get(SpatializerKind.Simple);

    /// <inheritdoc/>
    public void ProcessListener(in AudioListenerState listener) => this.listener = listener;

    /// <inheritdoc/>
    public SpatialParams Compute(in VoiceState voice) => ComputeKernel(0, listener, voice);

    internal static SpatialParams ComputeKernel(nint context, in AudioListenerState listener, in VoiceState voice)
    {
        if (!voice.Spatial)
        {
            return new(1, 1, 0);
        }

        var dx = (double)voice.Position.X - listener.Position.X;
        var dy = (double)voice.Position.Y - listener.Position.Y;
        var dz = (double)voice.Position.Z - listener.Position.Z;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var distance = (float)Math.Min(length, float.MaxValue);
        var direction = length > 0 ? new Vector3((float)(dx / length), (float)(dy / length), (float)(dz / length))
            : Vector3.Zero;
        var local = Vector3.Transform(direction, Quaternion.Conjugate(listener.Orientation));
        // The camera looks along +Z, so screen-right is the listener's local -X axis.
        var pan = Math.Clamp(-local.X, -1, 1);
        var gain = 1 - Math.Clamp((distance - voice.MinDistance) / (voice.MaxDistance - voice.MinDistance), 0, 1);
        return new(gain * MathF.Sqrt((1 - pan) * 0.5f), gain * MathF.Sqrt((1 + pan) * 0.5f), distance);
    }
}
