using System.Text.Json.Nodes;

namespace TriCoreDb;

// The document (NoSQL) family. Every method funnels through RequestAsync, so error
// mapping, cancellation and framing are identical to every other family.

public sealed partial class TriCoreClient
{
    /// <summary>Create a collection. Idempotent on the server.</summary>
    public Task<Response> DocumentCreateCollectionAsync(
        string collection, string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(
            Wire.Op("Document", "CreateCollection", new JsonObject { ["collection"] = Wire.Require(collection, nameof(collection)) }),
            database, cancellationToken);

    /// <summary>
    /// Insert a document and return the id actually stored.
    /// </summary>
    /// <param name="id">null to let the server assign a unique id.</param>
    public async Task<string> DocumentInsertAsync(
        string collection,
        IReadOnlyDictionary<string, object?> document,
        string? id = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["id"] = id,
            ["document"] = Wire.ObjectNode(Wire.RequireNotNull(document, nameof(document))),
        };
        var resp = await RequestAsync(Wire.Op("Document", "Insert", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Str(Wire.Json(resp), "id");
    }

    /// <summary>The document, or null when no document has that id.</summary>
    public async Task<IReadOnlyDictionary<string, object?>?> DocumentGetAsync(
        string collection, string id, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(
            Wire.Op("Document", "Get", CollectionId(collection, id)), database, cancellationToken).ConfigureAwait(false);
        var docs = Wire.Documents(resp);
        return docs.Count == 0 ? null : docs[0];
    }

    /// <summary>Find documents matching <paramref name="filter"/>.</summary>
    /// <param name="limit">null for no client-imposed limit (the server still caps).</param>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> DocumentFindAsync(
        string collection,
        DocumentFilter? filter = null,
        int? limit = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["filter"] = (filter ?? DocumentFilter.All()).ToWire(),
            ["limit"] = limit,
        };
        var resp = await RequestAsync(Wire.Op("Document", "Find", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Documents(resp);
    }

    /// <summary>Set fields on an existing document. Dot paths create or overwrite nested
    /// keys. <b>Not</b> an upsert: a missing id is an error.</summary>
    public Task<Response> DocumentUpdateAsync(
        string collection,
        string id,
        IReadOnlyDictionary<string, object?> set,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = CollectionId(collection, id);
        body["set"] = Wire.ObjectNode(Wire.RequireNotNull(set, nameof(set)));
        return RequestAsync(Wire.Op("Document", "Update", body), database, cancellationToken);
    }

    /// <summary>Apply an update to one document by id.</summary>
    /// <param name="upsert">create the document from the update when the id is missing;
    /// off by default, which matches <see cref="DocumentUpdateAsync"/>.</param>
    /// <returns>whether the document was created rather than modified.</returns>
    public async Task<bool> DocumentUpdateOneAsync(
        string collection,
        string id,
        DocumentUpdate update,
        bool upsert = false,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = CollectionId(collection, id);
        body["update"] = Wire.RequireNotNull(update, nameof(update)).ToWire();
        body["upsert"] = upsert;
        var resp = await RequestAsync(Wire.Op("Document", "UpdateOne", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Bool(Wire.Json(resp), "inserted");
    }

    /// <summary>Apply an update to every document matching <paramref name="filter"/>.
    /// Never an upsert: a filter matching nothing modifies nothing, and is not an error.</summary>
    public async Task<UpdateManyResult> DocumentUpdateManyAsync(
        string collection,
        DocumentFilter filter,
        DocumentUpdate update,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["filter"] = Wire.RequireNotNull(filter, nameof(filter)).ToWire(),
            ["update"] = Wire.RequireNotNull(update, nameof(update)).ToWire(),
        };
        var resp = await RequestAsync(Wire.Op("Document", "UpdateMany", body), database, cancellationToken).ConfigureAwait(false);
        return UpdateManyResult.Parse(Wire.Json(resp));
    }

    public Task<Response> DocumentDeleteAsync(
        string collection, string id, string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(Wire.Op("Document", "Delete", CollectionId(collection, id)), database, cancellationToken);

    public async Task<IReadOnlyList<string>> DocumentListCollectionsAsync(
        string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.UnitOp("Document", "ListCollections"), database, cancellationToken).ConfigureAwait(false);
        return Wire.Strings(Wire.Json(resp), "collections");
    }

    public Task<Response> DocumentDropCollectionAsync(
        string collection, string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(
            Wire.Op("Document", "DropCollection", new JsonObject { ["collection"] = Wire.Require(collection, nameof(collection)) }),
            database, cancellationToken);

    /// <summary>Create a top-level-field index on a collection.</summary>
    public Task<Response> DocumentCreateIndexAsync(
        string collection,
        string indexName,
        string field,
        bool unique = false,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["index_name"] = Wire.Require(indexName, nameof(indexName)),
            ["field"] = Wire.Require(field, nameof(field)),
            ["unique"] = unique,
        };
        return RequestAsync(Wire.Op("Document", "CreateIndex", body), database, cancellationToken);
    }

    public Task<Response> DocumentDropIndexAsync(
        string collection, string indexName, string database = "main", CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["index_name"] = Wire.Require(indexName, nameof(indexName)),
        };
        return RequestAsync(Wire.Op("Document", "DropIndex", body), database, cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentIndex>> DocumentListIndexesAsync(
        string collection, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(
            Wire.Op("Document", "ListIndexes", new JsonObject { ["collection"] = Wire.Require(collection, nameof(collection)) }),
            database, cancellationToken).ConfigureAwait(false);
        var list = new List<DocumentIndex>();
        foreach (var el in Wire.Items(Wire.Json(resp), "indexes")) list.Add(DocumentIndex.Parse(el));
        return list;
    }

    /// <summary>Run an aggregation pipeline. Stages apply strictly in the order given.</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> DocumentAggregateAsync(
        string collection,
        IEnumerable<AggregateStage> pipeline,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var stages = new JsonArray();
        foreach (var s in Wire.RequireNotNull(pipeline, nameof(pipeline)))
            stages.Add(Wire.RequireNotNull(s, "pipeline stage").ToWire());
        var body = new JsonObject
        {
            ["collection"] = Wire.Require(collection, nameof(collection)),
            ["pipeline"] = stages,
        };
        var resp = await RequestAsync(Wire.Op("Document", "Aggregate", body), database, cancellationToken).ConfigureAwait(false);
        return Wire.Documents(resp);
    }

    /// <summary>Collect approximate statistics for a collection (optimizer input).</summary>
    public async Task<DocumentStats> DocumentAnalyzeAsync(
        string collection, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(
            Wire.Op("Document", "Analyze", new JsonObject { ["collection"] = Wire.Require(collection, nameof(collection)) }),
            database, cancellationToken).ConfigureAwait(false);
        return DocumentStats.Parse(Wire.Json(resp));
    }

    private static JsonObject CollectionId(string collection, string id) => new()
    {
        ["collection"] = Wire.Require(collection, nameof(collection)),
        ["id"] = Wire.Require(id, nameof(id)),
    };
}
