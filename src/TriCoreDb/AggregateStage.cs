using System.Text.Json.Nodes;

namespace TriCoreDb;

/// <summary>
/// One stage of an aggregation pipeline.
///
/// Stages apply <b>strictly in the order given</b>; the order is semantics, not style.
/// <c>Match</c> before <c>Group</c> filters documents, after it filters groups;
/// <c>Limit</c> before <c>Sort</c> is a different query from <c>Limit</c> after.
///
/// Deliberately absent: <c>$lookup</c>, <c>$unwind</c>, <c>$facet</c>, <c>$out</c>,
/// <c>$addFields</c>, and computed expressions. The server does not implement them,
/// so offering them here could only produce a request it refuses.
/// </summary>
public sealed class AggregateStage
{
    private readonly JsonNode _wire;

    private AggregateStage(JsonNode wire) => _wire = wire;

    /// <summary>Filter with the same matcher <c>Find</c> uses.</summary>
    public static AggregateStage Match(DocumentFilter filter) =>
        new(new JsonObject { ["Match"] = Wire.RequireNotNull(filter, nameof(filter)).ToWire() });

    /// <summary>Group by the value at a dot-notation path. A document missing that path
    /// groups under <c>null</c> rather than being dropped.</summary>
    public static AggregateStage GroupByField(string field, params Accumulator[] accumulators) =>
        Group(new JsonObject { ["Field"] = Wire.Require(field, nameof(field)) }, accumulators);

    /// <summary>One group for the whole collection, keyed by a constant. This is how a
    /// collection-wide total is expressed; there is no implicit "no key".</summary>
    public static AggregateStage GroupAll(object? key, params Accumulator[] accumulators) =>
        Group(new JsonObject { ["Constant"] = Wire.ToNode(key) }, accumulators);

    private static AggregateStage Group(JsonObject by, Accumulator[] accumulators)
    {
        var accs = new JsonArray();
        foreach (var a in accumulators)
            accs.Add(Wire.RequireNotNull(a, "accumulator").ToWire());
        return new AggregateStage(new JsonObject
        {
            ["Group"] = new JsonObject { ["by"] = by, ["accumulators"] = accs },
        });
    }

    /// <summary>Sort by one or more keys. After a <c>Group</c> the addressable fields are
    /// <c>_id</c> and the accumulator outputs — not the original document's fields.</summary>
    public static AggregateStage Sort(params SortKey[] keys)
    {
        var arr = new JsonArray();
        foreach (var k in keys)
            arr.Add(new JsonObject
            {
                ["field"] = Wire.Require(k.Field, "field"),
                ["descending"] = k.Descending,
            });
        return new AggregateStage(new JsonObject { ["Sort"] = arr });
    }

    public static AggregateStage Skip(int n) =>
        n < 0
            ? throw new ArgumentOutOfRangeException(nameof(n), "skip must not be negative")
            : new AggregateStage(new JsonObject { ["Skip"] = n });

    public static AggregateStage Limit(int n) =>
        n < 0
            ? throw new ArgumentOutOfRangeException(nameof(n), "limit must not be negative")
            : new AggregateStage(new JsonObject { ["Limit"] = n });

    /// <summary>Keep (<paramref name="include"/> true) or drop the named top-level fields.
    /// Nested projection is not implemented and is refused by the server.</summary>
    public static AggregateStage Project(bool include, params string[] fields)
    {
        var arr = new JsonArray();
        foreach (var f in fields) arr.Add(Wire.Require(f, "field"));
        return new AggregateStage(new JsonObject
        {
            ["Project"] = new JsonObject { ["fields"] = arr, ["include"] = include },
        });
    }

    /// <summary>Replace the stream with a single document holding the input count.</summary>
    public static AggregateStage Count(string field) =>
        new(new JsonObject { ["Count"] = new JsonObject { ["field"] = Wire.Require(field, nameof(field)) } });

    internal JsonNode ToWire() => _wire.DeepClone();

    public override string ToString() => $"AggregateStage{_wire.ToJsonString()}";
}

/// <summary>One sort key. <c>Descending</c> defaults to ascending.</summary>
public readonly record struct SortKey(string Field, bool Descending = false);

/// <summary>
/// A <c>Group</c> accumulator: which reduction, over which field, into which output name.
///
/// <c>Count</c> takes no field because it counts documents, not values. The others read a
/// dot-notation path and ignore documents where it is missing or (for Sum/Avg) not a
/// number, so an absent field never contributes a zero.
/// </summary>
public sealed class Accumulator
{
    private readonly string _output;
    private readonly JsonNode _op;

    private Accumulator(string output, JsonNode op)
    {
        _output = Wire.Require(output, nameof(output));
        // `_id` holds the group key; writing an accumulator there would shadow it.
        if (output == "_id")
            throw new ArgumentException("`_id` holds the group key and cannot be an accumulator output", nameof(output));
        _op = op;
    }

    public static Accumulator Sum(string output, string field) => Field(output, "Sum", field);

    public static Accumulator Avg(string output, string field) => Field(output, "Avg", field);

    public static Accumulator Min(string output, string field) => Field(output, "Min", field);

    public static Accumulator Max(string output, string field) => Field(output, "Max", field);

    public static Accumulator Count(string output) => new(output, JsonValue.Create("Count")!);

    private static Accumulator Field(string output, string op, string field) =>
        new(output, new JsonObject { [op] = Wire.Require(field, nameof(field)) });

    internal JsonObject ToWire() => new() { ["output"] = _output, ["op"] = _op.DeepClone() };
}
