# Inline writing editor

Date: 2026-11-25. Status: approved.

## Intent

`ContentDocumentEditor` exposes the node tree directly: one labelled box for every text run, code span and link,
each with move and remove buttons. A paragraph with one link and one code span is five or more boxes. Reading or
revising a whole article this way is impractical. Authors should be able to write and edit prose as continuous,
formatted text, without losing anything the block editor can express or anything already stored.

## Goals

- Prose reads and edits like a document: paragraphs, headings, lists, quotes, bold, italic, inline code and links.
- Everything else the model supports is also usable in the same view: code blocks, images, tables, callouts,
  galleries, calls to action and extension blocks.
- Opening a document and not editing it changes nothing. Editing one thing leaves every other node as it was.
- The block editor stays available as a second mode for structural work.
- The editor is readable in light and dark host apps.

## Non-goals

- No change to the document model, serializer, schema version, validation rules or `ContentDocumentView`.
- No new formatting the model cannot store (underline, strikethrough, colour, soft line breaks).
- Not in this version: typing shortcuts (`## `, `- `, backticks), formatted or Markdown paste, drag and drop,
  selecting or deleting across a card, more syntax-highlighting languages, collaborative editing.

## Anti-goals (any of these makes it a failure)

- Silently dropping or rewriting stored content, including unknown properties (`Extra`).
- Losing typed text on save, mode switch, validation error or a dropped connection that later recovers.
- A long article or a large paste breaking the Blazor circuit (in Write mode; see Known limitations).

## Constraints

- Public MIT package, `net10.0`. No third-party JavaScript, no npm or bundler step.
- Write mode must work in interactive server rendering with SignalR's default 32 KB incoming message limit. Hosts
  must not need to raise it. Server rendering is what is supported and tested; WebAssembly and Auto are not tested,
  and nothing should block them needlessly.
- Must work under a Content-Security-Policy that allows only same-origin scripts, with no inline script or `eval`.
- Browsers: current Chrome, Edge, Firefox and Safari. Input method (IME) composition must work.
- Existing public parameters and behaviour keep working. A consumer that changes nothing keeps the block editor.

## Approaches

| Approach | Trade-off |
|---|---|
| **A. Our own `contenteditable` surface in this library, mapped to the existing typed nodes (chosen)** | No dependency and full control of the mapping. We own the browser quirks. |
| B. The same surface built inside one consuming app | No package release, but other consumers get nothing and there are two editors. |
| C. A third-party editor (for example ProseMirror or Tiptap) | Stronger editing engine, but adds a JavaScript dependency and build step, and a schema conversion that must preserve our unknown data. |

The product owner chose A over B and C, and asked for the library to be extended where needed so that nothing
important is missing. A similar typed-node surface, proven in another app, is the starting idea; it lacked inline
code, numbered lists, quotes, code blocks, images and tables.

## How it behaves

```text
[ Write | Blocks ]
[ B  I  <>  Link | Paragraph v | • List  1. List  Quote | Insert v ]

  Writing surface: paragraphs, headings, lists, quotes.
  Click anywhere and type.

  ┌ Code block   [csharp v]                     ↑ ↓ ✕ ┐
  │ options.UseNpgsql(connectionString)               │
  └───────────────────────────────────────────────────┘

  More writing.

  ┌ Callout   [Warning v]  Title [          ]   ↑ ↓ ✕ ┐
  │ writing surface                                   │
  └───────────────────────────────────────────────────┘
```

### Surfaces and cards

In Write mode a rich text block is shown as a sequence of **writing surfaces** and **cards**.

- A surface holds a run of consecutive top-level paragraphs, headings (levels 2 to 4), lists (bulleted and
  numbered, nested) and quotes. It looks borderless. A surface never spans two rich text blocks: neighbouring
  blocks are separate surfaces with a faint visible divider, and are not merged in storage. Text typed in an empty
  place that has no adjoining rich text block (for example between two callouts) creates a new one.
- Code blocks, images and tables are cards between surfaces.
  - Code: language select and a monospace text area. Tab inserts spaces; Escape then Tab leaves the field.
  - Image: preview through `IRichContentMediaResolver`, the host media picker, alt text and caption.
  - Table: a grid. Each cell is a small surface limited to inline formatting. Add or remove rows and columns, and
    mark the first row as a header.
- Other blocks (callout, gallery, call to action, extension) are cards using their existing editors. A callout's
  body works like a rich text block in Write mode: surfaces, plus code, image and table cards nested inside it.
- Cards are moved and removed only with their buttons. Backspace never deletes a card. An empty place to type is
  always reachable before and after every card (it is not stored until something is typed there).

### Toolbar and keyboard

One toolbar acts on the focused surface and stays visible while scrolling a long document: bold, italic, inline
code, link (add, edit, remove), style (paragraph, heading, subheading, small heading), bulleted list, numbered
list, quote, and Insert. Buttons show the state at the cursor and are disabled where the model forbids them (for
example headings inside a list item).

Ctrl/Cmd+B, I, K (link) and E (inline code). Enter makes a new paragraph or list item; Enter on an empty list item
or quote line leaves it. Tab and Shift+Tab nest and un-nest list items, only inside a list. Alt+F10 moves to the
toolbar and Escape returns. Shift+Enter behaves like Enter, because the model has no line break.

**Insert** places a code block, image or table after the current paragraph, splitting the surface. For callout,
gallery, call to action or an extension block it splits the rich text block, as the block editor does now. In a
callout body, Insert offers only code, image and table. In a table cell it is disabled.

Native browser undo and redo must work for typing and toolbar formatting. Browsers may keep one history for the
whole page, which is acceptable: a change made by undo in any surface, focused or not, reaches `Document`. A
surface's content is rewritten only when it really changed (replacement, Insert split), since that can reset the
history. Card add, move and remove are not undoable (same as today); removing a non-empty card asks for
confirmation. This includes the text area in a code card, which is subject to the same sending rules as surfaces.

### Paste

Plain text only. Blank lines separate paragraphs; single line breaks become spaces. Pasted HTML is never inserted.
Dropping content into a surface is blocked.

### Fidelity

- Mounting the editor, focusing, moving the cursor and switching modes never raises `DocumentChanged`.
- Only the top-level nodes the author actually changed are rebuilt from the page. All others keep their original
  node objects, so their JSON is unchanged.
- Anything a surface cannot reproduce exactly (a node carrying unknown properties anywhere inside it, or a shape
  the surface does not support) is shown as a locked card: readable, movable, removable, and editable in Blocks
  mode. It is never converted. Blocks mode already keeps unknown properties and must continue to.
- A changed node may be normalised: adjacent text runs merge, and empty formatting wrappers are dropped. A list
  item keeps its existing form (inline content directly, or paragraph children).
- Heading levels, list ordering and link URLs survive a round trip.

### Errors

The document stays the source of truth and `ErrorsChanged` keeps working. Errors show beside the surface or card.
The link box refuses an address `RichContentUrl.IsSafe` rejects, with the reason, so surfaces do not create unsafe
links. A new image card with no media or alt text is invalid until filled in, as today. If a surface's content
cannot be converted within the configured limits, the editor keeps what was typed on screen and reports an error
through `ErrorsChanged`, so a host that blocks saving on errors cannot save a stale version.

### Saving and the connection

Every edit the author has typed must reach the host's `Document` before a click made afterwards (such as Save) is
handled; no idle delay may open a gap. This is guaranteed for hosts whose `DocumentChanged` handler stores the
document before its first incomplete await, as `@bind-Document` does; the README says so.

Updates send only what changed, in every Write mode surface, table cell and code card. Anything that could exceed
the message limit is split into several ordinary messages, each handled without waiting, and applied when the last
arrives. Stream references are not used for edits, because reading one waits and a later click could overtake it.

A dropped connection is survived when the same circuit reconnects: the surface keeps the text and delivers what
was not acknowledged. If the circuit is lost (reload, or a paused circuit resumed as new components), unsent and
unsaved text is lost, as with the current editor. Browser-side drafts are out of scope.

If the host replaces `Document` (reload, conflict resolution), a surface whose content in the new document differs
from what the editor last had for it is redrawn, and the host wins over unsent edits there. Surfaces whose content
is the same by value are left alone, even if the host passes new objects, so cursor, undo and unsent text survive.
Otherwise, a surface with undelivered edits is never overwritten.

### Loading failure and Disabled

If the script fails to load, the editor shows a short notice and uses Blocks mode. `Disabled` makes surfaces,
cards and toolbar read-only.

## Package changes

- `ContentDocumentEditor` gains a mode parameter (`Blocks` default, `Write`), bindable, and shows the Write/Blocks
  switch. With the default the editor looks and behaves as before, plus the switch and the new styling. Hosts can
  hide the switch.
- JavaScript ships as an ES module static web asset, imported on demand by Write mode only. The viewer stays
  JavaScript-free.
- Styling: all editor colours come from documented CSS custom properties whose defaults follow the host's text
  colour, so dark themes stay readable. This fixes today's fixed dark-blue "Insert block" link and unstyled
  buttons in both modes. Elements created by script must be styled too (scoped CSS does not reach them by itself).
- README: remove the "no JavaScript interop" claim for the editor, and document the mode, keyboard, paste
  behaviour, CSS properties and Content-Security-Policy needs.
- Version 0.2.0 (`+semver: minor`). Merging to `main` publishes to NuGet.

## Verification

- Unit and bUnit tests: which nodes go to surfaces, cards or locked cards; operations; modes; errors; `Disabled`.
- **Real-browser tests** (a new Playwright test project running against the sample app, in CI on Chromium, with
  Firefox and WebKit also run before release): typing, every toolbar command, keyboard behaviour, paste, undo,
  Save straight after typing (including after a large paste), reconnect, and a strict CSP. On default SignalR
  settings: a 200 KB paste of about 200 paragraphs into an empty document is saved, and one that exceeds the node or
  text limits stays on screen with an error, without breaking the circuit.
- Round-trip fixtures: documents containing every node kind and unknown properties are opened in Write mode and
  must serialise identically; after one edit, only that node differs.
- Manual: IME composition in at least one browser, and keyboard-only use. Real Safari and screen-reader testing may
  not be possible here; say so in the release notes if not done.

## Known limitations

Blocks mode is not changed apart from styling. Its fields send their whole value on each keystroke, so one field
near or above 32 KB (a very large paste, or a code block near its limit) can exceed the default message limit and
drop the circuit. The same limit has been hit in practice in another app. This predates this work and is not fixed
in the library. A host that uses Blocks mode with large fields should raise
`HubOptions.MaximumReceiveMessageSize`; the README says so. Write mode must not depend on that.

## Known risks

- `contenteditable` behaves differently between browsers; the browser tests are the defence.
- Bold, italic and list commands would use `execCommand`, which is deprecated but widely supported and keeps native
  undo. Replacing it would mean writing our own undo.
- Inline code has no browser command and needs care to stay inside undo.

## Left to the builder

File and component layout, the script's internal design and message format, how changed nodes are detected, how
large payloads are sent, visual design within the sketch above, and test organisation.
