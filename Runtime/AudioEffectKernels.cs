using System;

namespace Turian.Audio;

static unsafe class AudioEffectKernels
{
    internal static void Reset(nint context)
    {
        var state = (AudioEffectCompiler.State*)context;
        state->Left = state->Right = state->Envelope = 0;
        state->Position = 0;
        var length = state->Kind == AudioEffectKind.Delay ? state->Length * 2 : 0;
        if (state->Kind == AudioEffectKind.Reverb)
        {
            for (var i = 0; i < 4; i++) { state->Positions[i] = 0; length += state->Sizes[i] * 2; }
        }
        if (state->Buffer != null) { new Span<float>(state->Buffer, length).Clear(); }
    }

    internal static void Process(nint context, float* samples, int frames)
    {
        var state = (AudioEffectCompiler.State*)context;
        for (var n = 0; n < frames; n++)
        {
            var left = samples[n * 2];
            var right = samples[n * 2 + 1];
            Apply(state, ref left, ref right);
            samples[n * 2] = left;
            samples[n * 2 + 1] = right;
        }
    }

    static void Apply(AudioEffectCompiler.State* state, ref float left, ref float right)
    {
        switch (state->Kind)
        {
            case AudioEffectKind.LowPass:
            case AudioEffectKind.HighPass: Filter(state, ref left, ref right); break;
            case AudioEffectKind.Delay: Delay(state, ref left, ref right); break;
            case AudioEffectKind.Reverb: Reverb(state, ref left, ref right); break;
            case AudioEffectKind.Compressor:
            case AudioEffectKind.Limiter: Compress(state, ref left, ref right); break;
            case AudioEffectKind.Distortion:
                var normalization = MathF.Tanh(state->Drive);
                left = MathF.Tanh(left * state->Drive) / normalization;
                right = MathF.Tanh(right * state->Drive) / normalization;
                break;
        }
    }

    static void Filter(AudioEffectCompiler.State* state, ref float left, ref float right)
    {
        state->Left += state->Coefficient * (left - state->Left);
        state->Right += state->Coefficient * (right - state->Right);
        if (state->Kind == AudioEffectKind.LowPass) { left = state->Left; right = state->Right; }
        else { left -= state->Left; right -= state->Right; }
    }

    static void Delay(AudioEffectCompiler.State* state, ref float left, ref float right)
    {
        var position = state->Position * 2;
        var delayedLeft = state->Buffer[position];
        var delayedRight = state->Buffer[position + 1];
        state->Buffer[position] = left + delayedLeft * state->Feedback;
        state->Buffer[position + 1] = right + delayedRight * state->Feedback;
        left = delayedLeft;
        right = delayedRight;
        state->Position = (state->Position + 1) % state->Length;
    }

    static void Reverb(AudioEffectCompiler.State* state, ref float left, ref float right)
    {
        var reverbedLeft = 0f;
        var reverbedRight = 0f;
        for (var comb = 0; comb < 4; comb++)
        {
            var offset = state->Offsets[comb] + state->Positions[comb] * 2;
            var l = state->Buffer[offset];
            var r = state->Buffer[offset + 1];
            state->Buffer[offset] = left + l * state->Feedback;
            state->Buffer[offset + 1] = right + r * state->Feedback * 0.97f;
            reverbedLeft += l;
            reverbedRight += r;
            state->Positions[comb] = (state->Positions[comb] + 1) % state->Sizes[comb];
        }
        left = reverbedLeft * 0.25f;
        right = reverbedRight * 0.25f;
    }

    static void Compress(AudioEffectCompiler.State* state, ref float left, ref float right)
    {
        var peak = Math.Max(Math.Abs(left), Math.Abs(right));
        var coefficient = peak > state->Envelope ? state->Attack : state->Release;
        state->Envelope = peak + (state->Envelope - peak) * coefficient;
        var gain = state->Envelope > state->Threshold
            ? MathF.Pow(state->Envelope / state->Threshold, 1 / state->Ratio - 1) : 1;
        left *= gain * state->Makeup;
        right *= gain * state->Makeup;
        if (state->Kind == AudioEffectKind.Limiter)
        {
            left = Math.Clamp(left, -state->Threshold, state->Threshold);
            right = Math.Clamp(right, -state->Threshold, state->Threshold);
        }
    }
}
