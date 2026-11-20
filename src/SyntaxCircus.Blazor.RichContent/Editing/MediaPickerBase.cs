using Microsoft.AspNetCore.Components;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>Base class for the host app's media picker, registered through <see cref="RichContentEditorOptions.MediaPicker"/>.</summary>
public abstract class MediaPickerBase : ComponentBase
{
    [Parameter] public MediaRef? Value { get; set; }

    [Parameter] public EventCallback<MediaRef?> ValueChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }
}
