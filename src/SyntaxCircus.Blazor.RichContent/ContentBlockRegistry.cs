using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SyntaxCircus.Blazor.RichContent;

public sealed record ContentBlockRegistration(string Type, string Label, Type Renderer, Type? Editor, Func<JsonElement> CreateDefault);

/// <summary>App-defined extension blocks: block type to renderer component (with a <c>JsonElement Data</c> parameter) and optional editor.</summary>
public sealed class ContentBlockRegistry
{
    private readonly Dictionary<string, ContentBlockRegistration> _registrations = new(StringComparer.Ordinal);

    public IEnumerable<ContentBlockRegistration> Registrations => _registrations.Values;

    public ContentBlockRegistry Add(string type, string label, Type renderer, Type? editor = null, Func<JsonElement>? createDefault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(renderer);
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
