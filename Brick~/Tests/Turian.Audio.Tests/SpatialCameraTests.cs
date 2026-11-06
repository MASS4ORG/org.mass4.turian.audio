namespace Turian.Audio.Tests;

/// <summary>Spatial audio follows the side on which a camera projects an emitter.</summary>
public sealed class SpatialCameraTests
{
    /// <summary>Left and right PCM channels agree with screen positions through listener and parent rotations.</summary>
    [Theory]
    [InlineData(-1, 0, 0, 0, false)]
    [InlineData(0, 0, 0, 0, false)]
    [InlineData(1, 0, 0, 0, false)]
    [InlineData(-1, 0, 90, 0, false)]
    [InlineData(1, 0, 90, 0, false)]
    [InlineData(-1, 30, 45, 60, true)]
    [InlineData(1, 30, 45, 60, true)]
    public void StereoMatchesProjectedEmitterSide(int side, float pitch, float yaw, float roll, bool rotateParent)
    {
        var parent = new Node { Position = new(3, 4, 5) };
        if (rotateParent) { parent.Rotation = new(15, 30, 20); }
        var node = new Node { Parent = parent, Position = new(0, 0, 2), Rotation = new(pitch, yaw, roll) };
        parent.Children.Add(node);
        var camera = new CameraComponent();
        node.AddComponent(camera);
        var position = camera.Position + camera.Front * 4 + camera.Right * side;
        var screenX = camera.Project(position).X;
        if (side == 0) { Assert.InRange(Math.Abs(screenX), 0, 0.00001f); }
        else { Assert.True(screenX * side > 0); }

        var settings = new AudioSettings { SampleRate = 8000 };
        using var device = new SilentAudioDevice();
        using var mixer = new AudioMixerGraph(settings, new SimpleSpatializer());
        using var service = new AudioService(device, mixer, settings);
        service.Prepare(new AudioEvent { EventName = "spatial", Loop = true, Spatial = true, MaxDistance = 20 },
            [AudioClip.FromPcm([0.5f], new(1, 8000))]);
        service.SetListener(new(node.GlobalTransform.Position, node.GlobalTransform.Orientation));
        service.Post("spatial", Guid.Empty, new AudioParameterSet { Position = position });
        var output = new float[256];
        device.Render(output);
        var left = output[^2];
        var right = output[^1];
        if (side == 0) { Assert.Equal(left, right, 5); }
        else { Assert.True((right - left) * side > 0, $"screen x={screenX}, left={left}, right={right}"); }
    }
}
