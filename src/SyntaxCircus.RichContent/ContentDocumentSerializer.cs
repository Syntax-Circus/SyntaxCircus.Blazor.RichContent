using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyntaxCircus.RichContent;

public static class ContentDocumentSerializer
{
    private static readonly HashSet<string> KnownBlocks = ["rich-text", "gallery", "callout", "cta", "extension"];

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
            foreach (var block in document.Blocks)
            {
                if (block is UnknownBlock unknown)
                {
                    unknown.Raw.WriteTo(writer);
                }
                else
                {
                    JsonSerializer.Serialize<ContentBlock>(writer, block, Options);
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
                    if (block is null)
                    {
                        return Fail("Block could not be read.");
                    }
                    blocks.Add(block);
                }
                else
                {
                    var id = element.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                    blocks.Add(new UnknownBlock(id, element.Clone()));
                }
            }

            return new ContentDocumentReadResult(new ContentDocument(schemaVersion, blocks.ToImmutable()), null);
        }
        catch (JsonException ex)
        {
            return Fail($"Invalid content JSON: {ex.Message}");
        }
    }

    private static ContentDocumentReadResult Fail(string error) => new(null, error);
}
