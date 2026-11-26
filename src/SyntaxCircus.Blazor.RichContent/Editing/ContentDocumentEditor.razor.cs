using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.Blazor.RichContent.Editing.Writing;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>
/// Block editor for a <see cref="ContentDocument"/>. The document is controlled: every edit raises
/// <see cref="DocumentChanged"/> and the host feeds the new value back in. Validation errors are recomputed whenever the document changes.
/// </summary>
public partial class ContentDocumentEditor
{
    private sealed record BlockChoice(string Label, bool NeedsMedia, bool IsText, Func<ContentBlock> Create);

    private ILookup<int?, RichContentError> _errorsByBlock = Array.Empty<RichContentError>().ToLookup(e => e.BlockIndex);
    private IReadOnlyList<RichContentError> _errors = [];
    private ContentDocument? _lastValidated;
    private RichContentLimits? _lastValidatedLimits;

    private readonly WritingSession _session = new();

    private sealed record ShownBlock(ContentBlock Block, int Index, object Key, bool IsPlace);

    // Somewhere to type where no rich text block adjoins, between the blocks with these ids.
    private sealed record EmptyPlace(string Id, string? PreviousId, string? NextId);

    private List<EmptyPlace> _places = [];

    private bool _blockRefused;

    [Inject] private ContentBlockRegistry Registry { get; set; } = default!;

    [Inject] private RichContentEditorOptions Options { get; set; } = default!;

    [Parameter, EditorRequired] public ContentDocument Document { get; set; } = default!;

    [Parameter] public EventCallback<ContentDocument> DocumentChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    /// <summary>
    /// The view: bind it with <c>@bind-Mode</c> to follow the author's choice. <see cref="RichContentEditorMode.Write"/>
    /// edits prose as continuous text. It needs interactive rendering, and a <see cref="DocumentChanged"/> handler that
    /// stores the document before its first incomplete await, as <c>@bind-Document</c> does. A change takes effect once
    /// the browser has shown the editor read-only, so that nothing typed is lost. It is refused while something on
    /// screen could not be saved, and <see cref="ModeChanged"/> then gives the view still shown. The editor shows the
    /// block view if Write mode's script cannot be loaded, and raises <see cref="ModeChanged"/>.
    /// </summary>
    [Parameter] public RichContentEditorMode Mode { get; set; } = RichContentEditorMode.Blocks;

    /// <summary>Raised when the view changes other than because <see cref="Mode"/> did, or a change was refused.</summary>
    [Parameter] public EventCallback<RichContentEditorMode> ModeChanged { get; set; }

    /// <summary>Whether to offer the Write and Blocks switch. It works while the editor is disabled.</summary>
    [Parameter] public bool ShowModeSwitch { get; set; } = true;

    [Parameter] public string Label { get; set; } = "Post body";

    [Parameter] public RichContentLimits? Limits { get; set; }

    [Parameter] public EventCallback<IReadOnlyList<RichContentError>> ErrorsChanged { get; set; }

    // In Write mode, edits can arrive before the host has passed the last document back down, so each one must
    // build on the one before.
    private ContentDocument? _parameter;
    private ContentDocument? _latest;

    private ContentDocument Latest => Writing && _latest is not null ? _latest : Document;

    private ImmutableArray<ContentBlock> Blocks => Document?.Blocks.IsDefault ?? true ? [] : Document.Blocks;

    private int MaxDepth => (Limits ?? RichContentLimits.Default).Depth;

    private bool HasMediaPicker => Options.MediaPicker is not null;

    private IEnumerable<RichContentError> DocumentErrors =>
        _errors.Where(e => e.BlockIndex is null);

    private IEnumerable<BlockChoice> Choices
    {
        get
        {
            yield return new("Text", false, true, () => new RichTextBlock(NewId(), [RichTextEditorOperations.Create(RichTextNodeKind.Paragraph)]));
            yield return new("Gallery", true, false, () => new GalleryBlock(NewId(), [], null));
            yield return new("Callout", false, false, () => new CalloutBlock(NewId(), CalloutTone.Info, null, [RichTextEditorOperations.Create(RichTextNodeKind.Paragraph)]));
            yield return new("Call to action", false, false, () => new CtaBlock(NewId(), "", null, "", "https://"));
            foreach (var registration in Registry.Registrations)
            {
                var captured = registration;
                yield return new(captured.Label, false, false, () => new ExtensionBlock(NewId(), captured.Type, captured.CreateDefault()));
            }
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        _session.Limits = Limits ?? RichContentLimits.Default;
        TakeMode();
        _session.Writing = Writing;

        if (!ReferenceEquals(Document, _parameter))
        {
            _parameter = _latest = Document;
        }

        if (Document is null)
        {
            return;
        }

        // Validation walks the whole tree; skip it when neither the (immutable) document nor the limits changed.
        if (!ReferenceEquals(Document, _lastValidated) || !Equals(Limits, _lastValidatedLimits))
        {
            _lastValidated = Document;
            _lastValidatedLimits = Limits;
            _errors = RichContentRules.Validate(Document, Limits);
            _errorsByBlock = _errors.ToLookup(e => e.BlockIndex);
        }

        await PublishErrorsAsync();
    }

    // The block's errors that are not already shown beside one of its surfaces or cards.
    private List<RichContentError> ErrorsFor(int index, ContentBlock block)
    {
        List<RichContentError> errors = [.. _errorsByBlock[index]];
        var nodes = block switch
        {
            RichTextBlock rich => rich.Nodes,
            CalloutBlock callout => callout.Nodes,
            _ => [],
        };
        if (Writing && errors.Count > 0 && !nodes.IsDefaultOrEmpty)
        {
            var beside = NodeErrors.For(nodes, _session.Limits);
            errors.RemoveAll(error => beside.Contains(error.Message));
        }

        return errors;
    }

    // Duplicate keys throw, and duplicate ids are a validation error the editor must still display.
    private object KeyFor(ContentBlock block, int index) =>
        block.Id is not null && Blocks.Count(b => string.Equals(b.Id, block.Id, StringComparison.Ordinal)) == 1 ? block.Id : index;

    private Task Update(ContentDocument document)
    {
        // In the block view the next edit builds on what the host passes back, which may refuse this one.
        if (Writing)
        {
            _latest = document;
        }

        return DocumentChanged.InvokeAsync(document);
    }

    // Where the block that was drawn at an index is now, or -1 if it has gone. In Write mode, blocks can have moved
    // since they were drawn: an edit or a click then must not reach whatever is at the old index.
    private int IndexOfDrawn(int index, Type type, string id)
    {
        if (!Writing)
        {
            return index;
        }

        var blocks = Latest.Blocks.IsDefault ? [] : Latest.Blocks;
        bool IsTarget(ContentBlock block) => block.GetType() == type && string.Equals(block.Id, id, StringComparison.Ordinal);
        return index >= 0 && index < blocks.Length && IsTarget(blocks[index]) ? index : blocks.ToList().FindIndex(IsTarget);
    }

    private int IndexOfDrawn(int index, ContentBlock drawn) => IndexOfDrawn(index, drawn.GetType(), drawn.Id);

    // Nothing here may await first: the edits of a callout's surfaces come this way.
    private Task Replace(int index, ContentBlock block)
    {
        var at = IndexOfDrawn(index, block);
        return at < 0 ? Task.CompletedTask : Update(ContentDocumentOperations.Replace(Latest, at, block));
    }

    private Task MoveBlock(int index, ContentBlock drawn, int delta)
    {
        var at = IndexOfDrawn(index, drawn);
        return at < 0 || CouldRedrawUnsaved() ? Task.CompletedTask : Update(ContentDocumentOperations.Move(Latest, at, delta));
    }

    private Task RemoveBlock(int index, ContentBlock drawn)
    {
        var at = IndexOfDrawn(index, drawn);
        return at < 0 || CouldRedrawUnsaved() ? Task.CompletedTask : Update(ContentDocumentOperations.Remove(Latest, at));
    }

    // Blocks whose ids are not unique are keyed by their position, so that moving one draws others again.
    private bool CouldRedrawUnsaved()
    {
        _blockRefused = _session.Rejected.Count > 0
            && Blocks.Select(block => block.Id).Distinct(StringComparer.Ordinal).Count() != Blocks.Length;
        return _blockRefused;
    }

    private bool HoldsSomething(int index, ContentBlock drawn)
    {
        var at = IndexOfDrawn(index, drawn);
        // What could not be saved is stored nowhere, and is only to be lost on purpose.
        return at >= 0 && (_session.HasRejectedInBlock(drawn.Id) || !WritingContent.IsEmpty(Latest.Blocks[at]));
    }

    // An edit from a writing surface. Nothing here may await first: see ScriptEditorBase.ReceiveEdit.
    private Task WriteNodes(int index, string id, ImmutableArray<RichTextNode> nodes)
    {
        var at = IndexOfDrawn(index, typeof(RichTextBlock), id);
        if (at >= 0)
        {
            return Replace(at, (RichTextBlock)Latest.Blocks[at] with { Nodes = nodes });
        }

        // The first thing typed or inserted in an empty place makes a block there. Otherwise the block has gone.
        return nodes.IsEmpty || PlaceOf(id) is not { } place
            ? Task.CompletedTask
            : Update(ContentDocumentOperations.Add(Latest, IndexOf(place), new RichTextBlock(id, nodes)));
    }

    // A block inserted from a writing surface, before the node at nodeIndex. See WritingBlock.InsertBlockAt.
    private Task InsertBlockAt(int index, string id, int nodeIndex, ContentBlock inserted)
    {
        var at = IndexOfDrawn(index, typeof(RichTextBlock), id);
        if (at < 0)
        {
            return PlaceOf(id) is { } place
                ? Update(ContentDocumentOperations.Add(Latest, IndexOf(place), inserted))
                : Task.CompletedTask;
        }

        // At either end there is nothing to split, and splitting would give the block a new identity.
        var nodes = ((RichTextBlock)Latest.Blocks[at]).Nodes;
        var length = nodes.IsDefault ? 0 : nodes.Length;
        return Update(nodeIndex <= 0 ? ContentDocumentOperations.Add(Latest, at, inserted)
            : nodeIndex >= length ? ContentDocumentOperations.Add(Latest, at + 1, inserted)
            : ContentDocumentOperations.SplitRichText(Latest, at, nodeIndex, inserted));
    }

    private IReadOnlyList<InsertChoice> TextInsertChoices =>
    [
        .. InsertChoice.ForNodes(MaxDepth),
        .. Choices.Where(choice => !choice.IsText).Select(choice => new InsertChoice(choice.Label, choice.NeedsMedia, null, choice.Create)),
    ];

    // The blocks as drawn. In Write mode, wherever two neighbours (or an end of the document) leave no rich text
    // block to type in, an empty place is drawn too. It is not part of the document until something is typed there.
    private List<ShownBlock> ShownBlocks()
    {
        var blocks = Blocks;
        var shown = new List<ShownBlock>(blocks.Length);
        var places = new List<EmptyPlace>();
        for (var gap = 0; gap <= blocks.Length; gap++)
        {
            var before = gap > 0 ? blocks[gap - 1] : null;
            var after = gap < blocks.Length ? blocks[gap] : null;
            if (Writing && before is not RichTextBlock && after is not RichTextBlock)
            {
                // A place between the same neighbours as last time is the same place. Its id is the one the block
                // will have, so it is also its key, before and after.
                var place = _places.Find(old => old.PreviousId == before?.Id && old.NextId == after?.Id && !places.Contains(old))
                    ?? new EmptyPlace(NewId(), before?.Id, after?.Id);
                places.Add(place);
                shown.Add(new(new RichTextBlock(place.Id, []), -1, place.Id, IsPlace: true));
            }

            if (after is not null)
            {
                shown.Add(new(after, gap, KeyFor(after, gap), IsPlace: false));
            }
        }

        // A place that shows what could not be saved stays, whatever has become of its neighbours.
        foreach (var place in _places.Where(old => !places.Contains(old) && _session.HasRejectedInBlock(old.Id)
            && !blocks.Any(block => block.Id == old.Id)))
        {
            places.Add(place);
            var gap = IndexOf(place);
            var at = shown.FindIndex(entry => !entry.IsPlace && entry.Index >= gap);
            shown.Insert(at < 0 ? shown.Count : at, new(new RichTextBlock(place.Id, []), -1, place.Id, IsPlace: true));
        }

        _places = places;
        return shown;
    }

    private EmptyPlace? PlaceOf(string id) => _places.Find(place => string.Equals(place.Id, id, StringComparison.Ordinal));

    // Where in the latest document a place is.
    private int IndexOf(EmptyPlace place)
    {
        var blocks = (Latest.Blocks.IsDefault ? [] : Latest.Blocks).ToList();
        if (place.PreviousId is { } previous && blocks.FindIndex(block => block.Id == previous) is var found && found >= 0)
        {
            return found + 1;
        }

        if (place.NextId is { } next && blocks.FindIndex(block => block.Id == next) is var following && following >= 0)
        {
            return following;
        }

        return place.PreviousId is null ? 0 : blocks.Count;
    }

    private Dictionary<string, object?> ExtensionParameters(ExtensionBlock extension, int index) => new()
    {
        [nameof(ExtensionBlockEditorBase.Data)] = extension.Data,
        [nameof(ExtensionBlockEditorBase.DataChanged)] = EventCallback.Factory.Create<System.Text.Json.JsonElement>(this, data => Replace(index, extension with { Data = data })),
        [nameof(ExtensionBlockEditorBase.Disabled)] = ReadOnly,
    };

    private string TitleOf(ContentBlock block) => block switch
    {
        RichTextBlock => "Text",
        GalleryBlock => "Gallery",
        CalloutBlock => "Callout",
        CtaBlock => "Call to action",
        ExtensionBlock extension when Registry.TryGet(extension.Type, out var registration) => registration.Label,
        ExtensionBlock extension => extension.Type,
        _ => "Unsupported block",
    };

    private static string KindOf(ContentBlock block) => block switch
    {
        RichTextBlock => "rich-text",
        GalleryBlock => "gallery",
        CalloutBlock => "callout",
        CtaBlock => "cta",
        ExtensionBlock => "extension",
        _ => "unknown",
    };

    private static string NewId() => ContentDocumentOperations.NewBlockId();
}
