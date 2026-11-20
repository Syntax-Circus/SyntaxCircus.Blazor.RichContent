namespace SyntaxCircus.RichContent;

/// <summary>Configurable size limits enforced by <see cref="RichContentRules"/>. Defaults are blog-sized.</summary>
public sealed record RichContentLimits(
    int Nodes = 4096,
    int Depth = 8,
    int TextBytes = 256 * 1024,
    int CodeBlockBytes = 32 * 1024,
    int Blocks = 200,
    int UrlCharacters = 2048,
    int GalleryImages = 24,
    int TableRows = 50,
    int TableCells = 10)
{
    public const int MinimumHeadingLevel = 2;
    public const int MaximumHeadingLevel = 4;
    public const int AltCharacters = 300;
    public const int CaptionCharacters = 300;
    public const int ExtensionDataBytes = 16 * 1024;

    public static RichContentLimits Default { get; } = new();
}
