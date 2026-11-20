using System.Collections.Immutable;
using System.Security.Cryptography;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.Editing;

/// <summary>
/// Pure operations over a <see cref="ContentDocument"/>'s blocks. Every block type, including unknown ones, is carried through untouched.
/// The id-based overloads act on the first block with that id; the index-based overloads act on exactly the block at that position
/// (use them whenever ids may be duplicated or empty).
/// </summary>
public static class ContentDocumentOperations
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>A fresh block id: <c>b-</c> followed by 8 lowercase base36 characters.</summary>
    public static string NewBlockId() =>
        "b-" + string.Create(8, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });

    /// <summary>Inserts <paramref name="block"/> at <paramref name="index"/>, clamped to the valid range.</summary>
    public static ContentDocument Add(ContentDocument doc, int index, ContentBlock block)
    {
        var blocks = BlocksOf(doc);
        return doc with { Blocks = blocks.Insert(Math.Clamp(index, 0, blocks.Length), block) };
    }

    /// <summary>Replaces the first block with the same id; unchanged when no block has that id.</summary>
    public static ContentDocument Replace(ContentDocument doc, ContentBlock block) =>
        Replace(doc, IndexOf(BlocksOf(doc), block.Id), block);

    /// <summary>Replaces the block at <paramref name="index"/>; unchanged when the index is out of range.</summary>
    public static ContentDocument Replace(ContentDocument doc, int index, ContentBlock block)
    {
        var blocks = BlocksOf(doc);
        return InRange(blocks, index) ? doc with { Blocks = blocks.SetItem(index, block) } : doc;
    }

    /// <summary>Removes the first block with this id. Removing the last block leaves a document with a single empty rich-text block.</summary>
    public static ContentDocument Remove(ContentDocument doc, string blockId) =>
        Remove(doc, IndexOf(BlocksOf(doc), blockId));

    /// <summary>Removes the block at <paramref name="index"/>; unchanged when out of range. Removing the last block leaves a single empty rich-text block.</summary>
    public static ContentDocument Remove(ContentDocument doc, int index)
    {
        var blocks = BlocksOf(doc);
        if (!InRange(blocks, index))
        {
            return doc;
        }

        var remaining = blocks.RemoveAt(index);
        return remaining.IsEmpty ? ContentDocument.Empty(NewBlockId()) : doc with { Blocks = remaining };
    }

    /// <summary>Moves the first block with this id by <paramref name="delta"/> positions, clamping at either end.</summary>
    public static ContentDocument Move(ContentDocument doc, string blockId, int delta) =>
        Move(doc, IndexOf(BlocksOf(doc), blockId), delta);

    /// <summary>Moves the block at <paramref name="index"/> by <paramref name="delta"/> positions, clamping at either end.</summary>
    public static ContentDocument Move(ContentDocument doc, int index, int delta)
    {
        var blocks = BlocksOf(doc);
        if (!InRange(blocks, index))
        {
            return doc;
        }

        var target = Math.Clamp(index + delta, 0, blocks.Length - 1);
        if (target == index)
        {
            return doc;
        }

        var block = blocks[index];
        return doc with { Blocks = blocks.RemoveAt(index).Insert(target, block) };
    }

    /// <summary>
    /// Splits the first rich-text block with this id before node <paramref name="nodeIndex"/>; see the index-based overload.
    /// </summary>
    public static ContentDocument SplitRichText(ContentDocument doc, string blockId, int nodeIndex, ContentBlock insert) =>
        SplitRichText(doc, IndexOf(BlocksOf(doc), blockId), nodeIndex, insert);

    /// <summary>
    /// Splits the rich-text block at <paramref name="index"/> before node <paramref name="nodeIndex"/> and puts <paramref name="insert"/> between the halves.
    /// The first half keeps the original id, the second gets a new one; an empty half is dropped.
    /// Unchanged when the index is out of range or the block is not rich text.
    /// </summary>
    public static ContentDocument SplitRichText(ContentDocument doc, int index, int nodeIndex, ContentBlock insert)
    {
        var blocks = BlocksOf(doc);
        if (!InRange(blocks, index) || blocks[index] is not RichTextBlock rich)
        {
            return doc;
        }

        var nodes = rich.Nodes.IsDefault ? [] : rich.Nodes;
        var split = Math.Clamp(nodeIndex, 0, nodes.Length);
        var replacement = new List<ContentBlock>(3);
        if (split > 0)
        {
            replacement.Add(rich with { Nodes = [.. nodes.Take(split)] });
        }

        replacement.Add(insert);
        if (split < nodes.Length)
        {
            replacement.Add(new RichTextBlock(NewBlockId(), [.. nodes.Skip(split)]));
        }

        return doc with { Blocks = blocks.RemoveAt(index).InsertRange(index, replacement) };
    }

    private static ImmutableArray<ContentBlock> BlocksOf(ContentDocument doc) => doc.Blocks.IsDefault ? [] : doc.Blocks;

    private static bool InRange(ImmutableArray<ContentBlock> blocks, int index) => index >= 0 && index < blocks.Length;

    private static int IndexOf(ImmutableArray<ContentBlock> blocks, string blockId)
    {
        for (var i = 0; i < blocks.Length; i++)
        {
            if (string.Equals(blocks[i].Id, blockId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
