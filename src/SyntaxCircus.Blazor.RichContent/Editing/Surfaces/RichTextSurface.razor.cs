using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
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

    private readonly ChunkAssembler _assembler = new();
    private SurfaceBuffer _buffer = default!;
    private ElementReference _element;
    private IJSObjectReference? _module;
    private DotNetObjectReference<RichTextSurface>? _self;

    // What the buffer holds, and the browser too once the latest redraw has reached it.
    private ImmutableArray<RichTextNode> _current = [];

    // The latest redraw, kept to be sent again until the browser is known to have it.
    private IReadOnlyList<KeyedNode> _redraw = [];
    private int _confirmedGeneration;
    private bool? _syncedDisabled;
    private bool _ready;
    private bool _loadFailed;
    private bool _disposed;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private ILogger<RichTextSurface> Logger { get; set; } = default!;

    // Absent for a surface used on its own, which has no toolbar to keep informed.
    [CascadingParameter] private WritingSession? Session { get; set; }

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

    // True from a redraw until the browser shows that it has it. The call that carries it can fail, be cancelled,
    // never answer or have no effect, and until then the browser may still show, and edit, the old content.
    private bool RedrawIsUnconfirmed => _confirmedGeneration < _buffer.Generation;

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

        if (redraw || RedrawIsUnconfirmed || Disabled != _syncedDisabled)
        {
            await SynchronizeAsync(redraw);
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
        catch (JSException exception)
        {
            _ready = false;
            Logger.LogError(exception, "The writing surface's script could not be loaded or run.");
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

    // Sends Disabled and, with a new redraw or while the last one is unconfirmed, the content. The script ignores
    // a redraw it already has, so sending one again cannot overwrite what has been typed since.
    private async ValueTask SyncAsync(bool redraw)
    {
        if (redraw)
        {
            // From here on, patches made against the old content are not applied.
            _current = Items;
            _redraw = _buffer.Reset(_current);
        }

        if (_module is not { } module)
        {
            return;
        }

        var disabled = Disabled;
        var generation = _buffer.Generation;
        var withContent = RedrawIsUnconfirmed;
        // The answer is the generation the browser then has. It is not this one if the redraw had to wait.
        var held = await module.InvokeAsync<int?>(
            "sync", _element, withContent ? _redraw : null, disabled, withContent, generation);
        if (held is not null)
        {
            _syncedDisabled = disabled;
        }

        if (held == generation)
        {
            Confirm(generation);
        }
    }

    private void Confirm(int generation) => _confirmedGeneration = Math.Max(_confirmedGeneration, generation);

    private ValueTask SynchronizeAsync(bool redraw) => LoggingScriptErrors(() => SyncAsync(redraw));

    private async Task SendRedrawAgainAsync()
    {
        try
        {
            await SynchronizeAsync(redraw: false);
        }
        catch (Exception exception)
        {
            // Nothing awaits this, so an unexpected failure has to be handed over to be seen.
            await DispatchExceptionAsync(exception);
        }
    }

    // For calls on a surface that has started, and for disposal. A script that threw is a defect: it is logged as
    // an error, but the author's other work is worth more than the circuit. Starting a surface does not use this,
    // so that a surface that cannot start says so.
    private async ValueTask LoggingScriptErrors(Func<ValueTask> call)
    {
        try
        {
            await Interop(call);
        }
        catch (JSException exception)
        {
            Logger.LogError(exception, "A call to the writing surface's script failed.");
        }
    }

    // A lost connection, a call given up on or a component on its way out must not end the circuit.
    private async ValueTask Interop(Func<ValueTask> call)
    {
        try
        {
            await call();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (OperationCanceledException exception)
        {
            Logger.LogDebug(exception, "A call to the writing surface's script was cancelled.");
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    /// <summary>
    /// One chunk of a patch from the browser. False means the patch was not applied, so the browser keeps it as
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
        var behind = false;
        try
        {
            if (_assembler.Add(sequence, index, count, chunk) is not { } payload)
            {
                return Task.FromResult(true);
            }

            var patch = JsonSerializer.Deserialize<SurfacePatch>(payload, JsonSerializerOptions.Web)
                ?? throw new FormatException("There is no patch.");

            // Made against content that has since been replaced. Generations start at 1, so a payload without one
            // is not old but wrong.
            behind = patch.Generation > 0 && patch.Generation < _buffer.Generation;
            if (!behind)
            {
                if (patch.Generation == _buffer.Generation)
                {
                    Confirm(patch.Generation);
                }

                nodes = _buffer.Apply(patch);
            }
            else
            {
                nodes = default;
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            OnEditRejected();
            return Task.FromResult(false);
        }

        if (behind)
        {
            // With the replacement confirmed, this was in flight when it happened and is dropped.
            if (!RedrawIsUnconfirmed)
            {
                return Task.FromResult(true);
            }

            // Otherwise the browser may never have had it. Answering true would let the browser forget an edit that
            // went nowhere, along with every one after it. Send the replacement again, and have the edit kept.
            _ = SendRedrawAgainAsync();
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

    /// <summary>
    /// Told the formatting at the cursor when it changes, or when the cursor comes to this surface. For the script only.
    /// </summary>
    [JSInvokable]
    public Task SelectionChanged(SurfaceSelection selection)
    {
        if (!_disposed && selection is not null)
        {
            Session?.Report(this, selection);
        }

        return Task.CompletedTask;
    }

    /// <summary>Told that the author asked for the link box with the keyboard. For the script only.</summary>
    [JSInvokable]
    public Task LinkRequested()
    {
        if (!_disposed)
        {
            Session?.RequestLink(this);
        }

        return Task.CompletedTask;
    }

    /// <summary>Runs a writing command where the cursor was last in this surface.</summary>
    internal Task CommandAsync(string name, string argument) =>
        _ready && !_disposed && _module is { } module
            ? LoggingScriptErrors(async () => await module.InvokeAsync<bool>("command", _element, name, argument)).AsTask()
            : Task.CompletedTask;

    /// <summary>Gives the surface the focus again, with the cursor where it was.</summary>
    internal Task RestoreAsync() =>
        _ready && !_disposed && _module is { } module
            ? LoggingScriptErrors(() => module.InvokeVoidAsync("restore", _element)).AsTask()
            : Task.CompletedTask;

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
        Session?.Forget(this);
        try
        {
            if (_module is { } module)
            {
                // Separately, so that one failing does not leave the other undone.
                await LoggingScriptErrors(() => module.InvokeVoidAsync("dispose", _element));
                await LoggingScriptErrors(module.DisposeAsync);
            }
        }
        finally
        {
            _self?.Dispose();
        }
    }
}
