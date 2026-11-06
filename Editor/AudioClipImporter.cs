using System;
using System.IO;
using Turian.Engine.Core;
using Turian.Editor.Core;

namespace Turian.Audio.Editor;

/// <summary>Imports WAV, MP3 and FLAC source bytes as audio clip assets.</summary>
public sealed class AudioClipImporter : IAssetImporter
{
    static readonly string[] Extensions = [".wav", ".mp3", ".flac"];

    /// <inheritdoc/>
    public bool IsValid(string filePath) =>
        Array.Exists(Extensions, extension => string.Equals(Path.GetExtension(filePath), extension,
            StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath) => new AudioClip { RelativePath = filePath };
}
