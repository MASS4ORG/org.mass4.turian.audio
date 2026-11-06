using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Turian.Editor.Core;

[assembly: UserCodeValidator(typeof(Turian.Audio.Editor.AudioEventValidator))]

namespace Turian.Audio.Editor;

/// <summary>Warns when a scene component posts audio without verifiable startup preparation and asset wiring.</summary>
public sealed class AudioEventValidator : IUserCodeValidator
{
    /// <inheritdoc/>
    public IEnumerable<UserCodeValidationWarning> Validate(string assetsDirectory)
    {
        if (!Directory.Exists(assetsDirectory)) { yield break; }
        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(assetsDirectory, "*.dataasset", SearchOption.AllDirectories))
        {
            if (Read(path) is not { ValueKind: JsonValueKind.Object } root) { continue; }
            if (root.TryGetProperty("__TypeId", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "bbf8bc67-0f8c-4613-a263-5f1ee7cb28f6" &&
                root.TryGetProperty("EventName", out var name) && name.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(name.GetString()) && root.TryGetProperty("Id", out var id) &&
                id.ValueKind == JsonValueKind.String)
            {
                events[id.GetString()!] = name.GetString()!;
            }
        }
        var instances = new List<JsonElement>();
        foreach (var path in Directory.EnumerateFiles(assetsDirectory, "*.prefab", SearchOption.AllDirectories))
        {
            if (Read(path) is { } root) { Collect(root, instances); }
            else
            {
                yield return new(path, 1, "AUD0002: Scene JSON is incomplete; audio event wiring could not be validated.");
            }
        }
        foreach (var path in Directory.EnumerateFiles(assetsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
            var metadata = Read(path + ".meta");
            var metadataId = metadata is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("Id", out var sourceId) &&
                sourceId.ValueKind == JsonValueKind.String
                ? sourceId.GetString() : null;
            var first = true;
            foreach (var type in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var typeId = type.AttributeLists.SelectMany(static list => list.Attributes)
                    .FirstOrDefault(static attribute => attribute.Name.ToString().EndsWith("TypeId", StringComparison.Ordinal) ||
                        attribute.Name.ToString().EndsWith("TypeIdAttribute", StringComparison.Ordinal))
                    ?.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
                var sourceMetaId = first ? metadataId : null;
                first = false;
                var attached = instances.Where(instance => instance.TryGetProperty("__TypeId", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    (typeId is not null && string.Equals(id.GetString(), typeId.Token.ValueText, StringComparison.OrdinalIgnoreCase) ||
                        sourceMetaId is not null && string.Equals(id.GetString(), sourceMetaId, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (attached.Length == 0) { continue; }
                var audioFields = type.Members.OfType<FieldDeclarationSyntax>()
                    .Where(static field => field.Declaration.Type.ToString().Contains("IAudioService", StringComparison.Ordinal) ||
                        field.Declaration.Type.ToString().Contains("AudioSource", StringComparison.Ordinal))
                    .SelectMany(static field => field.Declaration.Variables.Select(static variable => variable.Identifier.ValueText))
                    .ToHashSet(StringComparer.Ordinal);
                var references = type.Members.OfType<PropertyDeclarationSyntax>()
                    .Where(static property => property.Type.ToString().Contains("AudioEvent", StringComparison.Ordinal))
                    .Select(static property => property.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
                var startup = type.Members.OfType<MethodDeclarationSyntax>()
                    .Where(static method => method.Identifier.ValueText == "OnStart")
                    .SelectMany(static method => method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    .Where(invocation => Name(invocation) == "Prepare" && audioFields.Contains(Receiver(invocation)))
                    .ToArray();
                foreach (var post in type.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(invocation => Name(invocation) == "Post" && audioFields.Contains(Receiver(invocation))))
                {
                    var argument = post.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    var prepared = startup.Where(prepare => Receiver(prepare) == Receiver(post)).Any(prepare =>
                    {
                        var assignment = prepare.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
                        var sameResult = argument is IdentifierNameSyntax identifier &&
                            assignment?.Left.ToString() == identifier.Identifier.ValueText;
                        var property = prepare.ArgumentList.Arguments.FirstOrDefault()?.Expression
                            .DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                            .Select(static node => node.Identifier.ValueText).FirstOrDefault(references.Contains);
                        if (property is null) { return false; }
                        return attached.All(instance => instance.TryGetProperty(property, out var reference) &&
                            reference.ValueKind == JsonValueKind.Object &&
                            reference.TryGetProperty("AssetId", out var id) && id.ValueKind == JsonValueKind.String &&
                            events.TryGetValue(id.GetString()!, out var eventName) &&
                            (sameResult || argument is LiteralExpressionSyntax literal && literal.Token.ValueText == eventName));
                    });
                    if (!prepared)
                    {
                        yield return new(path, tree.GetLineSpan(post.Span).StartLinePosition.Line + 1,
                            "AUD0001: Cannot verify this audio Post is prepared in OnStart and wired to an AudioEvent in every scene instance. Prepare an assigned event reference at scene startup and post the returned name.");
                    }
                }
            }
        }
    }

    static JsonElement? Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new()
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    static string Name(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    static string Receiver(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Expression.ToString().Replace("this.", string.Empty),
        MemberBindingExpressionSyntax when invocation.Parent is ConditionalAccessExpressionSyntax conditional =>
            conditional.Expression.ToString().Replace("this.", string.Empty),
        _ => string.Empty,
    };

    static void Collect(JsonElement element, List<JsonElement> instances)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("__TypeId", out _)) { instances.Add(element.Clone()); }
            foreach (var property in element.EnumerateObject()) { Collect(property.Value, instances); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) { Collect(child, instances); }
        }
    }
}
