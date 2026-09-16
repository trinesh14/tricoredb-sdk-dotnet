using System.Text.Json;

namespace TriCoreDb;

// Typed results for the document, vector and graph families. Records rather than
// loose dictionaries: a misspelled key should not compile, and the wire's snake_case
// stops at this boundary so nothing downstream has to know it.

// -- document ------------------------------------------------------------------------------

/// <summary>How many documents a bulk update matched, and how many it actually changed.
/// They differ when an update sets a field to the value it already held.</summary>
public readonly record struct UpdateManyResult(long Matched, long Modified)
{
    internal static UpdateManyResult Parse(JsonElement e) =>
        new(Wire.Num(e, "matched"), Wire.Num(e, "modified"));
}

/// <summary>A secondary index on one top-level document field.</summary>
public sealed record DocumentIndex(string IndexName, string Field, bool Unique)
{
    internal static DocumentIndex Parse(JsonElement e) =>
        new(Wire.Str(e, "index_name"), Wire.Str(e, "field"), Wire.Bool(e, "unique"));
}

/// <summary>Approximate statistics collected by <c>Analyze</c>, used by the optimizer.</summary>
public sealed record DocumentStats(string Collection, long DocumentCount, long IndexedFields)
{
    internal static DocumentStats Parse(JsonElement e) =>
        new(Wire.Str(e, "analyzed"), Wire.Num(e, "document_count"), Wire.Num(e, "indexed_fields"));
}

// -- vector --------------------------------------------------------------------------------

/// <summary>Distance/similarity metric for a vector collection.</summary>
public enum VectorMetric
{
    /// <summary>Cosine similarity (higher = closer).</summary>
    Cosine,
    /// <summary>Dot product (higher = closer).</summary>
    Dot,
    /// <summary>Squared Euclidean distance. The server negates it so higher is always closer.</summary>
    L2,
}

/// <summary>How a collection's ANN index stores vectors in memory. Index-level only:
/// the durable records always keep full <c>f32</c> precision, so <c>Get</c> and
/// <c>ListVectors</c> return identical bytes either way.</summary>
public enum VectorQuantization
{
    /// <summary>Full <c>f32</c> vectors in the graph. The default.</summary>
    None,
    /// <summary>Per-vector int8 quantization, with an exact re-rank against the stored vectors.</summary>
    Int8,
}

internal static class EnumWire
{
    internal static string Wire(this VectorMetric m) => m switch
    {
        VectorMetric.Cosine => "cosine",
        VectorMetric.Dot => "dot",
        VectorMetric.L2 => "l2",
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    // Parsed strictly. An unknown spelling means the server and this driver disagree
    // about the protocol, and defaulting would hide that behind plausible-looking data.
    internal static VectorMetric Metric(string s) => s switch
    {
        "cosine" => VectorMetric.Cosine,
        "dot" => VectorMetric.Dot,
        "l2" => VectorMetric.L2,
        _ => throw new ProtocolException($"unknown vector metric `{s}`"),
    };

    internal static string Wire(this VectorQuantization q) => q switch
    {
        VectorQuantization.None => "none",
        VectorQuantization.Int8 => "int8",
        _ => throw new ArgumentOutOfRangeException(nameof(q)),
    };

    internal static VectorQuantization Quantization(string s) => s switch
    {
        "none" => VectorQuantization.None,
        "int8" => VectorQuantization.Int8,
        _ => throw new ProtocolException($"unknown vector quantization `{s}`"),
    };

    internal static string Wire(this GraphDirection d) => d switch
    {
        GraphDirection.Outgoing => "outgoing",
        GraphDirection.Incoming => "incoming",
        GraphDirection.Both => "both",
        _ => throw new ArgumentOutOfRangeException(nameof(d)),
    };

    internal static GraphDirection Direction(string s) => s switch
    {
        "outgoing" => GraphDirection.Outgoing,
        "incoming" => GraphDirection.Incoming,
        "both" => GraphDirection.Both,
        _ => throw new ProtocolException($"unknown graph direction `{s}`"),
    };
}

/// <summary>A stored vector with its metadata.</summary>
public sealed record VectorItem(string Id, float[] Vector, IReadOnlyDictionary<string, object?> Metadata)
{
    public int Dimension => Vector.Length;

    internal static VectorItem Parse(JsonElement e) =>
        new(Wire.Str(e, "id"), Wire.Floats(e, "vector"), Wire.Obj(e, "metadata"));

    public override string ToString() => $"VectorItem(id={Id}, dimension={Vector.Length})";
}

/// <summary>One search hit. <c>Score</c> is always "higher is closer", whatever the metric.</summary>
public sealed record VectorMatch(string Id, double Score, IReadOnlyDictionary<string, object?> Metadata)
{
    internal static VectorMatch Parse(JsonElement e) =>
        new(Wire.Str(e, "id"), Wire.Dbl(e, "score"), Wire.Obj(e, "metadata"));
}

/// <summary>A page of vectors. <c>Truncated</c> means the server capped the page —
/// ask again with a larger offset rather than assuming the collection ended.</summary>
public sealed record VectorPage(
    string Collection,
    IReadOnlyList<VectorItem> Vectors,
    bool Truncated,
    long Total);

/// <summary>Catalog metadata for one vector collection.</summary>
public sealed record VectorCollectionInfo(
    string Collection,
    int Dimension,
    VectorMetric Metric,
    long Count,
    VectorQuantization Quantization)
{
    internal static VectorCollectionInfo Parse(JsonElement e) =>
        new(Wire.Str(e, "collection"),
            (int)Wire.Num(e, "dimension"),
            EnumWire.Metric(Wire.Str(e, "metric")),
            Wire.Num(e, "count"),
            EnumWire.Quantization(Wire.OptStr(e, "quantization") ?? "none"));
}

// -- graph ---------------------------------------------------------------------------------

/// <summary>Direction to traverse edges from a node.</summary>
public enum GraphDirection
{
    /// <summary>Edges where the node is the <c>from</c> endpoint.</summary>
    Outgoing,
    /// <summary>Edges where the node is the <c>to</c> endpoint.</summary>
    Incoming,
    /// <summary>Both directions.</summary>
    Both,
}

public sealed record GraphNode(string Id, IReadOnlyList<string> Labels, IReadOnlyDictionary<string, object?> Properties)
{
    internal static GraphNode Parse(JsonElement e) =>
        new(Wire.Str(e, "id"), Wire.Strings(e, "labels"), Wire.Obj(e, "properties"));
}

public sealed record GraphEdge(
    string Id, string From, string To, string Label, IReadOnlyDictionary<string, object?> Properties)
{
    internal static GraphEdge Parse(JsonElement e) =>
        new(Wire.Str(e, "id"), Wire.Str(e, "from"), Wire.Str(e, "to"),
            Wire.Str(e, "label"), Wire.Obj(e, "properties"));
}

/// <summary>A node reachable in one hop, and the edge that reaches it.</summary>
public sealed record GraphNeighbor(string EdgeId, string NodeId, string Label, GraphDirection Direction)
{
    internal static GraphNeighbor Parse(JsonElement e) =>
        new(Wire.Str(e, "edge_id"), Wire.Str(e, "node_id"), Wire.Str(e, "label"),
            EnumWire.Direction(Wire.Str(e, "direction")));
}

/// <summary>
/// The result of a path search.
///
/// "No path within the depth bound" is <c>Found == false</c> on a successful call, not
/// an exception: a graph legitimately has unreachable pairs, and throwing would force
/// callers to use exceptions for control flow.
/// </summary>
public sealed record GraphPath(
    bool Found,
    string From,
    string To,
    GraphDirection Direction,
    long Hops,
    IReadOnlyList<string> NodePath,
    IReadOnlyList<string> EdgePath,
    double? TotalCost,
    string? Message)
{
    internal static GraphPath Parse(JsonElement e)
    {
        double? cost = null;
        if (e.TryGetProperty("total_cost", out var c) && c.ValueKind == JsonValueKind.Number)
            cost = c.GetDouble();
        var found = Wire.Bool(e, "found");
        return new GraphPath(
            found,
            Wire.Str(e, "from"),
            Wire.Str(e, "to"),
            EnumWire.Direction(Wire.Str(e, "direction")),
            Wire.NumOr(e, "hops", 0),
            found ? Wire.Strings(e, "node_path") : Array.Empty<string>(),
            found ? Wire.Strings(e, "edge_path") : Array.Empty<string>(),
            cost,
            Wire.OptStr(e, "message"));
    }
}

/// <summary>One node reached by a traversal, with the hop count that reached it.</summary>
public sealed record GraphTraversalNode(
    string Id, long Depth, IReadOnlyList<string> Labels, IReadOnlyDictionary<string, object?> Properties)
{
    internal static GraphTraversalNode Parse(JsonElement e) =>
        new(Wire.Str(e, "id"), Wire.Num(e, "depth"), Wire.Strings(e, "labels"), Wire.Obj(e, "properties"));
}

/// <summary>
/// A bounded BFS result.
/// </summary>
/// <param name="MaxDepth">the depth actually used — the server clamps the request,
/// so this can be lower than what was asked for.</param>
/// <param name="Limit">the limit actually used, likewise clamped.</param>
/// <param name="Truncated">the walk stopped at the limit or the visited-node cap,
/// so the node set is incomplete.</param>
public sealed record GraphTraversal(
    string Start,
    GraphDirection Direction,
    long MaxDepth,
    long Limit,
    bool Truncated,
    IReadOnlyList<GraphTraversalNode> Nodes);

/// <summary>One page of a node listing. <c>Total</c> counts the graph, not the page.</summary>
public sealed record GraphNodePage(string Graph, IReadOnlyList<GraphNode> Nodes, bool Truncated, long Total);

/// <summary>One page of an edge listing. <c>Total</c> counts the graph, not the page.</summary>
public sealed record GraphEdgePage(string Graph, IReadOnlyList<GraphEdge> Edges, bool Truncated, long Total);

/// <summary>
/// Rows from a read-only Cypher-subset query. Values are decoded JSON (string, long,
/// double, bool, list, dictionary or null) rather than stringified, so a query returning
/// a node's properties hands back a usable map instead of its <c>ToString</c>.
/// </summary>
public sealed record GraphQueryResult(
    string Graph, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated)
{
    public int Count => Rows.Count;
}

// -- cache ---------------------------------------------------------------------------------

/// <summary>One entry from a stream, with the id the server assigned it.</summary>
public sealed record StreamEntry(string Id, IReadOnlyList<KeyValuePair<byte[], byte[]>> Fields)
{
    /// <summary>The fields decoded as UTF-8 text. Convenient when the stream carries text,
    /// and wrong when it carries binary — <see cref="Fields"/> stays authoritative.</summary>
    public Dictionary<string, string> AsText()
    {
        var map = new Dictionary<string, string>();
        foreach (var kv in Fields)
            map[System.Text.Encoding.UTF8.GetString(kv.Key)] = System.Text.Encoding.UTF8.GetString(kv.Value);
        return map;
    }
}

/// <summary>A live key in a namespace, with its remaining TTL if it has one.</summary>
public sealed record CacheKeyInfo(string Key, long? TtlMs);
