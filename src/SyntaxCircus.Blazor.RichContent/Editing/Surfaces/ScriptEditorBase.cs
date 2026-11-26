using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SyntaxCircus.Blazor.RichContent.Editing.Writing;

namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

/// <summary>
/// Base of the components whose content a script of this package edits in the browser: the writing surface and the
/// code card. It draws their content there, takes the edits back in messages small enough for SignalR's default
/// limit, and makes sure that neither is lost. Only this package can derive from it.
/// </summary>
public abstract class ScriptEditorBase : ComponentBase, IAsyncDisposable
{
    private readonly ChunkAssembler _assembler = new();
    private IJSObjectReference? _module;
    private DotNetObjectReference<ScriptEditorBase>? _self;
    private ILogger? _logger;

    // The latest redraw, kept to be sent again until the browser is known to have it.
    private object? _redraw;
    private int _confirmedGeneration;
    private bool? _syncedDisabled;
    private bool _started;
    private bool _ready;

    private protected ScriptEditorBase()
    {
    }

    // The part of a writing block that this edits, or is inside, such as a table cell. Absent outside one.
    [CascadingParameter] private IWritingPart? WritingPart { get; set; }

    /// <summary>The element the script edits. The component's markup captures it.</summary>
    private protected ElementReference Element { get; set; }

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private ILoggerFactory LoggerFactory { get; set; } = default!;

    [Parameter] public bool Disabled { get; set; }

    /// <summary>Takes the focus once it is first able to.</summary>
    [Parameter] public bool FocusOnStart { get; set; }

    /// <summary>True while the author is composing text with an input method. No edits arrive meanwhile.</summary>
    internal bool IsComposing { get; private set; }

    /// <summary>True from a rejected edit until a later one is applied.</summary>
    internal bool HasRejectedEdit { get; private set; }

    private protected bool LoadFailed { get; private set; }

    private protected bool IsDisposed { get; private set; }

    /// <summary>The newest sequence number of an applied edit since the latest redraw, or 0.</summary>
    private protected long AppliedSequence { get; private set; }

    // True from a redraw until the browser shows that it has it. The call that carries it can fail, be cancelled,
    // never answer or have no effect, and until then the browser may still show, and edit, the old content.
    private protected bool RedrawIsUnconfirmed => _confirmedGeneration < ContentGeneration;

    private ILogger Logger => _logger ??= LoggerFactory.CreateLogger(GetType());

    private protected abstract string ScriptPath { get; }

    /// <summary>The function that gives the element the focus.</summary>
    private protected abstract string FocusFunction { get; }

    private protected virtual object AttachOptions => new { };

    /// <summary>Counts the redraws. An edit made against another generation cannot be applied.</summary>
    private protected abstract int ContentGeneration { get; }

    /// <summary>
    /// Takes the latest parameters. True if they differ from what is held, so that the browser must be redrawn.
    /// </summary>
    private protected abstract bool TakeParameters();

    /// <summary>Holds the latest parameters under a new generation, and returns what the script draws.</summary>
    private protected abstract object ResetContent();

    /// <summary>Reads one whole payload from the browser. Throws if it is malformed.</summary>
    private protected abstract ReceivedEdit ReadEdit(string payload);

    protected override async Task OnParametersSetAsync()
    {
        // Until the script is ready, the first synchronization will take the latest parameters.
        if (!_ready || IsDisposed)
        {
            return;
        }

        var redraw = TakeParameters();
        if (redraw || RedrawIsUnconfirmed || Disabled != _syncedDisabled)
        {
            await SynchronizeAsync(redraw);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // When renders lag, this can be an earlier render completing, with the element not drawn yet. It has a
        // reference only once it has been.
        if (_started || LoadFailed || IsDisposed || Element.Id is null)
        {
            return;
        }

        _started = true;
        try
        {
            await Interop(AttachAsync);
        }
        catch (JSException exception)
        {
            _ready = false;
            Logger.LogError(exception, "The writing editor's script could not be loaded or run.");
            // A seam for falling back to the block editor when the script cannot be loaded or run.
            LoadFailed = true;
            StateHasChanged();
        }
    }

    private async ValueTask AttachAsync()
    {
        var module = await JS.InvokeAsync<IJSObjectReference>("import", ScriptPath);
        if (IsDisposed)
        {
            await module.DisposeAsync();
            return;
        }

        _module = module;
        _self = DotNetObjectReference.Create(this);
        await module.InvokeVoidAsync("attach", Element, _self, AttachOptions);
        if (IsDisposed)
        {
            return;
        }

        _ready = true;
        await SyncAsync(redraw: true);
        if (FocusOnStart && !IsDisposed)
        {
            await LoggingScriptErrors(() => module.InvokeVoidAsync(FocusFunction, Element));
        }
    }

    // Sends Disabled and, with a new redraw or while the last one is unconfirmed, the content. The script ignores
    // a redraw it already has, so sending one again cannot overwrite what has been typed since.
    private async ValueTask SyncAsync(bool redraw)
    {
        if (redraw)
        {
            // From here on, edits made against the old content are not applied.
            _redraw = ResetContent();
            AppliedSequence = 0;
        }

        if (_module is not { } module)
        {
            return;
        }

        var disabled = Disabled;
        var generation = ContentGeneration;
        var withContent = RedrawIsUnconfirmed;
        // The answer is the generation the browser then has. It is not this one if the redraw had to wait.
        var held = await module.InvokeAsync<int?>(
            "sync", Element, withContent ? _redraw : null, disabled, withContent, generation);
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

    /// <summary>
    /// One chunk of an edit from the browser. False means the edit was not applied, so the browser keeps it as
    /// unacknowledged and sends it again with the author's next change. For the script only.
    /// </summary>
    /// <remarks>
    /// Nothing here may await before the change is raised: the next message from the browser, such as a click on
    /// Save, is handled as soon as this method first yields.
    /// </remarks>
    [JSInvokable]
    public Task<bool> ReceiveEdit(long sequence, int index, int count, string chunk)
    {
        // A component that has gone, or whose place is being rearranged, can store nothing. The edit must not be
        // acknowledged: the browser would take it to be saved.
        if (IsDisposed || WritingPart?.IsGone == true)
        {
            return Task.FromResult(false);
        }

        Func<Task>? deliver = null;
        var behind = false;
        try
        {
            if (_assembler.Add(sequence, index, count, chunk) is not { } payload)
            {
                return Task.FromResult(true);
            }

            var edit = ReadEdit(payload);

            // Made against content that has since been replaced. Generations start at 1, so a payload without one
            // is not old but wrong.
            behind = edit.Generation > 0 && edit.Generation < ContentGeneration;
            if (!behind)
            {
                if (edit.Generation == ContentGeneration)
                {
                    Confirm(edit.Generation);
                }

                deliver = edit.Apply();
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            // Seam: what is on screen cannot be converted, so the document is behind it until a later edit applies.
            HasRejectedEdit = true;
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

        HasRejectedEdit = false;
        AppliedSequence = sequence;
        return deliver is null ? Task.FromResult(true) : DeliverAsync(deliver);
    }

    private async Task<bool> DeliverAsync(Func<Task> deliver)
    {
        try
        {
            await deliver();
        }
        catch (Exception exception)
        {
            // The host's handler failed. Treat it as for any other event, instead of asking the browser to resend.
            await DispatchExceptionAsync(exception);
        }

        return true;
    }

    /// <summary>Told when the author starts and stops composing text. For the script only.</summary>
    [JSInvokable]
    public Task CompositionChanged(bool composing)
    {
        IsComposing = composing;
        return Task.CompletedTask;
    }

    /// <summary>Runs a function of the script on the element, if it has started.</summary>
    private protected Task InvokeScriptAsync(string function, params object?[] arguments) =>
        _ready && !IsDisposed && _module is { } module
            ? LoggingScriptErrors(() => module.InvokeVoidAsync(function, [Element, .. arguments])).AsTask()
            : Task.CompletedTask;

    // For calls on a component that has started, and for disposal. A script that threw is a defect: it is logged as
    // an error, but the author's other work is worth more than the circuit. Starting does not use this, so that a
    // component that cannot start says so.
    private async ValueTask LoggingScriptErrors(Func<ValueTask> call)
    {
        try
        {
            await Interop(call);
        }
        catch (JSException exception)
        {
            Logger.LogError(exception, "A call to the writing editor's script failed.");
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
            Logger.LogDebug(exception, "A call to the writing editor's script was cancelled.");
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
        }
    }

    private protected virtual void OnDisposing()
    {
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        GC.SuppressFinalize(this);
        OnDisposing();
        try
        {
            if (_module is { } module)
            {
                // Separately, so that one failing does not leave the other undone.
                await LoggingScriptErrors(() => module.InvokeVoidAsync("dispose", Element));
                await LoggingScriptErrors(module.DisposeAsync);
            }
        }
        finally
        {
            _self?.Dispose();
        }
    }

    /// <summary>An edit read from the browser. Apply returns what raises the change, or null if nothing changed.</summary>
    private protected readonly record struct ReceivedEdit(int Generation, Func<Func<Task>?> Apply);
}
