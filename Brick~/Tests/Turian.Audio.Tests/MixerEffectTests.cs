using System.Runtime.InteropServices;

namespace Turian.Audio.Tests;

/// <summary>Exercises built-in and custom bus processors, transitions, cleanup and callback allocation discipline.</summary>
public sealed unsafe class MixerEffectTests
{
    static AudioMixer Model(params AudioEffect[] effects)
    {
        var model = new AudioMixer();
        model.Buses[2].Effects.AddRange(effects);
        return model;
    }

    /// <summary>Effects execute in asset order, so a limiter before distortion differs from a final limiter.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectChainsPreserveAuthoredOrder(bool limiterFirst)
    {
        var distortion = new AudioEffect { Kind = AudioEffectKind.Distortion };
        var limiter = new AudioEffect { Kind = AudioEffectKind.Limiter, ThresholdDb = -12, Attack = 0.0001f };
        var model = limiterFirst ? Model(limiter, distortion) : Model(distortion, limiter);
        using var session = new MixerTests.Session(model);
        session.Tone("SFX", 0.8f);
        var output = session.Render(8000)[^1];
        Assert.InRange(output, limiterFirst ? 0.46f : 0.25f, limiterFirst ? 0.50f : 0.26f);
    }

    /// <summary>Filters attenuate their rejected bands and distortion/compression/limiting alter the signal.</summary>
    [Theory]
    [InlineData(AudioEffectKind.LowPass)]
    [InlineData(AudioEffectKind.HighPass)]
    [InlineData(AudioEffectKind.Distortion)]
    [InlineData(AudioEffectKind.Compressor)]
    [InlineData(AudioEffectKind.Limiter)]
    public void BuiltInProcessorsAffectTheirTargetSignal(AudioEffectKind kind)
    {
        var effect = new AudioEffect { Kind = kind, Cutoff = 100, Attack = 0.0001f };
        using var session = new MixerTests.Session(Model(effect));
        var alternating = kind == AudioEffectKind.LowPass;
        var samples = Enumerable.Range(0, 8000).Select(i => alternating && i % 2 == 0 ? -0.8f : 0.8f).ToArray();
        session.Audio.Prepare(new AudioEvent { EventName = "signal", Bus = "SFX", Loop = true },
            [AudioClip.FromPcm(samples, new(1, 8000))]);
        session.Audio.Post("signal", Guid.Empty, new AudioParameterSet());
        var output = session.Render(8000);
        Assert.All(output, static value => Assert.True(float.IsFinite(value)));
        switch (kind)
        {
            case AudioEffectKind.LowPass: Assert.InRange(Math.Abs(output[^1]), 0, 0.05f); break;
            case AudioEffectKind.HighPass: Assert.InRange(Math.Abs(output[^1]), 0, 0.0001f); break;
            case AudioEffectKind.Distortion: Assert.InRange(output[^1], 0.9f, 1); break;
            case AudioEffectKind.Compressor: Assert.InRange(output[^1], 0.3f, 0.4f); break;
            case AudioEffectKind.Limiter: Assert.InRange(output.Max(), 0.2f, MathF.Pow(10, -12f / 20) + 0.00001f); break;
        }
    }

    /// <summary>Delay and reverb produce decaying output after the original impulse and voice have ended.</summary>
    [Theory]
    [InlineData(AudioEffectKind.Delay)]
    [InlineData(AudioEffectKind.Reverb)]
    public void TimeEffectsRetainAndReleaseTheirTails(AudioEffectKind kind)
    {
        using var session = new MixerTests.Session(Model(new AudioEffect { Kind = kind, Seconds = 0.02f }));
        var samples = new float[81];
        samples[80] = 0.5f;
        session.Audio.Prepare(new AudioEvent { EventName = "impulse", Bus = "SFX" },
            [AudioClip.FromPcm(samples, new(1, 8000))]);
        session.Audio.Post("impulse", Guid.Empty, new AudioParameterSet());
        var output = session.Render(1600);
        Assert.Equal(0, output[160]);
        Assert.Contains(output.Skip(162), static value => value > 0.001f);
        Assert.Equal(0, session.Graph.ActiveVoiceCount);
        Assert.All(session.Render(16000), static value => Assert.True(float.IsFinite(value)));
        Assert.InRange(Math.Abs(session.Render()[^1]), 0, 0.000001f);
    }

    /// <summary>Snapshots crossfade effect wet amounts without abruptly replacing processor state.</summary>
    [Fact]
    public void SnapshotTransitionsEffectWetValues()
    {
        var model = Model(new AudioEffect { Kind = AudioEffectKind.HighPass, Wet = 0 });
        model.Snapshots.Add(new AudioSnapshot
        {
            Name = "Filtered", Values = [new AudioBusState { Bus = "SFX", EffectWet = [1] }],
        });
        model.Snapshots.Add(new AudioSnapshot { Name = "Dry" });
        using var session = new MixerTests.Session(model);
        session.Tone("SFX");
        Assert.Equal(0.2f, session.Render()[^1], 5);
        session.Audio.TransitionTo("Filtered", 0.1f);
        Assert.Equal(0.1f, session.Render(400)[^1], 4);
        Assert.InRange(Math.Abs(session.Render(400)[^1]), 0, 0.0001f);
        session.Audio.TransitionTo("Dry", 0);
        Assert.Equal(0.2f, session.Render()[^1], 5);
    }

    sealed class EffectOwner(float* pointer, Action disposed) : IDisposable
    {
        public void Dispose() { NativeMemory.Free(pointer); disposed(); }
    }

    sealed class Factory : IAudioEffectFactory
    {
        public string Id => "gain";
        public int Compiles, Disposes;
        public CompiledAudioEffect Compile(AudioEffect settings, int sampleRate)
        {
            Compiles++;
            var state = (float*)NativeMemory.Alloc(sizeof(float));
            *state = 3;
            return new(new AudioEffectKernel(&Process, (nint)state), new EffectOwner(state, () => Disposes++));
        }
        static void Process(nint context, float* samples, int frames)
        {
            for (var i = 0; i < frames * 2; i++) { samples[i] *= *(float*)context; }
        }
    }

    /// <summary>Custom factories run only at setup and their captured kernels/owners are used for playback/cleanup.</summary>
    [Fact]
    public void CustomEffectsCaptureOneKernelAndReleaseItsOwner()
    {
        var factory = new Factory();
        using (var session = new MixerTests.Session(Model(new AudioEffect
               { Kind = AudioEffectKind.Custom, CustomId = "gain", Wet = 0.5f }), effects: [factory]))
        {
            session.Tone("SFX");
            Assert.Equal(0.4f, session.Render()[^1], 5);
            session.Render();
            Assert.Equal(1, factory.Compiles);
        }
        Assert.Equal(1, factory.Disposes);
    }

    /// <summary>Warmed bus controls, sidechains, snapshots, DSP and meters allocate no memory in the callback path.</summary>
    [Fact]
    public void MixerControlsAndDspAllocateNoManagedMemoryDuringPlayback()
    {
        var model = Model(new AudioEffect { Kind = AudioEffectKind.Reverb },
            new AudioEffect { Kind = AudioEffectKind.Compressor });
        model.Buses[1].Ducking = new AudioDucking();
        model.Snapshots.Add(new AudioSnapshot { Name = "Normal" });
        using var session = new MixerTests.Session(model);
        session.Tone("Music");
        session.Tone("SFX");
        session.Tone("Voice");
        var output = new float[1024];
        for (var i = 0; i < 50; i++)
        {
            session.Audio.SetBusVolume("Music", 0.5f);
            session.Audio.TransitionTo("Normal", 0.01f);
            session.Device.Render(output);
            _ = session.Audio.GetMeter("Master");
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            session.Audio.SetBusVolume("Music", 0.5f);
            session.Audio.TransitionTo("Normal", 0.01f);
            session.Device.Render(output);
            _ = session.Audio.GetMeter("Master");
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
