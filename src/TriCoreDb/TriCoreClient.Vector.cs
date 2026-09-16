using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriCoreDb;

// The vector family: collections of fixed-dimension embeddings with metadata,
// searched by similarity.

public sealed partial class TriCoreClient
{
    /// <summary>Create a vector collection.</summary>
    /// <param name="dimension">every vector in the collection must have exactly this length.</param>
    /// <param name="quantization">index-level compression. The durable vectors keep full
    /// <c>f32</c> precision either way, so this trades recall for memory, never data.</param>
    public Task<Response> VectorCreateCollectionAsync(
        string collection,
        int dimension,
        VectorMetric metric = VectorMetric.Cosine,
        VectorQuantization quantization = VectorQuantization.None,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        if (dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimension), "dimension must be positive");
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["dimension"] = dimension,
            ["metric"] = metric.Wire(),
            ["quantization"] = quantization.Wire(),
        };
        return RequestAsync(Wire.Op("Vector", "CreateCollection", body), database, cancellationToken);
    }

    /// <summary>Insert or replace one vector. An existing id is overwritten wholesale —
    /// metadata is replaced, not merged.</summary>
    public Task<Response> VectorUpsertAsync(
        string collection,
        string id,
        IReadOnlyList<float> vector,
        IReadOnlyDictionary<string, object?>? metadata = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        Wire.RequireNotNull(vector, nameof(vector));
        var arr = new JsonArray();
        foreach (var f in vector) arr.Add(f);
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["id"] = Wire.Require(id, nameof(id)),
            ["vector"] = arr,
            ["metadata"] = metadata is null ? new JsonObject() : Wire.ObjectNode(metadata),
        };
        return RequestAsync(Wire.Op("Vector", "Upsert", body), database, cancellationToken);
    }

    /// <summary>The stored vector, or null when no vector has that id.</summary>
    public async Task<VectorItem?> VectorGetAsync(
        string collection, string id, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["id"] = Wire.Require(id, nameof(id)),
        };
        var resp = await RequestAsync(Wire.Op("Vector", "Get", body), database, cancellationToken).ConfigureAwait(false);
        // A miss is a `Json(null)` payload, not an error: absence is a normal answer.
        var json = Wire.Json(resp);
        return json.ValueKind == JsonValueKind.Null ? null : VectorItem.Parse(json);
    }

    public Task<Response> VectorDeleteAsync(
        string collection, string id, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["id"] = Wire.Require(id, nameof(id)),
        };
        return RequestAsync(Wire.Op("Vector", "Delete", body), database, cancellationToken);
    }

    /// <summary>
    /// Nearest neighbours of <paramref name="vector"/>, best first.
    /// </summary>
    /// <param name="filter">exact-equality metadata predicates (top-level fields only —
    /// the server supports no ranges and no nesting here). Null for no filtering.</param>
    public async Task<IReadOnlyList<VectorMatch>> VectorSearchAsync(
        string collection,
        IReadOnlyList<float> vector,
        int topK,
        IReadOnlyDictionary<string, object?>? filter = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        Wire.RequireNotNull(vector, nameof(vector));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK), "topK must be positive");
        var arr = new JsonArray();
        foreach (var f in vector) arr.Add(f);
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["vector"] = arr,
            ["top_k"] = topK,
            ["filter"] = filter is null ? null : Wire.ObjectNode(filter),
        };
        var resp = await RequestAsync(Wire.Op("Vector", "Search", body), database, cancellationToken).ConfigureAwait(false);
        var list = new List<VectorMatch>();
        foreach (var el in Wire.Items(Wire.Json(resp), "results")) list.Add(VectorMatch.Parse(el));
        return list;
    }

    public async Task<IReadOnlyList<string>> VectorListCollectionsAsync(
        string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.UnitOp("Vector", "ListCollections"), database, cancellationToken).ConfigureAwait(false);
        return Wire.Strings(Wire.Json(resp), "collections");
    }

    /// <summary>Catalog metadata: dimension, metric, quantization and stored count.</summary>
    public async Task<VectorCollectionInfo> VectorDescribeCollectionAsync(
        string collection, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(
            Wire.Op("Vector", "DescribeCollection", new JsonObject { ["collection"] = Wire.Require(collection, nameof(collection)) }),
            database, cancellationToken).ConfigureAwait(false);
        return VectorCollectionInfo.Parse(Wire.Json(resp));
    }

    /// <summary>One page of a collection's vectors. The server clamps
    /// <paramref name="limit"/>; check <c>Truncated</c> rather than assuming the page ended
    /// the collection.</summary>
    public async Task<VectorPage> VectorListVectorsAsync(
        string collection,
        int? limit = null,
        int? offset = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["limit"] = limit,
            ["offset"] = offset,
        };
        var resp = await RequestAsync(Wire.Op("Vector", "ListVectors", body), database, cancellationToken).ConfigureAwait(false);
        var json = Wire.Json(resp);
        var items = new List<VectorItem>();
        foreach (var el in Wire.Items(json, "vectors")) items.Add(VectorItem.Parse(el));
        return new VectorPage(
            Wire.Str(json, "collection"), items, Wire.BoolOr(json, "truncated", false), Wire.NumOr(json, "total", items.Count));
    }

    public Task<Response> VectorDropCollectionAsync(
        string collection, string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(
            Wire.Op("Vector", "DropCollection", new JsonObject { ["collection"] = Wire.Require(collection, nameof(collection)) }),
            database, cancellationToken);
}
