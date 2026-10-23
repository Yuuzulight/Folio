using System.Text.Json;

namespace Folio.Scripting.Ipc;

/// <summary>Host/content payloads carried by the versioned protocol.</summary>
public sealed record DisplayListFrame(long DocumentRevision, int ViewportWidth, int ViewportHeight, JsonElement Commands);

public sealed record ContentInputEvent(
    string Type,
    double X = 0,
    double Y = 0,
    int Button = 0,
    string? Key = null,
    string? Text = null,
    int Buttons = 0,
    int Modifiers = 0,
    double DeltaX = 0,
    double DeltaY = 0,
    string? Code = null);

public static class ContentMessagePayloads
{
    private static readonly HashSet<string> KnownCapabilityNames = typeof(FeatureCapabilities).GetProperties()
        .Where(property => property.PropertyType == typeof(bool))
        .Select(property => property.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetCapabilities(this ContentMessage message, out FeatureCapabilities capabilities)
    {
        capabilities = FeatureCapabilities.Default;
        try
        {
            if (message.Kind is not (ContentMessageKind.Load or ContentMessageKind.Capabilities)
                || message.Payload.ValueKind != JsonValueKind.Object) return false;
            JsonElement value = default;
            var wrapperFound = false;
            foreach (var property in message.Payload.EnumerateObject())
            {
                if (!property.Name.Equals("capabilities", StringComparison.OrdinalIgnoreCase)) continue;
                if (wrapperFound) return false;
                wrapperFound = true;
                value = property.Value;
            }
            if (!wrapperFound || value.ValueKind != JsonValueKind.Object) return false;

            // A malformed known field must fail closed. Unknown fields are deliberately ignored so
            // newer senders cannot grant an unrecognised capability to an older host.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return false;
                if (KnownCapabilityNames.Contains(property.Name)
                    && property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return false;
            }

            capabilities = JsonSerializer.Deserialize<FeatureCapabilities>(value.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? FeatureCapabilities.Default;
            return true;
        }
        catch (JsonException) { capabilities = FeatureCapabilities.Default; return false; }
        catch (InvalidOperationException) { capabilities = FeatureCapabilities.Default; return false; }
        catch (NotSupportedException) { capabilities = FeatureCapabilities.Default; return false; }
    }

    public static bool TryGetDisplayList(this ContentMessage message, out DisplayListFrame? frame)
    {
        frame = null;
        try
        {
            if (message.Kind != ContentMessageKind.Frame || message.Payload.ValueKind != JsonValueKind.Object
                || !message.Payload.TryGetProperty("documentRevision", out var revision)
                || !message.Payload.TryGetProperty("viewportWidth", out var width)
                || !message.Payload.TryGetProperty("viewportHeight", out var height)
                || !message.Payload.TryGetProperty("commands", out var commands)) return false;
            if (!revision.TryGetInt64(out var documentRevision) || !width.TryGetInt32(out var viewportWidth)
                || !height.TryGetInt32(out var viewportHeight)) return false;
            frame = new DisplayListFrame(documentRevision, viewportWidth, viewportHeight, commands.Clone());
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public static bool TryGetInput(this ContentMessage message, out ContentInputEvent? input)
    {
        input = null;
        try
        {
            if (message.Kind != ContentMessageKind.Input || message.Payload.ValueKind != JsonValueKind.Object) return false;
            var hasType = message.Payload.TryGetProperty("type", out var type)
                || message.Payload.TryGetProperty("Type", out type);
            if (!hasType || type.ValueKind != JsonValueKind.String) return false;
            input = JsonSerializer.Deserialize<ContentInputEvent>(message.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return input is not null && !string.IsNullOrWhiteSpace(type.GetString());
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
