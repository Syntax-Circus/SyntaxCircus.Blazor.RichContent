# Changelog

All notable changes to this project are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

### Added

- `SyntaxCircus.RichContent`: block-based content document model (`ContentDocument`, `ContentBlock` types,
  `RichTextNode`, `MediaRef`), `ContentDocumentSerializer` (versioned JSON, `schemaVersion` 1, unknown blocks
  round-trip verbatim) and `RichContentRules` / `RichContentLimits` validation with blog-sized defaults.
- `SyntaxCircus.Blazor.RichContent`: `ContentDocumentView` server renderer (ColorCode code highlighting with CSS
  classes, `rel="noopener"` on absolute links, ErrorBoundary isolation of extension blocks), `ContentDocumentEditor`,
  `AddRichContent` registration, `IRichContentMediaResolver`, `MediaPickerBase` and app-defined extension blocks
  (`ContentBlockRegistry`, `ExtensionBlockEditorBase`).
- Package README and a runnable sample app (`samples/RichContent.Sample`, not packed).
