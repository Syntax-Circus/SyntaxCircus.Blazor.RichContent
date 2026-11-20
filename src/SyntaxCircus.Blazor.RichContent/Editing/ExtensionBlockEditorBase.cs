using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>Base class for the editor of an app-defined extension block, registered through <see cref="ContentBlockRegistry.Add"/>.</summary>
public abstract class ExtensionBlockEditorBase : ComponentBase
{
    [Parameter] public JsonElement Data { get; set; }

    [Parameter] public EventCallback<JsonElement> DataChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }
}
