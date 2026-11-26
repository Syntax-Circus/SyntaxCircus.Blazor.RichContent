using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using SyntaxCircus.Blazor.RichContent.Editing.Surfaces;
using SyntaxCircus.Blazor.RichContent.Editing.Writing;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>Which view is shown, how it is changed without losing anything, and what is reported to the host.</summary>
public partial class ContentDocumentEditor : IDisposable
{
    // Who asked for a switch, which settles who has to be told about it.
    private enum SwitchOrigin
    {
        Author,
        Host,
        LoadFailure,
    }

    // The view shown. It follows Mode when Mode changes, and the switch in between.
    private RichContentEditorMode _shown;
    private RichContentEditorMode? _modeParameter;

    // The view asked for and not yet shown, and the number of the request.
    private RichContentEditorMode? _pending;
    private SwitchOrigin _pendingOrigin;
    private int _switches;
    // True once the browser has shown the read-only view that came of the latest request.
    private bool _drawn;

    private bool _switchRefused;
    private bool _loadFailed;
    private bool _disposed;
    private readonly CancellationTokenSource _disposal = new();

    private IReadOnlyList<RichContentError> _lastPublished = [];
    private bool _errorsPublished;

    private bool Writing => _shown == RichContentEditorMode.Write;

    // Also while a switch is on its way, so that nothing is typed into what is about to be replaced.
    private bool ReadOnly => Disabled || _pending is not null;

    protected override void OnInitialized()
    {
        _session.RejectionsChanged += RejectionsChanged;
        _session.ScriptFailed += ScriptFailed;
        _session.ReadOnlyChanged += ReadOnlyChanged;
        _session.DisabledBriefly = () => _pending is not null && !Disabled;
    }

    public void Dispose()
    {
        _disposed = true;
        _disposal.Cancel();
        _disposal.Dispose();
        _session.RejectionsChanged -= RejectionsChanged;
        _session.ScriptFailed -= ScriptFailed;
        _session.ReadOnlyChanged -= ReadOnlyChanged;
        GC.SuppressFinalize(this);
    }

    private void TakeMode()
    {
        var before = _modeParameter;
        _modeParameter = Mode;
        if (before is null)
        {
            _shown = Mode;
        }
        else if (before != Mode)
        {
            RequestMode(Mode, SwitchOrigin.Host);
        }
    }

    private void ChooseMode(RichContentEditorMode mode) => RequestMode(mode, SwitchOrigin.Author);

    // A host that has moved on is told what is still shown. Not from within the parameters it is setting.
    private async Task TellShownModeLaterAsync()
    {
        try
        {
            await Task.Yield();
            await InvokeAsync(() => _disposed ? Task.CompletedTask : ModeChanged.InvokeAsync(_shown));
        }
        catch (Exception exception)
        {
            await DispatchExceptionAsync(exception);
        }
    }

    // A switch takes two renders. The first shows the present view read-only. The view is replaced only once the
    // browser has drawn that (see DrawnBarrier) and, leaving the writing view, every part a script edits has answered
    // that it is read-only. The second matters when the call that makes one read-only is lost with a connection: the
    // render is sent again on reconnecting, and the call is not.
    private void RequestMode(RichContentEditorMode mode, SwitchOrigin origin)
    {
        if (mode == _shown)
        {
            if (_pending is not null)
            {
                _pending = null;
                // A host that asked for the other view, and has not itself come back, still holds it.
                if (_modeParameter != _shown)
                {
                    _ = TellShownModeLaterAsync();
                }
            }
        }
        else if (Writing && _session.Rejected.Count > 0)
        {
            // Known already, so the editor is not even made read-only: in Firefox that alone loses the undo history
            // the author may need to put things right.
            _pending = null;
            _switchRefused = true;
            if (origin == SwitchOrigin.Host)
            {
                _ = TellShownModeLaterAsync();
            }
        }
        else if (_pending != mode)
        {
            _pending = mode;
            _pendingOrigin = origin;
            _switchRefused = false;
            _drawn = false;
            _switches++;
        }
    }

    private RenderFragment Barrier(int number) => builder =>
    {
        builder.OpenComponent<DrawnBarrier>(0);
        builder.SetKey(number);
        builder.AddComponentParameter(1, nameof(DrawnBarrier.Drawn), (Func<Task>)(() => ViewDrawnAsync(number)));
        builder.CloseComponent();
    };

    private async Task ViewDrawnAsync(int number)
    {
        if (number != _switches)
        {
            return;
        }

        _drawn = true;
        var completed = CompleteSwitchAsync();
        if (IsWaitingFor(number))
        {
            _ = PromptUntilSwitchedAsync(number);
        }

        await completed;
    }

    private bool IsWaitingFor(int number) => !_disposed && number == _switches && _pending is not null;

    // The browser has just answered, so it is there. A part it has not confirmed lost its call, with a connection that
    // has since come back. Its own tries grow far apart; here the author is waiting, with the editor read-only.
    private async Task PromptUntilSwitchedAsync(int number)
    {
        try
        {
            var longest = Options.ScriptRetryDelay;
            var delay = longest / 8;
            while (IsWaitingFor(number))
            {
                _session.PromptUnconfirmed();
                await Task.Delay(delay, _disposal.Token);
                delay = delay * 2 < longest ? delay * 2 : longest;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await DispatchExceptionAsync(exception);
        }
    }

    private void ReadOnlyChanged()
    {
        if (!_disposed && _pending is not null && _drawn)
        {
            // Not here: this can be in the middle of a render, or of an edit that has not been answered.
            _ = CompleteSwitchLaterAsync();
        }
    }

    private async Task CompleteSwitchLaterAsync()
    {
        try
        {
            await Task.Yield();
            await InvokeAsync(CompleteSwitchAsync);
        }
        catch (Exception exception)
        {
            await DispatchExceptionAsync(exception);
        }
    }

    // By now whatever was typed is in the document, or is known not to fit in it.
    private async Task CompleteSwitchAsync()
    {
        if (_disposed || !_drawn || _pending is not { } mode || (Writing && !_session.AllReadOnly))
        {
            return;
        }

        _pending = null;
        if (Writing && _session.Rejected.Count > 0)
        {
            // What could not be saved exists only on screen, and would go with the view.
            _switchRefused = true;
            StateHasChanged();
            if (_pendingOrigin == SwitchOrigin.Host)
            {
                await ModeChanged.InvokeAsync(_shown);
            }

            return;
        }

        _shown = mode;
        _session.Writing = Writing;
        _places = [];
        // The new view builds on what the host holds. Anything it did not take from the old view is gone.
        _parameter = _latest = Document;
        if (Writing)
        {
            _loadFailed = false;
        }

        StateHasChanged();
        if (_pendingOrigin != SwitchOrigin.Host)
        {
            await ModeChanged.InvokeAsync(mode);
        }
    }

    private void ScriptFailed()
    {
        if (_disposed || !Writing)
        {
            return;
        }

        _loadFailed = true;
        RequestMode(RichContentEditorMode.Blocks, SwitchOrigin.LoadFailure);
        StateHasChanged();
    }

    private void RejectionsChanged(bool disposed)
    {
        if (_disposed)
        {
            return;
        }

        if (disposed)
        {
            // This is in the middle of a render, where nothing may be raised.
            _ = PublishAfterRenderAsync();
            return;
        }

        StateHasChanged();
        // Raised in this call: a click on Save is handled next.
        _ = ObserveAsync(PublishErrorsAsync());
    }

    private async Task PublishAfterRenderAsync()
    {
        try
        {
            await Task.Yield();
            // The whole editor may have gone with it, and then there is nothing to say.
            if (!_disposed)
            {
                StateHasChanged();
                await PublishErrorsAsync();
            }
        }
        catch (Exception exception)
        {
            await DispatchExceptionAsync(exception);
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            await DispatchExceptionAsync(exception);
        }
    }

    // The validation errors, then one for each surface, cell or code card that shows what the document does not have.
    private Task PublishErrorsAsync()
    {
        IReadOnlyList<RichContentError> errors = _session.Rejected.Count == 0
            ? _errors
            : [.. _errors, .. _session.Rejected.Select(Unconvertible)];
        if (_errorsPublished && errors.SequenceEqual(_lastPublished))
        {
            return Task.CompletedTask;
        }

        _errorsPublished = true;
        _lastPublished = errors;
        return ErrorsChanged.InvokeAsync(errors);
    }

    private RichContentError Unconvertible(ScriptEditorBase part)
    {
        List<ContentBlock> blocks = Latest.Blocks.IsDefault ? [] : [.. Latest.Blocks];
        var index = blocks.FindIndex(block => string.Equals(block.Id, part.BlockId, StringComparison.Ordinal));
        return new(part.BlockId, WriteModeMessages.UnconvertibleCode, WriteModeMessages.Unconvertible, index < 0 ? null : index);
    }
}
