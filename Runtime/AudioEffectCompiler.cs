using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Turian.Audio;

static unsafe class AudioEffectCompiler
{
    internal struct State
    {
        public AudioEffectKind Kind;
        public float Coefficient, Feedback, Threshold, Ratio, Attack, Release, Makeup, Drive, Envelope;
        public float Left, Right;
        public float* Buffer;
        public int Length, Position;
        public fixed int Sizes[4];
        public fixed int Positions[4];
        public fixed int Offsets[4];
    }

    sealed class Owner(State* state) : IDisposable
    {
        State* state = state;
        public void Dispose()
        {
            if (state is null) { return; }
            NativeMemory.Free(state->Buffer);
            NativeMemory.Free(state);
            state = null;
        }
    }

    internal static CompiledAudioEffect Compile(AudioEffect effect, int sampleRate,
        IReadOnlyDictionary<string, IAudioEffectFactory> custom, ref long bytes)
    {
        if (effect.Kind == AudioEffectKind.Custom)
        {
            if (!custom.TryGetValue(effect.CustomId, out var factory))
            {
                throw new ArgumentException($"No custom effect factory is registered as '{effect.CustomId}'.");
            }
            return factory.Compile(effect, sampleRate);
        }
        if (!Enum.IsDefined(effect.Kind)) { throw new ArgumentOutOfRangeException(nameof(effect.Kind)); }
        Validate(effect, sampleRate);
        var state = (State*)NativeMemory.AllocZeroed((nuint)sizeof(State));
        var owner = new Owner(state);
        try
        {
            state->Kind = effect.Kind;
            state->Coefficient = 1 - MathF.Exp(-MathF.Tau * effect.Cutoff / sampleRate);
            state->Feedback = effect.Feedback;
            state->Threshold = MathF.Pow(10, effect.ThresholdDb / 20);
            state->Ratio = effect.Kind == AudioEffectKind.Limiter ? 1000 : effect.Ratio;
            state->Attack = MathF.Exp(-1 / (sampleRate * effect.Attack));
            state->Release = MathF.Exp(-1 / (sampleRate * effect.Release));
            state->Makeup = MathF.Pow(10, effect.MakeupDb / 20);
            state->Drive = effect.Drive;
            var length = DelayState(state, effect, sampleRate);
            bytes = checked(bytes + sizeof(State) + (long)length * sizeof(float));
            if (bytes > 64 * 1024 * 1024) { throw new ArgumentException("Mixer DSP state exceeds the 64 MiB budget."); }
            state->Length = length / 2;
            if (length > 0) { state->Buffer = (float*)NativeMemory.AllocZeroed((nuint)length, sizeof(float)); }
            return new(new AudioEffectKernel(&AudioEffectKernels.Process, (nint)state, &AudioEffectKernels.Reset), owner);
        }
        catch { owner.Dispose(); throw; }
    }

    static int DelayState(State* state, AudioEffect effect, int sampleRate)
    {
    var length = 0;
    if (effect.Kind == AudioEffectKind.Delay) { length = Math.Max(1, (int)(sampleRate * effect.Seconds)) * 2; }
    if (effect.Kind == AudioEffectKind.Reverb)
    {
        for (var i = 0; i < 4; i++)
        {
            state->Offsets[i] = length;
            state->Sizes[i] = Math.Max(1, (int)(sampleRate * effect.Seconds * (0.149f + i * 0.037f)));
            length += state->Sizes[i] * 2;
        }
    }
        return length;
    }

    static void Validate(AudioEffect effect, int rate)
    {
        AudioMixerValidation.Range(effect.Cutoff, 1, Math.Min(96000, rate * 0.499f), nameof(effect.Cutoff));
        AudioMixerValidation.Range(effect.Seconds, 0.0001f, 5, nameof(effect.Seconds));
        AudioMixerValidation.Range(effect.Feedback, 0, 0.98f, nameof(effect.Feedback));
        AudioMixerValidation.Range(effect.ThresholdDb, -100, 0, nameof(effect.ThresholdDb));
        AudioMixerValidation.Range(effect.Ratio, 1, 1000, nameof(effect.Ratio));
        AudioMixerValidation.Range(effect.Attack, 0.0001f, 10, nameof(effect.Attack));
        AudioMixerValidation.Range(effect.Release, 0.0001f, 10, nameof(effect.Release));
        AudioMixerValidation.Range(effect.MakeupDb, -60, 60, nameof(effect.MakeupDb));
        AudioMixerValidation.Range(effect.Drive, 1, 100, nameof(effect.Drive));
    }
}
