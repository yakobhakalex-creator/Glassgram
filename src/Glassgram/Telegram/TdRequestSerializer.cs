using System.Text;
using System.Text.Json;

namespace Glassgram.Telegram;

/// <summary>
/// Serializes TDLib JSON requests. C# treats <c>@type</c> as an escaped identifier and
/// JsonSerializer therefore emits "type"; TDLib requires "@type" at every typed level.
/// </summary>
internal static class TdRequestSerializer
{
    public static string Serialize(object request, long? extra = null)
    {
        var source = JsonSerializer.SerializeToElement(request);
        if (source.ValueKind != JsonValueKind.Object)
            throw new JsonException("TDLib request must be a JSON object.");
        if (extra.HasValue && source.TryGetProperty("@extra", out _))
            throw new JsonException("TDLib request already contains @extra.");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteObject(writer, source, extra, root: true);
        }
        var json = Encoding.UTF8.GetString(stream.ToArray());
        using var verification = JsonDocument.Parse(json);
        var type = verification.RootElement.TryGetProperty("@type", out var typeElement) ? typeElement.GetString() ?? "" : "";
        if (type.Length == 0) throw new JsonException("TDLib request root is missing @type.");
        EnsureNoEscapedTypeNames(verification.RootElement, "$");
        return json;
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement value, long? extra = null, bool root = false)
    {
        // A typed TDLib object may legitimately have a schema field named "type"
        // (for example inputMessagePoll.type). Only escaped C# @type needs rewriting.
        var hasExplicitType = value.TryGetProperty("@type", out _);
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            writer.WritePropertyName(property.Name == "type" && !hasExplicitType ? "@type" : property.Name);
            WriteValue(writer, property.Value);
        }
        if (root && extra.HasValue) writer.WriteNumber("@extra", extra.Value);
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, value);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void EnsureNoEscapedTypeNames(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name == "type" && !value.TryGetProperty("@type", out _))
                    throw new JsonException($"TDLib typed object contains 'type' instead of '@type' at {path}.");
                EnsureNoEscapedTypeNames(property.Value, path + "." + property.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) EnsureNoEscapedTypeNames(item, $"{path}[{index++}]");
        }
    }
}
