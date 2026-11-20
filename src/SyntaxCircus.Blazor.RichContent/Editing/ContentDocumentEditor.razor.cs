using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>
/// Block editor for a <see cref="ContentDocument"/>. The document is controlled: every edit raises
/// <see cref="DocumentChanged"/> and the host feeds the new value back in. Validation errors are recomputed whenever the document changes.
/// </summary>
public partial class ContentDocumentEditor
{
    private sealed record BlockChoice(string Label, bool NeedsMedia, bool IsText, Func<ContentBlock> Create);

    private ILookup<string?, RichContentError> _errorsByBlock = Array.Empty<RichContentError>().ToLookup(e => e.BlockId);
    private IReadOnlyList<RichContentError> _errors = [];
    private IReadOnlyList<RichContentError> _lastPublished = [];
    private bool _errorsPublished;

    [Inject] private ContentBlockRegistry Registry { get; set; } = default!;

    [Inject] private RichContentEditorOptions Options { get; set; } = default!;

    [Parameter, EditorRequired] public ContentDocument Document { get; set; } = default!;

    [Parameter] public EventCallback<ContentDocument> DocumentChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string Label { get; set; } = "Post body";

    [Parameter] public RichContentLimits? Limits { get; set; }

    [Parameter] public EventCallback<IReadOnlyList<RichContentError>> ErrorsChanged { get; set; }

    private ImmutableArray<ContentBlock> Blocks => Document.Blocks.IsDefault ? [] : Document.Blocks;

    private int MaxDepth => (Limits ?? RichContentLimits.Default).Depth;

    private bool HasMediaPicker => Options.MediaPicker is not null;

    private IEnumerable<RichContentError> DocumentErrors =>
        _errors.Where(e => e.BlockId is null || !Blocks.Any(b => string.Equals(b.Id, e.BlockId, StringComparison.Ordinal)));

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
        _errors = RichContentRules.Validate(Document, Limits);
        _errorsByBlock = _errors.ToLookup(e => e.BlockId);
        if (!_errorsPublished || !_errors.SequenceEqual(_lastPublished))
        {
            _errorsPublished = true;
            _lastPublished = _errors;
            await ErrorsChanged.InvokeAsync(_errors);
        }
    }

    private IReadOnlyList<RichContentError> ErrorsFor(string id) => [.. _errorsByBlock[id]];

    // Duplicate keys throw, and duplicate ids are a validation error the editor must still display.
    private object KeyFor(ContentBlock block, int index) =>
        block.Id is not null && Blocks.Count(b => string.Equals(b.Id, block.Id, StringComparison.Ordinal)) == 1 ? block.Id : index;

    private Task Update(ContentDocument document) => DocumentChanged.InvokeAsync(document);

    private Task Replace(ContentBlock block) => Update(ContentDocumentOperations.Replace(Document, block));

    private Dictionary<string, object?> ExtensionParameters(ExtensionBlock extension) => new()
    {
        [nameof(ExtensionBlockEditorBase.Data)] = extension.Data,
        [nameof(ExtensionBlockEditorBase.DataChanged)] = EventCallback.Factory.Create<System.Text.Json.JsonElement>(this, data => Replace(extension with { Data = data })),
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
