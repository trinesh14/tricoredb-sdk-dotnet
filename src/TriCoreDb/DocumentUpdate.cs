using System.Text.Json.Nodes;

namespace TriCoreDb;

/// <summary>
/// Field mutations to apply to a document: <c>Set</c> overwrites the value at a
/// dot-notation path, <c>Inc</c> adds a number to it. Every set is applied before
/// every increment.
///
/// <code>
/// var update = DocumentUpdate.Builder().Set("city", "Pune").Inc("visits", 1).Build();
/// </code>
///
/// <c>Inc</c> never coerces: incrementing a field holding a string, bool, null, array
/// or object is a server-side error rather than a silent conversion, and a missing
/// field increments from zero. There is no <c>Dec</c> — a negative increment is exactly
/// that operation.
/// </summary>
public sealed class DocumentUpdate
{
    private readonly Dictionary<string, object?> _set;
    private readonly Dictionary<string, decimal> _inc;

    private DocumentUpdate(Dictionary<string, object?> set, Dictionary<string, decimal> inc)
    {
        _set = set;
        _inc = inc;
    }

    public static DocumentUpdateBuilder Builder() => new();

    /// <summary>Shorthand for a single set.</summary>
    public static DocumentUpdate Set(string path, object? value) => Builder().Set(path, value).Build();

    /// <summary>Shorthand for a single increment.</summary>
    public static DocumentUpdate Inc(string path, decimal delta) => Builder().Inc(path, delta).Build();

    internal JsonObject ToWire()
    {
        var obj = new JsonObject();
        if (_set.Count > 0)
        {
            var set = new JsonObject();
            foreach (var kv in _set) set[kv.Key] = Wire.ToNode(kv.Value);
            obj["set"] = set;
        }
        if (_inc.Count > 0)
        {
            var inc = new JsonObject();
            foreach (var kv in _inc) inc[kv.Key] = JsonValue.Create(kv.Value);
            obj["inc"] = inc;
        }
        return obj;
    }

    public sealed class DocumentUpdateBuilder
    {
        private readonly Dictionary<string, object?> _set = new();
        private readonly Dictionary<string, decimal> _inc = new();

        public DocumentUpdateBuilder Set(string path, object? value)
        {
            RequirePath(path);
            _set[path] = value;
            return this;
        }

        public DocumentUpdateBuilder Inc(string path, decimal delta)
        {
            RequirePath(path);
            _inc[path] = delta;
            return this;
        }

        public DocumentUpdate Build()
        {
            // An empty update would issue a write that changes nothing — almost
            // certainly a construction bug, and cheaper to catch here than to debug
            // as a no-op mutation in production.
            if (_set.Count == 0 && _inc.Count == 0)
                throw new ArgumentException(
                    "a DocumentUpdate needs at least one Set or Inc — an empty update would " +
                    "issue a write that changes nothing");
            return new DocumentUpdate(new(_set), new(_inc));
        }

        private static void RequirePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path must not be null or empty", nameof(path));
        }
    }
}
