using SyntaxCircus.Blazor.RichContent;
using SyntaxCircus.RichContent;

namespace RichContent.Demo;

/// <summary>Resolves every media id to a generated placeholder SVG served by the demo.</summary>
public sealed class DemoMediaResolver : IRichContentMediaResolver
{
    public ResolvedMedia? Resolve(MediaRef media) =>
        string.IsNullOrEmpty(media.Id) ? null : new ResolvedMedia("/img/" + Uri.EscapeDataString(media.Id) + ".svg", Width: 320, Height: 200);
}
