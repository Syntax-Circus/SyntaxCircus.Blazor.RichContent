using System.Collections.Immutable;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>Pure operations over rich-text node lists, driving the block editor.</summary>
public static class RichTextEditorOperations
{
    private static readonly RichTextNodeKind[] Inline =
        [RichTextNodeKind.Text, RichTextNodeKind.Emphasis, RichTextNodeKind.Strong, RichTextNodeKind.InlineCode, RichTextNodeKind.Link];

    private static readonly RichTextNodeKind[] InlineWithoutLink =
        [RichTextNodeKind.Text, RichTextNodeKind.Emphasis, RichTextNodeKind.Strong, RichTextNodeKind.InlineCode];

    private static readonly RichTextNodeKind[] ListItemChildren = [.. Inline, RichTextNodeKind.Paragraph, RichTextNodeKind.List];

    /// <summary>A minimal node of the given kind that satisfies the content rules (an image has no media until one is picked).</summary>
    public static RichTextNode Create(RichTextNodeKind kind) => kind switch
    {
        RichTextNodeKind.Text => new(kind, Text: ""),
        RichTextNodeKind.InlineCode => new(kind, Text: ""),
        RichTextNodeKind.CodeBlock => new(kind, Text: ""),
        RichTextNodeKind.Image => new(kind, Alt: ""),
        RichTextNodeKind.List => new(kind, Ordered: false, Children: [Create(RichTextNodeKind.ListItem)]),
        RichTextNodeKind.ListItem => new(kind, Children: [Create(RichTextNodeKind.Paragraph)]),
        RichTextNodeKind.Quote => new(kind, Children: [Create(RichTextNodeKind.Paragraph)]),
        RichTextNodeKind.Link => new(kind, Url: "https://", Children: [Create(RichTextNodeKind.Text)]),
        RichTextNodeKind.Heading => new(kind, HeadingLevel: 2, Children: [Create(RichTextNodeKind.Text)]),
        RichTextNodeKind.Table => new(kind, Children: [Row(header: true), Row(header: false)]),
        RichTextNodeKind.TableRow => Row(header: false),
        RichTextNodeKind.TableCell => Cell(header: false),
        _ => new(kind, Children: [Create(RichTextNodeKind.Text)]),
    };

    private static RichTextNode Row(bool header) =>
        new(RichTextNodeKind.TableRow, Children: [Cell(header), Cell(header)]);

    private static RichTextNode Cell(bool header) =>
        new(RichTextNodeKind.TableCell, Header: header, Children: [Create(RichTextNodeKind.Text)]);

    public static ImmutableArray<RichTextNode> Append(ImmutableArray<RichTextNode> nodes, RichTextNode node) =>
        (nodes.IsDefault ? [] : nodes).Add(node);

    /// <summary>Removes the node at <paramref name="index"/>; an out-of-range index leaves the list unchanged.</summary>
    public static ImmutableArray<T> Remove<T>(ImmutableArray<T> nodes, int index)
    {
        nodes = nodes.IsDefault ? [] : nodes;
        return index < 0 || index >= nodes.Length ? nodes : nodes.RemoveAt(index);
    }

    /// <summary>Moves the node by <paramref name="delta"/> positions, clamping at either end.</summary>
    public static ImmutableArray<T> Move<T>(ImmutableArray<T> nodes, int index, int delta)
    {
        nodes = nodes.IsDefault ? [] : nodes;
        if (index < 0 || index >= nodes.Length)
        {
            return nodes;
        }

        var target = Math.Clamp(index + delta, 0, nodes.Length - 1);
        if (target == index)
        {
            return nodes;
        }

        var item = nodes[index];
        return nodes.RemoveAt(index).Insert(target, item);
    }

    /// <summary>Appends a row with as many cells as the first row (default arrays count as empty; at least one cell).</summary>
    public static RichTextNode AddTableRow(RichTextNode table)
    {
        var rows = ChildrenOf(table);
        var columns = rows.Length > 0 ? Math.Max(1, ChildrenOf(rows[0]).Length) : 2;
        var row = new RichTextNode(
            RichTextNodeKind.TableRow,
            Children: [.. Enumerable.Range(0, columns).Select(_ => Create(RichTextNodeKind.TableCell))]);
        return table with { Children = rows.Add(row) };
    }

    /// <summary>Appends a cell to every row, a header cell when that row's first cell is a header.</summary>
    public static RichTextNode AddTableColumn(RichTextNode table) =>
        table with
        {
            Children =
            [
                .. ChildrenOf(table).Select(row =>
                {
                    var cells = ChildrenOf(row);
                    var header = cells.Length > 0 && cells[0].Header == true;
                    return row with { Children = cells.Add(Create(RichTextNodeKind.TableCell) with { Header = header }) };
                }),
            ],
        };

    /// <summary>True when every row has more than one cell (and there is at least one row).</summary>
    public static bool CanRemoveTableColumn(RichTextNode table)
    {
        var rows = ChildrenOf(table);
        return rows.Length > 0 && rows.All(row => ChildrenOf(row).Length > 1);
    }

    /// <summary>Removes the last cell of every row; unchanged when <see cref="CanRemoveTableColumn"/> is false.</summary>
    public static RichTextNode RemoveTableColumn(RichTextNode table) =>
        CanRemoveTableColumn(table)
            ? table with { Children = [.. ChildrenOf(table).Select(row => row with { Children = ChildrenOf(row).RemoveAt(ChildrenOf(row).Length - 1) })] }
            : table;

    private static ImmutableArray<RichTextNode> ChildrenOf(RichTextNode node) => node.Children.IsDefault ? [] : node.Children;

    /// <summary>
    /// Kinds that may be added directly inside <paramref name="parent"/>, mirroring <c>RichContentRules</c>.
    /// A link excludes <see cref="RichTextNodeKind.Link"/> because links cannot nest.
    /// </summary>
    public static IReadOnlyList<RichTextNodeKind> AllowedChildren(RichTextNodeKind parent) => parent switch
    {
        RichTextNodeKind.Link => InlineWithoutLink,
        RichTextNodeKind.Paragraph or RichTextNodeKind.Heading or RichTextNodeKind.Emphasis
            or RichTextNodeKind.Strong or RichTextNodeKind.TableCell => Inline,
        RichTextNodeKind.List => [RichTextNodeKind.ListItem],
        RichTextNodeKind.ListItem => ListItemChildren,
        RichTextNodeKind.Quote => [RichTextNodeKind.Paragraph, RichTextNodeKind.List],
        RichTextNodeKind.Table => [RichTextNodeKind.TableRow],
        RichTextNodeKind.TableRow => [RichTextNodeKind.TableCell],
        _ => [],
    };

    public static string Label(RichTextNodeKind kind) => kind switch
    {
        RichTextNodeKind.Emphasis => "Italic",
        RichTextNodeKind.Strong => "Bold",
        RichTextNodeKind.InlineCode => "Inline code",
        RichTextNodeKind.ListItem => "List item",
        RichTextNodeKind.CodeBlock => "Code block",
        RichTextNodeKind.TableRow => "Table row",
        RichTextNodeKind.TableCell => "Table cell",
        _ => kind.ToString(),
    };
}
