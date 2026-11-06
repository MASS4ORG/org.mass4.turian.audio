using Turian.Audio.Editor;
using Turian.Editor.Core;

namespace Turian.Audio.Tests;

/// <summary>Checks warnings for audio startup preparation and serialized scene event references.</summary>
public sealed class AudioEventValidatorTests
{
    /// <summary>Preparation must occur in startup and refer to an assigned event present in the scene.</summary>
    [Theory]
    [InlineData("", "audio?.Post(\"ui.click\", Id, parameters);", true, true, "ui.click")]
    [InlineData("name = audio.Prepare(ClickEvent);", "audio?.Post(name, Id, parameters);", true, false, "ui.click")]
    [InlineData("name = audio.Prepare(ClickEvent);", "audio.Post(\"ui.click\", Id, parameters);", true, false, "ui.click")]
    [InlineData("name = audio.Prepare(ClickEvent);", "audio.Post(\"other\", Id, parameters);", true, true, "ui.click")]
    [InlineData("name = audio.Prepare(ClickEvent);", "audio.Post(name, Id, parameters);", false, true, "ui.click")]
    [InlineData("", "name = audio.Prepare(ClickEvent); audio.Post(name, Id, parameters);", true, true, "ui.click")]
    [InlineData("name = audio.Prepare(ClickEvent);", "audio.Post(name, Id, parameters);", true, true, "")]
    public void ReportsUnverifiedPosts(string start, string update, bool assigned, bool warning, string eventName)
    {
        var root = Directory.CreateTempSubdirectory("audio-validation-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Motion.cs"), $$"""
                [TypeId("component")]
                class Motion : Component
                {
                    IAudioService? audio;
                    string name;
                    public DataAssetReference<AudioEvent>? ClickEvent { get; set; }
                    public override void OnStart() { {{start}} }
                    public override void OnUpdate(float delta) { {{update}} }
                }
                """);
            File.WriteAllText(Path.Combine(root.FullName, "Click.dataasset"), $$"""{"__TypeId":"bbf8bc67-0f8c-4613-a263-5f1ee7cb28f6","Id":"event","EventName":"{{eventName}}"}""");
            File.WriteAllText(Path.Combine(root.FullName, "Scene.prefab"), assigned
                ? """{"Components":[{"__TypeId":"component","ClickEvent":{"AssetId":"event"}}]}"""
                : """{"Components":[{"__TypeId":"component","ClickEvent":null}]}""");
            var warnings = new AudioEventValidator().Validate(root.FullName).ToArray();
            Assert.Equal(warning ? 1 : 0, warnings.Length);
            var logger = Substitute.For<ILogger>();
            UserCodeValidation.Report(root.FullName, logger);
            Assert.Equal(warning ? 1 : 0, logger.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == "Log"));
            if (warning)
            {
                Assert.Contains("AUD0001", warnings[0].Message);
                Assert.EndsWith("Motion.cs", warnings[0].File);
                Assert.True(warnings[0].Line > 0);
            }
        }
        finally { root.Delete(true); }
    }

    /// <summary>Malformed reference IDs warn without aborting validation of editable assets.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    public void MalformedReferenceProducesAWarning(string assetId)
    {
        var root = Directory.CreateTempSubdirectory("audio-validation-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Motion.cs"), """[TypeId("component")] class Motion { IAudioService audio; public DataAssetReference<AudioEvent> ClickEvent { get; set; } void OnStart() { audio.Prepare(ClickEvent); } void Update() { audio.Post("ui.click"); } }""");
            File.WriteAllText(Path.Combine(root.FullName, "Broken.dataasset"), """{"__TypeId":42}""");
            File.WriteAllText(Path.Combine(root.FullName, "Scene.prefab"), "{\"__TypeId\":\"component\",\"ClickEvent\":{\"AssetId\":" + assetId + "}}");
            Assert.Contains("AUD0001", Assert.Single(new AudioEventValidator().Validate(root.FullName)).Message);
        }
        finally { root.Delete(true); }
    }
    /// <summary>Projects without assets require no audio wiring diagnostics.</summary>
    [Fact]
    public void MissingDirectoryHasNoWarnings()
        => Assert.Empty(new AudioEventValidator().Validate(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

    /// <summary>Source metadata identifies scene components whose type registration is generated.</summary>
    [Fact]
    public void UsesSourceMetadataWithoutAnExplicitAttribute()
    {
        var root = Directory.CreateTempSubdirectory("audio-validation-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Motion.cs"), "class Motion : Component { IAudioService audio; void Update() { audio.Post(\"ui.click\", Id, parameters); } }");
            File.WriteAllText(Path.Combine(root.FullName, "Motion.cs.meta"), """{"Id":"component"}""");
            File.WriteAllText(Path.Combine(root.FullName, "Scene.prefab"), """{"Components":[{"__TypeId":"component"}]}""");
            Assert.Contains("AUD0001", Assert.Single(new AudioEventValidator().Validate(root.FullName)).Message);
        }
        finally { root.Delete(true); }
    }

    /// <summary>Partially edited scene JSON warns instead of aborting editor compilation.</summary>
    [Fact]
    public void IncompleteSceneProducesAWarning()
    {
        var root = Directory.CreateTempSubdirectory("audio-validation-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Scene.prefab"), "{");
            Assert.Contains("AUD0002", Assert.Single(new AudioEventValidator().Validate(root.FullName)).Message);
        }
        finally { root.Delete(true); }
    }
}
