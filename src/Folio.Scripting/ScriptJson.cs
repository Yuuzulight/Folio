using System.Collections;
using System.Text;
using System.Numerics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Encodings.Web;
using System.Runtime.CompilerServices;
using Folio.Scripting.Dom;
using Folio.Scripting.Ipc;
using Folio.Scripting.Native;

namespace Folio.Scripting;

/// <summary>Explicit JSON shapes for the isolated native host; no runtime-generated serializers.</summary>
internal static class ScriptJson
{
    internal static string Serialize<T>(T value) => Encoding.UTF8.GetString(SerializeToUtf8Bytes(value));
    internal static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, value, new(ReferenceEqualityComparer.Instance), 0, false);
        return buffer.ToArray();
    }
    internal static JsonNode? SerializeToNode<T>(T value) => JsonNode.Parse(SerializeToUtf8Bytes(value));
    internal static JsonElement SerializeToElement<T>(T value)
    {
        using var document = JsonDocument.Parse(SerializeToUtf8Bytes(value));
        return document.RootElement.Clone();
    }
    internal static T? Deserialize<T>(string text) => JsonSerializer.Deserialize(text, Info<T>());
    internal static T? Deserialize<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize(bytes, Info<T>());
    internal static T? Deserialize<T>(JsonElement element) => element.Deserialize(Info<T>());
    private static JsonTypeInfo<T> Info<T>() => ScriptingJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException($"No native JSON contract for {typeof(T)}.");

    internal static object Stringify(object? value)
    {
        if (Omitted(value)) return JavaScriptEvaluator.UndefinedValue.Instance;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(writer, value, new(ReferenceEqualityComparer.Instance), 0, true);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static bool Omitted(object? value) => value is JavaScriptEvaluator.UndefinedValue
        or NativeJavaScriptEngine.INativeCallable or Delegate;

    private static void Write(Utf8JsonWriter writer, object? value, HashSet<object> path, int depth, bool javascript)
    {
        if (javascript) NativeExecutionBudget.CheckCurrent();
        if (depth > 64) throw new JsonException("JSON nesting exceeds the native host limit.");
        switch (value)
        {
            case null: writer.WriteNullValue(); return;
            case string text: writer.WriteStringValue(text); return;
            case bool boolean: writer.WriteBooleanValue(boolean); return;
            case byte n: writer.WriteNumberValue(n); return;
            case sbyte n: writer.WriteNumberValue(n); return;
            case short n: writer.WriteNumberValue(n); return;
            case ushort n: writer.WriteNumberValue(n); return;
            case int n: writer.WriteNumberValue(n); return;
            case uint n: writer.WriteNumberValue(n); return;
            case long n: writer.WriteNumberValue(n); return;
            case ulong n: writer.WriteNumberValue(n); return;
            case decimal n: writer.WriteNumberValue(n); return;
            case float n when javascript && n == 0: writer.WriteNumberValue(0); return;
            case double n when javascript && n == 0: writer.WriteNumberValue(0); return;
            case float n when javascript && !float.IsFinite(n): writer.WriteNullValue(); return;
            case double n when javascript && !double.IsFinite(n): writer.WriteNullValue(); return;
            case float n: writer.WriteNumberValue(n); return;
            case double n: writer.WriteNumberValue(n); return;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); return;
            case JsonElement json: json.WriteTo(writer); return;
            case JsonNode node: node.WriteTo(writer); return;
            case BigInteger when javascript:
                throw JavaScriptEvaluator.ConvertHostException(WebIdlException.TypeError("BigInt cannot be serialized to JSON."));
            case NativeJavaScriptEngine.NativeDate date when javascript:
                if (!double.IsFinite(date.Milliseconds)) writer.WriteNullValue();
                else writer.WriteStringValue(DateTimeOffset.FromUnixTimeMilliseconds((long)date.Milliseconds).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                return;
            case NativeJavaScriptEngine.NativeError or NativeJavaScriptEngine.NativeMap or NativeJavaScriptEngine.NativeSet
                or NativeJavaScriptEngine.NativeWeakMap or NativeJavaScriptEngine.INativePromise when javascript:
                writer.WriteStartObject(); writer.WriteEndObject(); return;
            case NativeJavaScriptEngine.INativeBuffer buffer when javascript:
                writer.WriteStartObject();
                if (buffer.TypeName is not ("ArrayBuffer" or "DataView") && buffer.TryGetProperty("length", out var count))
                    for (var index = 0; index < Convert.ToInt32(count, CultureInfo.InvariantCulture); index++)
                    {
                        writer.WritePropertyName(index.ToString(CultureInfo.InvariantCulture));
                        buffer.TryGetIndex(index, out var item); Write(writer, item, path, depth + 1, true);
                    }
                writer.WriteEndObject(); return;
        }
        if (javascript && Omitted(value)) { writer.WriteNullValue(); return; }
        if (!path.Add(value))
        {
            if (javascript) throw JavaScriptEvaluator.ConvertHostException(WebIdlException.TypeError("Converting a circular structure to JSON."));
            throw new JsonException("A cycle was detected in JSON data.");
        }
        try
        {
            if (value is IReadOnlyDictionary<string, object?> properties)
            {
                writer.WriteStartObject();
                foreach (var (key, item) in properties)
                {
                    if (javascript && Omitted(item)) continue;
                    writer.WritePropertyName(key); Write(writer, item, path, depth + 1, javascript);
                }
                writer.WriteEndObject();
            }
            else if (value is IDictionary dictionary)
            {
                writer.WriteStartObject();
                foreach (DictionaryEntry item in dictionary)
                {
                    if (javascript && Omitted(item.Value)) continue;
                    if (item.Key is not string key) throw new JsonException("JSON object keys must be strings.");
                    writer.WritePropertyName(key); Write(writer, item.Value, path, depth + 1, javascript);
                }
                writer.WriteEndObject();
            }
            else if (value is IEnumerable sequence)
            {
                writer.WriteStartArray();
                foreach (var item in sequence) Write(writer, item, path, depth + 1, javascript);
                writer.WriteEndArray();
            }
            else if (ScriptingJsonContext.Default.GetTypeInfo(value.GetType()) is { } contract)
                JsonSerializer.Serialize(writer, value, contract);
#if !FOLIO_NATIVE_HOST
            else if (RuntimeFeature.IsDynamicCodeSupported && JsonSerializer.IsReflectionEnabledByDefault)
                JsonSerializer.Serialize(writer, value);
#endif
            else throw new NotSupportedException($"No native JSON contract for {value.GetType()}.");
        }
        finally { path.Remove(value); }
    }
}

[JsonSerializable(typeof(FeatureCapabilities))]
[JsonSerializable(typeof(ContentCacheBroker.Request), TypeInfoPropertyName = "StorageRequest")]
[JsonSerializable(typeof(CacheStorageState.Request), TypeInfoPropertyName = "CacheRequest")]
[JsonSerializable(typeof(ContentCacheBroker.Reply))]
[JsonSerializable(typeof(CacheStorageState.Snapshot))]
[JsonSerializable(typeof(CacheStorageState.Entry[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string[][]))]
internal partial class ScriptingJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FeatureCapabilities))]
internal partial class CapabilityJsonContext : JsonSerializerContext;
