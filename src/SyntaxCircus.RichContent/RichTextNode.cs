using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SyntaxCircus.RichContent;

public enum RichTextNodeKind
{
    Paragraph, Heading, Text, Emphasis, Strong, InlineCode, Link, List, ListItem,
    Quote, CodeBlock, Image, Table, TableRow, TableCell,
}

/// <summary>
/// One node of rich text. Which optional fields apply depends on <see cref="Kind"/>.
/// <see cref="Children"/> is <c>default</c> (not empty) when absent; use <c>IsDefaultOrEmpty</c>.
/// </summary>
public sealed record RichTextNode(
    RichTextNodeKind Kind,
    string? Text = null,
    string? Url = null,
    int? HeadingLevel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<RichTextNode> Children = default,
    bool? Ordered = null,
    string? Language = null,
    MediaRef? Media = null,
    string? Alt = null,
    string? Caption = null,
    bool? Header = null);
