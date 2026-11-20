using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>Recursive editor for one <see cref="RichTextNode"/> and its children.</summary>
public partial class RichTextNodeEditor
{
    private static readonly string[] LanguageOptions =
        ["csharp", "javascript", "typescript", "html", "xml", "css", "sql", "powershell", "python", "java", "cpp", "fsharp", "php", "json", "bash", "yaml"];

    [Parameter, EditorRequired] public RichTextNode Node { get; set; } = default!;

    [Parameter] public EventCallback<RichTextNode> NodeChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public int Depth { get; set; } = 1;

    [Parameter] public int MaxDepth { get; set; } = RichContentLimits.Default.Depth;

    private ImmutableArray<RichTextNode> Children => Node.Children.IsDefault ? [] : Node.Children;

    // Table cells stay in step with the other rows, so they are edited through the table's own add/remove column.
    private bool CellsLocked => Node.Kind == RichTextNodeKind.TableRow;

    private bool CanRemoveColumn => Children.Length > 0 && Children.All(row => row.Children.Length > 1);

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

    private Task AddRow()
    {
        var columns = Children.Length > 0 ? Math.Max(1, Children[0].Children.Length) : 2;
        var row = new RichTextNode(
            RichTextNodeKind.TableRow,
            Children: [.. Enumerable.Range(0, columns).Select(_ => RichTextEditorOperations.Create(RichTextNodeKind.TableCell))]);
        return Update(Node with { Children = RichTextEditorOperations.Append(Children, row) });
    }

    private Task AddColumn() =>
        Update(Node with
        {
            Children =
            [
                .. Children.Select(row =>
                {
                    var cells = row.Children.IsDefault ? [] : row.Children;
                    var header = cells.Length > 0 && cells[0].Header == true;
                    return row with { Children = cells.Add(RichTextEditorOperations.Create(RichTextNodeKind.TableCell) with { Header = header }) };
                }),
            ],
        });

    private Task RemoveColumn() =>
        CanRemoveColumn
            ? Update(Node with { Children = [.. Children.Select(row => row with { Children = row.Children.RemoveAt(row.Children.Length - 1) })] })
            : Task.CompletedTask;
}
