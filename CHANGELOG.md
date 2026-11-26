# Changelog

All notable changes to this project are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## 0.2.0

### Added

- Write mode for `ContentDocumentEditor`: prose is edited as continuous formatted text (paragraphs, headings, lists,
  quotes, bold, italic, inline code, links), with cards for code blocks, images, tables and the other block types, a
  toolbar, keyboard shortcuts, plain-text paste and Insert. Content it cannot reproduce exactly is kept as it is and
  shown as a "Preserved content" card. Only the top-level nodes the author changes are rebuilt.
- `ContentDocumentEditor.Mode` (`RichContentEditorMode.Blocks`, the default, or `Write`), bindable through
  `ModeChanged`, and `ShowModeSwitch` (default `true`).
- `RichTextSurface` (with `RichTextSurfaceProfile`), a writing surface that extension editors can use.
- The error code `surface-unconvertible` in `ErrorsChanged`: something on screen is not in the document. Hosts should
  block saving while it is there.
- `RichTextEditorOperations.SetTableHeaderRow`.
- CSS custom properties `--rt-border`, `--rt-muted`, `--rt-accent`, `--rt-error`, `--rt-surface`, `--rt-button-bg`,
  `--rt-button-text` and `--rt-toolbar-top`.
- JavaScript modules, shipped as static web assets and imported only for Write mode and `RichTextSurface`. No
  third-party script. The viewer and Blocks mode use none.
- Other public types in `Editing.Surfaces` and `Editing.Writing` are the editor's own components and their parts. They
  are public because Razor components are, are not a supported API, and may change in any version.

### Changed

- The editor shows a `Write | Blocks` switch by default. Set `ShowModeSwitch="false"` to hide it. Otherwise the default
  view is the block editor, as before.
- New editor styling in both views. Colours follow the host's text colour, so dark pages are readable; before, the
  "Insert block" link, error messages, hints and preserved-block notices used fixed colours. Buttons and fields are now
  themed, including those inside a host's media picker and extension editors; the theme is in the CSS cascade layer
  `rich-content-editor`, so the host's own unlayered rules win. The editor's root is now a flex column instead of a
  grid.

### Notes for hosts

See the README's "Write mode" section before turning it on. In short:

- A `DocumentChanged` handler must store the document before its first incomplete `await`. A handler that does not
  store an edit is not detected: the screen keeps it.
- Applying a link within about one network round trip of the last keystroke in the link box, and then saving at once,
  can miss the link.
- With a Content-Security-Policy, allow style attributes (`style-src-attr 'unsafe-inline'`, or `'unsafe-inline'` in
  `style-src`).
- Do not override fonts inside `[data-rt-surface]` with `!important`; keep `"rt-code"` first in a code font you set
  there.
- The editor sets `defaultParagraphSeparator`, which applies to the whole document.
- If you raise `UrlCharacters` above 4000, the Write mode link box shortens longer stored links when applied.
- Blocks mode is unchanged apart from styling: with large fields it still needs a higher
  `HubOptions.MaximumReceiveMessageSize`. Write mode does not.

### Known limitations

- Adding, moving and removing cards and blocks cannot be undone. Content that is redrawn may lose its undo history.
- In Firefox, earlier undo history is lost after the editor has been `Disabled` and enabled again.
- The toolbar does not use a roving tab index. After answering "No" to "Remove?", focus is not returned to the button.
- Table rows and columns are added and removed at the end only, and rows cannot be moved in Write mode.

### Not verified

- A real input method (IME). Only simulated composition is tested.
- Retail Firefox and real Safari. Tests use Playwright's Chromium, Firefox and WebKit builds; in WebKit the tests
  that cut, copy and paste with the real clipboard fail on the Windows machine used, so that is not verified there.
- Screen readers. While editing, inline code has no code semantics for them.
- Undo or redo chosen from the browser's menu while the editor is briefly frozen.
- Mobile browsers and touch keyboards, and Blazor WebAssembly or Auto rendering.

## 0.1.0 - 2026-11-20

### Added

- `SyntaxCircus.RichContent`: block-based content document model (`ContentDocument`, `ContentBlock` types,
  `RichTextNode`, `MediaRef`), `ContentDocumentSerializer` (versioned JSON, `schemaVersion` 1, unknown blocks
  round-trip verbatim) and `RichContentRules` / `RichContentLimits` validation with blog-sized defaults.
- `SyntaxCircus.Blazor.RichContent`: `ContentDocumentView` server renderer (ColorCode code highlighting with CSS
  classes, `rel="noopener"` on absolute links, ErrorBoundary isolation of extension blocks), `ContentDocumentEditor`,
  `AddRichContent` registration, `IRichContentMediaResolver`, `MediaPickerBase` and app-defined extension blocks
  (`ContentBlockRegistry`, `ExtensionBlockEditorBase`).
- Package README and a runnable sample app (`samples/RichContent.Sample`, not packed).

### Fixed

- Duplicate block ids no longer drop both blocks in the viewer: `RichContentError` carries `BlockIndex` and only the
  failing occurrence is skipped. `ContentDocumentOperations` gained index-based `Replace`/`Remove`/`Move`/`SplitRichText`,
  which the editor uses.
- Validation keeps checking later blocks after the text budget is exceeded; block titles and CTA text now count toward
  it and have limits (`BlockTitleCharacters`, `CtaBodyCharacters`).
- JSON `null` entries in node children, block nodes and gallery images are dropped on read (`null-image` is reported
  for code-built documents); the editor no longer offers scaffolds deeper than `MaxDepth`.
