using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SyntaxCircus.Blazor.RichContent.Editing.Writing;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

/// <summary>
/// Edits a run of rich-text nodes as continuous formatted text. It needs interactive rendering and loads a
/// JavaScript module from this package. Every node must be one the surface can reproduce exactly.
/// <see cref="NodesChanged"/> is raised only for the author's edits; nodes the author did not change keep their
/// original objects.
/// </summary>
public partial class RichTextSurface
{
    internal const string ModulePath = "./_content/SyntaxCircus.Blazor.RichContent/surface/surface.js";

    private SurfaceBuffer _buffer = default!;

    // What the buffer holds, and the browser too once the latest redraw has reached it.
    private ImmutableArray<RichTextNode> _current = [];

    // Absent for a surface used on its own, which has no toolbar to keep informed.
    [CascadingParameter] private WritingSession? Session { get; set; }

    /// <summary>Where Insert puts things for this surface; null where nothing can be inserted, as in a table cell.</summary>
    [CascadingParameter] internal IInsertPlace? InsertPlace { get; private set; }

    [Parameter] public ImmutableArray<RichTextNode> Nodes { get; set; } = [];

    /// <summary>
    /// Raised for each edit. A handler must store the nodes before its first incomplete await, so that a click made
    /// after typing sees them.
    /// </summary>
    [Parameter] public EventCallback<ImmutableArray<RichTextNode>> NodesChanged { get; set; }

    [Parameter] public string Label { get; set; } = "Rich text";

    /// <summary>Fixed for the life of the component.</summary>
    [Parameter] public RichTextSurfaceProfile Profile { get; set; }

    private ImmutableArray<RichTextNode> Items => Nodes.IsDefault ? [] : Nodes;

    private protected override string ScriptPath => ModulePath;

    private protected override string FocusFunction => "restore";

    private protected override object AttachOptions =>
        new { profile = Profile == RichTextSurfaceProfile.Inline ? "inline" : "prose" };

    private protected override int ContentGeneration => _buffer.Generation;

    protected override void OnInitialized() => _buffer = new SurfaceBuffer(Profile == RichTextSurfaceProfile.Inline);

    private protected override bool TakeParameters()
    {
        var nodes = Items;
        if (SameObjects(nodes, _current))
        {
            return false;
        }

        // The same values in new objects leave the text, the caret and the undo history alone.
        if (!_buffer.TryRebase(nodes))
        {
            return true;
        }

        _current = nodes;
        return false;
    }

    private protected override object ResetContent()
    {
        _current = Items;
        return _buffer.Reset(_current);
    }

    private protected override ReceivedEdit ReadEdit(string payload)
    {
        var patch = JsonSerializer.Deserialize<SurfacePatch>(payload, JsonSerializerOptions.Web)
            ?? throw new FormatException("There is no patch.");
        return new(patch.Generation, () => Apply(patch));
    }

    private Func<Task>? Apply(SurfacePatch patch)
    {
        var nodes = _buffer.Apply(patch);
        if (SameObjects(nodes, _current))
        {
            return null;
        }

        _current = nodes;
        return () => NodesChanged.InvokeAsync(nodes);
    }

    /// <summary>
    /// Told the formatting at the cursor when it changes, or when the cursor comes to this surface. For the script only.
    /// </summary>
    [JSInvokable]
    public Task SelectionChanged(SurfaceSelection selection)
    {
        if (!IsDisposed && selection is not null)
        {
            Session?.Report(this, selection);
        }

        return Task.CompletedTask;
    }

    /// <summary>Told that the author asked for the link box with the keyboard. For the script only.</summary>
    [JSInvokable]
    public Task LinkRequested()
    {
        if (!IsDisposed)
        {
            Session?.RequestLink(this);
        }

        return Task.CompletedTask;
    }

    /// <summary>Told that the script has made or removed the link the link box asked for. For the script only.</summary>
    [JSInvokable]
    public Task LinkApplied()
    {
        if (!IsDisposed)
        {
            Session?.CompleteLink(this);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Told that the author has gone somewhere that is neither a surface nor the toolbar, so that the toolbar stops
    /// acting on this one. For the script only.
    /// </summary>
    [JSInvokable]
    public Task SurfaceLeft()
    {
        if (!IsDisposed)
        {
            Session?.Forget(this);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Told that the author chose something to insert. <paramref name="afterKey"/> names the last node at or before
    /// the cursor, or is null with the cursor before them all. <paramref name="sent"/> is the sequence number of the
    /// newest edit the browser has sent since it was drawn, or 0. For the script only.
    /// </summary>
    /// <remarks>Inserting redraws this surface. So nothing is done unless everything typed here has been applied.</remarks>
    [JSInvokable]
    public async Task InsertRequested(int choice, string? afterKey, long sent)
    {
        if (IsDisposed || Disabled || InsertPlace is not { } place || HasRejectedEdit || RedrawIsUnconfirmed
            || (sent != 0 && sent != AppliedSequence) || _buffer.CountThrough(afterKey) is not { } before)
        {
            return;
        }

        try
        {
            await place.InsertAsync(choice, before);
        }
        catch (Exception exception)
        {
            // The host's handler failed, which would otherwise only fail a call the script does not wait for.
            await DispatchExceptionAsync(exception);
        }
    }

    /// <summary>Runs a writing command where the cursor was last in this surface.</summary>
    internal Task CommandAsync(string name, string argument) => InvokeScriptAsync("command", name, argument);

    /// <summary>Gives the surface the focus again, with the cursor where it was.</summary>
    internal Task RestoreAsync() => InvokeScriptAsync("restore");

    private protected override void OnDisposing() => Session?.Forget(this);

    private static bool SameObjects(ImmutableArray<RichTextNode> left, ImmutableArray<RichTextNode> right) =>
        left.Length == right.Length && left.AsSpan().SequenceEqual(right.AsSpan(), ReferenceEqualityComparer.Instance);
}
