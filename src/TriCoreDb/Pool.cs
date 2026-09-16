using System.Net.Sockets;

namespace TriCoreDb;

/// <summary>
/// A pool of <see cref="TriCoreClient"/> connections, safe under concurrent async use.
///
/// <para><b>Why this exists.</b> A <see cref="TriCoreClient"/> is a single request/response
/// stream: two tasks sharing one would interleave frames and read each other's replies. So
/// concurrency needs one connection per concurrent caller, and opening one per request costs a
/// TCP connect plus HELLO and AUTH round trip every time.</para>
///
/// <para><see cref="UseAsync"/> runs a callback with a connection that is exclusively the
/// callback's for its duration. That is deliberately a callback rather than a Rent/Return pair
/// returning a <see cref="TriCoreClient"/>: a returned handle is something a caller can retain
/// and share across tasks, which is exactly the corruption this type exists to prevent.</para>
///
/// <code>
/// await using var pool = new Pool("127.0.0.1", 18427, user: "admin", secret: "pw", size: 8);
/// await pool.UseAsync(async db =&gt; await db.ExecuteAsync("INSERT INTO t VALUES (1, 'ada')"));
/// </code>
/// </summary>
public sealed class Pool : IAsyncDisposable, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _secret;
    private readonly string _clientName;
    private readonly int _size;
    // Carried so every pooled connection gets the same TLS treatment; without it a pool
    // against a TLS server would fail to connect after the first handout, or worse,
    // quietly come up as plaintext.
    private readonly TlsOptions? _tls;

    // Holding a slot is what bounds the pool: a slot is taken before a connection is even
    // created, so the number in flight can never exceed `_size` regardless of how creation
    // and release interleave across tasks.
    private readonly SemaphoreSlim _slots;

    private readonly object _lock = new();
    // Connections are created lazily: a pool sized for peak load should not pay for peak
    // load at startup.
    private readonly Stack<TriCoreClient> _idle = new();
    private int _created;
    private bool _closed;

    public Pool(
        string host = "127.0.0.1",
        int port = TriCoreClient.DefaultPort,
        string? user = null,
        string? secret = null,
        int size = 8,
        string clientName = "tricoredb-dotnet-pool",
        TlsOptions? tls = null)
    {
        if (size < 1)
            throw new ArgumentException($"pool size must be >= 1, got {size}", nameof(size));
        _tls = tls;
        _host = host;
        _port = port;
        _user = user;
        _secret = secret;
        _size = size;
        _clientName = clientName;
        _slots = new SemaphoreSlim(size, size);
    }

    public int Size => _size;

    /// <summary>(Size, Created, Idle, InUse) -- for tests and diagnostics.</summary>
    public readonly record struct Stats(int Size, int Created, int Idle, int InUse);

    public Stats GetStats()
    {
        lock (_lock)
        {
            return new Stats(_size, _created, _idle.Count, _created - _idle.Count);
        }
    }

    /// <summary>Borrow a connection for the duration of <paramref name="action"/>.</summary>
    public Task UseAsync(
        Func<TriCoreClient, Task> action,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => UseAsync<object?>(async c =>
        {
            await action(c).ConfigureAwait(false);
            return null;
        }, timeout, cancellationToken);

    /// <summary>
    /// Borrow a connection for the duration of <paramref name="action"/>, returning its result.
    ///
    /// Blocks up to <paramref name="timeout"/> (default 10s) if every connection is in use, then
    /// throws <see cref="PoolTimeoutException"/> rather than silently growing past the pool's
    /// size -- an unbounded pool under load does not fix overload, it relocates it to the
    /// server. Honours <paramref name="cancellationToken"/> too: a cancelled wait surfaces as
    /// <see cref="OperationCanceledException"/>, distinct from a plain timeout, since the two
    /// mean different things to a caller (caller gave up vs. pool genuinely exhausted).
    /// </summary>
    public async Task<T> UseAsync<T>(
        Func<TriCoreClient, Task<T>> action,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(10);
        if (_closed) throw new TriCoreException("pool is closed");

        bool acquired = await _slots.WaitAsync(budget, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            throw new PoolTimeoutException(
                $"no pooled connection available within {budget.TotalMilliseconds}ms (size={_size}); "
                + "raise size or shorten your work");
        }

        TriCoreClient? conn;
        lock (_lock)
        {
            if (_closed)
            {
                _slots.Release();
                throw new TriCoreException("pool is closed");
            }
            conn = _idle.Count > 0 ? _idle.Pop() : null;
        }

        if (conn is null)
        {
            try
            {
                conn = await TriCoreClient.ConnectAsync(_host, _port, _user, _secret, _clientName, cancellationToken, _tls)
                    .ConfigureAwait(false);
            }
            catch
            {
                _slots.Release(); // give the slot back, or the pool permanently shrinks
                throw;
            }
            lock (_lock) { _created++; }
        }

        bool broken = false;
        bool leftOpen = false;
        T result;
        try
        {
            result = await action(conn).ConfigureAwait(false);
            if (conn.InTransaction)
            {
                // Rolled back here rather than returned to the pool mid-block: the next
                // borrower would otherwise be writing into somebody else's transaction.
                // Reported after the connection is released, so the slot comes back.
                leftOpen = true;
                broken = !await AbandonBlockAsync(conn).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            // A transport/protocol failure may leave the stream mid-frame; handing it to the
            // next caller would give them someone else's bytes. A server-side error (bad SQL,
            // say) is a perfectly healthy response and must NOT retire the connection, or the
            // pool churns on ordinary application errors.
            broken = IsBroken(e) || conn.IsPoisoned;
            if (!broken && conn.InTransaction)
            {
                // The caller's exception is the one to propagate; the block still has to go,
                // and the connection is retired if it will not.
                broken = !await AbandonBlockAsync(conn).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            // Runs even if `action` threw, or the slot never comes back and the pool
            // eventually deadlocks every caller.
            Release(conn, broken);
        }

        if (leftOpen)
        {
            throw new TriCoreException(
                "the callback returned with a session transaction still open on the pooled "
                + "connection; it has been rolled back rather than returned to the pool "
                + "mid-block. CommitAsync() or RollbackAsync() inside the callback, or use "
                + "db.WithTransactionAsync(...) / db.BeginTransactionAsync().");
        }
        return result;
    }

    /// <summary>
    /// Roll back a block a pooled callback left open. False when the connection must be
    /// retired instead — a rollback this driver could not complete leaves a session whose
    /// state no next borrower can assume anything about.
    /// </summary>
    private static async Task<bool> AbandonBlockAsync(TriCoreClient conn)
    {
        try
        {
            await conn.RollbackAsync("main", CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Release(TriCoreClient conn, bool broken)
    {
        // Never idle a connection mid-block, whatever path led here.
        if (conn.InTransaction) broken = true;
        bool shouldDispose = false;
        lock (_lock)
        {
            if (broken || _closed)
            {
                _created--;
                shouldDispose = true;
            }
            else
            {
                _idle.Push(conn);
            }
        }
        _slots.Release();
        if (shouldDispose)
        {
            conn.Dispose();
        }
    }

    /// <summary>
    /// Whether <paramref name="e"/> means the stream can no longer be trusted to be
    /// frame-aligned. <see cref="ProtocolException"/> is a direct framing failure;
    /// <see cref="SocketException"/>/<see cref="IOException"/>/<see cref="ObjectDisposedException"/>
    /// are transport failures. A plain <see cref="TriCoreException"/> from a server-reported
    /// application error (bad SQL) is a healthy connection and must not match here.
    /// </summary>
    /// <remarks>
    /// The connection's own <see cref="TriCoreClient.IsPoisoned"/> is consulted alongside
    /// this at the call site, which is the part that cannot fall out of date: a driver
    /// that refused a frame, timed out, or had its read cancelled knows it is
    /// un-resynchronisable whether or not the exception type happens to be listed here.
    /// </remarks>
    private static bool IsBroken(Exception e) =>
        e is ProtocolException
        || e is TriCoreTimeoutException
        || e is OperationCanceledException
        || e is SocketException
        || e is IOException
        || e is ObjectDisposedException;

    /// <summary>Closes every idle connection. In-use ones are closed as their UseAsync returns.</summary>
    public async ValueTask DisposeAsync()
    {
        List<TriCoreClient> toClose;
        lock (_lock)
        {
            _closed = true;
            toClose = new List<TriCoreClient>(_idle);
            _created -= _idle.Count;
            _idle.Clear();
        }
        foreach (var c in toClose)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort teardown */ }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
