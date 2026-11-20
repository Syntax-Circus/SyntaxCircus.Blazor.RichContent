# SyntaxCircus.Blazor.RichContent

Block-based rich content for Blazor apps, shipped as two NuGet packages that share one version:

- **SyntaxCircus.RichContent** - the framework-free document model (`ContentDocument`, blocks, rich text nodes), the
  versioned JSON serializer and the validation rules (`RichContentRules`, `RichContentLimits`). Unknown block types
  survive a read/write round trip untouched.
- **SyntaxCircus.Blazor.RichContent** - Blazor components for server-side rendering (`ContentDocumentView`) and
  editing (`ContentDocumentEditor`) that content. It references `SyntaxCircus.RichContent`.

Use only the first package if you just need to store, read or validate documents (for example in an API or a worker).
Add the second in the Blazor app that renders or edits them. There is no JavaScript interop, and component CSS is
scoped (`.razor.css`), so nothing leaks into your global styles.

## Install

```
dotnet add package SyntaxCircus.RichContent
dotnet add package SyntaxCircus.Blazor.RichContent
```

Target framework: `net10.0`.

## Quick start: the document model

```csharp
using SyntaxCircus.RichContent;

var json = ContentDocumentSerializer.Serialize(ContentDocument.Empty("b1"));
var result = ContentDocumentSerializer.Deserialize(json); // result.Document or result.Error

var errors = RichContentRules.Validate(result.Document!); // IReadOnlyList<RichContentError>
```

Built-in blocks: rich text, gallery, callout (info / tip / warning), call to action, and app-defined extension blocks.
Rich text nodes: paragraph, heading, emphasis, strong, inline code, link, ordered/unordered list, quote, code block,
image, and table.

## Registration

```csharp
using System.Text.Json;
using SyntaxCircus.Blazor.RichContent;

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddSingleton<IRichContentMediaResolver, MyMediaResolver>(); // required
builder.Services.AddRichContent(
    blocks => blocks.Add("project-card", "Project card",
        renderer: typeof(ProjectCardView),
        editor: typeof(ProjectCardEditor),
        createDefault: () => JsonDocument.Parse("""{"name":"New project"}""").RootElement.Clone()),
    editor => editor.MediaPicker = typeof(MyMediaPicker));
```

`AddRichContent` registers the block registry, the editor options and a default `ICodeHighlighter`
(`TryAdd`, so you can register your own first). Both arguments are optional.

Calling `AddRichContent` after `AddRazorComponents()` is recommended but not required. `AddRichContent` registers a
fallback `IErrorBoundaryLogger` (via `TryAdd`) so hosts that use a bare `HtmlRenderer` without Razor components, such as
RSS or email rendering, still work when an extension block fails. The only consequence of the order is which
`IErrorBoundaryLogger` is used: whichever was registered first wins, so with `AddRazorComponents()` first, its logger is
the one used.

Then render and edit:

```razor
<ContentDocumentEditor @bind-Document="_document" ErrorsChanged="OnErrors" />

<ContentDocumentView Document="_document" />
```

(`using SyntaxCircus.Blazor.RichContent.Editing` and `.Rendering` for the two components.)

- `ContentDocumentView` server-renders a stored document. Blocks that fail validation are skipped (and logged as a warning with block ids and error codes only); the other blocks still render, and document-level errors do not suppress any block.
- `ContentDocumentEditor` is a controlled component: every edit raises `DocumentChanged` and you feed the new
  document back in (`@bind-Document` does this). `ErrorsChanged` reports the current validation errors so you can
  block saving. Both accept an optional `Limits` parameter; the editor also takes `Label` and `Disabled`.

### Media: `IRichContentMediaResolver` (required)

Images and galleries store an opaque `MediaRef(Id, Variant?)`. You tell the package how to turn one into a URL:

```csharp
public sealed class MyMediaResolver : IRichContentMediaResolver
{
    public ResolvedMedia? Resolve(MediaRef media) =>
        new ResolvedMedia("/media/" + Uri.EscapeDataString(media.Id), Width: 1200, Height: 800);
}
```

`ResolvedMedia` carries `Src` plus optional `SrcSet`, `Sizes`, `Width` and `Height`. Return `null` for media that
cannot be resolved.

### Media picker: `MediaPickerBase`

The editor does not know your media library. Supply a component that derives from `MediaPickerBase` and register it
with `editor.MediaPicker = typeof(MyMediaPicker)`. Without a picker, adding images and galleries is disabled.

```razor
@inherits SyntaxCircus.Blazor.RichContent.Editing.MediaPickerBase

<button type="button" disabled="@Disabled" @onclick="Pick">Choose image</button>

@code {
    private Task Pick() => ValueChanged.InvokeAsync(new MediaRef("image-123"));
}
```

`MediaPickerBase` provides `Value`, `ValueChanged` (a `MediaRef?`; `null` clears) and `Disabled`.

## Extension blocks

An extension block is an app-defined block, stored with a `type` (matching `[a-z0-9-]{1,40}`) and a free-form `data`
object. Register one with `ContentBlockRegistry.Add(type, label, renderer, editor?, createDefault?)`:

- **Renderer** (required) - a component with a `[Parameter] public JsonElement Data { get; set; }`.
- **Editor** (optional) - a component deriving `ExtensionBlockEditorBase`, which provides `Data`, `DataChanged`
  (`EventCallback<JsonElement>`) and `Disabled`.
- **Default data** (optional) - `createDefault` supplies the `Data` of a newly added block; it defaults to `{}`.
  The `label` is what the editor's "add block" menu shows.

`Add` throws `ArgumentException` for a `type` outside `[a-z0-9-]{1,40}`, a renderer that is not a component, or an
editor that does not derive `ExtensionBlockEditorBase`. Editors and the host media picker are wrapped in an
`ErrorBoundary`, so a failing one shows "This block's editor failed to load" and leaves the block unchanged.

```razor
@* ProjectCardView.razor *@
@using System.Text.Json
<div class="project-card"><strong>@Name</strong></div>

@code {
    [Parameter] public JsonElement Data { get; set; }

    private string Name => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("name", out var n)
        ? n.GetString() ?? "" : "";
}
```

```razor
@* ProjectCardEditor.razor *@
@inherits SyntaxCircus.Blazor.RichContent.Editing.ExtensionBlockEditorBase
@using System.Text.Json

<input value="@Name" disabled="@Disabled" @oninput="Changed" />

@code {
    private string Name => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("name", out var n)
        ? n.GetString() ?? "" : "";

    private Task Changed(ChangeEventArgs e) =>
        DataChanged.InvokeAsync(JsonSerializer.SerializeToElement(new { name = e.Value?.ToString() ?? "" }));
}
```

**Renderers must tolerate any JSON shape in `Data`.** Stored content can be older, newer or hand-edited, so check
`ValueKind` and use `TryGetProperty` rather than assuming the shape you wrote. A renderer that throws is isolated by an
`ErrorBoundary`: that block renders nothing, the error is logged, and the rest of the document still renders.
Extension `Data` is limited to 16 KiB. Blocks whose type is not registered render nothing but stay in the document.

## Stored JSON

A document is plain JSON, safe to keep in a text or `jsonb` column:

```json
{
  "schemaVersion": 1,
  "blocks": [
    { "$block": "rich-text", "id": "b1", "nodes": [ { "kind": "paragraph", "children": [ { "kind": "text", "text": "Hello" } ] } ] },
    { "$block": "callout", "id": "b2", "tone": "tip", "title": "Note", "nodes": [] },
    { "$block": "extension", "id": "b3", "type": "project-card", "data": { "name": "Demo" } }
  ]
}
```

- `schemaVersion` is `1` (`ContentDocument.CurrentSchemaVersion`).
- Every block has a `$block` discriminator: `rich-text`, `gallery`, `callout`, `cta` or `extension`.
- All enums (node `kind`, callout `tone`) are camelCase strings.
- Always go through `ContentDocumentSerializer`; do not hand-roll the serializer options.

**Compatibility:** within schema version 1, changes are additive only (new optional properties, new block types, new
node kinds and enum values). Readers behave as follows:

- Unknown properties on known blocks, nodes and gallery images are preserved verbatim on re-serialize.
- A block with a `$block` this version does not know is preserved verbatim and renders nothing.
- A known block this reader cannot bind (for example a node kind or callout tone added by a newer writer) is preserved
  verbatim as an unsupported block. The editor shows it as unsupported, `Validate` reports `malformed-known-block`, and
  the viewer skips it; the rest of the document still reads and renders.
- Upgrade readers before writers. Any breaking change bumps `schemaVersion`; a reader rejects a version it does not support.

## Links and security

Rich text and call-to-action URLs are validated: `https:` (host required, no user info), `mailto:`, `tel:`, or a
root-relative path starting with a single `/` (not `//`). URLs are at most 2048 characters and may not contain
whitespace, control characters or `\ < > " '`. Anything else is a validation error and the document will not render.

The package does not know your site's host, so every absolute `https:` link is rendered with `rel="noopener"`.
Root-relative links do not get it.

## Code highlighting

Code blocks are highlighted on the server with ColorCode through the default `ICodeHighlighter`. It emits CSS classes,
never inline styles, so **your app supplies the colour theme**. Supported language ids include `cs`/`csharp`,
`js`/`javascript`, `ts`/`typescript`, `html`, `xml`, `css`, `sql`, `powershell`/`ps1`, `python`/`py`, `java`, `cpp`,
`fsharp` and `php`. An unsupported or missing language renders as escaped plain text. Register your own
`ICodeHighlighter` before `AddRichContent` to replace it.

A minimal theme. Scope it to your article container, because the package's isolated component CSS does not style
these classes:

```css
.article pre code .comment { color: #6a737d; font-style: italic; }
.article pre code .keyword { color: #d73a49; }
.article pre code .string { color: #032f62; }
.article pre code .number { color: #005cc5; }
.article pre code .htmlElementName { color: #22863a; }
.article pre code .htmlAttributeName { color: #6f42c1; }
.article pre code .htmlAttributeValue { color: #032f62; }
.article pre code .htmlComment { color: #6a737d; }
.article pre code .htmlTagDelimiter,
.article pre code .htmlOperator { color: #586069; }
```

## Limits

Enforced by `RichContentRules.Validate` and configurable through `RichContentLimits` (pass it as the `Limits`
parameter of the view and editor, or to `Validate`). The defaults are blog-sized:

| Limit | Default |
| --- | --- |
| Rich text nodes | 4096 |
| Nesting depth | 8 |
| Total text | 256 KiB (UTF-8) |
| Single code block | 32 KiB |
| Blocks per document | 200 |
| Heading levels | 2 to 4 |
| URL length | 2048 characters |
| Gallery images | 24 |
| Table size | 50 rows x 10 cells |
| Image alt / caption | 300 characters |
| Extension `Data` | 16 KiB |

```csharp
var limits = RichContentLimits.Default with { Blocks = 50, TextBytes = 64 * 1024 };
```

## Sample

`samples/RichContent.Sample` in the repository is a runnable Blazor Server app with an editor, a live preview, a media
picker and a `project-card` extension block. It is not packed.

## License

MIT
