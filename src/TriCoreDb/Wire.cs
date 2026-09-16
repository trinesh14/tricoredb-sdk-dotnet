using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriCoreDb;

/// <summary>
/// Encoding and decoding helpers shared by every typed operation.
///
/// The server's response payload is an externally tagged enum — <c>{"Json": {...}}</c>,
/// <c>{"Documents": [...]}</c>, <c>{"CacheValue": [...]}</c> — so unwrapping it is the
/// same three steps everywhere. Doing it here means a shape the server never sends
/// raises <see cref="ProtocolException"/> at the call site rather than surfacing as a
/// null or an empty list, which reads exactly like a legitimately empty result.
/// </summary>
internal static class Wire
{
    /// <summary>Build an externally tagged op: <c>{"Document": {"Insert": {...}}}</c>.</summary>
    internal static JsonObject Op(string family, string variant, JsonObject body) =>
        new() { [family] = new JsonObject { [variant] = body } };

    /// <summary>A payload-free variant, e.g. <c>{"Document": "ListCollections"}</c>.</summary>
    internal static JsonObject UnitOp(string family, string variant) =>
        new() { [family] = JsonValue.Create(variant) };

    /// <summary>The <c>Json</c> payload of a response, or a protocol error naming what arrived.</summary>
    internal static JsonElement Json(Response resp)
    {
        if (resp.Data.ValueKind == JsonValueKind.Object &&
            resp.Data.TryGetProperty("Json", out var j))
            return j;
        throw new ProtocolException($"expected a Json payload, got {Kind(resp.Data)}");
    }

    /// <summary>The <c>Documents</c> payload of a response.</summary>
    internal static IReadOnlyList<Dictionary<string, object?>> Documents(Response resp)
    {
        if (resp.Data.ValueKind == JsonValueKind.Object &&
            resp.Data.TryGetProperty("Documents", out var d) &&
            d.ValueKind == JsonValueKind.Array)
        {
            var docs = new List<Dictionary<string, object?>>(d.GetArrayLength());
            foreach (var el in d.EnumerateArray())
                docs.Add(ToDictionary(el));
            return docs;
        }
        throw new ProtocolException($"expected Documents, got {Kind(resp.Data)}");
    }

    /// <summary>The <c>CacheValue</c> payload: the bytes, or null on a miss.</summary>
    internal static byte[]? CacheValue(Response resp)
    {
        if (resp.Data.ValueKind == JsonValueKind.Object &&
            resp.Data.TryGetProperty("CacheValue", out var v))
            return v.ValueKind == JsonValueKind.Null ? null : Bytes(v);
        throw new ProtocolException($"expected CacheValue, got {Kind(resp.Data)}");
    }

    /// <summary>The name of the payload variant the server actually sent, for error text.</summary>
    internal static string Kind(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var prop in data.EnumerateObject())
                return prop.Name;
        return data.ValueKind == JsonValueKind.Undefined ? "<none>" : data.ValueKind.ToString();
    }

    // -- field readers ---------------------------------------------------------------------
    //
    // Each names the field it could not read. "expected `matched` to be a number" is a
    // fixable report; a NullReferenceException three frames up is not.

    internal static JsonElement Field(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v))
            return v;
        throw new ProtocolException($"response is missing the `{name}` field");
    }

    internal static string Str(JsonElement obj, string name)
    {
        var v = Field(obj, name);
        if (v.ValueKind == JsonValueKind.String) return v.GetString()!;
        if (v.ValueKind == JsonValueKind.Null) return "";
        throw new ProtocolException($"expected `{name}` to be a string, got {v.ValueKind}");
    }

    /// <summary>A string field that the server may legitimately omit or send as null.</summary>
    internal static string? OptStr(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    internal static long Num(JsonElement obj, string name)
    {
        var v = Field(obj, name);
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        throw new ProtocolException($"expected `{name}` to be a number, got {v.ValueKind}");
    }

    internal static long NumOr(JsonElement obj, string name, long fallback)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt64() : fallback;
    }

    internal static double Dbl(JsonElement obj, string name)
    {
        var v = Field(obj, name);
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        throw new ProtocolException($"expected `{name}` to be a number, got {v.ValueKind}");
    }

    internal static bool Bool(JsonElement obj, string name)
    {
        var v = Field(obj, name);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ProtocolException($"expected `{name}` to be a boolean, got {v.ValueKind}"),
        };
    }

    internal static bool BoolOr(JsonElement obj, string name, bool fallback)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    internal static IReadOnlyList<string> Strings(JsonElement obj, string name)
    {
        var v = Field(obj, name);
        if (v.ValueKind != JsonValueKind.Array)
            throw new ProtocolException($"expected `{name}` to be an array, got {v.ValueKind}");
        var list = new List<string>(v.GetArrayLength());
        foreach (var el in v.EnumerateArray()) list.Add(el.GetString() ?? "");
        return list;
    }

    internal static IReadOnlyList<JsonElement> Items(JsonElement obj, string name)
    {
        var v = Field(obj, name);
        if (v.ValueKind != JsonValueKind.Array)
            throw new ProtocolException($"expected `{name}` to be an array, got {v.ValueKind}");
        var list = new List<JsonElement>(v.GetArrayLength());
        foreach (var el in v.EnumerateArray()) list.Add(el);
        return list;
    }

    internal static float[] Floats(JsonElement obj, string name)
    {
        var v = Field(obj, name);
        if (v.ValueKind != JsonValueKind.Array)
            throw new ProtocolException($"expected `{name}` to be an array, got {v.ValueKind}");
        var arr = new float[v.GetArrayLength()];
        int i = 0;
        foreach (var el in v.EnumerateArray()) arr[i++] = (float)el.GetDouble();
        return arr;
    }

    internal static byte[] Bytes(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array)
            throw new ProtocolException($"expected a byte array, got {v.ValueKind}");
        var bytes = new byte[v.GetArrayLength()];
        int i = 0;
        foreach (var el in v.EnumerateArray()) bytes[i++] = (byte)el.GetInt32();
        return bytes;
    }

    /// <summary>An object field decoded to a plain dictionary; an absent field is empty.</summary>
    internal static Dictionary<string, object?> Obj(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return new();
        return v.ValueKind == JsonValueKind.Object ? ToDictionary(v) : new();
    }

    // -- CLR <-> JSON ----------------------------------------------------------------------

    /// <summary>Decode a JSON object into nested CLR values (dictionary/list/string/…).</summary>
    internal static Dictionary<string, object?> ToDictionary(JsonElement el)
    {
        var map = new Dictionary<string, object?>();
        if (el.ValueKind != JsonValueKind.Object) return map;
        foreach (var prop in el.EnumerateObject())
            map[prop.Name] = ToClr(prop.Value);
        return map;
    }

    internal static object? ToClr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(el),
        JsonValueKind.Array => ToList(el),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        _ => el.GetRawText(),
    };

    private static List<object?> ToList(JsonElement el)
    {
        var list = new List<object?>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray()) list.Add(ToClr(item));
        return list;
    }

    /// <summary>
    /// Encode a CLR value for the wire.
    ///
    /// Anything the BCL's JSON writer understands is accepted, including nested
    /// dictionaries and lists. This is deliberately permissive: document values are
    /// user data, and a driver that only accepted a fixed set of types would refuse
    /// documents the server stores happily.
    /// </summary>
    internal static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement el => JsonNode.Parse(el.GetRawText()),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        byte[] bytes => ByteArray(bytes),
        System.Collections.IDictionary dict => DictNode(dict),
        System.Collections.IEnumerable seq and not string => ListNode(seq),
        _ => JsonSerializer.SerializeToNode(value),
    };

    internal static JsonObject ObjectNode(IReadOnlyDictionary<string, object?> map)
    {
        var obj = new JsonObject();
        foreach (var kv in map) obj[kv.Key] = ToNode(kv.Value);
        return obj;
    }

    /// <summary>Bytes travel as a JSON array of numbers — see WIRE_REFERENCE.md.</summary>
    /// <summary>
    /// Bytes travel as a JSON array of numbers — see WIRE_REFERENCE.md.
    /// </summary>
    /// <remarks>
    /// The array is rendered as text and parsed back, rather than built one
    /// <c>JsonValue</c> at a time.
    ///
    /// The obvious loop allocates a boxed node per byte: 4,096 of them for a
    /// 4 KiB value, every one of which is then walked again at serialisation
    /// time. It cost this driver roughly 4x on large writes against the Java
    /// driver, which writes the digits directly (measured: 2,321 vs 10,238
    /// ops/s for 4 KiB values). Building the text once and handing it to the
    /// parser keeps the allocation proportional to the payload instead of to
    /// the node count.
    /// </remarks>
    internal static JsonNode ByteArray(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return new JsonArray();

        // Worst case 4 chars per element ("255,") plus the brackets.
        var sb = new System.Text.StringBuilder(bytes.Length * 4 + 2);
        sb.Append('[');
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(bytes[i]);
        }
        sb.Append(']');
        return JsonNode.Parse(sb.ToString())!;
    }

    private static JsonObject DictNode(System.Collections.IDictionary dict)
    {
        var obj = new JsonObject();
        foreach (System.Collections.DictionaryEntry e in dict)
            obj[Convert.ToString(e.Key) ?? ""] = ToNode(e.Value);
        return obj;
    }

    private static JsonArray ListNode(System.Collections.IEnumerable seq)
    {
        var arr = new JsonArray();
        foreach (var item in seq) arr.Add(ToNode(item));
        return arr;
    }

    // -- argument checking -----------------------------------------------------------------

    internal static string Require(string? value, string name) =>
        string.IsNullOrEmpty(value)
            ? throw new ArgumentException($"{name} must not be null or empty", name)
            : value;

    internal static T RequireNotNull<T>(T? value, string name) where T : class =>
        value ?? throw new ArgumentNullException(name);
}
