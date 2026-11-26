using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>What one editor's surfaces share with its toolbar: which surface it acts on, and the state there.</summary>
internal sealed class WritingSession
{
    /// <summary>
    /// The longest a field sent in one message may be in Write mode, so that the message stays within SignalR's
    /// default limit. The content rules allow less in every such field.
    /// </summary>
    public const int FieldCharacters = 4000;

    public RichContentLimits Limits { get; set; } = RichContentLimits.Default;

    /// <summary>True while the editor is in Write mode, which the block editors inside it follow.</summary>
    public bool Writing { get; set; }

    private readonly List<ScriptEditorBase> _rejected = [];
    private readonly List<ScriptEditorBase> _parts = [];

    public void Join(ScriptEditorBase part) => _parts.Add(part);

    public void Leave(ScriptEditorBase part)
    {
        if (_parts.Remove(part))
        {
            ReportReadOnly();
        }
    }

    /// <summary>
    /// True once nothing that a script edits can still hold text .NET has not had: each part is read-only in the
    /// browser, and what it showed then has been sent, or it never could be typed into.
    /// </summary>
    public bool AllReadOnly => _parts.TrueForAll(part => part.IsConfirmedReadOnly);

    /// <summary>Raised when <see cref="AllReadOnly"/> may have become true.</summary>
    public event Action? ReadOnlyChanged;

    public void ReportReadOnly() => ReadOnlyChanged?.Invoke();

    /// <summary>
    /// The surfaces, table cells and code cards that show something which could not be taken into the document.
    /// </summary>
    public IReadOnlyList<ScriptEditorBase> Rejected => _rejected;

    /// <summary>Raised when one starts or stops showing such content. True if it stopped by being disposed.</summary>
    public event Action<bool>? RejectionsChanged;

    public void SetRejected(ScriptEditorBase part, bool rejected, bool disposed = false)
    {
        if (rejected ? _rejected.Contains(part) : !_rejected.Remove(part))
        {
            return;
        }

        if (rejected)
        {
            _rejected.Add(part);
        }

        RejectionsChanged?.Invoke(disposed);
    }

    /// <summary>True when a part of a writing block, such as a card, shows content the document does not have.</summary>
    public bool HasRejectedIn(IWritingPart part) => _rejected.Exists(editor => ReferenceEquals(editor.WritingPart, part));

    /// <summary>True when a block, by its id, shows content the document does not have.</summary>
    public bool HasRejectedInBlock(string? blockId) =>
        _rejected.Exists(editor => string.Equals(editor.BlockId, blockId, StringComparison.Ordinal));

    /// <summary>Raised when a script of the writing view cannot be loaded or run.</summary>
    public event Action? ScriptFailed;

    public void ReportScriptFailure() => ScriptFailed?.Invoke();

    /// <summary>The surface that last had the cursor, which is the one the toolbar acts on.</summary>
    public RichTextSurface? Surface { get; private set; }

    /// <summary>The state at that surface's cursor; null exactly when <see cref="Surface"/> is.</summary>
    public SurfaceSelection? Selection { get; private set; }

    public event Action? Changed;

    /// <summary>Raised when the link box is asked for, for the surface the toolbar acts on.</summary>
    public event Action? LinkRequested;

    public void RequestLink(RichTextSurface surface)
    {
        if (ReferenceEquals(surface, Surface))
        {
            LinkRequested?.Invoke();
        }
    }

    /// <summary>Raised when the script has applied what the link box asked for.</summary>
    public event Action<RichTextSurface>? LinkApplied;

    public void CompleteLink(RichTextSurface surface) => LinkApplied?.Invoke(surface);

    public void Report(RichTextSurface surface, SurfaceSelection selection)
    {
        if (ReferenceEquals(surface, Surface) && selection == Selection)
        {
            return;
        }

        (Surface, Selection) = (surface, selection);
        Changed?.Invoke();
    }

    public void Forget(RichTextSurface surface)
    {
        if (!ReferenceEquals(surface, Surface))
        {
            return;
        }

        (Surface, Selection) = (null, null);
        Changed?.Invoke();
    }
}
