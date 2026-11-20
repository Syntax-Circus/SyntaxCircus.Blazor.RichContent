using System.Collections.Immutable;

namespace SyntaxCircus.RichContent;

public sealed record ContentDocument(int SchemaVersion, ImmutableArray<ContentBlock> Blocks)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>A current-version document holding one empty rich-text block.</summary>
    public static ContentDocument Empty(string firstBlockId) =>
        new(CurrentSchemaVersion, [new RichTextBlock(firstBlockId, [])]);
}

public sealed record ContentDocumentReadResult(ContentDocument? Document, string? Error);
