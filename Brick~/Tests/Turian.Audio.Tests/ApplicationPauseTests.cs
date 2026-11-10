namespace Turian.Audio.Tests;

/// <summary>The output device is released while the application is in the background and restored on return.</summary>
public sealed class ApplicationPauseTests
{
    sealed class CountingDevice : IAudioDevice
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public AudioDeviceState State { get; private set; } = AudioDeviceState.Stopped;

        public void Start(in AudioDeviceConfig config)
        {
            Starts++;
            State = AudioDeviceState.Started;
        }

        public void Stop()
        {
            if (State == AudioDeviceState.Started) { Stops++; }
            State = AudioDeviceState.Stopped;
        }

        public void Dispose() => Stop();
    }

    static (AudioService Audio, CountingDevice Device) Session()
    {
        var device = new CountingDevice();
        var settings = new AudioSettings { SampleRate = 8000, BufferFrames = 64 };
        return (new AudioService(device, new AudioMixerGraph(settings, new SimpleSpatializer()), settings), device);
    }

    static void Prepare(AudioService audio) =>
        audio.Prepare(new AudioEvent { EventName = "tone", Bus = "SFX", Loop = true }, [AudioClip.FromPcm([0.2f], new(1, 8000))]);

    /// <summary>A running device stops in the background, restarts on return, and repeats change nothing.</summary>
    [Fact]
    public void RunningDeviceFollowsTheForeground()
    {
        var (audio, device) = Session();
        using (audio)
        {
            Prepare(audio);
            Assert.Equal(1, device.Starts);

            audio.SetApplicationPaused(true);
            audio.SetApplicationPaused(true);
            Assert.Equal(AudioDeviceState.Stopped, device.State);
            Assert.Equal(1, device.Stops);

            audio.SetApplicationPaused(false);
            audio.SetApplicationPaused(false);
            Assert.Equal(AudioDeviceState.Started, device.State);
            Assert.Equal(2, device.Starts);
        }
    }

    /// <summary>A device that never started stays closed; one requested while paused opens on return.</summary>
    [Fact]
    public void DeviceRequestedInTheBackgroundOpensOnReturn()
    {
        var (audio, device) = Session();
        using (audio)
        {
            audio.SetApplicationPaused(true);
            audio.SetApplicationPaused(false);
            Assert.Equal(0, device.Starts);

            audio.SetApplicationPaused(true);
            Prepare(audio);
            Assert.Equal(0, device.Starts);

            audio.SetApplicationPaused(false);
            Assert.Equal(1, device.Starts);
        }
    }
}
