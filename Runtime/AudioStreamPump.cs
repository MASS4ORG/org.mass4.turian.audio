using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Turian.Audio;

// Decoder thread: opens queued sources, keeps active rings filled and recycles rings released by the mixer.
// The main thread only moves Free rings to Opening; every other transition happens here or in the mixer.
sealed unsafe class AudioStreamPump : IDisposable
{
    readonly record struct Request(int Slot, Func<IAudioStreamSource> Open, bool Loop);

    readonly int count;
    readonly int capacity;
    readonly AudioStreamBuffer* buffers;
    readonly IAudioStreamSource?[] sources;
    readonly bool[] loops;
    readonly bool[] rewound;
    readonly ConcurrentQueue<Request> requests = new();
    readonly AutoResetEvent wake = new(false);
    readonly ILogger? logger;
    Thread? thread;
    volatile bool running;
    bool disposed;

    public AudioStreamPump(int streams, int capacityFrames, ILogger? logger)
    {
        count = streams;
        capacity = (int)BitOperations.RoundUpToPowerOf2((uint)capacityFrames);
        this.logger = logger;
        sources = new IAudioStreamSource?[streams];
        loops = new bool[streams];
        rewound = new bool[streams];
        buffers = (AudioStreamBuffer*)NativeMemory.AllocZeroed((nuint)streams, (nuint)sizeof(AudioStreamBuffer));
        for (var i = 0; i < streams; i++)
        {
            buffers[i].samples = (float*)NativeMemory.AllocZeroed((nuint)(capacity * 2), sizeof(float));
            buffers[i].capacity = capacity;
        }
    }

    public int ActiveStreams
    {
        get
        {
            var active = 0;
            for (var i = 0; i < count; i++)
            {
                if (Volatile.Read(ref buffers[i].state) != AudioStreamBuffer.Free) { active++; }
            }
            return active;
        }
    }

    public long Underruns
    {
        get
        {
            long total = 0;
            for (var i = 0; i < count; i++) { total += Volatile.Read(ref buffers[i].underruns); }
            return total;
        }
    }

    // Main thread: returns zero when every stream is busy.
    public nint Acquire(Func<IAudioStreamSource> open, bool loop)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        for (var i = 0; i < count; i++)
        {
            if (Volatile.Read(ref buffers[i].state) != AudioStreamBuffer.Free) { continue; }
            Volatile.Write(ref buffers[i].state, AudioStreamBuffer.Opening);
            requests.Enqueue(new(i, open, loop));
            if (thread is null)
            {
                running = true;
                thread = new Thread(Run) { IsBackground = true, Name = "Turian audio streams" };
                thread.Start();
            }
            wake.Set();
            return (nint)(buffers + i);
        }
        return 0;
    }

    void Run()
    {
        while (running)
        {
            while (requests.TryDequeue(out var request)) { Open(request); }
            var busy = false;
            for (var i = 0; i < count; i++) { busy |= Service(i); }
            if (!busy) { wake.WaitOne(5); }
        }
    }

    void Open(Request request)
    {
        ref var buffer = ref buffers[request.Slot];
        try
        {
            var source = request.Open();
            sources[request.Slot] = source;
            var format = source.Format;
            if (format.Channels is < 1 or > 2 || format.SampleRate <= 0)
            {
                throw new NotSupportedException("Audio streams support mono or stereo PCM.");
            }
            loops[request.Slot] = request.Loop;
            buffer.channels = format.Channels;
            buffer.sampleRate = format.SampleRate;
            while (Fill(request.Slot, prefill: true)) { }
            Volatile.Write(ref buffer.state, AudioStreamBuffer.Ready);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "An audio stream could not be opened; its voice ends silently.");
            Volatile.Write(ref buffer.state, AudioStreamBuffer.Failed);
        }
    }

    bool Service(int slot)
    {
        ref var buffer = ref buffers[slot];
        var state = Volatile.Read(ref buffer.state);
        if (state is AudioStreamBuffer.Free or AudioStreamBuffer.Opening) { return false; }
        if (Volatile.Read(ref buffer.released) != 0)
        {
            Recycle(slot);
            return false;
        }
        return state == AudioStreamBuffer.Ready && Fill(slot, prefill: false);
    }

    // Returns true when more decoding is immediately possible.
    bool Fill(int slot, bool prefill)
    {
        ref var buffer = ref buffers[slot];
        if (buffer.ended != 0) { return false; }
        var free = capacity - (int)(buffer.written - Volatile.Read(ref buffer.read));
        if (free == 0 || !prefill && free < capacity / 4) { return false; }
        var start = (int)(buffer.written & (capacity - 1));
        var frames = Math.Min(free, capacity - start);
        int read;
        try
        {
            read = sources[slot]!.Read(new Span<float>(buffer.samples + start * buffer.channels, frames * buffer.channels));
            if (read < 0 || read > frames) { throw new InvalidOperationException("The PCM provider returned an invalid frame count."); }
            if (read == 0 && loops[slot] && !rewound[slot])
            {
                sources[slot]!.Seek(0);
                rewound[slot] = true;
                return true;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Audio stream decoding failed; the stream ends early.");
            read = 0;
        }
        if (read == 0)
        {
            Volatile.Write(ref buffer.ended, 1);
            return false;
        }
        rewound[slot] = false;
        Volatile.Write(ref buffer.written, buffer.written + read);
        return true;
    }

    void Recycle(int slot)
    {
        sources[slot]?.Dispose();
        sources[slot] = null;
        rewound[slot] = false;
        ref var buffer = ref buffers[slot];
        buffer.written = buffer.read = 0;
        buffer.ended = buffer.released = 0;
        buffer.channels = buffer.sampleRate = 0;
        Volatile.Write(ref buffer.state, AudioStreamBuffer.Free);
    }

    // Main thread, after the device and mixer have released every voice.
    public void Reset()
    {
        if (thread is not null)
        {
            running = false;
            wake.Set();
            thread.Join();
            thread = null;
        }
        requests.Clear();
        for (var i = 0; i < count; i++)
        {
            Recycle(i);
            buffers[i].underruns = 0;
        }
    }

    public void Dispose()
    {
        if (disposed) { return; }
        Reset();
        for (var i = 0; i < count; i++) { NativeMemory.Free(buffers[i].samples); }
        NativeMemory.Free(buffers);
        wake.Dispose();
        disposed = true;
    }
}
