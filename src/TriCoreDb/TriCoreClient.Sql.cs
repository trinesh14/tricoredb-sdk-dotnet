using System.Text;
using System.Text.Json.Nodes;

namespace TriCoreDb;

// SQL conveniences layered on Execute/Query: parameter binding and transactions.

public sealed partial class TriCoreClient
{
    /// <summary>Run a write, binding <c>?</c> placeholders from <paramref name="args"/>
    /// <b>server-side</b>.</summary>
    /// <remarks>
    /// <para>The values travel beside the SQL as a typed array and the server substitutes
    /// them at value positions its grammar has already fixed. A value therefore cannot become
    /// syntax however it is spelled: a quote, a backslash, or an argument that is itself a
    /// complete SQL statement is stored as the text it is.</para>
    ///
    /// <para>This requires <see cref="TriCoreClient.FeatureServerParams"/>, negotiated at
    /// HELLO. When the server did not grant it — one too old to negotiate — this throws by
    /// name <b>before sending anything</b>, rather than falling back to rendering the values
    /// into the statement text. The fallback is what this driver used to do unconditionally,
    /// and doing it silently would mean the same call binds on one connection and escapes on
    /// the next with nothing to tell them apart. A caller who genuinely wants the old
    /// behaviour asks for it by name with <see cref="SqlParams.Bind"/>.</para>
    ///
    /// <para>Empty <paramref name="args"/> needs no capability: with nothing to bind this is
    /// exactly the plain overload.</para>
    /// </remarks>
    public Task<Response> ExecuteAsync(
        string sql, object?[] args, string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(SqlOp("Exec", sql, args), database, cancellationToken);

    /// <summary>Run a read, binding <c>?</c> placeholders server-side.
    /// See <see cref="ExecuteAsync(string, object?[], string, CancellationToken)"/>.</summary>
    public async Task<Rows> QueryAsync(
        string sql, object?[] args, string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(SqlOp("Query", sql, args), database, cancellationToken)
            .ConfigureAwait(false);
        return RowsOf(resp);
    }

    /// <summary>Build a Query/Exec op, binding <paramref name="args"/> server-side.</summary>
    /// <remarks>
    /// Placeholder arity is deliberately left to the server: it counts <c>?</c> against the
    /// parameters it was given and refuses a mismatch by name, and it also understands
    /// <c>$n</c> placeholders, which a client-side scanner counting <c>?</c> would
    /// mis-report. One authority on what a statement's placeholders are is the point.
    /// </remarks>
    private JsonObject SqlOp(string kind, string sql, IReadOnlyList<object?>? args)
    {
        var body = new JsonObject { ["sql"] = sql };
        if (args is { Count: > 0 })
        {
            if (!ServerParamsGranted)
                throw new TriCoreException(ServerParamsNotGranted);
            body["params"] = SqlParams.Params(args);
        }
        return new JsonObject { ["Sql"] = new JsonObject { [kind] = body } };
    }

    /// <summary>The refusal for an ungranted <see cref="TriCoreClient.FeatureServerParams"/>,
    /// shared by every entry point that binds so one wording is searchable.</summary>
    internal const string ServerParamsNotGranted =
        "this server did not grant server-side parameters (SERVER_PARAMS is not in the granted " +
        "feature set), so this driver will not bind `?` placeholders on this connection; it will " +
        "not silently render the values into the statement text instead, because escaping and " +
        "binding are not the same guarantee. Upgrade the server, or call SqlParams.Bind(sql, args) " +
        "explicitly to accept client-side rendering";

    /// <summary>
    /// Run a pre-declared script atomically, in <b>one request</b>.
    /// </summary>
    /// <remarks>
    /// The server runs the <c>BEGIN ... COMMIT</c> script against an MVCC snapshot and
    /// flushes it as one atomic batch; <c>ROLLBACK</c>, or any error mid-script, discards
    /// the buffer. This is the right tool when every statement is known up front: one round
    /// trip, one replication event, and it works on <i>every</i> node — including the
    /// sharded and forwarding ones that withhold <see cref="TriCoreClient.FeatureSessionTxn"/>.
    ///
    /// <code>
    /// await db.TransactionAsync(
    ///     new SqlStatement("INSERT INTO t VALUES (?, ?)", 1, "ada"),
    ///     new SqlStatement("INSERT INTO t VALUES (?, ?)", 2, "bob"));
    /// </code>
    ///
    /// When a later statement depends on what an earlier one read, use
    /// <see cref="BeginAsync"/>/<see cref="CommitAsync"/>/<see cref="RollbackAsync"/> or
    /// <see cref="WithTransactionAsync{T}"/> instead.
    /// </remarks>
    /// <returns>the server's transaction summary: statement count, committed writes, outcome.</returns>
    public async Task<TransactionResult> TransactionAsync(
        IEnumerable<SqlStatement> statements, string database = "main", CancellationToken cancellationToken = default)
    {
        // The script keeps its `?` placeholders and the statements' arguments are
        // concatenated in statement order — which is exactly how the server binds a
        // multi-statement script, walking it left to right and consuming one parameter per
        // placeholder. So a transaction inherits the same guarantee a single bound
        // ExecuteAsync has, instead of pasting values into the text.
        var list = statements as IReadOnlyList<SqlStatement> ?? statements.ToList();
        var script = BuildScript(list, renderArgs: false);
        var args = ScriptArgs(list);
        var resp = args.Length == 0
            ? await ExecuteAsync(script, database, cancellationToken).ConfigureAwait(false)
            : await ExecuteAsync(script, args, database, cancellationToken).ConfigureAwait(false);
        return TransactionResult.Parse(Wire.Json(resp));
    }

    /// <summary>Run several statements atomically. Convenience overload.</summary>
    public Task<TransactionResult> TransactionAsync(params SqlStatement[] statements) =>
        TransactionAsync(statements, "main", CancellationToken.None);

    // -- session transactions ---------------------------------------------------------

    /// <summary>Open a session transaction on <b>this connection</b>.</summary>
    /// <remarks>
    /// <para>Every statement sent on this connection until <see cref="CommitAsync"/> or
    /// <see cref="RollbackAsync"/> runs inside it, at one snapshot, and is invisible to
    /// other connections until committed. The block belongs to this connection's socket: it
    /// cannot be committed from another connection, a pooled one included, and a dropped
    /// socket rolls it back.</para>
    ///
    /// <para>Requires the server to have granted <see cref="TriCoreClient.FeatureSessionTxn"/>
    /// in the handshake (<see cref="TriCoreClient.SessionTxnGranted"/>). If it did not — an
    /// older server, or a sharded / forwarding node — this throws by name <b>before sending
    /// anything</b>, rather than sending a BEGIN the server would run as a one-statement
    /// autocommit script; <see cref="TransactionAsync(IEnumerable{SqlStatement}, string, CancellationToken)"/>
    /// works everywhere.</para>
    ///
    /// <para>What the server enforces inside a block, in its own words when it happens: a
    /// failed statement aborts the block and every statement after it is refused until
    /// ROLLBACK; a second BEGIN is refused (no savepoints); DDL and multi-statement scripts
    /// are refused; a block left idle past the server's idle-in-transaction window (60 s by
    /// default) is rolled back and the connection closed; and a block that buffers more than
    /// <c>max_pending_writes_per_transaction</c> writes is aborted.</para>
    /// </remarks>
    /// <returns>the server's outcome, whose <c>Outcome</c> is <c>"began"</c>.</returns>
    public Task<TransactionResult> BeginAsync(
        string database = "main", CancellationToken cancellationToken = default)
    {
        if (!SessionTxnGranted)
        {
            throw new TriCoreException(
                "this server did not grant session transactions (SESSION_TXN is not in the granted " +
                "feature set), so BeginAsync()/CommitAsync()/RollbackAsync() cannot open a rollback " +
                "boundary on this connection; use TransactionAsync(...) to send the whole unit as " +
                "one `BEGIN; <statements>; COMMIT` request");
        }
        return TxnControlAsync("BEGIN", database, cancellationToken);
    }

    /// <summary>
    /// Commit the block opened by <see cref="BeginAsync"/>, durably and as one atomic batch.
    /// </summary>
    /// <remarks>
    /// A refusal is the server's own and ends the block either way: after a failed statement
    /// the server has already rolled it back and says so; a first-committer-wins conflict
    /// discards it.
    /// </remarks>
    public Task<TransactionResult> CommitAsync(
        string database = "main", CancellationToken cancellationToken = default) =>
        TxnControlAsync("COMMIT", database, cancellationToken);

    /// <summary>Discard the block opened by <see cref="BeginAsync"/>.</summary>
    public Task<TransactionResult> RollbackAsync(
        string database = "main", CancellationToken cancellationToken = default) =>
        TxnControlAsync("ROLLBACK", database, cancellationToken);

    /// <summary>
    /// <see cref="BeginAsync"/>, run <paramref name="body"/>, then <see cref="CommitAsync"/> —
    /// or <see cref="RollbackAsync"/> and rethrow if <paramref name="body"/> throws (the
    /// original exception is what propagates).
    /// </summary>
    /// <remarks>
    /// <paramref name="body"/> must issue its statements on the connection it is given — this
    /// one — and only this one. A statement on any other connection, a pooled one included, is
    /// outside the block: the server binds the transaction to this socket and refuses COMMIT
    /// from any other by name.
    /// </remarks>
    public async Task<T> WithTransactionAsync<T>(
        Func<TriCoreClient, Task<T>> body,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        Wire.RequireNotNull(body, nameof(body));
        await BeginAsync(database, cancellationToken).ConfigureAwait(false);
        T result;
        try
        {
            result = await body(this).ConfigureAwait(false);
        }
        catch
        {
            if (InTransaction)
            {
                try
                {
                    // The caller's exception is the one to report. If the socket is already
                    // gone the server has rolled the block back on its own.
                    await RollbackAsync(database, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // swallowed deliberately: see above
                }
            }
            throw;
        }
        await CommitAsync(database, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc cref="WithTransactionAsync{T}"/>
    public Task WithTransactionAsync(
        Func<TriCoreClient, Task> body,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        Wire.RequireNotNull(body, nameof(body));
        return WithTransactionAsync<object?>(async c =>
        {
            await body(c).ConfigureAwait(false);
            return null;
        }, database, cancellationToken);
    }

    /// <summary>
    /// A session transaction as an <c>await using</c> scope: disposing rolls the block back
    /// unless it was committed.
    /// </summary>
    /// <remarks>
    /// <code>
    /// await using (var tx = await db.BeginTransactionAsync())
    /// {
    ///     await db.ExecuteAsync("INSERT INTO t VALUES (1, 'ada')");
    ///     await tx.CommitAsync();
    /// }   // an early return, a `break`, or a thrown exception rolls back here
    /// </code>
    /// </remarks>
    public async Task<TriCoreTransaction> BeginTransactionAsync(
        string database = "main", CancellationToken cancellationToken = default)
    {
        await BeginAsync(database, cancellationToken).ConfigureAwait(false);
        return new TriCoreTransaction(this, database);
    }

    /// <summary>
    /// Transaction control travels as Exec: the server authorizes it as a write and refuses
    /// it on Query.
    /// </summary>
    /// <remarks>
    /// <c>_txnOpen</c> follows what the server answered. Every COMMIT/ROLLBACK reply — a
    /// refusal included — means the block is over, because the server ends it either way; the
    /// one exception is a request that never left this process, which is what
    /// <see cref="RequestNotSentException"/> names.
    /// </remarks>
    private async Task<TransactionResult> TxnControlAsync(
        string keyword, string database, CancellationToken cancellationToken)
    {
        Response resp;
        try
        {
            resp = await ExecuteAsync(keyword, database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (keyword != "BEGIN" && e is not RequestNotSentException)
                _txnOpen = false;
            throw;
        }
        _txnOpen = keyword == "BEGIN";
        return TransactionResult.Parse(Wire.Json(resp));
    }

    /// <summary>
    /// Assemble <c>BEGIN; …; COMMIT</c> from a list of statements.
    ///
    /// Caller-supplied transaction control is refused rather than passed through:
    /// nesting a second <c>BEGIN</c>, or committing early, changes what the script means
    /// in a way the caller almost certainly did not intend.
    /// </summary>
    internal static string BuildScript(IEnumerable<SqlStatement> statements) =>
        BuildScript(statements, renderArgs: true);

    /// <summary>Every statement's arguments, concatenated in statement order — the flat,
    /// positional list the server binds a multi-statement script against.</summary>
    internal static object?[] ScriptArgs(IEnumerable<SqlStatement> statements)
    {
        var args = new List<object?>();
        foreach (var s in statements)
            if (s.Args is { Length: > 0 })
                args.AddRange(s.Args);
        return args.ToArray();
    }

    private static string BuildScript(IEnumerable<SqlStatement> statements, bool renderArgs)
    {
        Wire.RequireNotNull(statements, nameof(statements));
        var parts = new List<string>();
        foreach (var s in statements)
        {
            var text = (renderArgs ? s.Render() : s.Sql).Trim().TrimEnd(';').Trim();
            if (text.Length == 0)
                throw new ArgumentException("each transaction statement must be non-empty SQL", nameof(statements));
            var first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
            if (first is "BEGIN" or "START" or "COMMIT" or "ROLLBACK")
                throw new ArgumentException(
                    $"TransactionAsync brackets the script itself — remove the `{first}` statement", nameof(statements));
            parts.Add(text);
        }
        if (parts.Count == 0)
            throw new ArgumentException("a transaction needs at least one statement", nameof(statements));

        var sb = new StringBuilder("BEGIN; ");
        sb.Append(string.Join("; ", parts));
        sb.Append("; COMMIT");
        return sb.ToString();
    }
}

/// <summary>
/// An open session transaction, scoped to an <c>await using</c> block.
/// </summary>
/// <remarks>
/// <para><see cref="DisposeAsync"/> rolls the block back unless <see cref="CommitAsync"/> or
/// <see cref="RollbackAsync"/> already ended it — so an early return, a <c>break</c> or a
/// thrown exception can never leave a block open on the connection. That is the whole point
/// of the type: the leak it prevents is a connection returned to a pool mid-block, which is
/// how one request's uncommitted writes become another's.</para>
///
/// <para>Statements go through the <b>connection</b>, not through this handle: the server
/// binds the block to the socket, so <see cref="Connection"/> — the very client
/// <c>BeginTransactionAsync</c> was called on — is the only place they belong.</para>
/// </remarks>
public sealed class TriCoreTransaction : IAsyncDisposable
{
    private readonly string _database;
    private bool _ended;

    internal TriCoreTransaction(TriCoreClient connection, string database)
    {
        Connection = connection;
        _database = database;
    }

    /// <summary>The connection this block is bound to. Every statement in it goes here.</summary>
    public TriCoreClient Connection { get; }

    /// <summary>Whether the block is still open.</summary>
    public bool Open => !_ended && Connection.InTransaction;

    /// <summary>Commit the block. Disposing afterwards is a no-op.</summary>
    public async Task<TransactionResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        var r = await Connection.CommitAsync(_database, cancellationToken).ConfigureAwait(false);
        _ended = true;
        return r;
    }

    /// <summary>Discard the block. Disposing afterwards is a no-op.</summary>
    public async Task<TransactionResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        var r = await Connection.RollbackAsync(_database, cancellationToken).ConfigureAwait(false);
        _ended = true;
        return r;
    }

    /// <summary>
    /// Roll back unless the block already ended.
    /// </summary>
    /// <remarks>
    /// Best-effort by necessity: this runs while an exception may already be propagating, and
    /// a rollback failure must not replace the caller's cause. A connection whose rollback
    /// fails is one whose socket is going away anyway, and the server rolls an abandoned block
    /// back on its own.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_ended) return;
        _ended = true;
        if (!Connection.InTransaction) return;
        try
        {
            await Connection.RollbackAsync(_database, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // see above
        }
    }
}

/// <summary>One statement in a transaction script, with optional bound arguments.</summary>
public readonly record struct SqlStatement(string Sql, params object?[] Args)
{
    internal string Render() =>
        Args is null || Args.Length == 0 ? Sql : SqlParams.Bind(Sql, Args);
}

/// <summary>What the server did with a transaction script.</summary>
/// <param name="Statements">how many statements ran.</param>
/// <param name="CommittedWrites">writes flushed as one atomic batch.</param>
/// <param name="DiscardedWrites">writes thrown away by a rollback.</param>
/// <param name="Outcome">"committed" or "rolled_back".</param>
public readonly record struct TransactionResult(
    long Statements, long CommittedWrites, long DiscardedWrites, string Outcome)
{
    internal static TransactionResult Parse(System.Text.Json.JsonElement e) => new(
        Wire.NumOr(e, "statements", 0),
        Wire.NumOr(e, "committed_writes", 0),
        Wire.NumOr(e, "discarded_writes", 0),
        Wire.OptStr(e, "transaction") ?? "unknown");
}
