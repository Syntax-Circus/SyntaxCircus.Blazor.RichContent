using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>Recursive editor for one <see cref="RichTextNode"/> and its children.</summary>
public partial class RichTextNodeEditor
{
    private static readonly string[] LanguageOptions =
        ["csharp", "javascript", "typescript", "html", "xml", "css", "sql", "powershell", "python", "java", "cpp", "fsharp", "php"];

    private IEnumerable<string> LanguageChoices => LanguageChoicesFor(Node.Language);

    // Aliases (cs, js, ...) are accepted by the highlighter but listed once, under their canonical name.
    internal static IEnumerable<string> LanguageChoicesFor(string? language) =>
        string.IsNullOrEmpty(language) || LanguageOptions.Contains(language) ? LanguageOptions : [.. LanguageOptions, language];

    [Parameter, EditorRequired] public RichTextNode Node { get; set; } = default!;

    [Parameter] public EventCallback<RichTextNode> NodeChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public int Depth { get; set; } = 1;

    [Parameter] public int MaxDepth { get; set; } = RichContentLimits.Default.Depth;

    // Rendering is skipped when the node (an immutable record, so reference equality suffices) and the display
    // parameters are unchanged, so editing one node does not re-render its siblings. Callbacks always read the live parameters.
    private RichTextNode? _seenNode;
    private bool _seenDisabled;
    private int _seenDepth;
    private int _seenMaxDepth;
    private bool _shouldRender = true;

    protected override void OnParametersSet()
    {
        _shouldRender = !ReferenceEquals(Node, _seenNode) || Disabled != _seenDisabled || Depth != _seenDepth || MaxDepth != _seenMaxDepth;
        _seenNode = Node;
        _seenDisabled = Disabled;
        _seenDepth = Depth;
        _seenMaxDepth = MaxDepth;
    }

    protected override bool ShouldRender() => _shouldRender;

    private ImmutableArray<RichTextNode> Children => Node.Children.IsDefault ? [] : Node.Children;

    // Table cells stay in step with the other rows, so they are edited through the table's own add/remove column.
    private bool CellsLocked => Node.Kind == RichTextNodeKind.TableRow;

    private bool CanRemoveColumn => RichTextEditorOperations.CanRemoveTableColumn(Node);

    private Task Update(RichTextNode node) => NodeChanged.InvokeAsync(node);

    private Task TextChanged(ChangeEventArgs e) => Update(Node with { Text = e.Value?.ToString() ?? "" });

    private Task UrlChanged(ChangeEventArgs e) => Update(Node with { Url = e.Value?.ToString() ?? "" });

    private Task AltChanged(ChangeEventArgs e) => Update(Node with { Alt = e.Value?.ToString() ?? "" });

    private Task CaptionChanged(ChangeEventArgs e) =>
        Update(Node with { Caption = string.IsNullOrEmpty(e.Value?.ToString()) ? null : e.Value!.ToString() });

    private Task LanguageChanged(ChangeEventArgs e) =>
        Update(Node with { Language = string.IsNullOrEmpty(e.Value?.ToString()) ? null : e.Value!.ToString() });

    private Task MediaChanged(MediaRef? media) => Update(Node with { Media = media });

    private Task OrderedChanged(ChangeEventArgs e) => Update(Node with { Ordered = e.Value is true });

    private Task LevelChanged(ChangeEventArgs e) =>
        int.TryParse(e.Value?.ToString(), out var level) ? Update(Node with { HeadingLevel = level }) : Task.CompletedTask;

    private Task Replace(int index, RichTextNode child) => Update(Node with { Children = Children.SetItem(index, child) });

    private Task Move(int index, int delta) => Update(Node with { Children = RichTextEditorOperations.Move(Children, index, delta) });

    private Task Remove(int index) => Update(Node with { Children = RichTextEditorOperations.Remove(Children, index) });

    private Task Add(RichTextNodeKind kind) =>
        Update(Node with { Children = RichTextEditorOperations.Append(Children, RichTextEditorOperations.Create(kind)) });

    private Task AddRow() => Update(RichTextEditorOperations.AddTableRow(Node));

    private Task AddColumn() => Update(RichTextEditorOperations.AddTableColumn(Node));

    private Task RemoveColumn() => Update(RichTextEditorOperations.RemoveTableColumn(Node));
}
