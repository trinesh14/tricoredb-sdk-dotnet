using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TriCoreDb.Tests.Support;

namespace TriCoreDb.Tests.Live;

/// <summary>Round trips, request ids, status handling and the pool, against a real server.</summary>
[Collection("live")]
[Trait("Category", "Live")]
public class ConnectionAndPoolTests
{
    private readonly TriCoreServer _server;

    public ConnectionAndPoolTests(LiveServer live) => _server = live.Server;

    [LiveFact]
    public async Task Handshake_grants_the_negotiated_features_and_a_session()
    {
        await using var db = await _server.ConnectAsync();
        Assert.True(db.ServerParamsGranted);
        Assert.True(db.SessionTxnGranted);
        Assert.False(string.IsNullOrEmpty(db.SessionId));
        Assert.False(db.IsSecure);
        await db.PingAsync();
    }

    [LiveFact]
    public async Task Cache_values_round_trip_and_a_miss_is_distinct_from_empty()
    {
        await using var db = await _server.ConnectAsync();
        var ns = Names.Unique("e2e");
        var greeting = "hello world"u8.ToArray();
        await db.CacheSetAsync(ns, "greeting", greeting);
        Assert.Equal(greeting, await db.CacheGetAsync(ns, "greeting"));

        await db.CacheSetAsync(ns, "empty", Array.Empty<byte>());
        var empty = await db.CacheGetAsync(ns, "empty");
        Assert.NotNull(empty);
        Assert.Empty(empty!);
        Assert.Null(await db.CacheGetAsync(ns, "absent"));

        Assert.True(await db.CacheDeleteAsync(ns, "greeting"));
        Assert.Null(await db.CacheGetAsync(ns, "greeting"));
        Assert.False(await db.CacheDeleteAsync(ns, "greeting"));

        var big = RandomNumberGenerator.GetBytes(100 * 1024);
        await db.CacheSetAsync(ns, "big", big);
        Assert.Equal(big, await db.CacheGetAsync(ns, "big"));
    }

    [LiveFact]
    public async Task Sql_writes_are_read_back_exactly_and_bad_statements_raise()
    {
        await using var db = await _server.ConnectAsync();
        var t = Names.Unique("e2e_users");
        await db.ExecuteAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, name TEXT)");
        var ins = await db.ExecuteAsync($"INSERT INTO {t} VALUES (1, 'ada')");
        Assert.Equal(1, ins.Data.GetProperty("Json").GetProperty("rows_affected").GetInt64());
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (2, 'grace')");
        var rows = await db.QueryAsync($"SELECT id, name FROM {t} ORDER BY id");
        Assert.Equal(new[] { "id", "name" }, rows.Columns);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "1", "ada" }, rows[0]);
        Assert.Equal(new[] { "2", "grace" }, rows[1]);
        Assert.Equal("grace", rows.Dicts()[1]["name"]);

        await Assert.ThrowsAsync<TriCoreException>(() => db.ExecuteAsync("THIS IS NOT VALID SQL AT ALL"));
        await Assert.ThrowsAsync<TriCoreException>(() => db.QueryAsync($"INSERT INTO {t} VALUES (3, 'x')"));
        Assert.Equal("2", (await db.QueryAsync($"SELECT COUNT(*) FROM {t}"))[0][0]);
    }

    [LiveFact]
    public async Task Two_connections_issue_different_request_ids()
    {
        await using var a = await _server.ConnectAsync();
        await using var b = await _server.ConnectAsync();
        var ra = await a.RequestAsync(SelectOne());
        var rb = await b.RequestAsync(SelectOne());
        Assert.NotEqual(ra.RequestId, rb.RequestId);
        Assert.StartsWith("dotnet-", ra.RequestId);
        Assert.Equal(a.LastRequestId, ra.RequestId);
    }

    [LiveFact]
    public async Task Six_concurrent_pooled_requests_reach_the_server_under_six_ids()
    {
        await using var pool = _server.NewPool(6);
        var ids = new ConcurrentBag<string>();
        var ready = new CountdownEvent(6);
        var tasks = Enumerable.Range(0, 6).Select(_ => pool.UseAsync(async db =>
        {
            ready.Signal();
            var resp = await db.RequestAsync(SelectOne());
            ids.Add(resp.RequestId);
        }, TimeSpan.FromSeconds(20))).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(6, ids.Distinct().Count());
    }

    [LiveFact]
    public async Task A_not_implemented_status_is_a_failure()
    {
        await using var db = await _server.ConnectAsync();
        var e = await Assert.ThrowsAsync<TriCoreException>(() =>
            db.RequestAsync(new JsonObject { ["Admin"] = JsonValue.Create("RebalanceStatus") }));
        Assert.Contains("not_implemented", e.Message);
    }

    [LiveFact]
    public async Task A_server_error_does_not_retire_a_pooled_connection()
    {
        await using var pool = _server.NewPool(1);
        await Assert.ThrowsAsync<TriCoreException>(() =>
            pool.UseAsync(db => db.QueryAsync("SELECT * FROM no_such_table_xyz")));
        var stats = pool.GetStats();
        Assert.Equal(1, stats.Idle);
        Assert.Equal(1, stats.Created);
    }

    [LiveFact]
    public async Task Pool_is_lazy_reuses_connections_and_stays_bounded_under_load()
    {
        var t = Names.Unique("pool");
        await using (var admin = await _server.ConnectAsync())
            await admin.ExecuteAsync($"CREATE TABLE {t} (id INT PRIMARY KEY)");

        await using (var pool = _server.NewPool(4))
        {
            Assert.Equal(0, pool.GetStats().Created);
            TriCoreClient? first = null, second = null;
            await pool.UseAsync(async c => { first = c; await c.PingAsync(); });
            Assert.Equal(new Pool.Stats(4, 1, 1, 0), pool.GetStats());
            await pool.UseAsync(c => { second = c; return Task.CompletedTask; });
            Assert.Same(first, second);
            Assert.Equal(1, pool.GetStats().Created);
        }

        const int size = 3, tasks = 20;
        await using (var pool = _server.NewPool(size))
        {
            var held = new ConcurrentDictionary<TriCoreClient, bool>(ReferenceEqualityComparer.Instance);
            int violations = 0, active = 0, peak = 0;
            await Task.WhenAll(Enumerable.Range(0, tasks).Select(id => pool.UseAsync(async conn =>
            {
                if (!held.TryAdd(conn, true)) Interlocked.Increment(ref violations);
                var now = Interlocked.Increment(ref active);
                int seen;
                do { seen = Volatile.Read(ref peak); } while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);
                try
                {
                    await conn.ExecuteAsync($"INSERT INTO {t} VALUES (?)", new object?[] { id });
                    await Task.Delay(20);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                    held.TryRemove(conn, out _);
                }
            }, TimeSpan.FromSeconds(10))));
            Assert.Equal(0, violations);
            Assert.Equal(size, peak);
            Assert.True(pool.GetStats().Created <= size);
        }

        await using var check = await _server.ConnectAsync();
        Assert.Equal(tasks.ToString(), (await check.QueryAsync($"SELECT COUNT(*) FROM {t}"))[0][0]);
    }

    [LiveFact]
    public async Task An_exhausted_pool_times_out_instead_of_growing()
    {
        await using var pool = _server.NewPool(1);
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = pool.UseAsync(async _ =>
        {
            holding.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<PoolTimeoutException>(() =>
            pool.UseAsync(c => c.PingAsync(), TimeSpan.FromMilliseconds(400)));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"timeout took {sw.ElapsedMilliseconds}ms");
        release.SetResult();
        await holder;
        Assert.Equal(1, pool.GetStats().Created);
    }

    [LiveFact]
    public async Task A_closed_connection_refuses_further_use()
    {
        var db = await _server.ConnectAsync();
        await db.DisposeAsync();
        var e = await Assert.ThrowsAsync<TriCoreException>(() => db.QueryAsync("SELECT 1"));
        Assert.Contains("closed", e.Message);
    }

    [LiveFact]
    public async Task Connecting_to_a_closed_port_fails()
    {
        var ex = await Record.ExceptionAsync(() => TriCoreClient.ConnectAsync("127.0.0.1", 1, "x", "y"));
        Assert.NotNull(ex);
    }

    private static JsonObject SelectOne() =>
        new() { ["Sql"] = new JsonObject { ["Query"] = new JsonObject { ["sql"] = "SELECT 1" } } };
}
