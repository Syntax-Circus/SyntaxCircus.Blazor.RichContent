# Inline Writing Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Write mode to `ContentDocumentEditor` in which prose is edited as continuous formatted text, without losing anything stored.

**Architecture:** A pure C# layer decides which top-level nodes a writing surface can edit exactly and applies keyed patches, so untouched nodes keep their original objects. A dependency-free ES module owns the `contenteditable` DOM and sends patches in small ordinary interop messages. Everything else is a card made of Blazor components. The model, serializer, rules and viewer do not change.

**Tech Stack:** .NET 10, Blazor interactive server, ES module static web asset, xUnit v3, Shouldly, bUnit, Microsoft.Playwright.

**Spec:** `docs/superpowers/specs/2026-11-25-inline-writing-editor-design.md` (the authority; read it first).

## Global Constraints

- Branch `feat/inline-writing-editor`. Do not push or open a PR before Task 8. Merging to `main` publishes to NuGet.
- No change to `src/SyntaxCircus.RichContent` or to rendering components.
- No third-party JavaScript, npm or bundler. No inline script, `eval` or `new Function`. Never assign `innerHTML` from data or pasted content.
- Write mode works with default `HubOptions` (32 KB incoming). No edit message carries more than 4,000 characters of payload.
- All chunks of one patch are invoked immediately, in one synchronous loop. Never chain a send behind an earlier send's promise: a later click could overtake it.
- Edits are not sent through `IJSStreamReference`. The .NET receive method completes `DocumentChanged` without adding awaits of its own.
- `Mode` defaults to `Blocks`. Existing public API and tests keep working.
- `TreatWarningsAsErrors` is on. `.cs` files use CRLF with a UTF-8 BOM.
- Public repository: no consuming-app names in code, docs, fixtures or commits. No attribution lines in commits.
- Build: `dotnet build SyntaxCircus.Blazor.RichContent.slnx -c Release`. Test: `dotnet test --solution SyntaxCircus.Blazor.RichContent.slnx -c Release`.

## Review Focus

1. **Enter inside a paragraph:** browsers copy attributes when they split an element, so two elements can carry one node key. Expect two correct nodes, neither lost nor duplicated. (Task 4)
2. **Markup the browser adds itself** (`span style`, `b`, `i`, `font`, `div`, trailing `br`, non-breaking spaces from spaces typed at a boundary): expect only model nodes and ordinary spaces where the author typed spaces. (Task 4)
3. **IME composition, then an immediate Save:** expect the composed text, once. (Task 4)
4. **Select all and delete in a surface:** expect an empty, still-typable surface and a valid document. (Task 5)
5. **Typing at the end of inline code or a link:** the author can leave it (Right arrow at the end), and following text is plain. (Task 5)

## File Structure

All under `src/SyntaxCircus.Blazor.RichContent/` unless shown otherwise.

| Path | Responsibility |
|---|---|
| `Editing/Surfaces/SurfaceSupport.cs` | Can a node be edited exactly in a surface? |
| `Editing/Surfaces/SurfacePlanner.cs` | Splits nodes into surface and card segments |
| `Editing/Surfaces/SurfaceNode.cs` | Wire shape and mapping to `RichTextNode` |
| `Editing/Surfaces/SurfaceBuffer.cs` | Keys, patch application, value comparison |
| `Editing/Surfaces/ChunkAssembler.cs` | Reassembles chunked patches |
| `Editing/Surfaces/RichTextSurface.razor(.cs)` | Interop for one surface |
| `Editing/Writing/WritingBlock.razor(.cs)` | One node list as surfaces, cards and empty places |
| `Editing/Writing/WritingToolbar.razor` | Toolbar markup |
| `Editing/Writing/CodeCard.razor`, `ImageCard.razor`, `TableCard.razor`, `LockedCard.razor` | Cards |
| `wwwroot/surface/dom.js`, `commands.js`, `transport.js`, `surface.js` | DOM and node mapping; toolbar and keys; chunking and resend; entry point |
| `tests/SyntaxCircus.Blazor.RichContent.BrowserTests/` | Playwright tests |
| `samples/RichContent.Sample/Components/Pages/Fixture.razor` | Browser-test page |

---

### Task 1: Classify and segment nodes

**Files:** Create `SurfaceSupport.cs`, `SurfacePlanner.cs`; test `tests/SyntaxCircus.Blazor.RichContent.Tests/SurfacePlannerTests.cs`.

**Interfaces (produces):**

```csharp
internal static class SurfaceSupport
{
    public static bool CanEdit(RichTextNode node);        // top-level paragraph, heading, list or quote
    public static bool CanEditInline(ImmutableArray<RichTextNode> nodes);  // table cell content
}
internal enum SegmentKind { Surface, Code, Image, Table, Locked }
internal sealed record Segment(SegmentKind Kind, int Start, int Count);
internal static class SurfacePlanner
{
    public static IReadOnlyList<Segment> Plan(ImmutableArray<RichTextNode> nodes);
}
```

Add `InternalsVisibleTo` for both test projects.

`CanEdit` is false when any node in the subtree has non-empty `Extra`; has a field its kind may not have; has a kind not allowed there by `RichContentRules`; or is a list item in any form other than (inline nodes, then lists) or (paragraphs, then lists). A table is a `Table` segment only if every row and cell passes the same test with inline cell content; otherwise `Locked`. Code and image nodes with `Extra` are `Locked`.

- [ ] **Step 1: Write failing tests**, including:

```csharp
[Fact] void ProseRunsAreOneSurface_AndSpecialNodesSplitThem()
// [P, H2, CodeBlock, List, Image, Quote] => Surface(0,2), Code(2,1), Surface(3,1), Image(4,1), Surface(5,1)
[Fact] void UnknownPropertyAnywhereInside_LocksThatTopLevelNodeOnly()
// Paragraph > Strong > Text with Extra {"x":1} => Locked for that node; neighbours stay surfaces
[Theory] void ListItemShapes(...)   // inline+list true; paragraph+list true; inline+paragraph false; list+inline false
[Fact] void EmptyNodes_PlanIsEmpty()   // default and [] => no segments
[Fact] void TableWithBlockInCell_IsLocked()
```

- [ ] **Step 2:** Run the test project; expect compile failure.
- [ ] **Step 3:** Implement. `CanEdit` walks iteratively and stops at `RichContentLimits.Default.Depth`.
- [ ] **Step 4:** Run tests; expect pass.
- [ ] **Step 5:** Commit `feat(editor): classify rich text nodes for writing surfaces`.

### Task 2: Keyed patches and chunks

**Files:** Create `SurfaceNode.cs`, `SurfaceBuffer.cs`, `ChunkAssembler.cs`; test `SurfaceBufferTests.cs`, `ChunkAssemblerTests.cs`.

**Interfaces (produces):**

```csharp
internal sealed record SurfaceNode(string Kind, string? Text = null, string? Url = null,
    int? Level = null, bool? Ordered = null, SurfaceNode[]? Children = null)
{
    public static SurfaceNode From(RichTextNode node);
    public RichTextNode ToNode();   // throws FormatException on an unknown kind
}
internal sealed record KeyedNode(string Key, SurfaceNode Node);
internal sealed record SurfacePatch(string[] Order, KeyedNode[] Changed);

internal sealed class SurfaceBuffer
{
    public IReadOnlyList<KeyedNode> Reset(ImmutableArray<RichTextNode> nodes);
    public ImmutableArray<RichTextNode> Apply(SurfacePatch patch); // keys not in Changed keep their object
    public bool Matches(ImmutableArray<RichTextNode> nodes);       // value equality via serialized JSON
}
internal sealed class ChunkAssembler
{
    // Returns the whole payload when the last chunk of a sequence arrives, otherwise null.
    public string? Add(long sequence, int index, int count, string chunk);
}
```

`Apply` rejects (throws `FormatException`) a key in `Order` that is neither known nor in `Changed`, a duplicate key, and more than `RichContentLimits.Default.Nodes` entries. `ChunkAssembler` ignores sequences at or below the last completed one (resends), restarts on index 0, rejects a gap, and caps a payload at 2 MiB.

- [ ] **Step 1: Write failing tests**, including:

```csharp
[Fact] void Apply_KeepsTheSameObjectForUnchangedKeys()      // ReferenceEquals for every unchanged node
[Fact] void Apply_ReordersInsertsAndRemoves()
[Fact] void Apply_UnknownOrDuplicateKey_Throws()
[Fact] void Matches_EqualValuesInNewObjects_IsTrue()
[Fact] void Mapping_RoundTripsEverySupportedKind()          // serialized JSON equal
[Fact] void Chunks_ResendOfACompletedSequence_IsIgnored()
[Fact] void Chunks_GapOrOversize_Rejected()
```

- [ ] **Step 2:** Run; expect failure. **Step 3:** Implement. **Step 4:** Run; expect pass.
- [ ] **Step 5:** Commit `feat(editor): keyed surface patches and chunk assembly`.

### Task 3: Browser test harness

**Files:** Create the `BrowserTests` project, `Fixture.razor`, `samples/RichContent.Sample/Fixtures.cs`; modify the `.slnx`, `Directory.Packages.props`, sample `Program.cs`.

**Interfaces (produces):**

- Route `/fixture/{name}?mode=write|blocks&delay=0`: renders the editor for a named document from `Fixtures`; a button `#save` writes `ContentDocumentSerializer.Serialize(document)` into `<pre id="saved">`; `#changes` shows the `DocumentChanged` count; `#errors` lists error codes; `#replace-same` supplies an equal-by-value copy; `#replace-other` supplies different content.
- Fixtures: `empty`, `everything` (every node and block kind, nested lists, both list item forms), `unknown` (unknown properties and an unknown block), `post` (about 60 top-level nodes).
- Sample config `Sample:StrictCsp=true` sends `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:`.
- `SampleHost` fixture: sample on Kestrel on a free loopback port (`WebApplicationFactory.UseKestrel`), default `HubOptions`. Browser chosen by env `RICHCONTENT_BROWSER` (`chromium` default, `firefox`, `webkit`).
- Helpers `Task<string> SaveAsync(IPage)` and `Task<int> ChangeCountAsync(IPage)`. Every test fails on any console error, CSP violation or lost circuit.

Pin the current stable `Microsoft.Playwright`.

- [ ] **Step 1:** Write `Blocks_Fixture_SavesIdenticalJson`: open `everything` in blocks mode, save, assert the text equals the fixture's serialization and the change count is 0.
- [ ] **Step 2:** Run; expect failure (no route).
- [ ] **Step 3:** Implement. Install browsers with `pwsh <test bin>/playwright.ps1 install chromium firefox webkit`.
- [ ] **Step 4:** Run; expect pass.
- [ ] **Step 5:** Commit `test: browser harness for the editor`.

### Task 4: Writing surface: show, type, deliver

**Files:** Create `RichTextSurface.razor(.cs)`, `wwwroot/surface/{surface,dom,transport}.js`, a minimal `WritingBlock`; modify `ContentDocumentEditor` (add `public enum RichContentEditorMode { Blocks, Write }` and `[Parameter] Mode`), `Fixture.razor`; test `BrowserTests/SurfaceTypingTests.cs`.

**Interfaces:**

- Consumes: Tasks 1 to 3.
- Produces: `RichTextSurface` parameters `Nodes`, `NodesChanged`, `Disabled`, `Label`, `Profile` (`Prose` or `Inline`).
- JS exports: `attach(element, dotNetRef, options)`, `sync(element, keyedNodes, disabled, force)`, `command(element, name, argument)`, `dispose(element)`.
- .NET invokable: `ReceiveEdit(long sequence, int index, int count, string chunk)`; `CompositionChanged(bool composing)`.

Rules for `dom.js`: build elements with DOM APIs only; each top-level element carries `data-key`; read only the allowed tags (`b`/`strong`, `i`/`em`, `code`, `a`, `p`, `h2`-`h4`, `ul`, `ol`, `li`, `blockquote`), unwrapping all others; merge adjacent text; turn non-breaking spaces the browser inserted back into spaces, while keeping any that were in the stored text. A node is "changed" only when its read signature differs from the signature last synced for its key; the first element keeps a duplicated key and later ones get new keys. Send nothing during composition; send at `compositionend`.

`transport.js` sends each patch immediately in chunks of at most 4,000 characters and keeps it until all its calls resolve; on failure it retries the newest unacknowledged state until it succeeds or the surface is disposed.

- [ ] **Step 1: Write failing browser tests**

```text
Opening_AndClickingAround_RaisesNoChange        // everything + unknown, write mode: JSON identical, changes == 0
Typing_ChangesOnlyThatNode                      // every other top-level node's JSON identical
SaveClickedImmediatelyAfterTyping_HasTheText    // type then click #save in one script task; no waits
Enter_SplitsParagraph_NoLostOrDuplicateNodes
BrowserMarkup_IsReducedToModelNodes             // insert span[style], font, div, br via DOM + input event
SpacesAtBoundaries_AreOrdinarySpaces
StoredNonBreakingSpace_Survives
Composition_ThenSave_HasTextOnce                // CDP Input.imeSetComposition on chromium; skip elsewhere
HtmlInNodeText_IsShownAsText                    // "<img src=x onerror=...>" renders literally
LargeEdit_OnDefaultHubOptions_KeepsCircuit      // insert 40 KB, then 200 KB of ~200 paragraphs into `empty`
LargeInsert_ThenImmediateSave_HasAllText
NonAsciiAndQuoteHeavyLargeEdit_KeepsCircuit     // 100 KB of emoji, CJK, quotes and backslashes
StrictCsp_TypingWorks
```

- [ ] **Step 2:** Run; expect failures. **Step 3:** Implement. **Step 4:** Run on chromium, firefox and webkit; expect pass.
- [ ] **Step 5:** Commit `feat(editor): writing surface`.

### Task 5: Toolbar, keyboard, links, paste, undo

**Files:** Create `WritingToolbar.razor`, `wwwroot/surface/commands.js`; modify `surface.js`, `WritingBlock`; test `BrowserTests/SurfaceCommandTests.cs`.

**Interfaces (produces):** command names `bold`, `italic`, `code`, `link` (argument: URL, or empty to remove), `style` (`p`,`h2`,`h3`,`h4`), `bullets`, `numbers`, `quote`. .NET invokable `SelectionChanged(SurfaceSelection)` with `record SurfaceSelection(bool Bold, bool Italic, bool Code, string? LinkUrl, string Style, string List, bool Quote, bool InListItem)`; send only when it differs. .NET validates link URLs with `RichContentUrl.IsSafe` before calling `link`.

Keys, paste, drop, toolbar state and the `Inline` profile are as in the spec's "Toolbar and keyboard" and "Paste". Paste uses `text/plain` only: blank lines split paragraphs, single newlines become spaces. Insert a multi-paragraph paste as one undoable operation. Use `execCommand` for formatting and for inline code (`insertHTML` with a fragment built through DOM APIs and serialized, never from raw pasted text).

- [ ] **Step 1: Write failing browser tests**, one per command asserting the saved nodes, plus:

```text
ShortcutsBIKE_Work                  EnterOnEmptyListItem_LeavesList      TabNestsOnlyInsideList
TabOutsideList_MovesFocus           AltF10AndEscape_MoveToToolbarAndBack ShiftEnter_MakesNewParagraph
Heading_DisabledInListItem          NumberedList_RoundTripsOrdered       LinkBox_RefusesUnsafeUrlWithReason
Paste_IgnoresHtml_SplitsOnBlankLines                                     Drop_IsBlocked
Undo_And_Redo_ReachDocument         UndoAffectingAnotherSurface_ReachesDocument
SelectAllDelete_LeavesTypableEmptySurface_DocumentValid
RightArrowLeavesInlineCodeAndLink
InlineProfile_HasNoBlockCommands
```

- [ ] **Step 2:** Run; expect failures. **Step 3:** Implement. **Step 4:** Run on all three browsers. If inline code cannot stay inside native undo in one of them, stop and report; do not write a custom undo.
- [ ] **Step 5:** Commit `feat(editor): writing commands, links and paste`.

### Task 6: Cards, empty places and Insert

**Files:** Create the four cards; finish `WritingBlock`; modify `ContentDocumentEditor`, `CalloutBlockEditor`; test `Tests/WritingLayoutTests.cs` (bUnit) and `BrowserTests/CardTests.cs`.

**Interfaces:**

- `WritingBlock` parameters: `Nodes`, `NodesChanged`, `Disabled`, `MaxDepth`, `InsertChoices` (rich text block: code, image, table and the block choices; callout: code, image, table), `InsertBlockAt` (`EventCallback<(int NodeIndex, ContentBlock Block)>`, which uses `ContentDocumentOperations.SplitRichText`).
- `CodeCard` takes `Node`, `NodeChanged`, `Disabled`; its text area uses `transport.js` chunking, not Blazor `@oninput`. Tab inserts four spaces; Escape then Tab leaves.
- `TableCard` uses `RichTextSurface` with `Profile="Inline"` per cell and the existing `RichTextEditorOperations` table methods. Add `SetTableHeaderRow(RichTextNode table, bool header)`.
- Empty places: a surface shown before, between and after cards where there is no prose. It stores nothing until typed in. Between two non-text blocks, typing adds a new rich text block there.
- Segment components are keyed so that typing never recreates a surface, including when an empty place first becomes real content.

- [ ] **Step 1: Write failing tests**

```text
(bUnit) Segments_RenderTheRightComponents      LockedCard_ShowsText_NoInputs_MoveAndRemoveOnly
(bUnit) CalloutInsert_OffersOnlyCodeImageTable TableCell_InsertDisabled
(bUnit) SetTableHeaderRow_TogglesFirstRowOnly  RemovingNonEmptyCard_Asks_EmptyDoesNot
(browser) CodeCard_40KbEdit_OnDefaultHubOptions_KeepsCircuit_AndSavesIt
(browser) CodeCard_TabAndEscapeTab             ImageCard_PickAltCaption_PreviewShown
(browser) Table_EditCell_AddRowAndColumn       Insert_SplitsSurface_KeepsAllNodes
(browser) TypingInEmptyPlace_KeepsFocusAndCaret_AndStores
(browser) BetweenTwoCallouts_TypingCreatesRichTextBlock
(browser) BackspaceAtStartAfterCard_DoesNotDeleteCard
(browser) LockedNodes_StayByteIdentical_AfterEditingNeighbours
```

Removal confirmation is an inline "Remove? Yes / No" in the card, not `window.confirm`.

- [ ] **Step 2:** Run; expect failures. **Step 3:** Implement. **Step 4:** Run; expect pass.
- [ ] **Step 5:** Commit `feat(editor): cards and insert in write mode`.

### Task 7: Mode switch, replacement, errors, failure

**Files:** Modify `ContentDocumentEditor`, `RichTextSurface`, `transport.js`; test `Tests/EditorModeTests.cs`, `BrowserTests/ResilienceTests.cs`.

**Interfaces (produces):** `Mode`, `EventCallback<RichContentEditorMode> ModeChanged`, `bool ShowModeSwitch = true`. New error codes, merged into `ErrorsChanged`: `surface-unconvertible`.

A surface calls `sync(..., force: true)` only when its new nodes do not `Match` its buffer; otherwise it leaves the DOM alone and re-bases the buffer on the new objects. A patch that fails to apply, or breaks node or depth limits so badly it cannot be converted, leaves the DOM as typed and raises `surface-unconvertible` until a later patch applies. Documents that convert but break limits go to `Document` and show the normal rule errors.

- [ ] **Step 1: Write failing tests**

```text
(bUnit) Default_IsBlocks_ExistingMarkupTestsStillPass   ModeSwitch_RaisesModeChanged_NotDocumentChanged
(bUnit) HiddenSwitch_NotRendered                        ModuleImportFails_ShowsNotice_AndBlocks
(browser) SwitchingModesBothWays_RaisesNoChange
(browser) EqualReplacement_KeepsCaretUndoAndText        DifferentReplacement_Redraws_OnlyChangedSurface
(browser) Disabled_NothingEditable
(browser) Reconnect_SameCircuit_DeliversTypedText       // drop the socket, type, restore
(browser) OverNodeLimitPaste_KeepsTextOnScreen_ReportsError_CircuitAlive
(browser) OverTextLimitPaste_SameResult
(browser) ErrorsShowBesideTheirSurface
(browser) Dispose_RemovingBlock_NoConsoleErrors
```

- [ ] **Step 2:** Run; expect failures. **Step 3:** Implement. **Step 4:** Run all tests on all three browsers.
- [ ] **Step 5:** Commit `feat(editor): mode switch and resilience`.

### Task 8: Styling, docs, CI, release

**Files:** Modify `ContentDocumentEditor.razor.css`, `RichTextEditor.razor.css`, new component `.razor.css`, `README.md`, `CHANGELOG.md`, `.github/workflows/build.yml`; test `BrowserTests/ThemeTests.cs`.

**Interfaces (produces):** CSS custom properties `--rt-border`, `--rt-muted`, `--rt-accent`, `--rt-error`, `--rt-surface`, `--rt-button-bg`, `--rt-button-text`, defaults derived from `currentColor` with `color-mix` and `inherit`. Script-created elements are reached with `::deep`.

- [ ] **Step 1:** `Theme_DarkAndLightHost_TextContrastAtLeast4_5`: for each mode, with the sample page dark then light, compute contrast of hint, insert summary, button, error and locked-card text. Expect failure today for the insert summary on dark.
- [ ] **Step 2:** Style; run; expect pass. Look at both modes in a real browser at 1280 and 390 px wide.
- [ ] **Step 3:** README: replace the "no JavaScript interop" statement; document Write mode, keys, paste, CSS properties, CSP needs, the `DocumentChanged` ordering condition, and that Blocks mode with large fields needs a higher `HubOptions.MaximumReceiveMessageSize`. CHANGELOG 0.2.0.
- [ ] **Step 4:** CI: after build, install Playwright Chromium with dependencies; the existing test step then runs browser tests.
- [ ] **Step 5:** Full Release build and tests; browser tests on all three engines. `dotnet pack` and confirm the module is in the package under `staticwebassets`. Manual: IME in one browser, keyboard-only pass. Record anything not done (real Safari, screen readers).
- [ ] **Step 6:** Commit `feat: inline writing editor` with `+semver: minor`. Run a whole-branch review. Ask before pushing; the PR description must say that merging publishes and that squash must keep `+semver: minor`.

## After release

Each consuming app upgrades in its own repository and plan.
