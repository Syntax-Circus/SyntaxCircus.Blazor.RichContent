using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
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

    private readonly ChunkAssembler _assembler = new();
    private SurfaceBuffer _buffer = default!;
    private ElementReference _element;
    private IJSObjectReference? _module;
    private DotNetObjectReference<RichTextSurface>? _self;

    // What the buffer, and so the browser, currently holds.
    private ImmutableArray<RichTextNode> _current = [];
    private bool _syncedDisabled;
    private bool _ready;
    private bool _loadFailed;
    private bool _disposed;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public ImmutableArray<RichTextNode> Nodes { get; set; } = [];

    /// <summary>
    /// Raised for each edit. A handler must store the nodes before its first incomplete await, so that a click made
    /// after typing sees them.
    /// </summary>
    [Parameter] public EventCallback<ImmutableArray<RichTextNode>> NodesChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string Label { get; set; } = "Rich text";

    /// <summary>Fixed for the life of the component.</summary>
    [Parameter] public RichTextSurfaceProfile Profile { get; set; }

    /// <summary>True while the author is composing text with an input method. No edits arrive meanwhile.</summary>
    internal bool IsComposing { get; private set; }

    private ImmutableArray<RichTextNode> Items => Nodes.IsDefault ? [] : Nodes;

    protected override void OnInitialized() => _buffer = new SurfaceBuffer(Profile == RichTextSurfaceProfile.Inline);

    protected override async Task OnParametersSetAsync()
    {
        // Until the surface is ready, the first synchronization will take the latest parameters.
        if (!_ready || _disposed)
        {
            return;
        }

        var nodes = Items;
        var redraw = false;
        if (!SameObjects(nodes, _current))
        {
            // The same values in new objects leave the text, the caret and the undo history alone.
            if (_buffer.TryRebase(nodes))
            {
                _current = nodes;
            }
            else
            {
                redraw = true;
            }
        }

        if (redraw || Disabled != _syncedDisabled)
        {
            await Interop(() => SyncAsync(redraw));
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            await Interop(AttachAsync);
        }
        catch (JSException)
        {
            // A seam for falling back to the block editor when the script cannot be loaded or run.
            _loadFailed = true;
            StateHasChanged();
        }
    }

    private async ValueTask AttachAsync()
    {
        var module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
        if (_disposed)
        {
            await module.DisposeAsync();
            return;
        }

        _module = module;
        _self = DotNetObjectReference.Create(this);
        var profile = Profile == RichTextSurfaceProfile.Inline ? "inline" : "prose";
        await module.InvokeVoidAsync("attach", _element, _self, new { profile });
        if (_disposed)
        {
            return;
        }

        _ready = true;
        await SyncAsync(redraw: true);
    }

    private async ValueTask SyncAsync(bool redraw)
    {
        var disabled = Disabled;
        _syncedDisabled = disabled;
        IReadOnlyList<KeyedNode>? keyed = null;
        if (redraw)
        {
            // From here on, patches made against the old content are dropped.
            _current = Items;
            keyed = _buffer.Reset(_current);
        }

        if (_module is { } module)
        {
            await module.InvokeVoidAsync("sync", _element, keyed, disabled, redraw, _buffer.Generation);
        }
    }

    // A lost connection or a component on its way out is not an error here.
    private async ValueTask Interop(Func<ValueTask> call)
    {
        try
        {
            await call();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    /// <summary>
    /// One chunk of a patch from the browser. False means the patch was rejected, so the browser keeps it as
    /// unacknowledged and sends it again with the author's next change. For the script only.
    /// </summary>
    /// <remarks>
    /// Nothing here may await before <see cref="NodesChanged"/> is raised: the next message from the browser, such
    /// as a click on Save, is handled as soon as this method first yields.
    /// </remarks>
    [JSInvokable]
    public Task<bool> ReceiveEdit(long sequence, int index, int count, string chunk)
    {
        if (_disposed)
        {
            return Task.FromResult(true);
        }

        ImmutableArray<RichTextNode> nodes;
        try
        {
            if (_assembler.Add(sequence, index, count, chunk) is not { } payload)
            {
                return Task.FromResult(true);
            }

            var patch = JsonSerializer.Deserialize<SurfacePatch>(payload, JsonSerializerOptions.Web)
                ?? throw new FormatException("There is no patch.");

            // Sent before the browser learned that the content had been replaced. Generations start at 1, so a
            // payload without one is not old but wrong.
            if (patch.Generation > 0 && patch.Generation < _buffer.Generation)
            {
                return Task.FromResult(true);
            }

            nodes = _buffer.Apply(patch);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            OnEditRejected();
            return Task.FromResult(false);
        }

        OnEditApplied();
        if (SameObjects(nodes, _current))
        {
            return Task.FromResult(true);
        }

        _current = nodes;
        return DeliverAsync(nodes);
    }

    /// <summary>Told when the author starts and stops composing text. For the script only.</summary>
    [JSInvokable]
    public Task CompositionChanged(bool composing)
    {
        IsComposing = composing;
        return Task.CompletedTask;
    }

    private async Task<bool> DeliverAsync(ImmutableArray<RichTextNode> nodes)
    {
        try
        {
            await NodesChanged.InvokeAsync(nodes);
        }
        catch (Exception exception)
        {
            // The host's handler failed. Treat it as for any other event, instead of asking the browser to resend.
            await DispatchExceptionAsync(exception);
        }

        return true;
    }

    // Seam: what is on screen cannot be converted, so the document is behind it until OnEditApplied is called.
    private void OnEditRejected() => HasRejectedEdit = true;

    private void OnEditApplied() => HasRejectedEdit = false;

    /// <summary>True from a rejected edit until a later one is applied.</summary>
    internal bool HasRejectedEdit { get; private set; }

    private static bool SameObjects(ImmutableArray<RichTextNode> left, ImmutableArray<RichTextNode> right) =>
        left.Length == right.Length && left.AsSpan().SequenceEqual(right.AsSpan(), ReferenceEqualityComparer.Instance);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
        try
        {
            if (_module is { } module)
            {
                await module.InvokeVoidAsync("dispose", _element);
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The page is gone, and the script with it.
        }
        finally
        {
            _self?.Dispose();
        }
    }
}
