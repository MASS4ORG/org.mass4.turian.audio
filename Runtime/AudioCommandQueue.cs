using System;
using System.Threading;

namespace Turian.Audio;

enum AudioCommandKind : byte { Play, Parameters, Emitter, Stop, Listener, Pause, Master, Bus, Snapshot }

struct AudioCommand
{
    public AudioCommandKind Kind;
    public VoiceDesc Voice;
    public int Bus;
    public AudioBusValues BusValues;
    public VoiceHandle Handle;
    public Guid Owner;
    public AudioParameterSet Parameters;
    public AudioListenerState Listener;
    // Stop fade seconds, pause flag (nonzero holds) or master volume.
    public float Value;
}

// One main-thread producer and one audio-thread consumer; publication follows writing the slot.
sealed class AudioCommandQueue(int capacity)
{
    readonly AudioCommand[] slots = new AudioCommand[checked(capacity + 1)];
    int read;
    int write;

    public bool TryWrite(in AudioCommand command)
    {
        var next = (write + 1) % slots.Length;
        if (next == Volatile.Read(ref read))
        {
            return false;
        }
        slots[write] = command;
        Volatile.Write(ref write, next);
        return true;
    }

    public bool TryRead(out AudioCommand command)
    {
        if (read == Volatile.Read(ref write))
        {
            command = default;
            return false;
        }
        command = slots[read];
        Volatile.Write(ref read, (read + 1) % slots.Length);
        return true;
    }

    public void Reset()
    {
        Array.Clear(slots);
        read = write = 0;
    }
}
