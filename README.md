# SyntaxCircus.Blazor.RichContent

[![Build](https://github.com/Syntax-Circus/SyntaxCircus.Blazor.RichContent/actions/workflows/build.yml/badge.svg)](https://github.com/Syntax-Circus/SyntaxCircus.Blazor.RichContent/actions/workflows/build.yml)
[![NuGet: SyntaxCircus.RichContent](https://img.shields.io/nuget/v/SyntaxCircus.RichContent.svg?label=SyntaxCircus.RichContent)](https://www.nuget.org/packages/SyntaxCircus.RichContent)
[![NuGet: SyntaxCircus.Blazor.RichContent](https://img.shields.io/nuget/v/SyntaxCircus.Blazor.RichContent.svg?label=SyntaxCircus.Blazor.RichContent)](https://www.nuget.org/packages/SyntaxCircus.Blazor.RichContent)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Block-based rich content for Blazor apps, shipped as two NuGet packages that share one version:

- **SyntaxCircus.RichContent** - the framework-free document model (`ContentDocument`, blocks, rich text nodes), the
  versioned JSON serializer and the validation rules (`RichContentRules`, `RichContentLimits`). Unknown block types
  survive a read/write round trip untouched.
- **SyntaxCircus.Blazor.RichContent** - Blazor components for server-side rendering (`ContentDocumentView`) and
  editing (`ContentDocumentEditor`) that content. It references `SyntaxCircus.RichContent`.

Use only the first package if you just need to store, read or validate documents (for example in an API or a worker).
Add the second in the Blazor app that renders or edits them. Component CSS is scoped (`.razor.css`), so it does not
restyle the rest of your app; it reaches your app through its scoped CSS bundle (`{AssemblyName}.styles.css`), which
your page must link. The viewer, and the editor's block view, use no JavaScript of this package. The editor's
[Write mode](#write-mode) loads JavaScript modules that ship in the package as static web assets: no third-party
script, npm or bundler step.

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
the one used. If you call `AddRichContent` first, the package fallback (which logs the failure through `ILogger`) is the
registered boundary logger and the framework default may not replace it.

Then render and edit:

```razor
<ContentDocumentEditor @bind-Document="_document" ErrorsChanged="OnErrors" />

<ContentDocumentView Document="_document" />
```

(`using SyntaxCircus.Blazor.RichContent.Editing` and `.Rendering` for the two components.)

- `ContentDocumentView` server-renders a stored document. Blocks that fail validation are skipped (and logged as a warning with block ids and error codes only); the other blocks still render, and document-level errors do not suppress any block.
- `ContentDocumentEditor` is a controlled component: every edit raises `DocumentChanged` and you feed the new
  document back in (`@bind-Document` does this). `ErrorsChanged` reports the current validation errors so you can
  block saving. Both accept an optional `Limits` parameter; the editor also takes `Label`, `Disabled`, `Mode`,
  `ModeChanged` and `ShowModeSwitch` (see [Write mode](#write-mode)).

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

## Write mode

The editor has two views of the same document. **Blocks** (the default) has one labelled field for every node, for
structural work. **Write** shows prose as continuous formatted text: paragraphs, headings (levels 2 to 4), bulleted
and numbered lists, quotes, bold, italic, inline code and links. Authors switch with the `Write | Blocks` buttons.

```razor
<ContentDocumentEditor @bind-Document="_document" @bind-Mode="_mode" ErrorsChanged="OnErrors" />

@code {
    private RichContentEditorMode _mode = RichContentEditorMode.Write;
}
```

Write mode needs interactive rendering. Interactive server rendering is what is supported and tested; WebAssembly and
Auto are not tested.

In Write mode, consecutive prose is a writing surface. Code blocks, images and tables are cards between surfaces, and
so are the other block types, which use the same editors as in Blocks mode (a callout's text is written like any other
text). Anything a surface cannot reproduce exactly, such as a node with unknown properties or a shape the surface does
not support (including content nested more than 8 levels deep), is shown as a "Preserved content" card: it can be
moved or removed, is edited in Blocks mode only, and is never converted. Opening a document, moving the cursor and
switching modes raise no `DocumentChanged`. Only the top-level nodes the author changes are rebuilt, and a rebuilt
node can be normalised (adjacent text runs merge, empty formatting is dropped).

Cards are moved and removed with their buttons only; Backspace never deletes one. There is somewhere to type before
and after every card (an empty one is outlined, and is not stored until something is typed). Removing a card, a
table's last row or column, or a block other than a text block asks first if it holds something. A text block's
**Remove** in its header does not ask, unless it shows text that could not be saved. Table rows and columns are added
and removed at the end only.

**Insert** puts a code block, image or table after the paragraph (or list or quote) with the cursor. A gallery,
callout, call to action or extension block splits the text block there. Inside a callout only code blocks, images and
tables are offered, and in a table cell Insert is disabled.

### `DocumentChanged`: store the document before awaiting

Every edit the author has made reaches your `DocumentChanged` handler before a click made afterwards, such as Save, is
handled. This holds **only if the handler stores the document before its first incomplete `await`**, as
`@bind-Document` does:

```csharp
private async Task OnDocumentChanged(ContentDocument document)
{
    _document = document;          // first
    await _drafts.SaveAsync(...);  // then anything slow
}
```

One exception, with the link box: pressing **Apply link** or Enter within about one network round trip of your last
keystroke in it means the server has to check the address before the link is made. A save clicked straight after can
miss that link. The link box stays open until the server has handled the click, so this is visible.

In Write mode the editor builds each edit on the one before, without waiting for you to pass the document back. If
your handler does **not** store a document (you reject the edit, or an `await` before storing fails), the editor is not
told: the screen keeps showing the edit, and later edits are built on it, so it can come back with them. **To revert,
pass a fresh copy of your previous document**, for example by serializing and deserializing it:

```csharp
_document = ContentDocumentSerializer.Deserialize(ContentDocumentSerializer.Serialize(previous)).Document!;
```

Passing the previous instance itself back does nothing: the editor still has it from you, and sees no change. Nor does
a `with` copy that shares its block contents redraw the text. The redraw that reverts **discards unsent text in that
place** too.

When you replace `Document` (a reload, a conflict resolution), places whose content is the same by value are left
alone, so the cursor, undo history and unsent text survive. A place whose content differs is redrawn and **your
document wins: unsent text there, and text there that could not be saved, is discarded**.

### Errors that must block saving

`ErrorsChanged` works as in Blocks mode. In addition, if what a surface, table cell or code card shows cannot be taken
into the document, the editor keeps it on screen and reports an error with the code `surface-unconvertible`.
**Block saving while `ErrorsChanged` contains `surface-unconvertible`: your `Document` is behind the screen.** This
happens, for example, to a paste far beyond the size limits (the largest single edit accepted is the larger of 2 MiB
and 4 × `TextBytes` + 256 × `Nodes`, and a surface can hold at most the larger of 4096 and `Nodes` top-level nodes),
or to content that would be nested more than 8 levels deep, even if your `Limits` allow more. While it lasts, the editor refuses a mode switch, and rearrangements that would redraw that text.

### Switching views

- `Mode` is `RichContentEditorMode.Blocks` (default) or `Write`. `ShowModeSwitch` (default `true`) shows the buttons;
  they stay enabled while the editor is `Disabled`.
- A switch takes at least a round trip. The editor is read-only until the browser has confirmed that everything typed
  has been sent, so a switch can wait as long as the connection does.
- `ModeChanged` is raised after the author's switch and after the fallback below. It is not raised for a switch you
  made by changing `Mode` that succeeds. It **is** raised, with the view still shown, when your change is refused
  (something could not be saved) or the author cancels it before it happens. An author's own refused switch shows a
  notice and raises nothing.
- If you set `Mode` one way (without `@bind-Mode`), handle `ModeChanged` and update your value. The editor follows
  `Mode` when its value changes, so a stale value means setting it again does nothing.
- If Write mode's script cannot be loaded or fails before it has drawn, the editor shows a notice, switches to Blocks
  and raises `ModeChanged(Blocks)`; **if you store the preference, you will store that**. Choosing Write again tries
  again. With `ShowModeSwitch="false"`, only your code (setting `Mode` back to `Write`) or a page reload brings Write
  back. A browser may remember a failed module import, so a reload may be needed; this has not been tested. A lost
  connection or a slow one does not cause the fallback.
- A Write editor's surfaces and code cards cannot be typed into until the toolbar's script is ready, at least one
  round trip after the page becomes interactive. While a script or a redraw is late, a "Trying again" notice is
  shown.
- Switching to Blocks does not change the message size limit of Blocks mode (below): a very large paragraph written in
  Write mode can drop the connection if it is then edited in Blocks mode.

### Message size (`HubOptions`)

Write mode works with SignalR's default 32 KB incoming message limit. Surfaces, table cells and code cards send their
edits in small pieces. The other fields on screen in Write mode (alt text, captions, titles, call to action fields and
the link address) send their whole value and are capped at 4,000 characters (`maxlength`). The content rules allow
less in all of them except URLs, which has two consequences if you raise `UrlCharacters` above 4000:

- Write mode cannot type or paste a longer address.
- On a longer stored link, the link box says that the address is too long to edit there and shows only its start.
  **It then applies no address at all**, neither that start nor one typed over it, since it cannot tell the two apart.
  The link is kept as it is. Edit it in Blocks mode, or use **Remove link** (applying an empty address does the same)
  and then link the text again.

A stored value that is already longer than a field's cap is shown whole and can be shortened but not extended. Each
keystroke still sends all of it, so one near 32 KB or more exceeds the default limit.

**Blocks mode is different:** its fields send their whole value on every keystroke, with no cap. A field near or above
32 KB (a very large paste, or a code block near its limit) can exceed the default limit and drop the circuit. If you
use Blocks mode with large fields, raise the limit:

```csharp
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 256 * 1024);
```

What your media picker and extension editors send is up to them.

### Connection

If the same circuit reconnects, Write mode keeps the text on screen and delivers what was not acknowledged. If the
circuit is lost (a reload, or a paused circuit resumed as new components), unsent and unsaved text is lost, as in
Blocks mode.

### Extension editors

An extension block's editor receives `Disabled`; **pass it on to any script-edited surface your editor contains**
(such as a `RichTextSurface`). The editor does not wait, at a mode switch, for a surface that was not given
`Disabled`, and such a surface stays writable while the editor is `Disabled`.

### Keyboard

| Keys | Action |
| --- | --- |
| Ctrl/Cmd+B, I | Bold, italic |
| Ctrl/Cmd+E | Inline code |
| Ctrl/Cmd+K | Link box: Enter applies, Escape cancels; applying an empty address removes the link. With nothing selected, the address becomes the link text |
| Enter | New paragraph or list item; on an empty list item or quote line, leaves it. Does nothing in a table cell |
| Shift+Enter | Same as Enter (the model has no line break) |
| Tab, Shift+Tab | In a list, nest and un-nest the item. When nothing can move, and outside lists, focus moves as usual |
| Right arrow | At the end of inline code that ends its line, leaves the code |
| Alt+F10 | Go to the toolbar. Left and Right move along it, Escape returns to the text. On the style list, Alt+Down opens it (in some browsers Up and Down apply a style at once and return to the text) |
| In a code card | Tab inserts four spaces; Escape then Tab leaves; Shift+Tab goes back |

Ctrl/Cmd+U does nothing: the model has no underline. Every toolbar button is also a normal Tab stop (the toolbar does
not use a roving tab index).

### Paste, drag and drop

Pasting into a surface takes **plain text only**; pasted HTML is never inserted. Blank lines separate paragraphs and
single line breaks become spaces (in a table cell, everything becomes one line). A code card is an ordinary text area.
Dropping onto a surface is blocked, and text in a surface cannot be dragged.

### Undo

The browser's undo and redo cover typing and toolbar formatting in surfaces and code cards (a browser may keep one
history for the whole page). Adding, moving and removing cards and blocks **cannot** be undone. Earlier typing in
surfaces that a rearrangement leaves in place can still be undone, and so can earlier typing after a mode switch that
was refused after it started. Content that is
redrawn (your replacement of it, or a surface split by Insert) may lose its history. **In Firefox, after the editor
has been `Disabled` and enabled again, earlier undo history is lost.**

### Content-Security-Policy

Write mode needs no inline script, `eval` or other origin for its scripts: `script-src 'self'` is enough, plus what
Blazor itself needs (such as `connect-src` for the WebSocket) and `img-src` for your media. **For styles, allow style
attributes**: either `style-src 'self' 'unsafe-inline'` (the policy the browser tests run under) or, more narrowly,
`style-src 'self'; style-src-attr 'unsafe-inline'`. The editing commands built into Chromium and WebKit write style
attributes in some operations, such as joining lines, and without this the browser blocks them and reports
`style-src-attr` violations. The narrower form was checked by hand for a short sequence of edits only.

### Styling

Colours come from these CSS custom properties. Set them on the editor or any element around it. The defaults are mixed
from your page's text colour (`currentColor`), so light and dark pages both work without setting any of them.

| Property | Used for | Default |
| --- | --- | --- |
| `--rt-border` | Borders and dividers | 28% of the text colour |
| `--rt-muted` | Hints and status notices | 80% of the text colour |
| `--rt-accent` | Links, "Insert block", focus and pressed buttons | the text colour mixed with blue |
| `--rt-error` | Error messages | the text colour mixed with red |
| `--rt-surface` | Background of the sticky toolbar | transparent, with what scrolls under it blurred |
| `--rt-button-bg` | Button background | 8% of the text colour |
| `--rt-button-text` | Button text | the text colour |

The Write mode toolbar stays at the top of the window while the document scrolls. If your page has a fixed header, set
`--rt-toolbar-top` to its height. Set `--rt-surface` to your page's background for an opaque toolbar. The theme of
buttons and fields (including those in your media picker and extension editors) is in the CSS cascade layer
`rich-content-editor`, so your own unlayered rules for them win. Sizing rules, such as button minimum height and full
width fields, are not layered.

**Do not override with `!important`, inside `[data-rt-surface]`, `font-weight` or `font-style`, or the `font-family`
of `code` or `font[face="rt-plain"]` elements.** The browser's formatting commands decide what is bold, italic or code
from how text looks, so the editor pins these (headings, for instance, show at normal weight while edited). While
editing, inline code is shown as `font[face="rt-code"]`. If you change the code font there (for `code` and
`font[face="rt-code"]`), **keep `"rt-code"` first in the font family list**, as in
`font-family: "rt-code", ui-monospace, monospace`; the editor recognises code by it.

### Other effects on the page

- While a surface has the focus the editor sets the browser's `defaultParagraphSeparator` to `p`. This setting belongs
  to the whole document, so it also affects other `contenteditable` elements on the page.

### Components and API

The supported API for hosts is `ContentDocumentEditor` (with `Mode`, `ModeChanged` and `ShowModeSwitch`),
`RichContentEditorMode`, and, for extension editors that want a writing surface, `RichTextSurface` (`Nodes`,
`NodesChanged`, `Disabled`, `FocusOnStart`, `Label`, `Profile` with `RichTextSurfaceProfile`; its nodes must be ones a
surface can edit, and `NodesChanged` handlers have the same store-before-await rule). Other public types in the
`Editing.Surfaces` and `Editing.Writing` namespaces (cards, the toolbar, `WritingBlock`, `InsertChoice`,
`ScriptEditorBase`, `SurfaceSelection`) are the editor's own parts. They are public because Razor components are, and
may change in any version. Their `[JSInvokable]` methods are for the package's scripts only.

### Browsers, and what has not been verified

The browser tests run on Playwright's Chromium, Firefox and WebKit builds. **Not verified:** a real input method (IME;
only simulated composition is tested), retail Firefox, real Safari (and cut, copy and paste with the real clipboard in
WebKit), screen readers, mobile and touch keyboards, and undo or redo chosen from the browser's menu while the editor
is briefly frozen. While editing, **inline code has no code semantics for screen readers**.

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
whitespace, control characters or `\ < > " '`. Anything else is a validation error; the viewer skips only the block that carries it.

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
| Total text (including block titles and CTA text) | 256 KiB (UTF-8) |
| Single code block | 32 KiB |
| Blocks per document | 200 |
| Heading levels | 2 to 4 |
| URL length | 2048 characters |
| Gallery images | 24 |
| Table size | 50 rows x 10 cells |
| Image alt / caption | 300 characters |
| Callout / gallery title, CTA heading and label | 200 characters |
| CTA body | 1000 characters |
| Extension `Data` | 16 KiB |

```csharp
var limits = RichContentLimits.Default with { Blocks = 50, TextBytes = 64 * 1024 };
```

## Sample

`samples/RichContent.Sample` in the repository is a runnable Blazor Server app with an editor, a live preview, a media
picker and a `project-card` extension block. It is not packed.

## License

MIT
