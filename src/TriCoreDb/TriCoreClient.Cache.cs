using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriCoreDb;

// The cache family: string values, TTLs, counters, and the collection types
// (lists, sets, hashes, streams).
//
// Values are `byte[]` throughout rather than `string`. The server stores opaque
// bytes, and a driver that only spoke strings would silently corrupt any value
// that is not valid UTF-8. The `*Text` overloads exist for the common case and
// are explicit about the encoding they impose.

public sealed partial class TriCoreClient
{
    // -- strings ---------------------------------------------------------------------------

    /// <summary>Liveness check routed through the cache core (distinct from the
    /// transport-level <see cref="PingAsync"/>, which never reaches a module).</summary>
    public Task<Response> CachePingAsync(string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(Wire.UnitOp("Cache", "Ping"), database, cancellationToken);

    /// <summary>Set a key, optionally with a time to live.</summary>
    public Task<Response> CacheSetAsync(
        string ns, string key, byte[] value, long? ttlMs = null,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["value"] = Wire.ByteArray(Wire.RequireNotNull(value, nameof(value)));
        body["ttl_ms"] = ttlMs;
        return RequestAsync(Wire.Op("Cache", "Set", body), database, cancellationToken);
    }

    /// <summary>Set a key from a string, encoded UTF-8.</summary>
    public Task<Response> CacheSetTextAsync(
        string ns, string key, string value, long? ttlMs = null,
        string database = "main", CancellationToken cancellationToken = default) =>
        CacheSetAsync(ns, key, Encoding.UTF8.GetBytes(Wire.RequireNotNull(value, nameof(value))), ttlMs, database, cancellationToken);

    /// <summary>The value, or null on a miss — distinct from an empty (zero-length) value.</summary>
    public async Task<byte[]?> CacheGetAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "Get", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        return Wire.CacheValue(resp);
    }

    /// <summary>The value decoded as UTF-8, or null on a miss.</summary>
    public async Task<string?> CacheGetTextAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var bytes = await CacheGetAsync(ns, key, database, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Delete a key. Returns whether it existed — deleting a missing key is not an error.</summary>
    public async Task<bool> CacheDeleteAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "Delete", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        return Wire.Bool(Wire.Json(resp), "deleted");
    }

    public async Task<bool> CacheExistsAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "Exists", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        return Wire.Bool(Wire.Json(resp), "exists");
    }

    /// <summary>Remaining time to live in milliseconds; null when the key is missing or
    /// has no expiry. Use <see cref="CacheExistsAsync"/> to tell those two apart.</summary>
    public async Task<long?> CacheTtlAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "Ttl", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        var json = Wire.Json(resp);
        if (!json.TryGetProperty("ttl_ms", out var v) || v.ValueKind != JsonValueKind.Number) return null;
        return v.GetInt64();
    }

    /// <summary>Delete every key in a namespace. Returns how many were removed.</summary>
    public async Task<long> CacheClearNamespaceAsync(
        string ns, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["namespace"] = Wire.Require(ns, nameof(ns)) };
        var resp = await RequestAsync(Wire.Op("Cache", "ClearNamespace", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "cleared");
    }

    /// <summary>Add <paramref name="by"/> to a counter and return the new value. A missing
    /// key starts at zero; a key holding a non-numeric value is an error, never a coercion.</summary>
    public async Task<long> CacheIncrAsync(
        string ns, string key, long by = 1, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["by"] = by;
        var resp = await RequestAsync(Wire.Op("Cache", "Incr", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "value");
    }

    /// <summary>Set or replace a key's TTL. Returns false when the key does not exist.</summary>
    public async Task<bool> CacheExpireAsync(
        string ns, string key, long ttlMs, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["ttl_ms"] = ttlMs;
        var resp = await RequestAsync(Wire.Op("Cache", "Expire", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Bool(Wire.Json(resp), "updated");
    }

    /// <summary>Remove a key's TTL, making it permanent. Returns false when it had none.</summary>
    public async Task<bool> CachePersistAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "Persist", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        return Wire.Bool(Wire.Json(resp), "persisted");
    }

    /// <summary>Set only if absent. Returns whether the write happened — the primitive
    /// behind a distributed lock.</summary>
    public async Task<bool> CacheSetNxAsync(
        string ns, string key, byte[] value, long? ttlMs = null,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["value"] = Wire.ByteArray(Wire.RequireNotNull(value, nameof(value)));
        body["ttl_ms"] = ttlMs;
        var resp = await RequestAsync(Wire.Op("Cache", "SetNx", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Bool(Wire.Json(resp), "set");
    }

    /// <summary>
    /// List live keys in a namespace.
    /// </summary>
    /// <param name="pattern">a simple glob where <c>*</c> matches any run of characters
    /// (<c>user:*</c>, <c>*:sess</c>, <c>*tmp*</c>); null lists everything.</param>
    public async Task<IReadOnlyList<CacheKeyInfo>> CacheKeysAsync(
        string ns, string? pattern = null, int? limit = null,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["namespace"] = Wire.Require(ns, nameof(ns)),
            ["pattern"] = pattern,
            ["limit"] = limit,
        };
        var resp = await RequestAsync(Wire.Op("Cache", "Keys", body), database, cancellationToken).ConfigureAwait(false);
        var list = new List<CacheKeyInfo>();
        foreach (var el in Wire.Items(Wire.Json(resp), "keys"))
        {
            long? ttl = el.TryGetProperty("ttl_ms", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null;
            list.Add(new CacheKeyInfo(Wire.Str(el, "key"), ttl));
        }
        return list;
    }

    // -- lists -----------------------------------------------------------------------------
    //
    // A key holds exactly one type at a time; operating on the wrong type is an error,
    // never a coercion. A mutation never resets the key's TTL, and a collection that
    // becomes empty deletes its key.

    /// <summary>Prepend elements. Returns the new length.</summary>
    public Task<long> CacheLPushAsync(
        string ns, string key, IEnumerable<byte[]> values,
        string database = "main", CancellationToken cancellationToken = default) =>
        PushAsync("LPush", ns, key, values, database, cancellationToken);

    /// <summary>Append elements. Returns the new length.</summary>
    public Task<long> CacheRPushAsync(
        string ns, string key, IEnumerable<byte[]> values,
        string database = "main", CancellationToken cancellationToken = default) =>
        PushAsync("RPush", ns, key, values, database, cancellationToken);

    private async Task<long> PushAsync(
        string variant, string ns, string key, IEnumerable<byte[]> values, string database, CancellationToken cancellationToken)
    {
        var body = KeyBody(ns, key);
        body["values"] = ByteArrays(values, nameof(values));
        var resp = await RequestAsync(Wire.Op("Cache", variant, body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "length");
    }

    /// <summary>Remove and return the first element, or null on an empty/missing key.</summary>
    public async Task<byte[]?> CacheLPopAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default) =>
        Wire.CacheValue(await RequestAsync(Wire.Op("Cache", "LPop", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false));

    /// <summary>Remove and return the last element, or null on an empty/missing key.</summary>
    public async Task<byte[]?> CacheRPopAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default) =>
        Wire.CacheValue(await RequestAsync(Wire.Op("Cache", "RPop", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false));

    /// <summary>An inclusive index range. Negative indices count from the end
    /// (<c>-1</c> is the last element) and out-of-range bounds clamp.</summary>
    public async Task<IReadOnlyList<byte[]>> CacheLRangeAsync(
        string ns, string key, long start, long stop,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["start"] = start;
        body["stop"] = stop;
        var resp = await RequestAsync(Wire.Op("Cache", "LRange", body), database, cancellationToken).ConfigureAwait(false);
        return ByteList(Wire.Json(resp), "values");
    }

    /// <summary>Number of elements (zero when the key is missing).</summary>
    public async Task<long> CacheLLenAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "LLen", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "length");
    }

    /// <summary>One element by index; negative counts from the end. Null when out of range.</summary>
    public async Task<byte[]?> CacheLIndexAsync(
        string ns, string key, long index, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["index"] = index;
        return Wire.CacheValue(await RequestAsync(Wire.Op("Cache", "LIndex", body), database, cancellationToken).ConfigureAwait(false));
    }

    // -- sets ------------------------------------------------------------------------------

    /// <summary>Add members. Returns how many were newly added.</summary>
    public async Task<long> CacheSAddAsync(
        string ns, string key, IEnumerable<byte[]> members,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["members"] = ByteArrays(members, nameof(members));
        var resp = await RequestAsync(Wire.Op("Cache", "SAdd", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "added");
    }

    /// <summary>Remove members. Returns how many were present.</summary>
    public async Task<long> CacheSRemAsync(
        string ns, string key, IEnumerable<byte[]> members,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["members"] = ByteArrays(members, nameof(members));
        var resp = await RequestAsync(Wire.Op("Cache", "SRem", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "removed");
    }

    public async Task<bool> CacheSIsMemberAsync(
        string ns, string key, byte[] member, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["member"] = Wire.ByteArray(Wire.RequireNotNull(member, nameof(member)));
        var resp = await RequestAsync(Wire.Op("Cache", "SIsMember", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Bool(Wire.Json(resp), "is_member");
    }

    /// <summary>Number of members (zero when the key is missing).</summary>
    public async Task<long> CacheSCardAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "SCard", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "cardinality");
    }

    /// <summary>Every member, in ascending byte order.</summary>
    public async Task<IReadOnlyList<byte[]>> CacheSMembersAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "SMembers", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        return ByteList(Wire.Json(resp), "members");
    }

    // -- hashes ----------------------------------------------------------------------------

    /// <summary>Set fields. Returns how many were newly created (as opposed to overwritten).</summary>
    public async Task<long> CacheHSetAsync(
        string ns, string key, IEnumerable<KeyValuePair<byte[], byte[]>> entries,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["entries"] = PairArray(entries, nameof(entries));
        var resp = await RequestAsync(Wire.Op("Cache", "HSet", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "created");
    }

    /// <summary>Set string fields, encoded UTF-8.</summary>
    public Task<long> CacheHSetTextAsync(
        string ns, string key, IReadOnlyDictionary<string, string> entries,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var pairs = new List<KeyValuePair<byte[], byte[]>>();
        foreach (var kv in Wire.RequireNotNull(entries, nameof(entries)))
            pairs.Add(new(Encoding.UTF8.GetBytes(kv.Key), Encoding.UTF8.GetBytes(kv.Value)));
        return CacheHSetAsync(ns, key, pairs, database, cancellationToken);
    }

    /// <summary>One field's value, or null when the field or key is absent.</summary>
    public async Task<byte[]?> CacheHGetAsync(
        string ns, string key, byte[] field, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["field"] = Wire.ByteArray(Wire.RequireNotNull(field, nameof(field)));
        return Wire.CacheValue(await RequestAsync(Wire.Op("Cache", "HGet", body), database, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Delete fields. Returns how many were present.</summary>
    public async Task<long> CacheHDelAsync(
        string ns, string key, IEnumerable<byte[]> fields,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["fields"] = ByteArrays(fields, nameof(fields));
        var resp = await RequestAsync(Wire.Op("Cache", "HDel", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "deleted");
    }

    /// <summary>Every field/value pair, in ascending field order. Returned as a list of
    /// pairs rather than a dictionary because hash fields are arbitrary bytes and need
    /// not be valid UTF-8 — so they cannot all be dictionary keys.</summary>
    public async Task<IReadOnlyList<KeyValuePair<byte[], byte[]>>> CacheHGetAllAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "HGetAll", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        var pairs = new List<KeyValuePair<byte[], byte[]>>();
        foreach (var el in Wire.Items(Wire.Json(resp), "entries"))
        {
            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 2)
                throw new ProtocolException("expected each hash entry to be a [field, value] pair");
            pairs.Add(new(Wire.Bytes(el[0]), Wire.Bytes(el[1])));
        }
        return pairs;
    }

    public async Task<bool> CacheHExistsAsync(
        string ns, string key, byte[] field, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["field"] = Wire.ByteArray(Wire.RequireNotNull(field, nameof(field)));
        var resp = await RequestAsync(Wire.Op("Cache", "HExists", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Bool(Wire.Json(resp), "exists");
    }

    /// <summary>Number of fields (zero when the key is missing).</summary>
    public async Task<long> CacheHLenAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "HLen", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "length");
    }

    // -- streams ---------------------------------------------------------------------------
    //
    // Append-only logs. Each entry carries a strictly increasing `<millis>-<seq>` id;
    // that ordering is the point, so XAdd refuses a caller id that is not greater than
    // the last. Consumer groups and blocking reads are out of scope in V1 and are
    // refused by name.

    /// <summary>Append an entry, returning the assigned id.</summary>
    /// <param name="id">null or <c>"*"</c> to auto-generate; <c>"&lt;ms&gt;"</c> or
    /// <c>"&lt;ms&gt;-*"</c> to fix the millisecond; <c>"&lt;ms&gt;-&lt;seq&gt;"</c> for an
    /// exact id. A non-increasing id is an error.</param>
    public async Task<string> CacheXAddAsync(
        string ns, string key, IEnumerable<KeyValuePair<byte[], byte[]>> fields, string? id = null,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["id"] = id;
        body["fields"] = PairArray(fields, nameof(fields));
        var resp = await RequestAsync(Wire.Op("Cache", "XAdd", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Str(Wire.Json(resp), "id");
    }

    /// <summary>Append an entry whose fields are strings, encoded UTF-8.</summary>
    public Task<string> CacheXAddTextAsync(
        string ns, string key, IReadOnlyDictionary<string, string> fields, string? id = null,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var pairs = new List<KeyValuePair<byte[], byte[]>>();
        foreach (var kv in Wire.RequireNotNull(fields, nameof(fields)))
            pairs.Add(new(Encoding.UTF8.GetBytes(kv.Key), Encoding.UTF8.GetBytes(kv.Value)));
        return CacheXAddAsync(ns, key, pairs, id, database, cancellationToken);
    }

    /// <summary>Number of entries (zero when the key is missing).</summary>
    public async Task<long> CacheXLenAsync(
        string ns, string key, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.Op("Cache", "XLen", KeyBody(ns, key)), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "length");
    }

    /// <summary>Entries whose id falls in the inclusive range. <c>-</c> and <c>+</c> are
    /// the min/max ids; a bare <c>&lt;ms&gt;</c> spans that whole millisecond.</summary>
    public async Task<IReadOnlyList<StreamEntry>> CacheXRangeAsync(
        string ns, string key, string start = "-", string end = "+", int? count = null,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["start"] = Wire.Require(start, nameof(start));
        body["end"] = Wire.Require(end, nameof(end));
        body["count"] = count;
        var resp = await RequestAsync(Wire.Op("Cache", "XRange", body), database, cancellationToken).ConfigureAwait(false);
        return Entries(Wire.Json(resp));
    }

    /// <summary>Entries strictly newer than <paramref name="after"/> — the non-blocking
    /// poll primitive. Pass <c>"$"</c> for "only new entries". This read never blocks.</summary>
    public async Task<IReadOnlyList<StreamEntry>> CacheXReadAsync(
        string ns, string key, string after = "0-0", int? count = null,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var body = KeyBody(ns, key);
        body["after"] = Wire.Require(after, nameof(after));
        body["count"] = count;
        var resp = await RequestAsync(Wire.Op("Cache", "XRead", body), database, cancellationToken).ConfigureAwait(false);
        return Entries(Wire.Json(resp));
    }

    /// <summary>Delete entries by exact id. Returns how many were present.</summary>
    public async Task<long> CacheXDelAsync(
        string ns, string key, IEnumerable<string> ids,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var arr = new JsonArray();
        foreach (var id in Wire.RequireNotNull(ids, nameof(ids))) arr.Add(id);
        var body = KeyBody(ns, key);
        body["ids"] = arr;
        var resp = await RequestAsync(Wire.Op("Cache", "XDel", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "deleted");
    }

    /// <summary>Cap the stream by evicting the oldest entries. Returns how many were evicted.</summary>
    public async Task<long> CacheXTrimAsync(
        string ns, string key, int maxLen, string database = "main", CancellationToken cancellationToken = default)
    {
        if (maxLen < 0) throw new ArgumentOutOfRangeException(nameof(maxLen), "maxLen must not be negative");
        var body = KeyBody(ns, key);
        body["max_len"] = maxLen;
        var resp = await RequestAsync(Wire.Op("Cache", "XTrim", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "trimmed");
    }

    // -- shared helpers --------------------------------------------------------------------

    private static JsonObject KeyBody(string ns, string key) => new()
    {
        ["namespace"] = Wire.Require(ns, "namespace"),
        ["key"] = Wire.Require(key, "key"),
    };

    private static JsonArray ByteArrays(IEnumerable<byte[]> values, string name)
    {
        var arr = new JsonArray();
        foreach (var v in Wire.RequireNotNull(values, name))
            arr.Add(Wire.ByteArray(v ?? throw new ArgumentException($"{name} must not contain null", name)));
        if (arr.Count == 0) throw new ArgumentException($"{name} must not be empty", name);
        return arr;
    }

    private static JsonArray PairArray(IEnumerable<KeyValuePair<byte[], byte[]>> entries, string name)
    {
        var arr = new JsonArray();
        foreach (var kv in Wire.RequireNotNull(entries, name))
            arr.Add(new JsonArray(Wire.ByteArray(kv.Key), Wire.ByteArray(kv.Value)));
        if (arr.Count == 0) throw new ArgumentException($"{name} must not be empty", name);
        return arr;
    }

    private static IReadOnlyList<byte[]> ByteList(JsonElement json, string field)
    {
        var list = new List<byte[]>();
        foreach (var el in Wire.Items(json, field)) list.Add(Wire.Bytes(el));
        return list;
    }

    private static IReadOnlyList<StreamEntry> Entries(JsonElement json)
    {
        var list = new List<StreamEntry>();
        foreach (var el in Wire.Items(json, "entries"))
        {
            var fields = new List<KeyValuePair<byte[], byte[]>>();
            foreach (var pair in Wire.Items(el, "fields"))
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2)
                    throw new ProtocolException("expected each stream field to be a [field, value] pair");
                fields.Add(new(Wire.Bytes(pair[0]), Wire.Bytes(pair[1])));
            }
            list.Add(new StreamEntry(Wire.Str(el, "id"), fields));
        }
        return list;
    }
}
