namespace SyntaxCircus.RichContent;

/// <summary>Opaque reference to a media item owned by the host app, with an optional rendition name.</summary>
public sealed record MediaRef(string Id, string? Variant = null);
