using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SyntaxCircus.RichContent;

public sealed record RichContentError(string? BlockId, string Code, string Message);

/// <summary>Structural and size validation for <see cref="ContentDocument"/> and rich-text trees.</summary>
public static partial class RichContentRules
{
    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z0-9+#-]{1,20}$")]
    private static partial Regex LanguagePattern();

    public static IReadOnlyList<RichContentError> Validate(ContentDocument document, RichContentLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        limits ??= RichContentLimits.Default;

        var errors = new List<RichContentError>();
        var blocks = document.Blocks.IsDefault ? [] : document.Blocks;
        if (blocks.Length > limits.Blocks)
        {
            errors.Add(new(null, "too-many-blocks", $"A document may have at most {limits.Blocks} blocks."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var textBytes = 0;
        foreach (var block in blocks)
        {
            var id = block.Id;
            if (string.IsNullOrEmpty(id) || !IdPattern().IsMatch(id))
            {
                errors.Add(new(id, "invalid-block-id", "Block ids must match [a-z0-9-]{1,40}."));
            }
            else if (!seen.Add(id))
            {
                errors.Add(new(id, "duplicate-block-id", $"Block id '{id}' is used more than once."));
            }

            switch (block)
            {
                case RichTextBlock rich:
                    WalkNodes(rich.Nodes, id, limits, errors, ref textBytes);
                    break;
                case CalloutBlock callout:
                    if (!Enum.IsDefined(callout.Tone))
                    {
                        errors.Add(new(id, "invalid-callout-tone", "Callout tone is not recognised."));
                    }
                    WalkNodes(callout.Nodes, id, limits, errors, ref textBytes);
                    break;
                case GalleryBlock gallery:
                    ValidateGallery(gallery, limits, errors);
                    break;
                case CtaBlock cta:
                    if (string.IsNullOrWhiteSpace(cta.Label))
                    {
                        errors.Add(new(id, "cta-label-required", "A call to action needs a label."));
                    }
                    if (!RichContentUrl.IsSafe(cta.Url, limits.UrlCharacters))
                    {
                        errors.Add(new(id, "unsafe-url", "The call to action URL is not allowed."));
                    }
                    break;
                case ExtensionBlock extension:
                    if (string.IsNullOrEmpty(extension.Type) || !IdPattern().IsMatch(extension.Type))
                    {
                        errors.Add(new(id, "invalid-extension-type", "Extension type must match [a-z0-9-]{1,40}."));
                    }
                    if (extension.Data.ValueKind != JsonValueKind.Undefined
                        && Encoding.UTF8.GetByteCount(extension.Data.GetRawText()) > RichContentLimits.ExtensionDataBytes)
                    {
                        errors.Add(new(id, "extension-data-too-large", $"Extension data may be at most {RichContentLimits.ExtensionDataBytes} bytes."));
                    }
                    break;
                case UnknownBlock unknown:
                    // Genuinely unknown discriminators are preserved verbatim; a known one that failed to bind is malformed.
                    if (unknown.Raw.ValueKind == JsonValueKind.Object
                        && unknown.Raw.TryGetProperty("$block", out var discriminator)
                        && discriminator.ValueKind == JsonValueKind.String
                        && ContentDocumentSerializer.IsKnownDiscriminator(discriminator.GetString()))
                    {
                        errors.Add(new(string.IsNullOrEmpty(id) ? null : id, "malformed-known-block",
                            $"A '{discriminator.GetString()}' block is missing required members."));
                    }
                    break;
            }
        }

        return errors;
    }

    public static IReadOnlyList<RichContentError> ValidateNodes(ImmutableArray<RichTextNode> nodes, string? blockId, RichContentLimits? limits = null)
    {
        var errors = new List<RichContentError>();
        var textBytes = 0;
        WalkNodes(nodes, blockId, limits ?? RichContentLimits.Default, errors, ref textBytes);
        return errors;
    }

    private static void ValidateGallery(GalleryBlock gallery, RichContentLimits limits, List<RichContentError> errors)
    {
        var id = gallery.Id;
        var images = gallery.Images.IsDefault ? [] : gallery.Images;
        if (images.Length is 0 || images.Length > limits.GalleryImages)
        {
            errors.Add(new(id, "gallery-size", $"A gallery needs between 1 and {limits.GalleryImages} images."));
        }

        foreach (var image in images)
        {
            if (image?.Media is null || string.IsNullOrWhiteSpace(image.Media.Id))
            {
                errors.Add(new(id, "gallery-media-required", "Every gallery image needs a media reference."));
            }
            if (image is null || string.IsNullOrWhiteSpace(image.Alt) || image.Alt.Length > RichContentLimits.AltCharacters)
            {
                errors.Add(new(id, "alt-required", $"Every gallery image needs alt text of at most {RichContentLimits.AltCharacters} characters."));
            }
            if (image?.Caption is { Length: > RichContentLimits.CaptionCharacters })
            {
                errors.Add(new(id, "caption-too-long", $"Captions may be at most {RichContentLimits.CaptionCharacters} characters."));
            }
        }
    }

    // Iterative walk so excessive depth/count is rejected before any recursive renderer sees the tree.
    private static void WalkNodes(ImmutableArray<RichTextNode> nodes, string? blockId, RichContentLimits limits, List<RichContentError> errors, ref int textBytes)
    {
        if (nodes.IsDefault)
        {
            errors.Add(new(blockId, "nodes-required", "Rich text nodes are required."));
            return;
        }

        var pending = new Stack<(RichTextNode Node, int Depth, RichTextNodeKind? Parent, bool WithinLink)>();
        for (var i = nodes.Length - 1; i >= 0; i--)
        {
            pending.Push((nodes[i], 1, null, false));
        }

        var count = 0;
        while (pending.TryPop(out var current))
        {
            var (node, depth, parent, withinLink) = current;
            var textBytesBefore = textBytes;
            if (node is null)
            {
                errors.Add(new(blockId, "null-node", "Nodes cannot be null."));
                continue;
            }

            if (++count > limits.Nodes)
            {
                errors.Add(new(blockId, "too-many-nodes", $"Rich text may have at most {limits.Nodes} nodes."));
                return;
            }

            if (depth > limits.Depth)
            {
                errors.Add(new(blockId, "depth-exceeded", $"Rich text may be nested at most {limits.Depth} levels deep."));
                return;
            }

            if (!Enum.IsDefined(node.Kind))
            {
                errors.Add(new(blockId, "invalid-node-kind", "Node kind is not recognised."));
                continue;
            }

            var kind = node.Kind;
            var children = node.Children.IsDefault ? [] : node.Children;
            var found = new List<(string Code, string Message)>();

            if (!IsAllowedChild(parent, kind))
            {
                found.Add(("invalid-node-parent", parent is null
                    ? $"{kind} cannot be a top-level node."
                    : $"{kind} cannot be placed inside {parent}."));
            }

            // Leaf-versus-container shape.
            if (kind is RichTextNodeKind.Text or RichTextNodeKind.InlineCode or RichTextNodeKind.CodeBlock)
            {
                if (node.Text is null)
                {
                    found.Add(("text-required", $"{kind} requires text."));
                }
                else
                {
                    var bytes = Encoding.UTF8.GetByteCount(node.Text);
                    if (kind == RichTextNodeKind.CodeBlock && bytes > limits.CodeBlockBytes)
                    {
                        found.Add(("code-block-too-large", $"A code block may be at most {limits.CodeBlockBytes} bytes."));
                    }
                    textBytes += bytes;
                }
                if (children.Length > 0)
                {
                    found.Add(("unexpected-children", $"{kind} cannot have children."));
                }
            }
            else if (kind == RichTextNodeKind.Image)
            {
                if (node.Text is not null)
                {
                    found.Add(("unexpected-text", "Image cannot have text."));
                }
                if (children.Length > 0)
                {
                    found.Add(("unexpected-children", "Image cannot have children."));
                }
            }
            else
            {
                if (node.Text is not null)
                {
                    found.Add(("unexpected-text", $"{kind} cannot have text."));
                }
                if (children.Length == 0)
                {
                    found.Add(("children-required", $"{kind} requires children."));
                }
            }

            // Field applicability.
            if (kind == RichTextNodeKind.Link)
            {
                if (!RichContentUrl.IsSafe(node.Url, limits.UrlCharacters))
                {
                    found.Add(("unsafe-url", "The link URL is not allowed."));
                }
                if (withinLink)
                {
                    found.Add(("nested-link", "Links cannot be nested inside links."));
                }
            }
            else if (node.Url is not null)
            {
                found.Add(("unexpected-url", $"{kind} cannot have a URL."));
            }

            if (kind == RichTextNodeKind.Heading)
            {
                if (node.HeadingLevel is not (>= RichContentLimits.MinimumHeadingLevel and <= RichContentLimits.MaximumHeadingLevel))
                {
                    found.Add(("invalid-heading-level", $"Heading level must be {RichContentLimits.MinimumHeadingLevel} to {RichContentLimits.MaximumHeadingLevel}."));
                }
            }
            else if (node.HeadingLevel is not null)
            {
                found.Add(("unexpected-heading-level", $"{kind} cannot have a heading level."));
            }

            if (kind != RichTextNodeKind.List && node.Ordered is not null)
            {
                found.Add(("unexpected-ordered", $"{kind} cannot be ordered."));
            }

            if (kind == RichTextNodeKind.CodeBlock)
            {
                if (node.Language is not null && !LanguagePattern().IsMatch(node.Language))
                {
                    found.Add(("invalid-language", "Code language must match [a-z0-9+#-]{1,20}."));
                }
            }
            else if (node.Language is not null)
            {
                found.Add(("unexpected-language", $"{kind} cannot have a language."));
            }

            if (kind == RichTextNodeKind.Image)
            {
                if (node.Media is null || string.IsNullOrWhiteSpace(node.Media.Id))
                {
                    found.Add(("media-required", "Image requires a media reference."));
                }
                if (string.IsNullOrWhiteSpace(node.Alt) || node.Alt.Length > RichContentLimits.AltCharacters)
                {
                    found.Add(("alt-required", $"Image requires alt text of at most {RichContentLimits.AltCharacters} characters."));
                }
                if (node.Caption is { Length: > RichContentLimits.CaptionCharacters })
                {
                    found.Add(("caption-too-long", $"Captions may be at most {RichContentLimits.CaptionCharacters} characters."));
                }
            }
            else if (node.Media is not null || node.Alt is not null || node.Caption is not null)
            {
                found.Add(("unexpected-image-fields", $"{kind} cannot have media, alt text or a caption."));
            }

            if (kind != RichTextNodeKind.TableCell && node.Header is not null)
            {
                found.Add(("unexpected-header", $"{kind} cannot be a header."));
            }

            if (kind == RichTextNodeKind.Table && children.Length > limits.TableRows)
            {
                found.Add(("table-too-large", $"A table may have at most {limits.TableRows} rows."));
            }
            else if (kind == RichTextNodeKind.TableRow && children.Length > limits.TableCells)
            {
                found.Add(("table-too-large", $"A table row may have at most {limits.TableCells} cells."));
            }

            foreach (var (code, message) in found)
            {
                errors.Add(new(blockId, code, message));
            }

            // Report only when this node crosses the budget, so later blocks do not repeat the error.
            if (textBytes > limits.TextBytes)
            {
                if (textBytesBefore <= limits.TextBytes)
                {
                    errors.Add(new(blockId, "text-too-large", $"Total text may be at most {limits.TextBytes} bytes."));
                }
                return;
            }

            var childWithinLink = withinLink || kind == RichTextNodeKind.Link;
            for (var i = children.Length - 1; i >= 0; i--)
            {
                pending.Push((children[i], depth + 1, kind, childWithinLink));
            }
        }
    }

    private static bool IsInline(RichTextNodeKind kind) =>
        kind is RichTextNodeKind.Text or RichTextNodeKind.Emphasis or RichTextNodeKind.Strong
            or RichTextNodeKind.InlineCode or RichTextNodeKind.Link;

    private static bool IsAllowedChild(RichTextNodeKind? parent, RichTextNodeKind child) => parent switch
    {
        null => child is RichTextNodeKind.Paragraph or RichTextNodeKind.Heading or RichTextNodeKind.List
            or RichTextNodeKind.Quote or RichTextNodeKind.CodeBlock or RichTextNodeKind.Image or RichTextNodeKind.Table,
        RichTextNodeKind.Paragraph or RichTextNodeKind.Heading or RichTextNodeKind.Emphasis
            or RichTextNodeKind.Strong or RichTextNodeKind.Link or RichTextNodeKind.TableCell => IsInline(child),
        RichTextNodeKind.List => child == RichTextNodeKind.ListItem,
        RichTextNodeKind.ListItem => IsInline(child) || child is RichTextNodeKind.Paragraph or RichTextNodeKind.List,
        RichTextNodeKind.Quote => child is RichTextNodeKind.Paragraph or RichTextNodeKind.List,
        RichTextNodeKind.Table => child == RichTextNodeKind.TableRow,
        RichTextNodeKind.TableRow => child == RichTextNodeKind.TableCell,
        _ => false, // leaf kinds
    };
}
