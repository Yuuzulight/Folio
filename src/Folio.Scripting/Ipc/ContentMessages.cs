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
    public static bool TryGetCapabilities(this ContentMessage message, out FeatureCapabilities capabilities)
    {
        capabilities = FeatureCapabilities.Default;
        if (message.Kind is not (ContentMessageKind.Load or ContentMessageKind.Capabilities)
            || !message.Payload.TryGetProperty("capabilities", out var value)) return false;
        capabilities = JsonSerializer.Deserialize<FeatureCapabilities>(value.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? FeatureCapabilities.Default;
        return true;
    }

    public static bool TryGetDisplayList(this ContentMessage message, out DisplayListFrame? frame)
    {
        frame = null;
        if (message.Kind != ContentMessageKind.Frame || !message.Payload.TryGetProperty("documentRevision", out var revision)
            || !message.Payload.TryGetProperty("viewportWidth", out var width)
            || !message.Payload.TryGetProperty("viewportHeight", out var height)
            || !message.Payload.TryGetProperty("commands", out var commands)) return false;
        if (!revision.TryGetInt64(out var documentRevision) || !width.TryGetInt32(out var viewportWidth)
            || !height.TryGetInt32(out var viewportHeight)) return false;
        frame = new DisplayListFrame(documentRevision, viewportWidth, viewportHeight, commands.Clone());
        return true;
    }

    public static bool TryGetInput(this ContentMessage message, out ContentInputEvent? input)
    {
        input = null;
        if (message.Kind != ContentMessageKind.Input || !message.Payload.TryGetProperty("type", out var type)) return false;
        input = JsonSerializer.Deserialize<ContentInputEvent>(message.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return input is not null && !string.IsNullOrWhiteSpace(type.GetString());
    }
}
