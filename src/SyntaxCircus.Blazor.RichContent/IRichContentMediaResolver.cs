using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent;

/// <summary>A renderable image: source plus optional responsive attributes.</summary>
public sealed record ResolvedMedia(string Src, string? SrcSet = null, string? Sizes = null, int? Width = null, int? Height = null);

/// <summary>Maps an opaque <see cref="MediaRef"/> to a URL the host app can serve. The host app must register one.</summary>
public interface IRichContentMediaResolver
{
    /// <summary>Returns <c>null</c> when the media cannot be resolved.</summary>
    ResolvedMedia? Resolve(MediaRef media);
}
