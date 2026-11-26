using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>What one editor's surfaces share with its toolbar: which surface it acts on, and the state there.</summary>
internal sealed class WritingSession
{
    public RichContentLimits Limits { get; set; } = RichContentLimits.Default;

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
