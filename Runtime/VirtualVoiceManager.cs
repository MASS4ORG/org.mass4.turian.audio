using System;

namespace Turian.Audio;

// Session policy stays above the mixer seam so middleware replacements retain Turian's voice budget.
sealed class VirtualVoiceManager(int capacity, IMixerGraph mixer)
{
    readonly VoiceDesc[] voices = new VoiceDesc[capacity];
    AudioListenerState listener;

    public VoiceHandle Play(in VoiceDesc desc)
    {
        var slot = -1;
        var priority = int.MaxValue;
        var farthest = -1d;
        for (var i = 0; i < voices.Length; i++)
        {
            ref var voice = ref voices[i];
            if (voice.Handle.Value == 0 || !mixer.IsPlaying(voice.Handle))
            {
                slot = i;
                break;
            }
            var distance = Distance(voice);
            if (voice.Priority < priority || voice.Priority == priority && distance > farthest)
            {
                slot = i;
                priority = voice.Priority;
                farthest = distance;
            }
        }
        ref var target = ref voices[slot];
        if (target.Handle.Value != 0 && mixer.IsPlaying(target.Handle))
        {
            if (desc.Priority < priority || desc.Priority == priority && Distance(desc) > farthest)
            {
                return default;
            }
            mixer.StopVoice(target.Handle, 0.005f);
        }
        var handle = mixer.CreateVoice(desc);
        if (handle.Value != 0)
        {
            target = desc with { Handle = handle };
        }
        return handle;
    }

    double Distance(in VoiceDesc voice)
    {
        if (!voice.Spatial)
        {
            return 0;
        }
        var position = voice.Parameters.Position;
        var x = (double)position.X - listener.Position.X;
        var y = (double)position.Y - listener.Position.Y;
        var z = (double)position.Z - listener.Position.Z;
        return x * x + y * y + z * z;
    }

    public void SetParameters(VoiceHandle voice, in AudioParameterSet parameters)
    {
        for (var i = 0; i < voices.Length; i++)
        {
            if (voices[i].Handle == voice)
            {
                voices[i] = voices[i] with { Parameters = parameters };
                break;
            }
        }
        mixer.SetParameters(voice, parameters);
    }

    public void SetEmitterParameters(Guid owner, in AudioParameterSet parameters)
    {
        for (var i = 0; i < voices.Length; i++)
        {
            if (voices[i].Owner == owner)
            {
                voices[i] = voices[i] with { Parameters = parameters };
            }
        }
        mixer.SetEmitterParameters(owner, parameters);
    }

    public void SetListener(in AudioListenerState listener)
    {
        this.listener = listener;
        mixer.SetListener(listener);
    }

    public void Stop(Guid owner, float fade)
    {
        mixer.Stop(owner, fade);
        for (var i = 0; i < voices.Length; i++)
        {
            if (voices[i].Owner == owner)
            {
                voices[i] = default;
            }
        }
    }

    public void Reset()
    {
        Array.Clear(voices);
        listener = default;
        mixer.Reset();
    }
}
