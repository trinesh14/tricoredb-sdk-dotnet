using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriCoreDb;

// The graph family: labelled nodes, typed directed edges, traversals and a
// read-only Cypher subset.

public sealed partial class TriCoreClient
{
    public Task<Response> GraphCreateAsync(
        string graph, string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(
            Wire.Op("Graph", "CreateGraph", new JsonObject { ["graph"] = Wire.Require(graph, nameof(graph)) }),
            database, cancellationToken);

    /// <summary>Insert or replace a node. An existing id is overwritten wholesale.</summary>
    public Task<Response> GraphAddNodeAsync(
        string graph,
        string id,
        IEnumerable<string>? labels = null,
        IReadOnlyDictionary<string, object?>? properties = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var labelArr = new JsonArray();
        if (labels is not null) foreach (var l in labels) labelArr.Add(l);
        var body = new JsonObject
        {
            ["graph"] = Wire.Require(graph, nameof(graph)),
            ["id"] = Wire.Require(id, nameof(id)),
            ["labels"] = labelArr,
            ["properties"] = properties is null ? new JsonObject() : Wire.ObjectNode(properties),
        };
        return RequestAsync(Wire.Op("Graph", "AddNode", body), database, cancellationToken);
    }

    /// <summary>The node, or null when no node has that id.</summary>
    public async Task<GraphNode?> GraphGetNodeAsync(
        string graph, string id, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(
            Wire.Op("Graph", "GetNode", GraphId(graph, id)), database, cancellationToken).ConfigureAwait(false);
        var json = Wire.Json(resp);
        return json.ValueKind == JsonValueKind.Null ? null : GraphNode.Parse(json);
    }

    /// <summary>Insert or replace a directed edge. Both endpoints must already exist.</summary>
    public Task<Response> GraphAddEdgeAsync(
        string graph,
        string id,
        string from,
        string to,
        string label,
        IReadOnlyDictionary<string, object?>? properties = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["graph"] = Wire.Require(graph, nameof(graph)),
            ["id"] = Wire.Require(id, nameof(id)),
            ["from"] = Wire.Require(from, nameof(from)),
            ["to"] = Wire.Require(to, nameof(to)),
            ["label"] = Wire.Require(label, nameof(label)),
            ["properties"] = properties is null ? new JsonObject() : Wire.ObjectNode(properties),
        };
        return RequestAsync(Wire.Op("Graph", "AddEdge", body), database, cancellationToken);
    }

    /// <summary>The edge, or null when no edge has that id.</summary>
    public async Task<GraphEdge?> GraphGetEdgeAsync(
        string graph, string id, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(
            Wire.Op("Graph", "GetEdge", GraphId(graph, id)), database, cancellationToken).ConfigureAwait(false);
        var json = Wire.Json(resp);
        return json.ValueKind == JsonValueKind.Null ? null : GraphEdge.Parse(json);
    }

    /// <summary>Nodes one hop away, with the edge that reaches each.</summary>
    public async Task<IReadOnlyList<GraphNeighbor>> GraphNeighborsAsync(
        string graph,
        string nodeId,
        GraphDirection direction = GraphDirection.Both,
        string? label = null,
        int? limit = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["graph"] = Wire.Require(graph, nameof(graph)),
            ["node_id"] = Wire.Require(nodeId, nameof(nodeId)),
            ["direction"] = direction.Wire(),
            ["label"] = label,
            ["limit"] = limit,
        };
        var resp = await RequestAsync(Wire.Op("Graph", "Neighbors", body), database, cancellationToken).ConfigureAwait(false);
        var list = new List<GraphNeighbor>();
        foreach (var el in Wire.Items(Wire.Json(resp), "neighbors")) list.Add(GraphNeighbor.Parse(el));
        return list;
    }

    /// <summary>Delete a node. Its incident edges go with it.</summary>
    public Task<Response> GraphDeleteNodeAsync(
        string graph, string id, string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(Wire.Op("Graph", "DeleteNode", GraphId(graph, id)), database, cancellationToken);

    public Task<Response> GraphDeleteEdgeAsync(
        string graph, string id, string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(Wire.Op("Graph", "DeleteEdge", GraphId(graph, id)), database, cancellationToken);

    public async Task<IReadOnlyList<string>> GraphListAsync(
        string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.UnitOp("Graph", "ListGraphs"), database, cancellationToken).ConfigureAwait(false);
        return Wire.Strings(Wire.Json(resp), "graphs");
    }

    public Task<Response> GraphDropAsync(
        string graph, string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(
            Wire.Op("Graph", "DropGraph", new JsonObject { ["graph"] = Wire.Require(graph, nameof(graph)) }),
            database, cancellationToken);

    /// <summary>
    /// Bounded breadth-first walk from <paramref name="start"/>.
    /// </summary>
    /// <remarks>The server clamps <paramref name="maxDepth"/> and <paramref name="limit"/>;
    /// the returned <see cref="GraphTraversal"/> reports the values actually used.</remarks>
    public async Task<GraphTraversal> GraphTraverseAsync(
        string graph,
        string start,
        GraphDirection direction = GraphDirection.Both,
        string? label = null,
        int? maxDepth = null,
        int? limit = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["graph"] = Wire.Require(graph, nameof(graph)),
            ["start"] = Wire.Require(start, nameof(start)),
            ["direction"] = direction.Wire(),
            ["label"] = label,
            ["max_depth"] = maxDepth,
            ["limit"] = limit,
        };
        var resp = await RequestAsync(Wire.Op("Graph", "Traverse", body), database, cancellationToken).ConfigureAwait(false);
        var json = Wire.Json(resp);
        var nodes = new List<GraphTraversalNode>();
        foreach (var el in Wire.Items(json, "nodes")) nodes.Add(GraphTraversalNode.Parse(el));
        return new GraphTraversal(
            Wire.Str(json, "start"),
            EnumWire.Direction(Wire.Str(json, "direction")),
            Wire.Num(json, "max_depth"),
            Wire.Num(json, "limit"),
            Wire.Bool(json, "truncated"),
            nodes);
    }

    /// <summary>Fewest-hop path. "No path" comes back as <c>Found == false</c>, not an
    /// exception — a graph legitimately has unreachable pairs.</summary>
    public async Task<GraphPath> GraphShortestPathAsync(
        string graph,
        string from,
        string to,
        GraphDirection direction = GraphDirection.Both,
        string? label = null,
        int? maxDepth = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = FromTo(graph, from, to, direction, label);
        body["max_depth"] = maxDepth;
        var resp = await RequestAsync(Wire.Op("Graph", "ShortestPath", body), database, cancellationToken).ConfigureAwait(false);
        return GraphPath.Parse(Wire.Json(resp));
    }

    /// <summary>
    /// Least-cost path by summed edge weight. Distinct from
    /// <see cref="GraphShortestPathAsync"/>, which minimises hops: with unequal weights the
    /// two return different paths and neither substitutes for the other.
    /// </summary>
    /// <param name="weightProperty">edge property holding the cost, or null for the
    /// server's default (<c>"weight"</c>). An edge missing it, or holding a non-number,
    /// weighs 1.0. Negative weights are refused rather than silently mis-solved.</param>
    public async Task<GraphPath> GraphWeightedShortestPathAsync(
        string graph,
        string from,
        string to,
        GraphDirection direction = GraphDirection.Both,
        string? label = null,
        string? weightProperty = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = FromTo(graph, from, to, direction, label);
        body["weight_property"] = weightProperty;
        var resp = await RequestAsync(Wire.Op("Graph", "WeightedShortestPath", body), database, cancellationToken).ConfigureAwait(false);
        return GraphPath.Parse(Wire.Json(resp));
    }

    /// <summary>Edges incident to a node. <see cref="GraphDirection.Both"/> counts each edge once.</summary>
    public async Task<long> GraphDegreeAsync(
        string graph,
        string nodeId,
        GraphDirection direction = GraphDirection.Both,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["graph"] = Wire.Require(graph, nameof(graph)),
            ["node_id"] = Wire.Require(nodeId, nameof(nodeId)),
            ["direction"] = direction.Wire(),
        };
        var resp = await RequestAsync(Wire.Op("Graph", "Degree", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Num(Wire.Json(resp), "degree");
    }

    public async Task<GraphNodePage> GraphListNodesAsync(
        string graph, int? limit = null, int? offset = null,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(
            Wire.Op("Graph", "ListNodes", Page(graph, limit, offset)), database, cancellationToken).ConfigureAwait(false);
        var json = Wire.Json(resp);
        var nodes = new List<GraphNode>();
        foreach (var el in Wire.Items(json, "nodes")) nodes.Add(GraphNode.Parse(el));
        return new GraphNodePage(Wire.Str(json, "graph"), nodes, Wire.Bool(json, "truncated"), Wire.Num(json, "total"));
    }

    public async Task<GraphEdgePage> GraphListEdgesAsync(
        string graph, int? limit = null, int? offset = null,
        string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(
            Wire.Op("Graph", "ListEdges", Page(graph, limit, offset)), database, cancellationToken).ConfigureAwait(false);
        var json = Wire.Json(resp);
        var edges = new List<GraphEdge>();
        foreach (var el in Wire.Items(json, "edges")) edges.Add(GraphEdge.Parse(el));
        return new GraphEdgePage(Wire.Str(json, "graph"), edges, Wire.Bool(json, "truncated"), Wire.Num(json, "total"));
    }

    /// <summary>
    /// Run a read-only Cypher-subset query.
    ///
    /// Supported: MATCH / WHERE / RETURN with labels, property predicates, relationship
    /// direction and type, explicitly bounded variable-length paths, DISTINCT, ORDER BY /
    /// SKIP / LIMIT and global aggregates. Every other clause — all write clauses, OPTIONAL
    /// MATCH, WITH, UNWIND, CALL, path variables, shortestPath(), parameters — is refused
    /// by name rather than ignored, because a query that silently drops a clause returns a
    /// confidently wrong answer.
    /// </summary>
    public async Task<GraphQueryResult> GraphQueryAsync(
        string graph, string cypher, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["graph"] = Wire.Require(graph, nameof(graph)),
            ["cypher"] = Wire.Require(cypher, nameof(cypher)),
        };
        var resp = await RequestAsync(Wire.Op("Graph", "Query", body), database, cancellationToken).ConfigureAwait(false);
        var json = Wire.Json(resp);
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var row in Wire.Items(json, "rows"))
        {
            var cells = new List<object?>();
            foreach (var cell in row.EnumerateArray()) cells.Add(Wire.ToClr(cell));
            rows.Add(cells);
        }
        return new GraphQueryResult(
            Wire.Str(json, "graph"), Wire.Strings(json, "columns"), rows, Wire.BoolOr(json, "truncated", false));
    }

    private static JsonObject GraphId(string graph, string id) => new()
    {
        ["graph"] = Wire.Require(graph, nameof(graph)),
        ["id"] = Wire.Require(id, nameof(id)),
    };

    private static JsonObject Page(string graph, int? limit, int? offset) => new()
    {
        ["graph"] = Wire.Require(graph, nameof(graph)),
        ["limit"] = limit,
        ["offset"] = offset,
    };

    private static JsonObject FromTo(string graph, string from, string to, GraphDirection direction, string? label) => new()
    {
        ["graph"] = Wire.Require(graph, nameof(graph)),
        ["from"] = Wire.Require(from, nameof(from)),
        ["to"] = Wire.Require(to, nameof(to)),
        ["direction"] = direction.Wire(),
        ["label"] = label,
    };
}
