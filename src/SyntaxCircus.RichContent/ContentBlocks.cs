using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyntaxCircus.RichContent;

public enum CalloutTone { Info, Tip, Warning }

/// <summary>
/// Base of all content blocks. Known blocks are polymorphic via the "$block" discriminator;
/// <see cref="UnknownBlock"/> is handled explicitly by <see cref="ContentDocumentSerializer"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$block", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RichTextBlock), "rich-text")]
[JsonDerivedType(typeof(GalleryBlock), "gallery")]
[JsonDerivedType(typeof(CalloutBlock), "callout")]
[JsonDerivedType(typeof(CtaBlock), "cta")]
[JsonDerivedType(typeof(ExtensionBlock), "extension")]
public abstract record ContentBlock(string Id);

public sealed record RichTextBlock(string Id, ImmutableArray<RichTextNode> Nodes) : ContentBlock(Id);

public sealed record GalleryImage(MediaRef Media, string Alt, string? Caption);

public sealed record GalleryBlock(string Id, ImmutableArray<GalleryImage> Images, string? Title) : ContentBlock(Id);

public sealed record CalloutBlock(string Id, CalloutTone Tone, string? Title, ImmutableArray<RichTextNode> Nodes) : ContentBlock(Id);

public sealed record CtaBlock(string Id, string Heading, string? Body, string Label, string Url) : ContentBlock(Id);

/// <summary>An app-defined block (e.g. a project card); <see cref="Type"/> names it, <see cref="Data"/> is its payload.</summary>
public sealed record ExtensionBlock(string Id, string Type, JsonElement Data) : ContentBlock(Id);

/// <summary>A block whose "$block" type this version does not know; preserved verbatim so it round-trips.</summary>
public sealed record UnknownBlock(string Id, JsonElement Raw) : ContentBlock(Id);
