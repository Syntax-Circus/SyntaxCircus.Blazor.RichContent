namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>Host-app configuration for the editor components.</summary>
public sealed class RichContentEditorOptions
{
    private Type? _mediaPicker;

    /// <summary>A component deriving <see cref="MediaPickerBase"/> used to choose images. When <c>null</c> the image and gallery adds are disabled.</summary>
    public Type? MediaPicker
    {
        get => _mediaPicker;
        set
        {
            if (value is not null && !typeof(MediaPickerBase).IsAssignableFrom(value))
            {
                throw new ArgumentException($"{value.Name} must derive from {nameof(MediaPickerBase)}.", nameof(value));
            }

            _mediaPicker = value;
        }
    }
}
