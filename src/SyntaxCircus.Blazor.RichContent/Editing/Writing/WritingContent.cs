using System.Collections.Immutable;
using System.Text.Json;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>
/// Whether a card holds nothing, so that it can be removed without asking. Anything with unknown properties holds
/// something, as does any block this package cannot see into.
/// </summary>
internal static class WritingContent
{
    // What a new call to action points at.
    private const string NewAddress = "https://";

    public static bool IsEmpty(ContentBlock block) => block switch
    {
        CalloutBlock callout => HasNone(callout.Extra) && string.IsNullOrEmpty(callout.Title) && AreEmpty(callout.Nodes),
        GalleryBlock gallery => HasNone(gallery.Extra) && string.IsNullOrEmpty(gallery.Title) && gallery.Images.IsDefaultOrEmpty,
        CtaBlock cta => HasNone(cta.Extra) && string.IsNullOrEmpty(cta.Heading) && string.IsNullOrEmpty(cta.Body)
            && string.IsNullOrEmpty(cta.Label) && (string.IsNullOrEmpty(cta.Url) || cta.Url == NewAddress),
        _ => false,
    };

    public static bool IsEmpty(RichTextNode node) => AreEmpty([node]);

    // Without recursion: a tree can be nested arbitrarily deeply.
    private static bool AreEmpty(ImmutableArray<RichTextNode> nodes)
    {
        var pending = new Stack<RichTextNode?>();
        foreach (var node in nodes.IsDefault ? [] : nodes)
        {
            pending.Push(node);
        }

        while (pending.TryPop(out var node))
        {
            if (node is null || !HasNone(node.Extra) || !string.IsNullOrEmpty(node.Text)
                || !string.IsNullOrEmpty(node.Alt) || !string.IsNullOrEmpty(node.Caption)
                || !string.IsNullOrEmpty(node.Media?.Id) || !string.IsNullOrEmpty(node.Language) || !string.IsNullOrEmpty(node.Url))
            {
                return false;
            }

            foreach (var child in node.Children.IsDefault ? [] : node.Children)
            {
                pending.Push(child);
            }
        }

        return true;
    }

    private static bool HasNone(IDictionary<string, JsonElement>? extra) => extra is not { Count: > 0 };
}
