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
    private IReadOnlyList<RichContentError> _lastPublished = [];
    private bool _errorsPublished;
    private ContentDocument? _lastValidated;
    private RichContentLimits? _lastValidatedLimits;

    private readonly WritingSession _session = new();

    private sealed record ShownBlock(ContentBlock Block, int Index, object Key, bool IsPlace);

    // Somewhere to type where no rich text block adjoins, between the blocks with these ids.
    private sealed record EmptyPlace(string Id, string? PreviousId, string? NextId);

    private List<EmptyPlace> _places = [];

    [Inject] private ContentBlockRegistry Registry { get; set; } = default!;

    [Inject] private RichContentEditorOptions Options { get; set; } = default!;

    [Parameter, EditorRequired] public ContentDocument Document { get; set; } = default!;

    [Parameter] public EventCallback<ContentDocument> DocumentChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    /// <summary>
    /// <see cref="RichContentEditorMode.Write"/> edits prose as continuous text. It needs interactive rendering, and
    /// a <see cref="DocumentChanged"/> handler that stores the document before its first incomplete await, as
    /// <c>@bind-Document</c> does.
    /// </summary>
    [Parameter] public RichContentEditorMode Mode { get; set; } = RichContentEditorMode.Blocks;

    [Parameter] public string Label { get; set; } = "Post body";

    [Parameter] public RichContentLimits? Limits { get; set; }

    [Parameter] public EventCallback<IReadOnlyList<RichContentError>> ErrorsChanged { get; set; }

    // In Write mode, edits can arrive before the host has passed the last document back down, so each one must
    // build on the one before.
    private ContentDocument? _parameter;
    private ContentDocument? _latest;

    private ContentDocument Latest => Mode == RichContentEditorMode.Write && _latest is not null ? _latest : Document;

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
        _session.Writing = Mode == RichContentEditorMode.Write;

        if (!ReferenceEquals(Document, _parameter))
        {
            _parameter = _latest = Document;
        }

        // Validation walks the whole tree; skip it when neither the (immutable) document nor the limits changed.
        if (Document is null || (ReferenceEquals(Document, _lastValidated) && Equals(Limits, _lastValidatedLimits)))
        {
            return;
        }

        _lastValidated = Document;
        _lastValidatedLimits = Limits;
        _errors = RichContentRules.Validate(Document, Limits);
        _errorsByBlock = _errors.ToLookup(e => e.BlockIndex);
        if (!_errorsPublished || !_errors.SequenceEqual(_lastPublished))
        {
            _errorsPublished = true;
            _lastPublished = _errors;
            await ErrorsChanged.InvokeAsync(_errors);
        }
    }

    private IReadOnlyList<RichContentError> ErrorsFor(int index) => [.. _errorsByBlock[index]];

    // Duplicate keys throw, and duplicate ids are a validation error the editor must still display.
    private object KeyFor(ContentBlock block, int index) =>
        block.Id is not null && Blocks.Count(b => string.Equals(b.Id, block.Id, StringComparison.Ordinal)) == 1 ? block.Id : index;

    private Task Update(ContentDocument document)
    {
        _latest = document;
        return DocumentChanged.InvokeAsync(document);
    }

    // Where the block that was drawn at an index is now, or -1 if it has gone. In Write mode, blocks can have moved
    // since they were drawn: an edit or a click then must not reach whatever is at the old index.
    private int IndexOfDrawn(int index, Type type, string id)
    {
        if (Mode != RichContentEditorMode.Write)
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
        return at < 0 ? Task.CompletedTask : Update(ContentDocumentOperations.Move(Latest, at, delta));
    }

    private Task RemoveBlock(int index, ContentBlock drawn)
    {
        var at = IndexOfDrawn(index, drawn);
        return at < 0 ? Task.CompletedTask : Update(ContentDocumentOperations.Remove(Latest, at));
    }

    private bool HoldsSomething(int index, ContentBlock drawn)
    {
        var at = IndexOfDrawn(index, drawn);
        return at >= 0 && !WritingContent.IsEmpty(Latest.Blocks[at]);
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
            if (Mode == RichContentEditorMode.Write && before is not RichTextBlock && after is not RichTextBlock)
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
        [nameof(ExtensionBlockEditorBase.Disabled)] = Disabled,
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
