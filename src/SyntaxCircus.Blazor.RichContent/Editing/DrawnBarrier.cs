using Microsoft.AspNetCore.Components;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>
/// Shows nothing, and calls <see cref="Drawn"/> once the browser has applied the render that first held it. With
/// server rendering a component is only told it has rendered when the browser has acknowledged that render, and the
/// browser's messages are handled in the order sent. So by then, everything the browser sent before it showed what
/// was drawn has been handled. A new one is made for each wait.
/// </summary>
internal sealed class DrawnBarrier : ComponentBase
{
    [Parameter] public Func<Task> Drawn { get; set; } = () => Task.CompletedTask;

    protected override Task OnAfterRenderAsync(bool firstRender) => firstRender ? Drawn() : Task.CompletedTask;
}
