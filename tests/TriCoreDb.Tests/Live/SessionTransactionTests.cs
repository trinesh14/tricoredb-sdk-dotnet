using TriCoreDb.Tests.Support;

namespace TriCoreDb.Tests.Live;

/// <summary>Session transactions against a real server. Each test owns its own table.</summary>
[Collection("live")]
[Trait("Category", "Live")]
public class SessionTransactionTests
{
    private readonly TriCoreServer _server;

    public SessionTransactionTests(LiveServer live) => _server = live.Server;

    private async Task<(TriCoreClient Db, string Table)> SetupAsync()
    {
        var db = await _server.ConnectAsync();
        Assert.True(db.SessionTxnGranted, $"the server granted SESSION_TXN (granted={db.GrantedFeatures})");
        var table = Names.Unique("txn");
        await db.ExecuteAsync($"CREATE TABLE {table} (id INT PRIMARY KEY, v INT)");
        return (db, table);
    }

    private static async Task<long> CountAsync(TriCoreClient db, string table) =>
        long.Parse((await db.QueryAsync($"SELECT COUNT(*) FROM {table}"))[0][0]);

    [LiveFact]
    public async Task Rollback_discards_the_block()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        var began = await db.BeginAsync();
        Assert.Equal("began", began.Outcome);
        Assert.True(db.InTransaction);
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (1, 100)");
        Assert.Equal(1, await CountAsync(db, t));
        var rolled = await db.RollbackAsync();
        Assert.Equal("rolled_back", rolled.Outcome);
        Assert.Equal(1, rolled.DiscardedWrites);
        Assert.False(db.InTransaction);
        Assert.Equal(0, await CountAsync(db, t));
    }

    [LiveFact]
    public async Task Commit_persists_and_another_connection_sees_it()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        await using var other = await _server.ConnectAsync();
        await db.BeginAsync();
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (?, ?)", new object?[] { 2, 200 });
        Assert.Equal(0, await CountAsync(other, t));
        var committed = await db.CommitAsync();
        Assert.Equal("committed", committed.Outcome);
        Assert.Equal(1, committed.CommittedWrites);
        Assert.False(db.InTransaction);
        var rows = await other.QueryAsync($"SELECT id, v FROM {t}");
        Assert.Equal(new[] { "2", "200" }, rows[0]);
    }

    [LiveFact]
    public async Task One_request_transaction_still_commits_on_a_session_capable_connection()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        var result = await db.TransactionAsync(
            new SqlStatement($"INSERT INTO {t} VALUES (?, ?)", 20, 2000),
            new SqlStatement($"INSERT INTO {t} VALUES (?, ?)", 21, 2100));
        Assert.Equal("committed", result.Outcome);
        Assert.Equal(2, result.CommittedWrites);
        Assert.False(db.InTransaction);
        Assert.Equal(2, await CountAsync(db, t));
    }

    [LiveFact]
    public async Task An_error_inside_the_block_aborts_it_until_it_ends()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        await db.BeginAsync();
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (3, 300)");
        await Assert.ThrowsAsync<TriCoreException>(() => db.ExecuteAsync("INSERT INTO no_such_table VALUES (1, 1)"));
        var next = await Assert.ThrowsAsync<TriCoreException>(() => db.ExecuteAsync($"INSERT INTO {t} VALUES (4, 400)"));
        Assert.Contains("aborted", next.Message);
        Assert.Contains("ROLLBACK", next.Message);
        Assert.True(db.InTransaction);
        var commit = await Assert.ThrowsAsync<TriCoreException>(() => db.CommitAsync());
        Assert.Contains("`COMMIT` is refused", commit.Message);
        Assert.Contains("rolled back", commit.Message);
        Assert.False(db.InTransaction);
        Assert.Equal(0, await CountAsync(db, t));
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (9, 900)");
        Assert.Equal(1, await CountAsync(db, t));
    }

    [LiveFact]
    public async Task Another_connection_cannot_see_or_commit_the_block()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        await using var other = await _server.ConnectAsync();
        await db.BeginAsync();
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (5, 500)");
        Assert.Equal(1, await CountAsync(db, t));
        Assert.Equal(0, await CountAsync(other, t));
        var e = await Assert.ThrowsAsync<TriCoreException>(() => other.CommitAsync());
        Assert.Contains("`COMMIT` is refused", e.Message);
        Assert.Contains("no transaction is open", e.Message);
        Assert.Equal(0, await CountAsync(other, t));
        await db.CommitAsync();
        Assert.Equal(1, await CountAsync(other, t));
    }

    [LiveFact]
    public async Task A_nested_begin_is_refused_and_the_block_survives()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        await db.BeginAsync();
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (6, 600)");
        var e = await Assert.ThrowsAsync<TriCoreException>(() => db.BeginAsync());
        Assert.Contains("`BEGIN` is refused", e.Message);
        Assert.Contains("already open", e.Message);
        Assert.True(db.InTransaction);
        await db.RollbackAsync();
        Assert.Equal(0, await CountAsync(db, t));
    }

    [LiveFact]
    public async Task WithTransactionAsync_rolls_back_and_rethrows_or_commits_and_returns()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        var boom = new InvalidOperationException("application error after a write");
        var seen = await Assert.ThrowsAsync<InvalidOperationException>(() => db.WithTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync($"INSERT INTO {t} VALUES (7, 700)");
            throw boom;
        }));
        Assert.Same(boom, seen);
        Assert.False(db.InTransaction);
        Assert.Equal(0, await CountAsync(db, t));

        var value = await db.WithTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync($"INSERT INTO {t} VALUES (8, 800)");
            return "done";
        });
        Assert.Equal("done", value);
        await using var other = await _server.ConnectAsync();
        Assert.Equal("800", (await other.QueryAsync($"SELECT v FROM {t} WHERE id = 8"))[0][0]);
    }

    [LiveFact]
    public async Task Await_using_rolls_back_unless_committed()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        await using (var tx = await db.BeginTransactionAsync())
        {
            await db.ExecuteAsync($"INSERT INTO {t} VALUES (14, 1400)");
            Assert.True(tx.Open);
            Assert.Equal(1, await CountAsync(db, t));
        }
        Assert.False(db.InTransaction);
        Assert.Equal(0, await CountAsync(db, t));

        await using (var tx = await db.BeginTransactionAsync())
        {
            await db.ExecuteAsync($"INSERT INTO {t} VALUES (14, 1400)");
            await tx.CommitAsync();
        }
        Assert.False(db.InTransaction);
        Assert.Equal(1, await CountAsync(db, t));
    }

    [LiveFact]
    public async Task Without_SESSION_TXN_begin_refuses_by_name_and_a_raw_BEGIN_is_refused_by_the_server()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        await using var plain = await _server.ConnectAsync(TriCoreClient.Features & ~TriCoreClient.FeatureSessionTxn);
        Assert.False(plain.SessionTxnGranted);
        Assert.True(plain.ServerParamsGranted);
        var e = await Assert.ThrowsAsync<TriCoreException>(() => plain.BeginAsync());
        Assert.Contains("did not grant session transactions", e.Message);
        Assert.False(plain.InTransaction);

        var script = await plain.TransactionAsync(
            new SqlStatement($"INSERT INTO {t} VALUES (10, 1000)"),
            new SqlStatement($"INSERT INTO {t} VALUES (11, 1100)"));
        Assert.Equal("committed", script.Outcome);
        Assert.Equal(2, await CountAsync(db, t));

        var raw = await Assert.ThrowsAsync<TriCoreException>(() => plain.ExecuteAsync("BEGIN"));
        Assert.Contains("BEGIN; <statements>; COMMIT", raw.Message);
    }

    [LiveFact]
    public async Task A_pool_never_keeps_a_connection_mid_block()
    {
        var (db, t) = await SetupAsync();
        await using var _ = db;
        await using var pool = _server.NewPool(2);

        var leak = await Assert.ThrowsAsync<TriCoreException>(() => pool.UseAsync(async c =>
        {
            await c.BeginAsync();
            await c.ExecuteAsync($"INSERT INTO {t} VALUES (11, 1100)");
        }));
        Assert.Contains("still open", leak.Message);
        Assert.Contains("rolled back", leak.Message);
        Assert.Equal(0, await CountAsync(db, t));

        var appErr = new InvalidOperationException("callback failed mid-block");
        var propagated = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.UseAsync(async c =>
        {
            await c.BeginAsync();
            await c.ExecuteAsync($"INSERT INTO {t} VALUES (12, 1200)");
            throw appErr;
        }));
        Assert.Same(appErr, propagated);
        Assert.Equal(0, await CountAsync(db, t));

        var reused = await pool.UseAsync(async c => (InTxn: c.InTransaction, N: await CountAsync(c, t)));
        Assert.False(reused.InTxn);
        Assert.Equal(0, reused.N);
        Assert.Equal(1, pool.GetStats().Created);

        var viaPool = await pool.UseAsync(c => c.WithTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync($"INSERT INTO {t} VALUES (13, 1300)");
            return "pooled";
        }));
        Assert.Equal("pooled", viaPool);
        Assert.Equal(1, await CountAsync(db, t));
    }
}
