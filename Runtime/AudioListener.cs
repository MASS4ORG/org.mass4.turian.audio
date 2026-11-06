using Microsoft.Extensions.DependencyInjection;
using MASS4.Attributes;
using Turian.Engine.Core;

namespace Turian.Audio;

/// <summary>Publishes the listener node's global pose to the session audio service.</summary>
[DisallowMultipleComponent]
[ComponentContextMenu("Audio/Listener")]
[TypeId("66b53ae9-f948-4ff0-8859-bb024611e46d")]
public sealed class AudioListener : Component
{
    /// <inheritdoc/>
    public override void OnLateUpdate(float deltaTime)
    {
        if (Node is { } node)
        {
            var pose = node.GlobalTransform;
            Services?.GetService<IAudioService>()?.SetListener(new(pose.Position, pose.Orientation));
        }
    }
}
