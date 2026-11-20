using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyntaxCircus.RichContent;

public static class ContentDocumentSerializer
{
    // Single source of truth: the [JsonDerivedType] discriminators declared on ContentBlock.
    private static readonly HashSet<string> KnownBlocks = typeof(ContentBlock)
        .GetCustomAttributes<JsonDerivedTypeAttribute>()
        .Select(a => a.TypeDiscriminator as string)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(ContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteStartArray("blocks");
            foreach (var block in document.Blocks.IsDefault ? [] : document.Blocks)
            {
                if (block is UnknownBlock unknown)
                {
                    unknown.Raw.WriteTo(writer);
                }
                else
                {
                    JsonSerializer.Serialize(writer, Normalize(block), Options);
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ContentDocumentReadResult Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = Options.MaxDepth });
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail("Content document must be a JSON object.");
            }

            if (!root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schemaVersion))
            {
                return Fail("Content document is missing a numeric schemaVersion.");
            }

            if (schemaVersion != ContentDocument.CurrentSchemaVersion)
            {
                return Fail($"Unsupported content schema version {schemaVersion}; this version supports {ContentDocument.CurrentSchemaVersion}.");
            }

            if (!root.TryGetProperty("blocks", out var blocksElement) || blocksElement.ValueKind != JsonValueKind.Array)
            {
                return Fail("Content document is missing a blocks array.");
            }

            var blocks = ImmutableArray.CreateBuilder<ContentBlock>();
            foreach (var element in blocksElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return Fail("Each block must be a JSON object.");
                }

                var type = element.TryGetProperty("$block", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (type is not null && KnownBlocks.Contains(type))
                {
                    var block = element.Deserialize<ContentBlock>(Options);
                    if (block is not null && HasRequiredMembers(block))
                    {
                        blocks.Add(block);
                        continue;
                    }
                }

                var id = element.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                blocks.Add(new UnknownBlock(id, element.Clone()));
            }

            return new ContentDocumentReadResult(new ContentDocument(schemaVersion, blocks.ToImmutable()), null);
        }
        catch (JsonException ex)
        {
            return Fail($"Invalid content JSON: {ex.Message}");
        }
    }

    internal static bool IsKnownDiscriminator(string? type) => type is not null && KnownBlocks.Contains(type);

    private static bool HasRequiredMembers(ContentBlock block) => block.Id is not null && block switch
    {
        RichTextBlock b => !b.Nodes.IsDefault,
        CalloutBlock b => !b.Nodes.IsDefault,
        GalleryBlock b => !b.Images.IsDefault,
        ExtensionBlock b => b.Type is not null && b.Data.ValueKind != JsonValueKind.Undefined,
        _ => true,
    };

    // Hand-built documents may carry default (uninitialized) ImmutableArrays, which cannot be serialized.
    private static ContentBlock Normalize(ContentBlock block) => block switch
    {
        RichTextBlock b when b.Nodes.IsDefault => b with { Nodes = [] },
        CalloutBlock b when b.Nodes.IsDefault => b with { Nodes = [] },
        GalleryBlock b when b.Images.IsDefault => b with { Images = [] },
        _ => block,
    };

    private static ContentDocumentReadResult Fail(string error) => new(null, error);
}
