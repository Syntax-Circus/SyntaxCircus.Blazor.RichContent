using System.Text.Json;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>
/// Edits a code block in Write mode. Its text is delivered as a writing surface's is, in small messages through this
/// package's script, so it needs interactive rendering. <see cref="NodeChanged"/> is raised only for the author's
/// edits.
/// </summary>
public partial class CodeCard
{
    internal const string ModulePath = "./_content/SyntaxCircus.Blazor.RichContent/surface/code.js";

    // The latest node: what was passed in, with every edit since.
    private RichTextNode? _node;
    private RichTextNode? _parameter;

    // The text the browser was last given or has sent since.
    private string _text = "";
    private int _generation;

    [Parameter, EditorRequired] public RichTextNode Node { get; set; } = default!;

    /// <summary>
    /// Raised for each edit. A handler must store the node before its first incomplete await, so that a click made
    /// after typing sees it.
    /// </summary>
    [Parameter] public EventCallback<RichTextNode> NodeChanged { get; set; }

    private RichTextNode Current => _node ?? Node;

    private protected override string ScriptPath => ModulePath;

    private protected override string FocusFunction => "focus";

    private protected override int ContentGeneration => _generation;

    protected override void OnParametersSet()
    {
        // The same object as last time may be older than the edits made since.
        if (!ReferenceEquals(Node, _parameter))
        {
            _parameter = _node = Node;
        }
    }

    private protected override bool TakeParameters() => !string.Equals(Current.Text ?? "", _text, StringComparison.Ordinal);

    private protected override object ResetContent()
    {
        _text = Current.Text ?? "";
        _generation++;
        return _text;
    }

    private protected override ReceivedEdit ReadEdit(string payload)
    {
        var patch = JsonSerializer.Deserialize<CodePatch>(payload, JsonSerializerOptions.Web)
            ?? throw new FormatException("There is no patch.");
        return new(patch.Generation, () => Apply(patch));
    }

    private Func<Task>? Apply(CodePatch patch)
    {
        if (patch.Generation != _generation || patch.Changed is not { Length: <= 1 } changed)
        {
            throw new FormatException("The patch is incomplete or for another generation.");
        }

        if (changed.Length == 0)
        {
            return null;
        }

        var text = changed[0]?.Node ?? throw new FormatException("The patch has no text.");
        if (string.Equals(text, _text, StringComparison.Ordinal))
        {
            return null;
        }

        _text = text;
        var node = _node = Current with { Text = text };
        return () => NodeChanged.InvokeAsync(node);
    }

    // This composes on the latest text, which a render may not yet have passed back.
    private Task LanguageChanged(ChangeEventArgs e)
    {
        var language = e.Value?.ToString();
        _node = Current with { Language = string.IsNullOrEmpty(language) ? null : language };
        return NodeChanged.InvokeAsync(_node);
    }

    // The shape of the script's patches: the one entry is the whole text.
    private sealed record CodePatch(int Generation, CodeEntry?[]? Changed);

    private sealed record CodeEntry(string? Node);
}
