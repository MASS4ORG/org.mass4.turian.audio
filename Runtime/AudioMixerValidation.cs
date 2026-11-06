using System;
using System.Collections.Generic;
using System.Linq;

namespace Turian.Audio;

static class AudioMixerValidation
{
    internal static void Range(float value, float min, float max, string name)
    {
        if (!float.IsFinite(value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, $"Expected a finite value from {min} through {max}.");
        }
    }

    internal static (Dictionary<string, int> Indices, int[] Order, ulong[] Ancestors) Validate(AudioMixer model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateRoot(model.Buses);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < model.Buses.Count; i++)
        {
            ValidateBus(model.Buses[i], i, indices);
        }
        var ancestors = new ulong[model.Buses.Count];
        for (var i = 0; i < ancestors.Length; i++)
        {
            ancestors[i] = Ancestors(model.Buses, indices, i);
        }
        ValidateSnapshots(model, indices);
        return (indices, Enumerable.Range(0, ancestors.Length)
            .OrderByDescending(i => System.Numerics.BitOperations.PopCount(ancestors[i])).ToArray(), ancestors);
    }

    static void ValidateRoot(List<AudioBus> buses)
    {
        if (buses is null || buses.Count is < 1 or > 64)
        {
            throw new ArgumentException("A mixer needs one through sixty-four buses.");
        }
        var root = buses[0] ?? throw new ArgumentException("The Master bus cannot be null.");
        if (root.Name != "Master" || !string.IsNullOrEmpty(root.Parent))
        {
            throw new ArgumentException("The first bus must be the parentless Master bus.");
        }
    }

    static void ValidateBus(AudioBus bus, int index, Dictionary<string, int> indices)
    {
        ArgumentNullException.ThrowIfNull(bus);
        if (string.IsNullOrWhiteSpace(bus.Name) || !indices.TryAdd(bus.Name, index))
        {
            throw new ArgumentException("Bus names must be nonempty and unique.");
        }
        Range(bus.Volume, 0, 16, nameof(bus.Volume));
        if (bus.Effects is null || bus.Effects.Count > 16 || bus.Effects.Any(static effect => effect is null))
        {
            throw new ArgumentException("A bus supports at most sixteen nonnull effects.");
        }
    }

    static ulong Ancestors(List<AudioBus> buses, Dictionary<string, int> indices, int current)
    {
        var result = 0UL;
        while (true)
        {
            var bit = 1UL << current;
            if ((result & bit) != 0) { throw new ArgumentException("Bus parents contain a cycle."); }
            result |= bit;
            if (current == 0) { return result; }
            if (!indices.TryGetValue(buses[current].Parent, out current))
            {
                throw new ArgumentException("Every non-Master bus must name an existing parent.");
            }
        }
    }

    static void ValidateSnapshots(AudioMixer model, Dictionary<string, int> indices)
    {
        if (model.Snapshots is null || model.Snapshots.Count > 64)
        {
            throw new ArgumentException("A mixer supports at most sixty-four snapshots.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in model.Snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ValidateSnapshotName(snapshot, names);
            var overridden = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in snapshot.Values)
            {
                ValidateTarget(state, model, indices, overridden);
            }
        }
    }

    static void ValidateSnapshotName(AudioSnapshot snapshot, HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Name) || !names.Add(snapshot.Name) || snapshot.Values is null)
        {
            throw new ArgumentException("Snapshots need unique nonempty names and a nonnull target list.");
        }
    }

    static void ValidateTarget(AudioBusState state, AudioMixer model, Dictionary<string, int> indices,
        HashSet<string> overridden)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Bus is null || !indices.TryGetValue(state.Bus, out var index) || !overridden.Add(state.Bus))
        {
            throw new ArgumentException("Snapshots must name existing buses once each.");
        }
        Range(state.Volume, 0, 16, nameof(state.Volume));
        ValidateWet(state.EffectWet, model.Buses[index].Effects.Count);
    }

    static void ValidateWet(List<float> targets, int effects)
    {
        if (targets is null || targets.Count != 0 && targets.Count != effects)
        {
            throw new ArgumentException("Snapshot wet targets must cover the bus's complete effect chain.");
        }
        foreach (var wet in targets) { Range(wet, 0, 1, nameof(targets)); }
    }
}
