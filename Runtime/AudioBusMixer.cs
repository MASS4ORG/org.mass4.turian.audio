using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Turian.Audio;

sealed unsafe partial class AudioBusMixer : IDisposable
{
    internal readonly Dictionary<string, int> Indices;
    internal readonly IReadOnlyList<string> Names;
    internal readonly IReadOnlyList<string> SnapshotNames;
    internal readonly AudioBusValues[] Defaults;
    internal readonly AudioBusValues[][] SnapshotValues;
    internal readonly int[] Parents;
    internal readonly int Frames;
    internal readonly float[] Samples;
    internal readonly bool[] VoiceEnabled;
    readonly int sampleRate;
    readonly int[] order;
    readonly ulong[] ancestors;
    readonly AudioBusValues[] values;
    readonly Ramp[] gains;
    readonly float[] peaks;
    readonly float[] sums;
    readonly float[] published;
    readonly float[] detector;
    readonly Duck[] ducks;
    readonly AudioEffectKernel[][] effects;
    readonly Ramp[][] wet;
    readonly float[][] wetDefaults;
    readonly float[][][] snapshotWet;
    readonly float[] dry;
    readonly List<IDisposable> owners = [];
    bool disposed;

    struct Ramp
    {
        public float Value, Target, Step;
        public int Remaining;
        public void Set(float target, int frames)
        {
            Target = target;
            Remaining = frames;
            Step = frames == 0 ? 0 : (target - Value) / frames;
            if (frames == 0) { Value = target; }
        }
        public float Next()
        {
            if (Remaining > 0 && --Remaining == 0) { Value = Target; }
            else if (Remaining > 0) { Value += Step; }
            return Value;
        }
    }

    struct Duck
    {
        public int Source;
        public float Threshold, Reduced, Attack, Release, Gain;
    }

    internal AudioBusMixer(AudioMixer model, int sampleRate, int frames,
        IEnumerable<IAudioEffectFactory>? factories = null)
    {
        (Indices, order, ancestors) = AudioMixerValidation.Validate(model);
        this.sampleRate = sampleRate;
        Frames = frames;
        var count = model.Buses.Count;
        Names = Array.AsReadOnly<string>([.. model.Buses.Select(static b => b.Name)]);
        SnapshotNames = Array.AsReadOnly<string>([.. model.Snapshots.Select(static s => s.Name)]);
        Defaults = [.. model.Buses.Select(static b => new AudioBusValues(b.Volume, b.Mute, b.Solo))];
        values = (AudioBusValues[])Defaults.Clone();
        SnapshotValues = [.. model.Snapshots.Select(snapshot => CompileSnapshot(snapshot))];
        Parents = [.. model.Buses.Select((b, i) => i == 0 ? -1 : Indices[b.Parent])];
        gains = new Ramp[count];
        ducks = new Duck[count];
        Samples = new float[checked(count * frames * 2)];
        dry = new float[frames * 2];
        peaks = new float[count * 2];
        sums = new float[count * 2];
        published = new float[count * 4];
        detector = new float[count];
        VoiceEnabled = new bool[count];
        effects = new AudioEffectKernel[count][];
        wet = new Ramp[count][];
        wetDefaults = new float[count][];
        snapshotWet = new float[model.Snapshots.Count][][];
        var custom = (factories ?? []).ToDictionary(static factory => factory.Id, StringComparer.Ordinal);
        long memory = 0;
        try
        {
            for (var i = 0; i < count; i++) { CompileBus(model.Buses[i], i, custom, ref memory); }
            CompileWetSnapshots(model);
            RecomputeGains(0);
        }
        catch { Dispose(); throw; }
    }

    void CompileBus(AudioBus bus, int index, IReadOnlyDictionary<string, IAudioEffectFactory> custom, ref long memory)
    {
        effects[index] = new AudioEffectKernel[bus.Effects.Count];
        wet[index] = new Ramp[bus.Effects.Count];
        wetDefaults[index] = new float[bus.Effects.Count];
        for (var e = 0; e < bus.Effects.Count; e++)
        {
            var effect = bus.Effects[e];
            AudioMixerValidation.Range(effect.Wet, 0, 1, nameof(effect.Wet));
            var compiled = AudioEffectCompiler.Compile(effect, sampleRate, custom, ref memory);
            if (compiled.Owner is not null) { owners.Add(compiled.Owner); }
            if (compiled.Kernel.Process == null) { throw new ArgumentException("An effect has no kernel."); }
            effects[index][e] = compiled.Kernel;
            wet[index][e].Set(effect.Wet, 0);
            wetDefaults[index][e] = effect.Wet;
        }
        ducks[index] = CompileDuck(bus.Ducking, index);
    }

    void CompileWetSnapshots(AudioMixer model)
    {
        for (var s = 0; s < model.Snapshots.Count; s++)
        {
            snapshotWet[s] = [.. wetDefaults.Select(static targets => (float[])targets.Clone())];
            foreach (var target in model.Snapshots[s].Values)
            {
                if (target.EffectWet.Count > 0) { target.EffectWet.CopyTo(snapshotWet[s][Indices[target.Bus]]); }
            }
        }
    }

    AudioBusValues[] CompileSnapshot(AudioSnapshot snapshot)
    {
        var result = (AudioBusValues[])Defaults.Clone();
        foreach (var value in snapshot.Values)
        {
            result[Indices[value.Bus]] = new(value.Volume, value.Mute, value.Solo);
        }
        return result;
    }

    Duck CompileDuck(AudioDucking? source, int target)
    {
        if (source is null) { return new Duck { Source = -1, Gain = 1 }; }
        if (!Indices.TryGetValue(source.SourceBus, out var index) || (ancestors[index] & (1UL << target)) != 0)
        {
            throw new ArgumentException("A ducking detector must name a bus outside the target's subtree.");
        }
        AudioMixerValidation.Range(source.ThresholdDb, -100, 0, nameof(source.ThresholdDb));
        AudioMixerValidation.Range(source.ReductionDb, 0, 80, nameof(source.ReductionDb));
        AudioMixerValidation.Range(source.Attack, 0.0001f, 10, nameof(source.Attack));
        AudioMixerValidation.Range(source.Release, 0.0001f, 10, nameof(source.Release));
        return new Duck
        {
            Source = index, Gain = 1, Threshold = MathF.Pow(10, source.ThresholdDb / 20),
            Reduced = MathF.Pow(10, -source.ReductionDb / 20),
            Attack = MathF.Exp(-1 / (source.Attack * sampleRate)),
            Release = MathF.Exp(-1 / (source.Release * sampleRate)),
        };
    }

    internal void Set(int bus, AudioBusValues state, float seconds = 0.02f)
    {
        values[bus] = state;
        RecomputeGains(seconds);
    }

    internal void Snapshot(int index, float seconds)
    {
        SnapshotValues[index].CopyTo(values, 0);
        for (var bus = 0; bus < wet.Length; bus++)
        {
            for (var e = 0; e < wet[bus].Length; e++)
            {
                wet[bus][e].Set(snapshotWet[index][bus][e], (int)(seconds * sampleRate));
            }
        }
        RecomputeGains(seconds);
    }

    void RecomputeGains(float seconds)
    {
        ulong solos = 0;
        ulong routes = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].Solo) { solos |= 1UL << i; routes |= ancestors[i]; }
        }
        var frames = (int)(seconds * sampleRate);
        for (var i = 0; i < values.Length; i++)
        {
            VoiceEnabled[i] = solos == 0 || (ancestors[i] & solos) != 0;
            var audible = VoiceEnabled[i] || (routes & (1UL << i)) != 0;
            gains[i].Set(values[i].Mute || !audible ? 0 : values[i].Volume, frames);
        }
    }

    internal void Begin()
    {
        Array.Clear(peaks);
        Array.Clear(sums);
    }

    internal void Process(int frames)
    {
        MeasureDetectors(frames);
        fixed (float* samples = Samples)
        {
            foreach (var bus in order)
            {
                var buffer = samples + bus * Frames * 2;
                ProcessEffects(bus, buffer, frames);
                ProcessFader(bus, buffer, frames, samples);
            }
        }
    }

    void MeasureDetectors(int frames)
    {
        Array.Clear(detector);
        for (var bus = 0; bus < values.Length; bus++)
        {
            var offset = bus * Frames * 2;
            for (var n = 0; n < frames * 2; n++)
            {
                detector[bus] = Math.Max(detector[bus], Math.Abs(Samples[offset + n]));
            }
        }
        foreach (var bus in order)
        {
            if (Parents[bus] >= 0) { detector[Parents[bus]] = Math.Max(detector[Parents[bus]], detector[bus]); }
        }
    }

    void ProcessEffects(int bus, float* buffer, int frames)
    {
        for (var e = 0; e < effects[bus].Length; e++)
        {
            new ReadOnlySpan<float>(buffer, frames * 2).CopyTo(dry);
            var kernel = effects[bus][e];
            kernel.Process(kernel.Context, buffer, frames);
            for (var n = 0; n < frames; n++)
            {
                var amount = wet[bus][e].Next();
                buffer[n * 2] = dry[n * 2] + (buffer[n * 2] - dry[n * 2]) * amount;
                buffer[n * 2 + 1] = dry[n * 2 + 1] + (buffer[n * 2 + 1] - dry[n * 2 + 1]) * amount;
            }
        }
    }

    void ProcessFader(int bus, float* buffer, int frames, float* samples)
    {
        for (var n = 0; n < frames; n++)
        {
            var gain = gains[bus].Next() * DuckGain(bus);
            for (var channel = 0; channel < 2; channel++)
            {
                var value = buffer[n * 2 + channel] * gain;
                buffer[n * 2 + channel] = value;
                var meter = bus * 2 + channel;
                peaks[meter] = Math.Max(peaks[meter], Math.Abs(value));
                sums[meter] += value * value;
                if (Parents[bus] >= 0)
                {
                    samples[Parents[bus] * Frames * 2 + n * 2 + channel] += value;
                }
            }
        }
    }

    float DuckGain(int bus)
    {
        ref var duck = ref ducks[bus];
        var reduction = duck.Source >= 0 && detector[duck.Source] > duck.Threshold ? duck.Reduced : 1;
        var coefficient = reduction < duck.Gain ? duck.Attack : duck.Release;
        duck.Gain = reduction + (duck.Gain - reduction) * coefficient;
        return duck.Gain;
    }

    internal void Publish(int frames)
    {
        for (var i = 0; i < values.Length; i++)
        {
            Volatile.Write(ref published[i * 4], peaks[i * 2]);
            Volatile.Write(ref published[i * 4 + 1], peaks[i * 2 + 1]);
            Volatile.Write(ref published[i * 4 + 2], frames == 0 ? 0 : MathF.Sqrt(sums[i * 2] / frames));
            Volatile.Write(ref published[i * 4 + 3], frames == 0 ? 0 : MathF.Sqrt(sums[i * 2 + 1] / frames));
        }
    }

    internal AudioBusMeter Meter(int index) => new(Volatile.Read(ref published[index * 4]),
        Volatile.Read(ref published[index * 4 + 1]), Volatile.Read(ref published[index * 4 + 2]),
        Volatile.Read(ref published[index * 4 + 3]));

    internal void Reset()
    {
        if (disposed) { return; }
        Defaults.CopyTo(values, 0);
        for (var i = 0; i < ducks.Length; i++) { ducks[i].Gain = 1; }
        for (var bus = 0; bus < effects.Length; bus++)
        {
            for (var e = 0; e < effects[bus].Length; e++)
            {
                var kernel = effects[bus][e];
                if (kernel.Reset != null) { kernel.Reset(kernel.Context); }
                wet[bus][e].Set(wetDefaults[bus][e], 0);
            }
        }
        RecomputeGains(0);
        Begin();
        Array.Clear(published);
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var owner in owners) { owner.Dispose(); }
        owners.Clear();
    }
}
