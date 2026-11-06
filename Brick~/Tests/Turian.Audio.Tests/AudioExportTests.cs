
namespace Turian.Audio.Tests;

/// <summary>Checks native audio staging and project audio configuration.</summary>
public sealed class AudioExportTests
{
    /// <summary>Audio settings use the ordinary project DataAsset serialization path.</summary>
    [Fact]
    public void SettingsRoundTripThroughDataAssetSerialization()
    {
        var settings = new AudioSettings
        {
            SampleRate = 44100,
            BufferFrames = 256,
            MaxVirtualVoices = 32,
            DeviceName = "Example device",
        };
        var copy = Assert.IsType<AudioSettings>(Serializer.LoadData<DataAsset>(Serializer.Serialize<DataAsset>(settings)));
        Assert.Equal(44100, copy.SampleRate);
        Assert.Equal(256, copy.BufferFrames);
        Assert.Equal(32, copy.MaxVirtualVoices);
        Assert.Equal("Example device", copy.DeviceName);
    }

    /// <summary>Invalid callback budgets and voice capacities are rejected before native initialization.</summary>
    [Theory]
    [InlineData(7999, 512, 64, 1024)]
    [InlineData(192001, 512, 64, 1024)]
    [InlineData(48000, 15, 64, 1024)]
    [InlineData(48000, 8193, 64, 1024)]
    [InlineData(48000, 512, 0, 1024)]
    [InlineData(48000, 512, 4097, 1024)]
    [InlineData(48000, 512, 64, 1)]
    [InlineData(48000, 512, 64, 65537)]
    public void InvalidSettingsFailBeforePlayback(int rate, int frames, int voices, int commands)
    {
        var settings = new AudioSettings
        {
            SampleRate = rate,
            BufferFrames = frames,
            MaxVirtualVoices = voices,
            CommandCapacity = commands,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMixerGraph(settings, new SimpleSpatializer()));
    }
}
