using SyntaxCircus.Blazor.RichContent;
using SyntaxCircus.RichContent;

namespace RichContent.Sample;

/// <summary>Resolves every media id to a generated placeholder SVG served by the sample app.</summary>
public sealed class SampleMediaResolver : IRichContentMediaResolver
{
    public ResolvedMedia? Resolve(MediaRef media) =>
        string.IsNullOrEmpty(media.Id) ? null : new ResolvedMedia("/img/" + Uri.EscapeDataString(media.Id) + ".svg", Width: 320, Height: 200);
}
