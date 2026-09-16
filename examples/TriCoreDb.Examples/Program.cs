using System.Text;
using TriCoreDb;

namespace TriCoreDb.Examples;

/// <summary>
/// Runnable samples for every TriCoreDB module.
///
/// <code>
/// dotnet run --project TriCoreDb.Examples            # list the samples
/// dotnet run --project TriCoreDb.Examples -- sql     # run just one
/// dotnet run --project TriCoreDb.Examples -- all     # run every one
/// </code>
///
/// One entry point rather than nine projects: that is the form a .NET solution
/// actually runs, and each sample stays independent — none depends on another
/// having run first.
///
/// Connection settings come from the environment (see .env.example):
/// TRICOREDB_HOST, TRICOREDB_PORT, TRICOREDB_USER, TRICOREDB_PASSWORD.
/// </summary>
internal static class Program
{
    private static readonly string Host = EnvOr("TRICOREDB_HOST", "127.0.0.1");
    private static readonly int Port = int.Parse(EnvOr("TRICOREDB_PORT", "9425"));
    private static readonly string User = EnvOr("TRICOREDB_USER", "admin");
    private static readonly string Password = EnvOr("TRICOREDB_PASSWORD", "admin");

    private static async Task<int> Main(string[] args)
    {
        var which = args.Length > 0 ? args[0] : "";
        var samples = new Dictionary<string, Func<Task>>
        {
            ["basic"] = BasicAsync,
            ["sql"] = SqlAsync,
            ["nosql"] = NoSqlAsync,
            ["vector"] = VectorAsync,
            ["graph"] = GraphAsync,
            ["cache"] = CacheAsync,
            ["errors"] = ErrorsAsync,
            ["concurrency"] = ConcurrencyAsync,
        };

        if (which == "all")
        {
            foreach (var s in samples.Values) await s().ConfigureAwait(false);
            return 0;
        }
        if (samples.TryGetValue(which, out var run))
        {
            await run().ConfigureAwait(false);
            return 0;
        }

        Console.WriteLine("usage: dotnet run --project TriCoreDb.Examples -- <sample>");
        Console.WriteLine($"  {string.Join("  ", samples.Keys)}  all");
        return args.Length > 0 ? 2 : 0;
    }

    // -- harness -------------------------------------------------------------

    private static string EnvOr(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    private static Task<TriCoreClient> ConnectAsync(string clientName = "tricoredb-example") =>
        TriCoreClient.ConnectAsync(Host, Port, User, Password, clientName);

    /// <summary>Run a sample, print a banner, and always close the connection.
    /// Exits non-zero on failure with the exception intact — a sample that
    /// swallows the reason it failed is worse than no sample.</summary>
    private static async Task RunAsync(string title, Func<TriCoreClient, Task> body)
    {
        Console.WriteLine($"\n=== {title} ===");
        Console.WriteLine($"connecting to {Host}:{Port} as {User}\n");
        try
        {
            await using var db = await ConnectAsync().ConfigureAwait(false);
            await body(db).ConfigureAwait(false);
            Console.WriteLine($"\n{title}: OK");
        }
        catch (Exception e)
        {
            Console.WriteLine($"\n{title}: FAILED");
            Console.WriteLine(e);
            Environment.Exit(1);
        }
    }

    private static void Show(string label, object? value) =>
        Console.WriteLine($"  {label,-26} {Format(value)}");

    private static void Section(string name) =>
        Console.WriteLine($"\n  -- {name} {new string('-', Math.Max(0, 58 - name.Length))}");

    private static string Format(object? v) => v switch
    {
        null => "null",
        byte[] b => Encoding.UTF8.GetString(b),
        string s => s,
        System.Collections.IEnumerable seq => "[" + string.Join(", ", seq.Cast<object?>().Select(Format)) + "]",
        _ => v.ToString() ?? "",
    };

    /// <summary>Drop a fixture, ignoring "it was not there" — samples must re-run.</summary>
    private static async Task IgnoreMissingAsync(Func<Task> f)
    {
        try { await f().ConfigureAwait(false); }
        catch (TriCoreException) { /* a fresh store has no such fixture */ }
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    // -- basic ---------------------------------------------------------------

    private static Task BasicAsync() => RunAsync("Basic connection", async db =>
    {
        Show("session id", db.SessionId);

        // Two different liveness checks, and the difference matters.
        await db.PingAsync();
        Show("ping (transport)", "PONG - the socket and framing are alive");
        await db.CachePingAsync();
        Show("cache ping (module)", "ok - auth, routing and dispatch all work");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var rows = await db.QueryAsync("SELECT 1");
        Show("SELECT 1", rows.Select(r => string.Join(",", r)));
        Show("round trip", $"{started.Elapsed.TotalMilliseconds:F2} ms");

        // A second connection is independent: its own session, its own ids.
        await using (var other = await ConnectAsync("tricoredb-example-second"))
        {
            Show("second session id", other.SessionId);
            Show("sessions differ", other.SessionId != db.SessionId);
        }
        Show("second connection", "closed by await using");
    });

    // -- sql -----------------------------------------------------------------

    private static Task SqlAsync() => RunAsync("SQL", async db =>
    {
        const string t = "ex_sql_users";
        await IgnoreMissingAsync(() => db.ExecuteAsync($"DROP TABLE {t}"));

        Section("schema");
        await db.ExecuteAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, name TEXT, city TEXT, age INT)");
        Show("created", t);
        await db.ExecuteAsync($"CREATE INDEX {t}_city ON {t} (city)");
        Show("index", $"{t}_city on city");

        Section("insert, with bound parameters");
        // `?` is bound client-side. The quote in O'Brien is escaped for you;
        // building this string with interpolation is where injection bugs come from.
        foreach (var (id, name, city, age) in new[]
                 {
                     (1, "ada", "Pune", 36),
                     (2, "O'Brien", "Cork", 41),
                     (3, "grace", "Pune", 45),
                 })
        {
            await db.ExecuteAsync($"INSERT INTO {t} VALUES (?, ?, ?, ?)", new object?[] { id, name, city, age });
            Show($"insert {id}", "ok");
        }

        Section("select");
        Show("all rows", (await db.QueryAsync($"SELECT * FROM {t}")).Select(r => string.Join(",", r)));
        Show("by city", (await db.QueryAsync($"SELECT name FROM {t} WHERE city = ?", new object?[] { "Pune" }))
            .Select(r => r[0]));
        Show("quote round-trip", (await db.QueryAsync($"SELECT name FROM {t} WHERE id = ?", new object?[] { 2 }))
            .Select(r => r[0]));
        Show("aggregate", (await db.QueryAsync($"SELECT city, COUNT(id) FROM {t} GROUP BY city"))
            .Select(r => string.Join("=", r)));
        Show("as dictionaries", (await db.QueryAsync($"SELECT id, name FROM {t} WHERE age > ?", new object?[] { 40 }))
            .Dicts().Select(d => string.Join(",", d.Select(kv => $"{kv.Key}={kv.Value}"))));

        Section("update and delete");
        await db.ExecuteAsync($"UPDATE {t} SET city = ? WHERE id = ?", new object?[] { "Mumbai", 1 });
        Show("after update", (await db.QueryAsync($"SELECT city FROM {t} WHERE id = ?", new object?[] { 1 }))
            .Select(r => r[0]));
        await db.ExecuteAsync($"DELETE FROM {t} WHERE id = ?", new object?[] { 3 });
        Show("remaining", (await db.QueryAsync($"SELECT id FROM {t}")).Count);

        Section("transactions");
        // A transaction is one request carrying a whole BEGIN..COMMIT script.
        // There is no session-scoped transaction: a bare BEGIN commits at once.
        var tr = await db.TransactionAsync(
            new SqlStatement($"INSERT INTO {t} VALUES (?, ?, ?, ?)", 10, "tx-a", "Delhi", 30),
            new SqlStatement($"INSERT INTO {t} VALUES (?, ?, ?, ?)", 11, "tx-b", "Delhi", 31));
        Show("committed", tr);
        Show("rows now", (await db.QueryAsync($"SELECT id FROM {t}")).Count);

        try
        {
            await db.TransactionAsync(
                new SqlStatement($"INSERT INTO {t} VALUES (?, ?, ?, ?)", 20, "ghost", "Nowhere", 1),
                new SqlStatement("INSERT INTO no_such_table VALUES (1)"));
        }
        catch (TriCoreException e)
        {
            Show("aborted script", Truncate(e.Message, 60));
        }
        var ghost = await db.QueryAsync($"SELECT id FROM {t} WHERE id = ?", new object?[] { 20 });
        Show("ghost row present?", ghost.Count == 0 ? "no - the whole script was discarded" : "YES (bug!)");

        Section("plans");
        Show("EXPLAIN (indexed)", (await db.ExecuteAsync($"EXPLAIN SELECT * FROM {t} WHERE id = 1")).Data.ToString());
        Show("EXPLAIN (scan)", (await db.ExecuteAsync($"EXPLAIN SELECT * FROM {t} WHERE age > 30")).Data.ToString());

        Section("cleanup");
        await db.ExecuteAsync($"DROP TABLE {t}");
        Show("dropped", t);
    });

    // -- nosql ---------------------------------------------------------------

    private static Task NoSqlAsync() => RunAsync("NoSQL (document)", async db =>
    {
        const string c = "ex_people";
        await IgnoreMissingAsync(() => db.DocumentDropCollectionAsync(c));

        Section("collection");
        await db.DocumentCreateCollectionAsync(c);
        Show("created", c);

        Section("insert");
        var generated = await db.DocumentInsertAsync(c,
            new Dictionary<string, object?> { ["name"] = "ada", ["city"] = "Pune", ["age"] = 36 });
        Show("server-assigned id", generated);
        await db.DocumentInsertAsync(c,
            new Dictionary<string, object?> { ["name"] = "grace", ["city"] = "Pune", ["age"] = 45 }, "grace");
        await db.DocumentInsertAsync(c,
            new Dictionary<string, object?> { ["name"] = "linus", ["city"] = "Helsinki", ["age"] = 54 }, "linus");
        Show("chosen ids", new[] { "grace", "linus" });

        Section("read");
        Show("get by id", (await db.DocumentGetAsync(c, "grace"))?["name"]);
        Show("get a miss", await db.DocumentGetAsync(c, "nobody") is null
            ? "null - a miss is not an error" : "found?!");
        Show("find all", (await db.DocumentFindAsync(c)).Count);
        Show("city = Pune", (await db.DocumentFindAsync(c, DocumentFilter.Eq("city", "Pune")))
            .Select(d => d["name"]));
        Show("Pune AND age > 40", (await db.DocumentFindAsync(c, DocumentFilter.And(
            DocumentFilter.Eq("city", "Pune"), DocumentFilter.Gt("age", 40)))).Select(d => d["name"]));
        Show("with a limit", (await db.DocumentFindAsync(c, limit: 1)).Count);

        Section("update");
        await db.DocumentUpdateAsync(c, "grace", new Dictionary<string, object?> { ["city"] = "Arlington" });
        Show("set a field", (await db.DocumentGetAsync(c, "grace"))?["city"]);
        await db.DocumentUpdateOneAsync(c, "grace", DocumentUpdate.Inc("age", 1));
        Show("increment", (await db.DocumentGetAsync(c, "grace"))?["age"]);
        Show("upsert created?", await db.DocumentUpdateOneAsync(c, "newcomer",
            DocumentUpdate.Set("name", "newcomer"), upsert: true));
        Show("update many", await db.DocumentUpdateManyAsync(c, DocumentFilter.Eq("city", "Pune"),
            DocumentUpdate.Set("region", "west")));

        Section("indexes and aggregation");
        await db.DocumentCreateIndexAsync(c, "ex_by_city", "city");
        Show("indexes", (await db.DocumentListIndexesAsync(c)).Select(i => i.IndexName));
        Show("analyze", await db.DocumentAnalyzeAsync(c));
        // Stages apply strictly in the order given: that order is semantics.
        var byCity = await db.DocumentAggregateAsync(c, new[]
        {
            AggregateStage.GroupByField("city", Accumulator.Count("n")),
            AggregateStage.Sort(new SortKey("_id")),
        });
        Show("count by city", byCity.Select(d => $"{d["_id"]}={d["n"]}"));

        Section("delete");
        await db.DocumentDeleteAsync(c, "newcomer");
        Show("after delete", (await db.DocumentFindAsync(c)).Count);
        await db.DocumentDropIndexAsync(c, "ex_by_city");
        await db.DocumentDropCollectionAsync(c);
        Show("dropped", c);
    });

    // -- vector --------------------------------------------------------------

    private static Task VectorAsync() => RunAsync("Vector", async db =>
    {
        const string c = "ex_embeddings";
        await IgnoreMissingAsync(() => db.VectorDropCollectionAsync(c));

        Section("collection");
        await db.VectorCreateCollectionAsync(c, 4);
        Show("created", $"{c} (dimension 4, cosine)");
        Show("describe", await db.VectorDescribeCollectionAsync(c));

        Section("insert");
        await db.VectorUpsertAsync(c, "v1", new float[] { 1, 0, 0, 0 },
            new Dictionary<string, object?> { ["tag"] = "alpha", ["lang"] = "en" });
        await db.VectorUpsertAsync(c, "v2", new float[] { 0, 1, 0, 0 },
            new Dictionary<string, object?> { ["tag"] = "beta", ["lang"] = "en" });
        await db.VectorUpsertAsync(c, "v3", new float[] { 0.9f, 0.1f, 0, 0 },
            new Dictionary<string, object?> { ["tag"] = "alpha", ["lang"] = "fr" });
        Show("upserted", 3);

        Section("search");
        // `score` is always "higher is closer", whatever the metric - for l2 the
        // server negates the distance so callers never branch on the metric.
        var hits = await db.VectorSearchAsync(c, new float[] { 1, 0, 0, 0 }, 3);
        Show("nearest to [1,0,0,0]", hits.Select(h => $"{h.Id}={h.Score:F4}"));
        var filtered = await db.VectorSearchAsync(c, new float[] { 1, 0, 0, 0 }, 5,
            new Dictionary<string, object?> { ["tag"] = "alpha" });
        Show("filtered tag=alpha", filtered.Select(h => h.Id));
        Show("topK bounds it", (await db.VectorSearchAsync(c, new float[] { 1, 0, 0, 0 }, 1)).Count);

        Section("read and page");
        Show("get v1", await db.VectorGetAsync(c, "v1"));
        Show("get a miss", await db.VectorGetAsync(c, "nope") is null
            ? "null - a miss is not an error" : "found?!");
        var page = await db.VectorListVectorsAsync(c);
        Show("list", $"{page.Vectors.Count} of {page.Total}, truncated={page.Truncated}");

        Section("overwrite");
        // Upsert replaces wholesale: metadata is not merged.
        await db.VectorUpsertAsync(c, "v1", new float[] { 0, 0, 1, 0 },
            new Dictionary<string, object?> { ["tag"] = "gamma" });
        var after = await db.VectorGetAsync(c, "v1");
        Show("lang key gone?", after!.Metadata.ContainsKey("lang") ? "NO (bug!)" : "yes - metadata is replaced");

        Section("cleanup");
        await db.VectorDeleteAsync(c, "v2");
        Show("after delete", (await db.VectorListVectorsAsync(c)).Total);
        await db.VectorDropCollectionAsync(c);
        Show("dropped", c);
    });

    // -- graph ---------------------------------------------------------------

    private static Task GraphAsync() => RunAsync("Graph", async db =>
    {
        const string g = "ex_social";
        await IgnoreMissingAsync(() => db.GraphDropAsync(g));

        Section("graph");
        await db.GraphCreateAsync(g);
        Show("created", g);

        Section("nodes");
        await db.GraphAddNodeAsync(g, "ada", new[] { "Person" },
            new Dictionary<string, object?> { ["name"] = "ada", ["city"] = "Pune" });
        await db.GraphAddNodeAsync(g, "grace", new[] { "Person" },
            new Dictionary<string, object?> { ["name"] = "grace", ["city"] = "Pune" });
        await db.GraphAddNodeAsync(g, "linus", new[] { "Person" },
            new Dictionary<string, object?> { ["name"] = "linus", ["city"] = "Helsinki" });
        await db.GraphAddNodeAsync(g, "acme", new[] { "Company" },
            new Dictionary<string, object?> { ["name"] = "ACME" });
        Show("nodes", 4);
        Show("get ada", (await db.GraphGetNodeAsync(g, "ada"))?.Labels);
        Show("get a miss", await db.GraphGetNodeAsync(g, "nobody") is null
            ? "null - a miss is not an error" : "found?!");

        Section("edges");
        // Weights are lopsided on purpose so fewest-hops and least-cost disagree.
        await db.GraphAddEdgeAsync(g, "e1", "ada", "grace", "KNOWS",
            new Dictionary<string, object?> { ["weight"] = 1 });
        await db.GraphAddEdgeAsync(g, "e2", "grace", "linus", "KNOWS",
            new Dictionary<string, object?> { ["weight"] = 1 });
        await db.GraphAddEdgeAsync(g, "e3", "ada", "linus", "KNOWS",
            new Dictionary<string, object?> { ["weight"] = 10 });
        Show("edges", 3);

        Section("traversal");
        Show("neighbours of ada",
            (await db.GraphNeighborsAsync(g, "ada", GraphDirection.Outgoing)).Select(n => n.NodeId));
        Show("degree of ada", await db.GraphDegreeAsync(g, "ada", GraphDirection.Outgoing));
        var walk = await db.GraphTraverseAsync(g, "ada", GraphDirection.Outgoing, maxDepth: 2);
        Show("traverse depth 2", walk.Nodes.Select(n => $"{n.Id}@{n.Depth}"));
        Show("clamped to", $"max_depth={walk.MaxDepth} limit={walk.Limit} truncated={walk.Truncated}");

        Section("paths");
        var hops = await db.GraphShortestPathAsync(g, "ada", "linus", GraphDirection.Outgoing);
        Show("fewest hops", $"{string.Join(" -> ", hops.NodePath)} ({hops.Hops} hop)");
        var cost = await db.GraphWeightedShortestPathAsync(g, "ada", "linus", GraphDirection.Outgoing,
            weightProperty: "weight");
        Show("least cost", $"{string.Join(" -> ", cost.NodePath)} (cost {cost.TotalCost})");
        // Two different algorithms; on this graph they genuinely disagree.
        Show("same path?", hops.NodePath.SequenceEqual(cost.NodePath) ? "yes" : "no - as expected");
        var none = await db.GraphShortestPathAsync(g, "ada", "acme", GraphDirection.Outgoing);
        Show("unreachable", $"found={none.Found} - not an error, just no path");

        Section("cypher (read-only subset)");
        var people = await db.GraphQueryAsync(g, "MATCH (n:Person) RETURN n.name ORDER BY n.name");
        Show("MATCH label", people.Rows.Select(r => r[0]));
        try
        {
            await db.GraphQueryAsync(g, "MATCH (n) DETACH DELETE n");
        }
        catch (TriCoreException e)
        {
            // Write clauses are refused by name rather than ignored: a query that
            // silently dropped one would return a confidently wrong answer.
            Show("write clause", "refused - " + Truncate(e.Message, 56));
        }

        Section("cleanup");
        Show("list nodes", (await db.GraphListNodesAsync(g)).Total);
        Show("list edges", (await db.GraphListEdgesAsync(g)).Total);
        await db.GraphDropAsync(g);
        Show("dropped", g);
    });

    // -- cache ---------------------------------------------------------------

    private static Task CacheAsync() => RunAsync("Cache", async db =>
    {
        const string ns = "ex_cache";
        await db.CacheClearNamespaceAsync(ns);

        Section("strings and TTL");
        await db.CacheSetAsync(ns, "greeting", B("hello"));
        Show("get", await db.CacheGetTextAsync(ns, "greeting"));
        Show("exists", await db.CacheExistsAsync(ns, "greeting"));
        Show("get a miss", await db.CacheGetAsync(ns, "nope") is null
            ? "null - distinct from a stored empty value" : "found?!");
        await db.CacheSetAsync(ns, "session", B("abc123"), 60_000);
        Show("ttl", await db.CacheTtlAsync(ns, "session"));
        Show("ttl of a permanent key", $"{await db.CacheTtlAsync(ns, "greeting")} - null means 'no expiry'");
        Show("persist", await db.CachePersistAsync(ns, "session"));
        Show("expire", await db.CacheExpireAsync(ns, "session", 30_000));
        Show("expire a missing key", await db.CacheExpireAsync(ns, "nope", 1000));

        Section("counters and locks");
        Show("incr from absent", await db.CacheIncrAsync(ns, "hits"));
        Show("incr again", await db.CacheIncrAsync(ns, "hits", 4));
        Show("setnx takes it", await db.CacheSetNxAsync(ns, "lock", B("owner-a"), 30_000));
        Show("setnx loses", await db.CacheSetNxAsync(ns, "lock", B("owner-b"), 30_000));
        Show("holder", await db.CacheGetTextAsync(ns, "lock"));

        Section("key browsing");
        Show("keys", (await db.CacheKeysAsync(ns)).Select(k => k.Key));
        Show("keys matching se*", (await db.CacheKeysAsync(ns, "se*")).Select(k => k.Key));

        Section("lists");
        Show("rpush", await db.CacheRPushAsync(ns, "queue", new[] { B("a"), B("b") }));
        Show("lpush", await db.CacheLPushAsync(ns, "queue", new[] { B("z") }));
        Show("lrange 0..-1", await db.CacheLRangeAsync(ns, "queue", 0, -1));
        Show("llen", await db.CacheLLenAsync(ns, "queue"));
        Show("lindex -1", await db.CacheLIndexAsync(ns, "queue", -1));
        Show("lpop / rpop",
            $"{Format(await db.CacheLPopAsync(ns, "queue"))} / {Format(await db.CacheRPopAsync(ns, "queue"))}");

        Section("sets");
        Show("sadd", await db.CacheSAddAsync(ns, "tags", new[] { B("x"), B("y"), B("z") }));
        Show("sadd a duplicate", $"{await db.CacheSAddAsync(ns, "tags", new[] { B("x") })} - already present");
        Show("sismember", await db.CacheSIsMemberAsync(ns, "tags", B("x")));
        Show("scard", await db.CacheSCardAsync(ns, "tags"));
        Show("smembers", await db.CacheSMembersAsync(ns, "tags"));
        Show("srem", await db.CacheSRemAsync(ns, "tags", new[] { B("x") }));

        Section("hashes");
        Show("hset", await db.CacheHSetTextAsync(ns, "profile",
            new Dictionary<string, string> { ["name"] = "ada", ["city"] = "Pune" }));
        Show("hset overwrite", $"{await db.CacheHSetTextAsync(ns, "profile",
            new Dictionary<string, string> { ["city"] = "Mumbai" })} - 0 newly created");
        Show("hget", await db.CacheHGetAsync(ns, "profile", B("city")));
        Show("hgetall", (await db.CacheHGetAllAsync(ns, "profile"))
            .Select(p => $"{Encoding.UTF8.GetString(p.Key)}={Encoding.UTF8.GetString(p.Value)}"));
        Show("hexists", await db.CacheHExistsAsync(ns, "profile", B("name")));
        Show("hlen", await db.CacheHLenAsync(ns, "profile"));
        Show("hdel", await db.CacheHDelAsync(ns, "profile", new[] { B("city") }));

        Section("streams");
        var id1 = await db.CacheXAddTextAsync(ns, "events", new Dictionary<string, string> { ["type"] = "signup" });
        var id2 = await db.CacheXAddTextAsync(ns, "events", new Dictionary<string, string> { ["type"] = "login" });
        Show("xadd", $"{id1}, {id2}");
        Show("ids increase", string.CompareOrdinal(id2, id1) > 0 ? "yes - strictly monotonic" : "NO (bug!)");
        Show("xlen", await db.CacheXLenAsync(ns, "events"));
        Show("xrange", (await db.CacheXRangeAsync(ns, "events")).Select(e => e.AsText()["type"]));
        // The non-blocking poll primitive: everything newer than what I last saw.
        Show("xread after id1", (await db.CacheXReadAsync(ns, "events", id1)).Select(e => e.AsText()["type"]));
        Show("xread after id2",
            $"{(await db.CacheXReadAsync(ns, "events", id2)).Count} - caught up, and it does not block");
        Show("xdel", await db.CacheXDelAsync(ns, "events", new[] { id2 }));
        Show("xtrim to 0", await db.CacheXTrimAsync(ns, "events", 0));

        Section("type safety");
        try
        {
            await db.CacheLPushAsync(ns, "greeting", new[] { B("nope") });
        }
        catch (TriCoreException e)
        {
            // A key holds exactly one type at a time. Operating on the wrong one
            // is an error, never a coercion that discards the existing value.
            Show("list op on a string", "refused - " + Truncate(e.Message, 56));
        }

        Section("cleanup");
        Show("cleared", await db.CacheClearNamespaceAsync(ns));
    });

    // -- errors --------------------------------------------------------------

    private static Task ErrorsAsync() => RunAsync("Error model", async db =>
    {
        Section("server refusals");
        await Expect("unknown table", () => db.QueryAsync("SELECT * FROM no_such_table_xyz"));
        await Expect("syntax error", () => db.QueryAsync("SELCT 1"));
        await Expect("unknown collection", () => db.DocumentFindAsync("no_such_collection_xyz"));
        await Expect("unknown graph", () => db.GraphListNodesAsync("no_such_graph_xyz"));
        await Expect("write sent as a read", () => db.QueryAsync("INSERT INTO t VALUES (1)"));

        Section("client-side validation");
        await Expect("empty member list", () => db.CacheSAddAsync("ns", "k", Array.Empty<byte[]>()));
        await Expect("placeholder mismatch", () => db.QueryAsync("SELECT ?", Array.Empty<object?>()));
        await Expect("unbindable value", () => db.QueryAsync("SELECT ?", new object?[] { new object() }));
        await Expect("nested BEGIN", () => db.TransactionAsync(new SqlStatement("BEGIN")));
        await Expect("empty cancel id", () => db.CancelAsync(""));

        Section("connection failures");
        await Expect("server unavailable", async () =>
            await TriCoreClient.ConnectAsync("127.0.0.1", 1, "x", "y"));

        Section("using a closed connection");
        var doomed = await ConnectAsync("tricoredb-example-doomed");
        await doomed.DisposeAsync();
        await Expect("request after close", () => doomed.QueryAsync("SELECT 1"));

        Section("authentication");
        // Under `dev_auth = true` any non-empty secret is accepted by design, so
        // this is reported rather than asserted: claiming a pass here would be
        // claiming the server rejected a credential it was configured to accept.
        try
        {
            await using var bad = await TriCoreClient.ConnectAsync(Host, Port, User, "definitely-not-the-password");
            Show("wrong password", "accepted - this server runs dev auth (any non-empty secret)");
        }
        catch (AuthException e)
        {
            Show("wrong password", $"AuthException: {Truncate(e.Message, 60)}");
        }
    });

    private static async Task Expect(string label, Func<Task> f)
    {
        try
        {
            await f().ConfigureAwait(false);
            Show(label, "NO ERROR RAISED - this sample is wrong, or the server changed");
            Environment.Exit(1);
        }
        catch (Exception e)
        {
            Show(label, $"{e.GetType().Name}: {Truncate(e.Message, 66)}");
        }
    }

    // -- concurrency ---------------------------------------------------------

    private static Task ConcurrencyAsync() => RunAsync("Concurrency", async db =>
    {
        const string ns = "ex_conc";
        await db.CacheClearNamespaceAsync(ns);

        Section("one connection is a queue");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            await db.CacheSetAsync(ns, $"serial-{i}", B(i.ToString()));
        }
        Show("50 sets, serial", $"{sw.Elapsed.TotalMilliseconds:F1} ms");

        Section("a pool gives real concurrency");
        foreach (var size in new[] { 1, 10, 50 })
        {
            await using var pool = new Pool(Host, Port, User, Password, size);
            var start = System.Diagnostics.Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
                pool.UseAsync(conn => conn.CacheSetAsync(ns, $"pool-{i}", B(i.ToString())))));
            Show($"200 sets, pool of {size}", $"{start.Elapsed.TotalMilliseconds:F1} ms");
        }

        Section("results stay matched to their callers");
        // The real hazard is not slowness, it is a reply landing on the wrong
        // caller. Each task writes a value only it knows and reads it back; a
        // crossed wire shows up as a mismatch, not a plausible wrong number.
        await using (var pool = new Pool(Host, Port, User, Password, 20))
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 500).Select(i =>
                pool.UseAsync(async conn =>
                {
                    var key = $"match-{i}";
                    var want = $"value-{i}";
                    await conn.CacheSetAsync(ns, key, B(want));
                    return await conn.CacheGetTextAsync(ns, key) == want;
                })));
            var crossed = results.Count(ok => !ok);
            Show("500 write+read pairs", $"{results.Length - crossed} matched, {crossed} crossed");

            Section("concurrent counter increments");
            // Every increment must land: the server serialises them, so the
            // final value is the arithmetic total.
            await Task.WhenAll(Enumerable.Range(0, 300).Select(_ =>
                pool.UseAsync(conn => conn.CacheIncrAsync(ns, "counter"))));
            Show("300 concurrent incr", $"counter = {await db.CacheGetTextAsync(ns, "counter")}");
        }

        Section("the mistake the pool prevents");
        // Overlapping requests on ONE connection. Without the driver's in-flight
        // guard this deadlocks: the second reader consumes the first reply's
        // body as a header and then awaits bytes that never arrive.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            try { await db.QueryAsync("SELECT 1"); return (string?)null; }
            catch (TriCoreException e) { return e.Message; }
        }));
        var refused = outcomes.Count(o => o is not null);
        Show("3 overlapped requests", $"{outcomes.Length - refused} ran, {refused} refused");
        if (outcomes.FirstOrDefault(o => o is not null) is { } msg)
        {
            Show("the refusal says", Truncate(msg, 60));
        }
        // And the connection is still perfectly usable.
        Show("connection still works", (await db.QueryAsync("SELECT 1")).Select(r => string.Join(",", r)));

        await db.CacheClearNamespaceAsync(ns);
        Show("cleaned up", ns);
    });
}
