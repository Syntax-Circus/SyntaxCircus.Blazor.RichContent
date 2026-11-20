using Microsoft.AspNetCore.Components.Rendering;

namespace SyntaxCircus.Blazor.RichContent.Rendering;

/// <summary>Shared figure markup for inline images and gallery items.</summary>
internal static class ImageFigure
{
    public static void Build(RenderTreeBuilder builder, ResolvedMedia? media, string? alt, string? caption)
    {
        var hasCaption = !string.IsNullOrWhiteSpace(caption);
        builder.OpenElement(40, "figure");
        if (media is not null && !string.IsNullOrWhiteSpace(media.Src))
        {
            builder.OpenElement(41, "img");
            builder.AddAttribute(42, "src", media.Src);
            if (media.SrcSet is not null)
            {
                builder.AddAttribute(43, "srcset", media.SrcSet);
            }

            if (media.Sizes is not null)
            {
                builder.AddAttribute(44, "sizes", media.Sizes);
            }

            if (media.Width is { } width)
            {
                builder.AddAttribute(45, "width", width);
            }

            if (media.Height is { } height)
            {
                builder.AddAttribute(46, "height", height);
            }

            builder.AddAttribute(47, "alt", alt ?? "");
            builder.AddAttribute(48, "loading", "lazy");
            builder.AddAttribute(49, "decoding", "async");
            builder.CloseElement();
            if (hasCaption)
            {
                builder.OpenElement(50, "figcaption");
                builder.AddContent(51, caption);
                builder.CloseElement();
            }
        }
        else
        {
            // Unresolvable media: keep the words, drop the picture.
            var text = hasCaption ? caption : alt;
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.OpenElement(52, "figcaption");
                builder.AddContent(53, text);
                builder.CloseElement();
            }
        }

        builder.CloseElement();
    }
}
