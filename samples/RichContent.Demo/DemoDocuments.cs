using System.Collections.Immutable;
using System.Text.Json;
using SyntaxCircus.RichContent;
using static SyntaxCircus.RichContent.RichTextNodeKind;

namespace RichContent.Demo;

/// <summary>A document the demo can load.</summary>
public sealed record DemoSample(string Id, string Label, Func<ContentDocument> Create);

/// <summary>The made-up documents the demo offers. Every call to <see cref="DemoSample.Create"/> builds new objects.</summary>
public static class DemoDocuments
{
    public static IReadOnlyList<DemoSample> All { get; } =
    [
        new("everything", "Every block type", Everything),
        new("long", "A long post", Long),
        new("empty", "Empty", () => ContentDocument.Empty("start")),
        new("unsupported", "Content the editor does not support", Unsupported),
    ];

    private static ContentDocument Everything() => new(ContentDocument.CurrentSchemaVersion,
    [
        new RichTextBlock("intro",
        [
            P(T("A paragraph with "), Strong(T("bold")), T(", "), Em(T("italic")), T(", "), Code("inline code"),
                T(" and "), Link("https://example.com/guide", T("a link")), T(".")),
            H(2, T("A heading")),
            H(3, T("A subheading with "), Code("code")),
            H(4, T("A small heading")),
            Ul(
                Li(T("A bulleted item")),
                Li(T("An item with a nested list"), Ol(Li(T("First nested step")), Li(T("Second nested step"))))),
            Ol(Li(T("A numbered item")), Li(T("Another numbered item"))),
            Quote(P(T("A quoted paragraph."))),
            CodeBlock("csharp", "if (a < b && c > d)\n{\n    Console.WriteLine(\"hello\");\n}"),
            P(T("Writing between two cards.")),
            Image("lighthouse", "A lighthouse", "A caption under the picture", "wide"),
            Table(
                Row(Cell(true, T("Name")), Cell(true, T("Notes"))),
                Row(Cell(false, Strong(T("First row"))), Cell(false, Link("https://example.com/notes", T("Linked notes")))),
                Row(Cell(false, Code("second")), Cell(false, T("Plain notes")))),
            P(T("The last paragraph of this block.")),
        ]),
        new CalloutBlock("tip", CalloutTone.Tip, "A tip",
        [
            P(T("Callout text with "), Em(T("emphasis")), T(".")),
            CodeBlock("bash", "dotnet build"),
        ]),
        new CalloutBlock("warning", CalloutTone.Warning, null, [P(T("A warning without a title."))]),
        new GalleryBlock("gallery",
        [
            new GalleryImage(new MediaRef("orchard"), "An orchard", "First"),
            new GalleryImage(new MediaRef("workshop"), "A workshop", null),
        ], "A gallery"),
        new CtaBlock("cta", "Try it", "A short description of what happens next.", "Read more", "https://example.com/more"),
        new ExtensionBlock("project", "project-card", JsonSerializer.SerializeToElement(new { name = "Paper kite" })),
    ]);

    private static ContentDocument Long()
    {
        static IEnumerable<RichTextNode> Section(int number) =>
        [
            H(2, T($"Section {number}")),
            P(T($"Section {number} opens with a paragraph that has "), Strong(T("bold text")), T(", "), Code("someCall()"),
                T(" and "), Link($"https://example.com/section/{number}", T("a link")),
                T(". It carries on for a while, so that the page is long enough to scroll.")),
            P(T("A second paragraph with only plain text, long enough to wrap onto more than one line on an ordinary screen.")),
            H(3, T($"Details of section {number}")),
            Ul(Li(T("First point")), Li(T("Second point"), Ol(Li(T("A nested step"))))),
            CodeBlock("csharp", $"var section = {number};\nConsole.WriteLine(section);"),
            Quote(P(T("A line worth quoting."))),
            P(Em(T("An aside.")), T($" Section {number} ends here.")),
        ];

        return new(ContentDocument.CurrentSchemaVersion,
        [
            new RichTextBlock("first-half", [.. Enumerable.Range(1, 4).SelectMany(Section)]),
            new CalloutBlock("aside", CalloutTone.Info, "Worth knowing", [P(T("A callout in the middle of the post."))]),
            new RichTextBlock("second-half", [.. Enumerable.Range(5, 4).SelectMany(Section)]),
        ]);
    }

    // Written as JSON, the way a newer version of the format might have stored it.
    private static ContentDocument Unsupported()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "blocks": [
                {
                  "$block": "rich-text",
                  "id": "mixed",
                  "nodes": [
                    { "kind": "paragraph", "children": [ { "kind": "text", "text": "A paragraph the editor fully understands." } ] },
                    { "kind": "paragraph", "futureAlign": "center", "children": [ { "kind": "text", "text": "A paragraph with an unknown property." } ] },
                    { "kind": "codeBlock", "text": "let x = 1;", "futureHighlight": [ 1, 2 ] },
                    { "kind": "paragraph", "children": [ { "kind": "text", "text": "Another paragraph the editor understands." } ] }
                  ],
                  "futureSetting": "keep me"
                },
                { "$block": "poll", "id": "poll", "question": "A block type the editor has never heard of", "options": [ "a", "b" ] },
                { "$block": "cta", "id": "cta", "heading": "Next", "label": "Go", "url": "https://example.com/", "futureStyle": "primary" }
              ]
            }
            """;

        return ContentDocumentSerializer.Deserialize(json).Document
            ?? throw new InvalidOperationException("The unsupported sample is not readable.");
    }

    private static RichTextNode Node(RichTextNodeKind kind, RichTextNode[] children) =>
        new(kind, Children: [.. children]);

    private static RichTextNode T(string text) => new(Text, Text: text);

    private static RichTextNode Code(string text) => new(InlineCode, Text: text);

    private static RichTextNode Strong(params RichTextNode[] children) => Node(RichTextNodeKind.Strong, children);

    private static RichTextNode Em(params RichTextNode[] children) => Node(Emphasis, children);

    private static RichTextNode Link(string url, params RichTextNode[] children) => Node(RichTextNodeKind.Link, children) with { Url = url };

    private static RichTextNode P(params RichTextNode[] children) => Node(Paragraph, children);

    private static RichTextNode H(int level, params RichTextNode[] children) => Node(Heading, children) with { HeadingLevel = level };

    private static RichTextNode Ul(params RichTextNode[] items) => Node(List, items) with { Ordered = false };

    private static RichTextNode Ol(params RichTextNode[] items) => Node(List, items) with { Ordered = true };

    private static RichTextNode Li(params RichTextNode[] children) => Node(ListItem, children);

    private static RichTextNode Quote(params RichTextNode[] children) => Node(RichTextNodeKind.Quote, children);

    private static RichTextNode CodeBlock(string? language, string text) => new(RichTextNodeKind.CodeBlock, Text: text, Language: language);

    private static RichTextNode Image(string id, string alt, string? caption, string? variant) =>
        new(RichTextNodeKind.Image, Media: new MediaRef(id, variant), Alt: alt, Caption: caption);

    private static RichTextNode Table(params RichTextNode[] rows) => Node(RichTextNodeKind.Table, rows);

    private static RichTextNode Row(params RichTextNode[] cells) => Node(TableRow, cells);

    private static RichTextNode Cell(bool? header, params RichTextNode[] children) => Node(TableCell, children) with { Header = header };
}
