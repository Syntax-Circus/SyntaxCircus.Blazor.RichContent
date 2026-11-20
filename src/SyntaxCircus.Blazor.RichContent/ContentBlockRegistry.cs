using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.Blazor.RichContent.Editing;

namespace SyntaxCircus.Blazor.RichContent;

public sealed record ContentBlockRegistration(string Type, string Label, Type Renderer, Type? Editor, Func<JsonElement> CreateDefault);

/// <summary>App-defined extension blocks: block type to renderer component (with a <c>JsonElement Data</c> parameter) and optional editor.</summary>
public sealed partial class ContentBlockRegistry
{
    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex TypePattern();

    private readonly Dictionary<string, ContentBlockRegistration> _registrations = new(StringComparer.Ordinal);

    public IEnumerable<ContentBlockRegistration> Registrations => _registrations.Values;

    /// <param name="type">Block type name, 1 to 40 characters of lowercase letters, digits and hyphens.</param>
    /// <param name="label">Label shown in the editor's insert menu.</param>
    /// <param name="renderer">A component with a <c>JsonElement Data</c> parameter.</param>
    /// <param name="editor">Optional component deriving <see cref="ExtensionBlockEditorBase"/>.</param>
    /// <param name="createDefault">Creates the data of a newly inserted block; defaults to <c>{}</c>.</param>
    public ContentBlockRegistry Add(string type, string label, Type renderer, Type? editor = null, Func<JsonElement>? createDefault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(renderer);
        if (!TypePattern().IsMatch(type))
        {
            throw new ArgumentException("A block type must be 1 to 40 characters of lowercase letters, digits and hyphens.", nameof(type));
        }

        if (!typeof(IComponent).IsAssignableFrom(renderer))
        {
            throw new ArgumentException($"{renderer.Name} must be a component.", nameof(renderer));
        }

        if (editor is not null && !typeof(ExtensionBlockEditorBase).IsAssignableFrom(editor))
        {
            throw new ArgumentException($"{editor.Name} must derive from {nameof(ExtensionBlockEditorBase)}.", nameof(editor));
        }

        if (_registrations.ContainsKey(type))
        {
            throw new InvalidOperationException($"A block type '{type}' is already registered.");
        }

        _registrations[type] = new ContentBlockRegistration(type, label, renderer, editor, createDefault ?? EmptyObject);
        return this;
    }

    public bool TryGet(string type, [MaybeNullWhen(false)] out ContentBlockRegistration registration)
    {
        if (type is not null && _registrations.TryGetValue(type, out var found))
        {
            registration = found;
            return true;
        }

        registration = null;
        return false;
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
