using System.Collections.Immutable;
using System.Text.Json;
using SyntaxCircus.RichContent;
using static SyntaxCircus.RichContent.RichTextNodeKind;

namespace RichContent.Sample;

/// <summary>
/// Named documents served at <c>/fixture/{name}</c> for the browser tests. Every call builds new objects, and
/// every fixture is valid under <see cref="RichContentRules"/>.
/// </summary>
public static class Fixtures
{
    /// <summary>The text <see cref="Replaced"/> puts in the node it changes.</summary>
    public const string ReplacementText = "Replaced by the host.";

    public static IReadOnlyList<string> Names { get; } = ["empty", "everything", "unknown", "post"];

    public static ContentDocument? Get(string name) => name switch
    {
        "empty" => ContentDocument.Empty("start"),
        "everything" => Everything(),
        "unknown" => Unknown(),
        "post" => Post(),
        _ => null,
    };

    /// <summary>A by-value copy in new objects, as a host that reloads the same stored document would supply.</summary>
    public static ContentDocument Copy(ContentDocument document) =>
        ContentDocumentSerializer.Deserialize(ContentDocumentSerializer.Serialize(document)).Document
        ?? throw new InvalidOperationException("The document did not survive serialization.");

    /// <summary>
    /// A copy in which only the first top-level node of the first rich text block differs: it is now a paragraph
    /// holding <see cref="ReplacementText"/>. An empty block gains that paragraph.
    /// </summary>
    public static ContentDocument Replaced(ContentDocument document)
    {
        var copy = Copy(document);
        var paragraph = P(T(ReplacementText));
        for (var i = 0; i < copy.Blocks.Length; i++)
        {
            if (copy.Blocks[i] is RichTextBlock rich)
            {
                var nodes = rich.Nodes.IsEmpty ? [paragraph] : rich.Nodes.SetItem(0, paragraph);
                return copy with { Blocks = copy.Blocks.SetItem(i, rich with { Nodes = nodes }) };
            }
        }

        return copy with { Blocks = copy.Blocks.Add(new RichTextBlock("replaced", [paragraph])) };
    }

    private static ContentDocument Everything() => new(ContentDocument.CurrentSchemaVersion,
    [
        new RichTextBlock("prose",
        [
            P(T("A plain paragraph with "), Strong(T("bold")), T(", "), Em(T("italic")), T(", "), Code("inline code"),
                T(" and "), Link("https://example.com/docs", T("a link")), T(".")),
            H(2, T("A heading")),
            H(3, T("A subheading with "), Code("code")),
            H(4, T("A small heading")),
            P(Strong(Em(T("Bold italic"))), T(" then "), Link("/relative/page", Strong(T("a bold link"))), T(".")),
            // Non-breaking spaces an author stored on purpose; editing must not turn them into ordinary spaces.
            P(T("Keep 10 km and  two together.")),
            // Adjacent text runs, which an untouched node must keep unmerged.
            P(T("Two adjacent "), T("text runs.")),
            P(T("Characters that look like markup: <b>not bold</b> & \"quotes\" \\ backslash.")),
            // Inline-form items, nesting an ordered list and, below it, a list with no ordering stored.
            Ul(
                Li(T("An item with inline content")),
                Li(T("An item with a nested list"),
                    Ol(
                        Li(T("Nested "), Strong(T("numbered"))),
                        Li(T("Deeper"), List(null, Li(T("Ordering not stored"))))))),
            // Paragraph-form items.
            Ol(
                Li(P(T("A paragraph item"))),
                Li(P(T("First paragraph")), P(T("Second paragraph"))),
                Li(P(T("A paragraph item with a nested list")), Ul(Li(P(T("Nested paragraph item")))))),
            List(null, Li(T("A top-level list with no ordering stored"))),
            Quote(P(T("A quoted paragraph.")), Ul(Li(T("A quoted list item"))), P(T("More of the quote."))),
            CodeBlock("csharp", "if (a < b && c > d)\n{\n    Console.WriteLine(\"<tag> & text\");\n}\n\tindented with a tab"),
            P(T("Writing between two cards.")),
            CodeBlock(null, "no language"),
            Image("mountains", "Mountains at dusk", "A caption", "wide"),
            Image("harbour", "A harbour", null, null),
            Table(
                Row(Cell(true, T("Name")), Cell(true, T("Value"))),
                Row(Cell(false, Strong(T("Bold cell"))), Cell(false, Link("https://example.com/cell", T("Linked cell")))),
                Row(Cell(null, Code("code")), Cell(null, T("")))),
            P(T("The last paragraph of the first block.")),
        ]),
        // A neighbouring rich text block: a separate surface, never merged with the one above.
        new RichTextBlock("more-prose", [P(T("A second rich text block, next to the first."))]),
        new CalloutBlock("callout-tip", CalloutTone.Tip, "A tip",
        [
            P(T("Callout text with "), Em(T("emphasis")), T(".")),
            CodeBlock("bash", "dotnet build"),
            Image("forest", "A forest", null, null),
            Table(Row(Cell(true, T("In a callout")))),
            Ul(Li(T("A callout list item"))),
        ]),
        new CalloutBlock("callout-warning", CalloutTone.Warning, null, [P(T("A second callout, directly after the first."))]),
        new GalleryBlock("gallery",
        [
            new GalleryImage(new MediaRef("mountains"), "Mountains", "First"),
            new GalleryImage(new MediaRef("harbour", "wide"), "Harbour", null),
        ], "A gallery"),
        new CtaBlock("cta", "Try it", "A short description.", "Read more", "https://example.com/more"),
        new ExtensionBlock("project", "project-card", JsonSerializer.SerializeToElement(new { name = "Sample project", stars = 3 })),
    ]);

    // Written as JSON, the way a newer writer would have stored it.
    private static ContentDocument Unknown()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "blocks": [
                {
                  "$block": "rich-text",
                  "id": "mixed",
                  "nodes": [
                    { "kind": "paragraph", "children": [ { "kind": "text", "text": "A paragraph this version fully understands." } ] },
                    { "kind": "paragraph", "futureAlign": "center", "children": [ { "kind": "text", "text": "A paragraph with an unknown property." } ] },
                    { "kind": "paragraph", "children": [ { "kind": "strong", "children": [ { "kind": "text", "text": "An unknown property deep inside.", "futureMark": { "colour": "red" } } ] } ] },
                    { "kind": "paragraph", "children": [ { "kind": "text", "text": "Another paragraph this version understands." } ] },
                    { "kind": "list", "ordered": false, "children": [ { "kind": "listItem", "futureChecked": true, "children": [ { "kind": "text", "text": "An item with an unknown property." } ] } ] },
                    { "kind": "codeBlock", "text": "let x = 1;", "futureHighlight": [ 1, 2 ] },
                    { "kind": "image", "media": { "id": "mountains" }, "alt": "Mountains", "futureFocalPoint": "top" },
                    { "kind": "table", "children": [ { "kind": "tableRow", "children": [ { "kind": "tableCell", "header": false, "futureSpan": 2, "children": [ { "kind": "text", "text": "A cell" } ] } ] } ] },
                    { "kind": "paragraph", "children": [ { "kind": "text", "text": "The closing paragraph." } ] }
                  ],
                  "futureBlockSetting": "keep me"
                },
                { "$block": "poll", "id": "future-block", "question": "A block this version has never heard of", "options": [ "a", "b" ] },
                {
                  "$block": "callout",
                  "id": "callout",
                  "tone": "info",
                  "nodes": [ { "kind": "paragraph", "children": [ { "kind": "text", "text": "A callout with an unknown property." } ] } ],
                  "futureIcon": "star"
                },
                {
                  "$block": "gallery",
                  "id": "gallery",
                  "images": [ { "media": { "id": "harbour" }, "alt": "Harbour", "futureCredit": "someone" } ]
                },
                { "$block": "cta", "id": "cta", "heading": "Next", "label": "Go", "url": "https://example.com/", "futureStyle": "primary" },
                { "$block": "extension", "id": "project", "type": "project-card", "data": { "name": "Sample", "future": [ 1 ] }, "futureLayout": 2 }
              ]
            }
            """;

        return ContentDocumentSerializer.Deserialize(json).Document
            ?? throw new InvalidOperationException("The unknown fixture is not readable.");
    }

    // About sixty top-level nodes, like a long article, with a few cards among the prose.
    private static ContentDocument Post()
    {
        static IEnumerable<RichTextNode> Section(int number) =>
        [
            H(2, T($"Section {number}")),
            P(T($"Section {number} opens with a paragraph that has "), Strong(T("bold text")), T(", "), Code("someCall()"),
                T(" and "), Link($"https://example.com/section/{number}", T("a link")), T(". It then carries on for a while, so that the page is long enough to scroll.")),
            P(T("A second paragraph with only plain text, long enough to wrap onto more than one line on an ordinary screen.")),
            H(3, T($"Details of section {number}")),
            Ul(Li(T("First point")), Li(T("Second point"), Ol(Li(T("A nested step"))))),
            P(T("A paragraph before the example.")),
            CodeBlock("csharp", $"var section = {number};\nConsole.WriteLine(section);"),
            Quote(P(T("A line worth quoting."))),
            P(Em(T("An emphasised aside.")), T(" Followed by ordinary text.")),
            P(T($"Section {number} ends here.")),
        ];

        return new(ContentDocument.CurrentSchemaVersion,
        [
            new RichTextBlock("part-one", [.. Enumerable.Range(1, 4).SelectMany(Section)]),
            new CalloutBlock("aside", CalloutTone.Info, "Worth knowing", [P(T("A callout in the middle of the article."))]),
            new RichTextBlock("part-two", [.. Enumerable.Range(5, 2).SelectMany(Section)]),
        ]);
    }

    private static RichTextNode Node(RichTextNodeKind kind, RichTextNode[] children) =>
        new(kind, Children: ImmutableArray.Create(children));

    private static RichTextNode T(string text) => new(Text, Text: text);

    private static RichTextNode Code(string text) => new(InlineCode, Text: text);

    private static RichTextNode Strong(params RichTextNode[] children) => Node(RichTextNodeKind.Strong, children);

    private static RichTextNode Em(params RichTextNode[] children) => Node(Emphasis, children);

    private static RichTextNode Link(string url, params RichTextNode[] children) => Node(RichTextNodeKind.Link, children) with { Url = url };

    private static RichTextNode P(params RichTextNode[] children) => Node(Paragraph, children);

    private static RichTextNode H(int level, params RichTextNode[] children) => Node(Heading, children) with { HeadingLevel = level };

    private static RichTextNode List(bool? ordered, params RichTextNode[] items) => Node(RichTextNodeKind.List, items) with { Ordered = ordered };

    private static RichTextNode Ul(params RichTextNode[] items) => List(false, items);

    private static RichTextNode Ol(params RichTextNode[] items) => List(true, items);

    private static RichTextNode Li(params RichTextNode[] children) => Node(ListItem, children);

    private static RichTextNode Quote(params RichTextNode[] children) => Node(RichTextNodeKind.Quote, children);

    private static RichTextNode CodeBlock(string? language, string text) => new(RichTextNodeKind.CodeBlock, Text: text, Language: language);

    private static RichTextNode Image(string id, string alt, string? caption, string? variant) =>
        new(RichTextNodeKind.Image, Media: new MediaRef(id, variant), Alt: alt, Caption: caption);

    private static RichTextNode Table(params RichTextNode[] rows) => Node(RichTextNodeKind.Table, rows);

    private static RichTextNode Row(params RichTextNode[] cells) => Node(TableRow, cells);

    private static RichTextNode Cell(bool? header, params RichTextNode[] children) => Node(TableCell, children) with { Header = header };
}
