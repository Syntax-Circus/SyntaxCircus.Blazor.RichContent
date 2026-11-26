using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace SyntaxCircus.Blazor.RichContent.Editing.Surfaces;

// Getting the browser to hold what it should, and trying again until it does.
public abstract partial class ScriptEditorBase
{
    // One try at getting the browser to hold what it should. What is merely lost, cancelled or disconnected is left to
    // the watch. A script that throws before anything has ever been drawn cannot be used. One that throws later is a
    // defect, which is logged, but the author's other work is worth more than the circuit.
    private async Task Attempt(Func<ValueTask> call)
    {
        try
        {
            await Interop(call);
        }
        catch (JSException exception) when (_confirmedGeneration == 0 && !IsDisposed)
        {
            if (!LoadFailed)
            {
                _ready = false;
                LoadFailed = true;
                StopWatching();
                Logger.LogError(exception, "The writing editor's script could not be loaded or run.");
                StateHasChanged();
                Session?.ReportScriptFailure();
                Session?.ReportReadOnly();
            }
        }
        catch (JSException exception)
        {
            _scriptBroken = true;
            Logger.LogError(exception, "A call to the writing editor's script failed.");
            Session?.ReportReadOnly();
        }

        Settle();
    }

    // Loads and attaches the script, then draws. Safe to repeat: the script keeps what it has already drawn.
    private async ValueTask AttachAsync()
    {
        if (_module is null)
        {
            var imported = await JS.InvokeAsync<IJSObjectReference>("import", ScriptPath);
            if (IsDisposed)
            {
                await imported.DisposeAsync();
                return;
            }

            // Another try can have got there first. It is the same module either way.
            _module ??= imported;
        }

        var module = _module;
        _self ??= DotNetObjectReference.Create(this);
        await module.InvokeVoidAsync("attach", Element, _self, AttachOptions);
        if (IsDisposed)
        {
            return;
        }

        _ready = true;
        // Content is taken from the parameters only if none has been sent yet, when nothing can have been typed. Later
        // ones arrive in OnParametersSetAsync, and outside it they can be older than what has been typed since, as
        // while renders are held back: a redraw taken from them would undo it.
        await SyncAsync(redraw: _redraw is null);
        if (FocusOnStart && !_focused && !IsDisposed)
        {
            _focused = true;
            await LoggingScriptErrors(() => module.InvokeVoidAsync(FocusFunction, Element));
        }
    }

    // True while there is something the browser should have and is not known to.
    private bool Behind =>
        _started && !IsDisposed && !LoadFailed && (!_ready || RedrawIsUnconfirmed || _syncedDisabled != Disabled);

    // A call that carries a redraw can be lost, cancelled or never answered, and nothing else need ever happen to
    // send it again. So while the browser is behind, it is tried again on a timer, and the author is told.
    private void Watch()
    {
        if (_watch is null && Behind)
        {
            _watch = new CancellationTokenSource();
            _ = WatchAsync(_watch.Token);
        }
    }

    private async Task WatchAsync(CancellationToken cancellation)
    {
        try
        {
            var delay = Options.ScriptRetryDelay;
            var longest = delay * 8;
            var failing = 0;
            while (true)
            {
                await Task.Delay(delay, cancellation);
                if (!Behind)
                {
                    break;
                }

                // A redraw waits for a composition to end, which is the author's doing and nothing to report.
                if (!Stalled && !IsComposing)
                {
                    Stalled = true;
                    StateHasChanged();
                }

                if (!_scriptBroken || failing++ < ScriptAttempts)
                {
                    // Not awaited: a call that never answers must not stop the next try.
                    _ = RetryAsync();
                }

                delay = delay * 2 < longest ? delay * 2 : longest;
            }

            Settle();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await DispatchExceptionAsync(exception);
        }
    }

    private async Task RetryAsync()
    {
        try
        {
            await Attempt(AttachAsync);
        }
        catch (Exception exception)
        {
            // Nothing awaits this, so an unexpected failure has to be handed over to be seen.
            await DispatchExceptionAsync(exception);
        }
    }

    // Prompts a try at once, from outside: see ContentDocumentEditor's switch.
    internal void Nudge()
    {
        if (Behind)
        {
            _ = RetryAsync();
        }
    }

    // Tries again at once, as the author asked, and watches afresh.
    private protected Task TryAgainAsync()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
        var tried = Behind ? RetryAsync() : Task.CompletedTask;
        Watch();
        return tried;
    }

    private void Settle()
    {
        if (!Behind && !IsDisposed)
        {
            StopWatching();
        }
    }

    private void StopWatching()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
        if (Stalled && !IsDisposed)
        {
            Stalled = false;
            StateHasChanged();
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
        var number = ++_syncs;
        if (disabled != _sentDisabled)
        {
            // The browser may have it from now on, whether or not an answer ever says so.
            _sentDisabled = disabled;
            _sentDisabledSince = number;
            _syncedDisabled = null;
            _confirmedReadOnly = false;
        }

        // The answer is the generation the browser then has. It is not this one if the redraw had to wait. Before
        // answering that it is read-only, the script sends again whatever may not have arrived, so that has been
        // handled by the time the answer is.
        var held = await module.InvokeAsync<int?>(
            "sync", Element, withContent ? _redraw : null, disabled, withContent, generation,
            disabled && Session?.DisabledBriefly() == true);
        if (held is not null)
        {
            _scriptBroken = false;
            // An answer to anything sent since the value last changed will do, not only to the latest: tries can be
            // closer together than a slow connection takes to answer.
            if (number >= _sentDisabledSince)
            {
                _syncedDisabled = disabled;
                if (disabled)
                {
                    _confirmedReadOnly = true;
                    Session?.ReportReadOnly();
                }
            }
        }

        if (held == generation)
        {
            Confirm(generation);
        }
    }
}
