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

    private Task Replace(int index, ContentBlock block) => Update(ContentDocumentOperations.Replace(Latest, index, block));

    // An edit from a writing surface. Nothing here may await first: see RichTextSurface.ReceiveEdit.
    private Task WriteNodes(int index, string id, ImmutableArray<RichTextNode> nodes)
    {
        var blocks = Latest.Blocks;
        bool IsTarget(ContentBlock block) => block is RichTextBlock && string.Equals(block.Id, id, StringComparison.Ordinal);
        if (index >= blocks.Length || !IsTarget(blocks[index]))
        {
            // It has moved, or has been removed.
            index = blocks.ToList().FindIndex(IsTarget);
        }

        return index < 0
            ? Task.CompletedTask
            : Replace(index, (RichTextBlock)blocks[index] with { Nodes = nodes });
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
