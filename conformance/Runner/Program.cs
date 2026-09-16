using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TriCoreDb;

// .NET conformance runner.
//
//   dotnet Runner.dll <host> <port> <user> <secret>   < scenario.json
//
// Reads the shared scenario on stdin, executes each step through the TriCoreDb
// package's PUBLIC TYPED API (src/TriCoreDb in this repository), and writes one
// JSON observation per line.
//
// This file makes no assertions and knows no expected values — it is never told
// any. Every judgement lives in tricoredb-sdk-spec/conformance/scenario.js so that
// all SDKs are held to one standard. The only job here is:
//
//   canonical action + args  ->  the SDK method a user would call
//   the SDK's typed result   ->  canonical JSON
//
// The normalisation is mechanical field-copying. It must never compute, default,
// or invent a value: if the driver returns nothing, the canonical answer is
// nothing, and the shared assertion fails. That is the mechanism by which a
// stubbed method is caught.

namespace TriCoreDb.Conformance;

internal static class Program
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private static async Task<int> Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "127.0.0.1";
        var port = args.Length > 1 ? int.Parse(args[1]) : TriCoreClient.DefaultPort;
        var user = args.Length > 2 ? args[2] : "conformance";
        var secret = args.Length > 3 ? args[3] : "pw";

        var scenarioText = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        var scenario = JsonNode.Parse(scenarioText)!.AsObject();
        var steps = scenario["steps"]!.AsArray();

        await using var db = await TriCoreClient.ConnectAsync(host, port, user, secret).ConfigureAwait(false);

        // Values from earlier steps, for the few actions whose argument IS an earlier
        // answer (a stream cursor, an id to delete). Resolving it here is mechanical
        // lookup, not judgement: the scenario names the step it wants.
        var results = new Dictionary<string, JsonNode?>();

        foreach (var stepNode in steps)
        {
            var step = stepNode!.AsObject();
            var id = step["id"]!.GetValue<string>();
            var action = step["action"]!.GetValue<string>();
            var a = step["args"]?.AsObject() ?? new JsonObject();

            try
            {
                var value = await Dispatch(db, action, a, results).ConfigureAwait(false);
                if (value is null)
                {
                    // No .NET method exists for this action. This is the shape a
                    // genuinely missing operation takes.
                    Emit(new JsonObject
                    {
                        ["id"] = id,
                        ["status"] = "unsupported",
                        ["error"] = $"no .NET SDK method for action {action}",
                    });
                }
                else
                {
                    results[id] = value.DeepClone();
                    Emit(new JsonObject { ["id"] = id, ["status"] = "ok", ["value"] = value });
                }
            }
            catch (Exception e)
            {
                Emit(new JsonObject
                {
                    ["id"] = id,
                    ["status"] = "error",
                    ["error"] = $"{e.GetType().Name}: {e.Message}",
                });
            }
        }

        return 0;
    }

    private static void Emit(JsonObject o) => Console.Out.WriteLine(o.ToJsonString(Compact));

    // -- canonical spec -> SDK builders ----------------------------------------------------
    //
    // The scenario ships filters and pipelines as neutral descriptions. Translating them
    // through the SDK's own builders (rather than hand-writing wire JSON) is deliberate:
    // an SDK that cannot express `And` or `Group` fails the step instead of quietly
    // sending JSON its users could not have produced.

    private static DocumentFilter Filter(JsonNode? spec)
    {
        var o = spec!.AsObject();
        var op = o["op"]!.GetValue<string>();
        return op switch
        {
            "all" => DocumentFilter.All(),
            "eq" => DocumentFilter.Eq(Field(o), Val(o["value"])),
            "gt" => DocumentFilter.Gt(Field(o), Val(o["value"])),
            "contains" => DocumentFilter.Contains(Field(o), Val(o["value"])),
            "and" => DocumentFilter.And(o["filters"]!.AsArray().Select(Filter).ToArray()),
            _ => throw new NotSupportedException($"unsupported filter op in the scenario: {op}"),
        };

        static string Field(JsonObject o) => o["field"]!.GetValue<string>();
    }

    private static Accumulator Acc(JsonNode? spec)
    {
        var o = spec!.AsObject();
        var op = o["op"]!.GetValue<string>();
        var output = o["output"]!.GetValue<string>();
        return op switch
        {
            "sum" => Accumulator.Sum(output, o["field"]!.GetValue<string>()),
            "count" => Accumulator.Count(output),
            _ => throw new NotSupportedException($"unsupported accumulator: {op}"),
        };
    }

    private static AggregateStage Stage(JsonNode? spec)
    {
        var o = spec!.AsObject();
        var kind = o["stage"]!.GetValue<string>();
        switch (kind)
        {
            case "match":
                return AggregateStage.Match(Filter(o["filter"]));
            case "group":
                return AggregateStage.GroupByField(
                    o["by"]!["field"]!.GetValue<string>(),
                    o["accumulators"]!.AsArray().Select(Acc).ToArray());
            case "sort":
                return AggregateStage.Sort(o["keys"]!.AsArray()
                    .Select(k => new SortKey(
                        k!["field"]!.GetValue<string>(),
                        k["descending"]?.GetValue<bool>() ?? false))
                    .ToArray());
            case "count":
                return AggregateStage.Count(o["field"]!.GetValue<string>());
            default:
                throw new NotSupportedException($"unsupported aggregate stage: {kind}");
        }
    }

    // -- action table ----------------------------------------------------------------------

    private static async Task<JsonNode?> Dispatch(TriCoreClient db, string action, JsonObject a,
        Dictionary<string, JsonNode?> results) => action switch
    {
        // ---- document ----
        "doc.createCollection" => await Unit(db.DocumentCreateCollectionAsync(Str(a, "collection"))),
        "doc.dropCollection" => await Unit(db.DocumentDropCollectionAsync(Str(a, "collection"))),
        "doc.listCollections" => new JsonObject { ["names"] = Arr(await db.DocumentListCollectionsAsync()) },
        "doc.insert" => new JsonObject
        {
            ["id"] = await db.DocumentInsertAsync(Str(a, "collection"), Doc(a["document"]), OptStr(a, "id")),
        },
        "doc.get" => Found(await db.DocumentGetAsync(Str(a, "collection"), Str(a, "id")), d =>
            new JsonObject { ["found"] = true, ["doc"] = Node(d) }),
        "doc.find" => new JsonObject
        {
            ["docs"] = Docs(await db.DocumentFindAsync(Str(a, "collection"), Filter(a["filter"]), OptInt(a, "limit"))),
        },
        "doc.update" => await Unit(db.DocumentUpdateAsync(Str(a, "collection"), Str(a, "id"), Doc(a["set"]))),
        "doc.updateOne" => await UpdateOne(db, a),
        "doc.updateMany" => await UpdateMany(db, a),
        "doc.delete" => await Unit(db.DocumentDeleteAsync(Str(a, "collection"), Str(a, "id"))),
        "doc.createIndex" => await Unit(db.DocumentCreateIndexAsync(
            Str(a, "collection"), Str(a, "indexName"), Str(a, "field"), a["unique"]?.GetValue<bool>() ?? false)),
        "doc.dropIndex" => await Unit(db.DocumentDropIndexAsync(Str(a, "collection"), Str(a, "indexName"))),
        "doc.listIndexes" => new JsonObject
        {
            ["indexes"] = new JsonArray((await db.DocumentListIndexesAsync(Str(a, "collection")))
                .Select(i => (JsonNode)new JsonObject { ["name"] = i.IndexName, ["field"] = i.Field }).ToArray()),
        },
        "doc.analyze" => new JsonObject
        {
            ["document_count"] = (await db.DocumentAnalyzeAsync(Str(a, "collection"))).DocumentCount,
        },
        "doc.aggregate" => new JsonObject
        {
            ["docs"] = Docs(await db.DocumentAggregateAsync(
                Str(a, "collection"), a["pipeline"]!.AsArray().Select(Stage).ToList())),
        },

        // ---- vector ----
        "vec.createCollection" => await Unit(db.VectorCreateCollectionAsync(
            Str(a, "collection"), a["dimension"]!.GetValue<int>(), Metric(OptStr(a, "metric")))),
        "vec.dropCollection" => await Unit(db.VectorDropCollectionAsync(Str(a, "collection"))),
        "vec.listCollections" => new JsonObject { ["names"] = Arr(await db.VectorListCollectionsAsync()) },
        "vec.upsert" => await Unit(db.VectorUpsertAsync(
            Str(a, "collection"), Str(a, "id"), Floats(a["vector"]), Doc(a["metadata"]))),
        "vec.get" => Found(await db.VectorGetAsync(Str(a, "collection"), Str(a, "id")), v => new JsonObject
        {
            ["found"] = true,
            ["id"] = v.Id,
            ["vector"] = new JsonArray(v.Vector.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
            ["metadata"] = Node(v.Metadata),
        }),
        "vec.delete" => await Unit(db.VectorDeleteAsync(Str(a, "collection"), Str(a, "id"))),
        "vec.search" => await Search(db, a),
        "vec.describeCollection" => await Describe(db, a),
        "vec.listVectors" => await ListVectors(db, a),

        // ---- graph ----
        "graph.create" => await Unit(db.GraphCreateAsync(Str(a, "graph"))),
        "graph.drop" => await Unit(db.GraphDropAsync(Str(a, "graph"))),
        "graph.listGraphs" => new JsonObject { ["names"] = Arr(await db.GraphListAsync()) },
        "graph.addNode" => await Unit(db.GraphAddNodeAsync(
            Str(a, "graph"), Str(a, "id"), Strings(a["labels"]), Doc(a["properties"]))),
        "graph.getNode" => Found(await db.GraphGetNodeAsync(Str(a, "graph"), Str(a, "id")), n => new JsonObject
        {
            ["found"] = true,
            ["id"] = n.Id,
            ["labels"] = Arr(n.Labels),
            ["properties"] = Node(n.Properties),
        }),
        "graph.deleteNode" => await Unit(db.GraphDeleteNodeAsync(Str(a, "graph"), Str(a, "id"))),
        "graph.addEdge" => await Unit(db.GraphAddEdgeAsync(
            Str(a, "graph"), Str(a, "id"), Str(a, "from"), Str(a, "to"), Str(a, "label"), Doc(a["properties"]))),
        "graph.getEdge" => Found(await db.GraphGetEdgeAsync(Str(a, "graph"), Str(a, "id")), e => new JsonObject
        {
            ["found"] = true,
            ["id"] = e.Id,
            ["from"] = e.From,
            ["to"] = e.To,
            ["label"] = e.Label,
            ["properties"] = Node(e.Properties),
        }),
        "graph.deleteEdge" => await Unit(db.GraphDeleteEdgeAsync(Str(a, "graph"), Str(a, "id"))),
        "graph.neighbors" => await Neighbors(db, a),
        "graph.degree" => new JsonObject
        {
            ["degree"] = await db.GraphDegreeAsync(Str(a, "graph"), Str(a, "nodeId"), Dir(OptStr(a, "direction"))),
        },
        "graph.traverse" => await Traverse(db, a),
        "graph.shortestPath" => await ShortestPath(db, a),
        "graph.weightedShortestPath" => await WeightedShortestPath(db, a),
        "graph.listNodes" => await ListNodes(db, a),
        "graph.listEdges" => await ListEdges(db, a),
        "graph.query" => await Query(db, a),

        // ---- sql ----
        //
        // The Query/Exec split is a security boundary, not a convenience: Query is a
        // read and may only run SELECT. Both get their own action so the scenario can
        // prove the refusal as well as the success.
        "sql.execute" => await SqlExecute(db, a),
        "sql.query" => await SqlQuery(db, a),

        // ---- cache ----
        //
        // Values cross this boundary as text, but every call below goes through the
        // driver's BYTE-oriented API. An SDK that could only speak strings would be
        // unable to store what the server actually stores.
        "cache.ping" => await Unit(db.CachePingAsync()),
        "cache.set" => await Unit(db.CacheSetAsync(Str(a, "namespace"), Str(a, "key"),
            Bytes(Str(a, "value")), OptLong(a, "ttlMs"))),
        "cache.get" => Found(await db.CacheGetAsync(Str(a, "namespace"), Str(a, "key"))),
        "cache.delete" => new JsonObject
        {
            ["deleted"] = await db.CacheDeleteAsync(Str(a, "namespace"), Str(a, "key")),
        },
        "cache.exists" => new JsonObject
        {
            ["exists"] = await db.CacheExistsAsync(Str(a, "namespace"), Str(a, "key")),
        },
        "cache.ttl" => Ttl(await db.CacheTtlAsync(Str(a, "namespace"), Str(a, "key"))),
        "cache.clearNamespace" => new JsonObject
        {
            ["cleared"] = await db.CacheClearNamespaceAsync(Str(a, "namespace")),
        },
        "cache.incr" => new JsonObject
        {
            ["value"] = await db.CacheIncrAsync(Str(a, "namespace"), Str(a, "key"), Int(a, "by")),
        },
        "cache.expire" => new JsonObject
        {
            ["updated"] = await db.CacheExpireAsync(Str(a, "namespace"), Str(a, "key"), Int(a, "ttlMs")),
        },
        "cache.persist" => new JsonObject
        {
            ["persisted"] = await db.CachePersistAsync(Str(a, "namespace"), Str(a, "key")),
        },
        "cache.setNx" => new JsonObject
        {
            ["set"] = await db.CacheSetNxAsync(Str(a, "namespace"), Str(a, "key"),
                Bytes(Str(a, "value")), OptLong(a, "ttlMs")),
        },
        "cache.keys" => await CacheKeys(db, a),

        "cache.lPush" => new JsonObject
        {
            ["length"] = await db.CacheLPushAsync(Str(a, "namespace"), Str(a, "key"), ByteList(a, "values")),
        },
        "cache.rPush" => new JsonObject
        {
            ["length"] = await db.CacheRPushAsync(Str(a, "namespace"), Str(a, "key"), ByteList(a, "values")),
        },
        "cache.lPop" => Found(await db.CacheLPopAsync(Str(a, "namespace"), Str(a, "key"))),
        "cache.rPop" => Found(await db.CacheRPopAsync(Str(a, "namespace"), Str(a, "key"))),
        "cache.lRange" => new JsonObject
        {
            ["values"] = Texts(await db.CacheLRangeAsync(Str(a, "namespace"), Str(a, "key"),
                Int(a, "start"), Int(a, "stop"))),
        },
        "cache.lLen" => new JsonObject
        {
            ["length"] = await db.CacheLLenAsync(Str(a, "namespace"), Str(a, "key")),
        },
        "cache.lIndex" => Found(await db.CacheLIndexAsync(Str(a, "namespace"), Str(a, "key"), Int(a, "index"))),

        "cache.sAdd" => new JsonObject
        {
            ["added"] = await db.CacheSAddAsync(Str(a, "namespace"), Str(a, "key"), ByteList(a, "members")),
        },
        "cache.sRem" => new JsonObject
        {
            ["removed"] = await db.CacheSRemAsync(Str(a, "namespace"), Str(a, "key"), ByteList(a, "members")),
        },
        "cache.sIsMember" => new JsonObject
        {
            ["isMember"] = await db.CacheSIsMemberAsync(Str(a, "namespace"), Str(a, "key"),
                Bytes(Str(a, "member"))),
        },
        "cache.sCard" => new JsonObject
        {
            ["cardinality"] = await db.CacheSCardAsync(Str(a, "namespace"), Str(a, "key")),
        },
        "cache.sMembers" => new JsonObject
        {
            ["members"] = Texts(await db.CacheSMembersAsync(Str(a, "namespace"), Str(a, "key"))),
        },

        "cache.hSet" => new JsonObject
        {
            ["created"] = await db.CacheHSetAsync(Str(a, "namespace"), Str(a, "key"), PairList(a, "entries")),
        },
        "cache.hGet" => Found(await db.CacheHGetAsync(Str(a, "namespace"), Str(a, "key"),
            Bytes(Str(a, "field")))),
        "cache.hDel" => new JsonObject
        {
            ["deleted"] = await db.CacheHDelAsync(Str(a, "namespace"), Str(a, "key"), ByteList(a, "fields")),
        },
        "cache.hGetAll" => await HGetAll(db, a),
        "cache.hExists" => new JsonObject
        {
            ["exists"] = await db.CacheHExistsAsync(Str(a, "namespace"), Str(a, "key"), Bytes(Str(a, "field"))),
        },
        "cache.hLen" => new JsonObject
        {
            ["length"] = await db.CacheHLenAsync(Str(a, "namespace"), Str(a, "key")),
        },

        "cache.xAdd" => new JsonObject
        {
            ["id"] = await db.CacheXAddAsync(Str(a, "namespace"), Str(a, "key"), PairList(a, "fields")),
        },
        "cache.xLen" => new JsonObject
        {
            ["length"] = await db.CacheXLenAsync(Str(a, "namespace"), Str(a, "key")),
        },
        "cache.xRange" => Entries(await db.CacheXRangeAsync(Str(a, "namespace"), Str(a, "key"),
            Str(a, "start"), Str(a, "end"))),
        "cache.xRead" => Entries(await db.CacheXReadAsync(Str(a, "namespace"), Str(a, "key"),
            IdFromStep(results, Str(a, "afterStep")))),
        "cache.xDel" => new JsonObject
        {
            ["deleted"] = await db.CacheXDelAsync(Str(a, "namespace"), Str(a, "key"),
                a["idsFromSteps"]!.AsArray().Select(s => IdFromStep(results, s!.GetValue<string>())).ToList()),
        },
        "cache.xTrim" => new JsonObject
        {
            ["trimmed"] = await db.CacheXTrimAsync(Str(a, "namespace"), Str(a, "key"), Int(a, "maxLen")),
        },
        // Consumer groups are refused by name in V1. The driver exposes no typed helper
        // for an operation that can only fail, so this goes through the raw request path
        // — which is itself the honest answer to "can this SDK reach the operation".
        "cache.xGroup" => await Unit(db.RequestAsync(new JsonObject
        {
            ["Cache"] = new JsonObject
            {
                ["XGroup"] = new JsonObject
                {
                    ["namespace"] = Str(a, "namespace"),
                    ["key"] = Str(a, "key"),
                    ["command"] = Str(a, "command"),
                },
            },
        })),

        // ---- llm ----
        "llm.schema" => new JsonObject
        {
            ["rendered"] = await db.LlmSchemaAsync(Format(Str(a, "format"))),
        },
        "llm.context" => new JsonObject
        {
            ["rendered"] = await db.LlmContextAsync(
                a["sources"]!.AsArray().Select(s => s!["sql"] is { } q
                    ? LlmSource.Sql(q.GetValue<string>())
                    : LlmSource.DocumentFind(s["collection"]!.GetValue<string>())).ToList(),
                Format(Str(a, "format"))),
        },

        // ---- admin ----
        "admin.ping" => await Unit(db.AdminPingAsync()),
        "admin.status" => new JsonObject { ["status"] = Node(await db.AdminStatusAsync()) },

        _ => null,
    };

    // -- actions needing more than one expression -------------------------------------------

    private static async Task<JsonNode?> UpdateOne(TriCoreClient db, JsonObject a)
    {
        await db.DocumentUpdateOneAsync(
            Str(a, "collection"), Str(a, "id"), Update(a), a["upsert"]?.GetValue<bool>() ?? false).ConfigureAwait(false);
        return new JsonObject();
    }

    private static async Task<JsonNode?> UpdateMany(TriCoreClient db, JsonObject a)
    {
        var r = await db.DocumentUpdateManyAsync(Str(a, "collection"), Filter(a["filter"]), Update(a)).ConfigureAwait(false);
        return new JsonObject { ["matched"] = r.Matched, ["modified"] = r.Modified };
    }

    private static async Task<JsonNode?> Search(TriCoreClient db, JsonObject a)
    {
        var filter = a["filter"] is JsonObject f ? Doc(f) : null;
        var hits = await db.VectorSearchAsync(
            Str(a, "collection"), Floats(a["vector"]), a["topK"]!.GetValue<int>(), filter).ConfigureAwait(false);
        return new JsonObject
        {
            ["ids"] = new JsonArray(hits.Select(h => (JsonNode)JsonValue.Create(h.Id)!).ToArray()),
            ["scores"] = new JsonArray(hits.Select(h => (JsonNode)JsonValue.Create(h.Score)).ToArray()),
        };
    }

    private static async Task<JsonNode?> Describe(TriCoreClient db, JsonObject a)
    {
        var d = await db.VectorDescribeCollectionAsync(Str(a, "collection")).ConfigureAwait(false);
        return new JsonObject
        {
            ["dimension"] = d.Dimension,
            ["metric"] = MetricName(d.Metric),
            ["count"] = d.Count,
        };
    }

    private static async Task<JsonNode?> ListVectors(TriCoreClient db, JsonObject a)
    {
        var page = await db.VectorListVectorsAsync(Str(a, "collection")).ConfigureAwait(false);
        return new JsonObject
        {
            ["ids"] = new JsonArray(page.Vectors.Select(v => (JsonNode)JsonValue.Create(v.Id)!).ToArray()),
            ["total"] = page.Total,
        };
    }

    private static async Task<JsonNode?> Neighbors(TriCoreClient db, JsonObject a)
    {
        var ns = await db.GraphNeighborsAsync(
            Str(a, "graph"), Str(a, "nodeId"), Dir(OptStr(a, "direction")), OptStr(a, "label")).ConfigureAwait(false);
        return new JsonObject
        {
            ["nodeIds"] = new JsonArray(ns.Select(n => (JsonNode)JsonValue.Create(n.NodeId)!).ToArray()),
            ["edgeIds"] = new JsonArray(ns.Select(n => (JsonNode)JsonValue.Create(n.EdgeId)!).ToArray()),
        };
    }

    private static async Task<JsonNode?> Traverse(TriCoreClient db, JsonObject a)
    {
        var t = await db.GraphTraverseAsync(
            Str(a, "graph"), Str(a, "start"), Dir(OptStr(a, "direction")), null, OptInt(a, "maxDepth")).ConfigureAwait(false);
        var depths = new JsonObject();
        foreach (var n in t.Nodes) depths[n.Id] = n.Depth;
        return new JsonObject
        {
            ["ids"] = new JsonArray(t.Nodes.Select(n => (JsonNode)JsonValue.Create(n.Id)!).ToArray()),
            ["depths"] = depths,
        };
    }

    private static async Task<JsonNode?> ShortestPath(TriCoreClient db, JsonObject a)
    {
        var p = await db.GraphShortestPathAsync(Str(a, "graph"), Str(a, "from"), Str(a, "to")).ConfigureAwait(false);
        return new JsonObject
        {
            ["found"] = p.Found,
            ["hops"] = p.Hops,
            ["nodePath"] = Arr(p.NodePath),
            ["edgePath"] = Arr(p.EdgePath),
        };
    }

    private static async Task<JsonNode?> WeightedShortestPath(TriCoreClient db, JsonObject a)
    {
        var p = await db.GraphWeightedShortestPathAsync(
            Str(a, "graph"), Str(a, "from"), Str(a, "to"),
            weightProperty: OptStr(a, "weightProperty")).ConfigureAwait(false);
        return new JsonObject
        {
            ["found"] = p.Found,
            ["totalCost"] = p.TotalCost is { } c ? JsonValue.Create(c) : null,
            ["nodePath"] = Arr(p.NodePath),
            ["edgePath"] = Arr(p.EdgePath),
        };
    }

    private static async Task<JsonNode?> ListNodes(TriCoreClient db, JsonObject a)
    {
        var page = await db.GraphListNodesAsync(Str(a, "graph")).ConfigureAwait(false);
        var labels = new JsonObject();
        foreach (var n in page.Nodes) labels[n.Id] = Arr(n.Labels);
        return new JsonObject
        {
            ["ids"] = new JsonArray(page.Nodes.Select(n => (JsonNode)JsonValue.Create(n.Id)!).ToArray()),
            ["labels"] = labels,
            ["total"] = page.Total,
        };
    }

    private static async Task<JsonNode?> ListEdges(TriCoreClient db, JsonObject a)
    {
        var page = await db.GraphListEdgesAsync(Str(a, "graph")).ConfigureAwait(false);
        var labels = new JsonObject();
        foreach (var e in page.Edges) labels[e.Id] = e.Label;
        return new JsonObject
        {
            ["ids"] = new JsonArray(page.Edges.Select(e => (JsonNode)JsonValue.Create(e.Id)!).ToArray()),
            ["labels"] = labels,
            ["total"] = page.Total,
        };
    }

    private static async Task<JsonNode?> Query(TriCoreClient db, JsonObject a)
    {
        var q = await db.GraphQueryAsync(Str(a, "graph"), Str(a, "cypher")).ConfigureAwait(false);
        var rows = new JsonArray();
        foreach (var row in q.Rows)
            rows.Add(new JsonArray(row.Select(Node).ToArray()));
        return new JsonObject { ["columns"] = Arr(q.Columns), ["rows"] = rows };
    }

    // -- small conversions ------------------------------------------------------------------

    private static async Task<JsonNode?> Unit(Task task)
    {
        await task.ConfigureAwait(false);
        return new JsonObject();
    }

    private static async Task<JsonNode?> Unit<T>(Task<T> task)
    {
        await task.ConfigureAwait(false);
        return new JsonObject();
    }

    private static JsonNode Found<T>(T? value, Func<T, JsonObject> present) where T : class =>
        value is null ? new JsonObject { ["found"] = false } : present(value);

    private static JsonNode Found<T>(T? value, Func<T, JsonObject> present) where T : struct =>
        value is null ? new JsonObject { ["found"] = false } : present(value.Value);

    private static string Str(JsonObject a, string name) => a[name]!.GetValue<string>();

    private static string? OptStr(JsonObject a, string name) =>
        a[name] is { } n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;

    private static int? OptInt(JsonObject a, string name) =>
        a[name] is { } n && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<int>() : null;

    /// <summary>The scenario's spelling for a metric the driver handed back. A plain
    /// mapping of the enum, never a re-derivation from anything else.</summary>
    private static string MetricName(VectorMetric m) => m switch
    {
        VectorMetric.Cosine => "cosine",
        VectorMetric.Dot => "dot",
        VectorMetric.L2 => "l2",
        _ => m.ToString(),
    };

    private static VectorMetric Metric(string? s) => s is null ? VectorMetric.Cosine : s switch
    {
        "cosine" => VectorMetric.Cosine,
        "dot" => VectorMetric.Dot,
        "l2" => VectorMetric.L2,
        _ => throw new NotSupportedException($"unsupported metric in the scenario: {s}"),
    };

    // Defaults to `Outgoing` to match the other runners, which do the same. A scenario
    // step that cares always names the direction.
    private static GraphDirection Dir(string? s) => s is null ? GraphDirection.Outgoing : s switch
    {
        "outgoing" => GraphDirection.Outgoing,
        "incoming" => GraphDirection.Incoming,
        "both" => GraphDirection.Both,
        _ => throw new NotSupportedException($"unsupported direction in the scenario: {s}"),
    };

    private static DocumentUpdate Update(JsonObject a)
    {
        var b = DocumentUpdate.Builder();
        if (a["set"] is JsonObject set)
            foreach (var kv in set) b.Set(kv.Key, Val(kv.Value));
        if (a["inc"] is JsonObject inc)
            foreach (var kv in inc) b.Inc(kv.Key, kv.Value!.GetValue<decimal>());
        return b.Build();
    }

    private static Dictionary<string, object?> Doc(JsonNode? node)
    {
        var map = new Dictionary<string, object?>();
        if (node is JsonObject o)
            foreach (var kv in o) map[kv.Key] = Val(kv.Value);
        return map;
    }

    private static List<float> Floats(JsonNode? node) =>
        node is JsonArray arr ? arr.Select(x => x!.GetValue<float>()).ToList() : new List<float>();

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray arr ? arr.Select(x => x!.GetValue<string>()).ToList() : new List<string>();

    private static JsonArray Arr(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    /// <summary>Decode a scenario JSON value into the CLR shape the SDK takes.</summary>
    private static object? Val(JsonNode? node)
    {
        if (node is null) return null;
        switch (node.GetValueKind())
        {
            case JsonValueKind.String: return node.GetValue<string>();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            case JsonValueKind.Number:
                var raw = node.ToJsonString();
                return raw.Contains('.') || raw.Contains('e') || raw.Contains('E')
                    ? node.GetValue<double>()
                    : node.GetValue<long>();
            case JsonValueKind.Array:
                return node.AsArray().Select(Val).ToList();
            case JsonValueKind.Object:
                return Doc(node);
            default:
                return null;
        }
    }

    /// <summary>Re-encode a driver-returned CLR value as canonical JSON.</summary>
    private static JsonNode? Node(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        decimal m => JsonValue.Create(m),
        IReadOnlyDictionary<string, object?> map => Node(map),
        System.Collections.IEnumerable seq => new JsonArray(seq.Cast<object?>().Select(Node).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonNode Node(IReadOnlyDictionary<string, object?> map)
    {
        var o = new JsonObject();
        foreach (var kv in map) o[kv.Key] = Node(kv.Value);
        return o;
    }

    private static JsonArray Docs(IReadOnlyList<IReadOnlyDictionary<string, object?>> docs) =>
        new(docs.Select(d => Node(d)).ToArray());

    // -- sql / cache actions needing more than one expression --------------------------------

    private static async Task<JsonNode?> SqlExecute(TriCoreClient db, JsonObject a)
    {
        var resp = await db.ExecuteAsync(Str(a, "sql")).ConfigureAwait(false);
        // Not every Exec answers with a row count (DDL and transaction scripts do
        // not), so an absent field stays null rather than becoming 0.
        JsonNode? affected = null;
        if (resp.Data.ValueKind == JsonValueKind.Object
            && resp.Data.TryGetProperty("Json", out var j)
            && j.ValueKind == JsonValueKind.Object
            && j.TryGetProperty("rows_affected", out var n)
            && n.ValueKind == JsonValueKind.Number)
        {
            affected = JsonValue.Create(n.GetInt64());
        }
        return new JsonObject { ["rowsAffected"] = affected };
    }

    private static async Task<JsonNode?> SqlQuery(TriCoreClient db, JsonObject a)
    {
        var rows = await db.QueryAsync(Str(a, "sql")).ConfigureAwait(false);
        var out_ = new JsonArray();
        foreach (var row in rows)
        {
            out_.Add(new JsonArray(row.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()));
        }
        return new JsonObject { ["columns"] = Arr(rows.Columns), ["rows"] = out_ };
    }

    private static async Task<JsonNode?> CacheKeys(TriCoreClient db, JsonObject a)
    {
        var keys = await db.CacheKeysAsync(Str(a, "namespace"), OptStr(a, "pattern")).ConfigureAwait(false);
        return new JsonObject { ["keys"] = Arr(keys.Select(k => k.Key)) };
    }

    private static async Task<JsonNode?> HGetAll(TriCoreClient db, JsonObject a)
    {
        var pairs = await db.CacheHGetAllAsync(Str(a, "namespace"), Str(a, "key")).ConfigureAwait(false);
        var entries = new JsonArray();
        foreach (var p in pairs)
        {
            entries.Add(new JsonArray(JsonValue.Create(Text(p.Key)), JsonValue.Create(Text(p.Value))));
        }
        return new JsonObject { ["entries"] = entries };
    }

    // -- cache encoding helpers --------------------------------------------------------------
    //
    // Mechanical translation only: text from the scenario becomes bytes for the driver
    // and back again. No defaults, no computed values.

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string Text(byte[] b) => Encoding.UTF8.GetString(b);

    private static List<byte[]> ByteList(JsonObject a, string name) =>
        a[name]!.AsArray().Select(v => Bytes(v!.GetValue<string>())).ToList();

    private static List<KeyValuePair<byte[], byte[]>> PairList(JsonObject a, string name) =>
        a[name]!.AsArray()
            .Select(p => new KeyValuePair<byte[], byte[]>(
                Bytes(p![0]!.GetValue<string>()), Bytes(p[1]!.GetValue<string>())))
            .ToList();

    private static JsonArray Texts(IReadOnlyList<byte[]> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(Text(v))!).ToArray());

    /// <summary>The canonical shape for "a value, or a miss".</summary>
    private static JsonNode Found(byte[]? v) =>
        v is null ? new JsonObject { ["found"] = false }
                  : new JsonObject { ["found"] = true, ["value"] = Text(v) };

    private static JsonNode Ttl(long? ttl) =>
        ttl is null ? new JsonObject { ["hasTtl"] = false }
                    : new JsonObject { ["hasTtl"] = true, ["ttlMs"] = ttl.Value };

    private static JsonNode Entries(IReadOnlyList<StreamEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var fields = new JsonArray();
            foreach (var f in e.Fields)
            {
                fields.Add(new JsonArray(JsonValue.Create(Text(f.Key)), JsonValue.Create(Text(f.Value))));
            }
            arr.Add(new JsonObject { ["id"] = e.Id, ["fields"] = fields });
        }
        return new JsonObject { ["entries"] = arr };
    }

    /// <summary>
    /// The <c>id</c> an earlier step returned. A missing step yields an empty string
    /// and the call then fails, which is the correct outcome for a scenario naming a
    /// step that did not run.
    /// </summary>
    private static string IdFromStep(Dictionary<string, JsonNode?> results, string step) =>
        results.TryGetValue(step, out var v) && v?["id"] is { } id ? id.GetValue<string>() : "";

    private static long? OptLong(JsonObject a, string name) =>
        a[name] is { } n && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<long>() : null;

    private static int Int(JsonObject a, string name) => a[name]!.GetValue<int>();

    private static OutputFormat Format(string name) => name switch
    {
        "native" => OutputFormat.Native,
        "json" => OutputFormat.Json,
        "toon" => OutputFormat.Toon,
        "markdown" => OutputFormat.Markdown,
        _ => throw new NotSupportedException($"unsupported output format: {name}"),
    };
}
