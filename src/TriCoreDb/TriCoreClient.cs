using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriCoreDb;

/// <summary>
/// A connection to a TriCoreDB server, speaking the native `tricore` wire protocol
/// (see docs/protocol/WIRE_REFERENCE.md). No PostgreSQL/MongoDB wire compatibility layer
/// is involved and there are no dependencies beyond the BCL.
///
/// Not thread-safe: a connection is one request/response stream, so sharing it across
/// tasks/threads interleaves frames. Use one client per logical caller.
/// </summary>
public sealed partial class TriCoreClient : IDisposable, IAsyncDisposable
{
    private const string Protocol = "tricore";
    private const int ProtocolVersion = 1;
    public const int DefaultPort = 8427;

    /// <summary>
    /// This SDK's own version, so a consumer can pin one (V2 P2).
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <c>ProtocolVersion</c>, which is the protocol
    /// major. The two move for different reasons, and collapsing them would make
    /// one a silent proxy for the other.
    /// </remarks>
    public const string SdkVersion = "0.1.0";

    /// <summary>
    /// This SDK understands a request-scoped correlation id, which the server
    /// joins to its access log, audit trail and cancel registry.
    /// </summary>
    public const ulong FeatureCorrelationId = 1UL;

    /// <summary>
    /// The server binds <c>?</c> placeholders to a typed parameter array carried beside
    /// the statement, instead of this driver rendering the values into the SQL text
    /// before sending it.
    /// </summary>
    /// <remarks>
    /// <para>This is the difference between escaping and binding. Client-side rendering has
    /// to reproduce the server's literal syntax exactly for every type, and any mismatch is
    /// either a wrong value or a parse error; a bound parameter is substituted at a value
    /// position the grammar has already fixed, so a value can never become syntax however
    /// it is spelled.</para>
    ///
    /// <para>The argument-taking <c>ExecuteAsync</c>/<c>QueryAsync</c> overloads require it
    /// and refuse by name when it was not granted, rather than falling back to the rendering
    /// they used to do — a silent downgrade from binding to escaping is exactly the failure
    /// this bit exists to make visible.</para>
    /// </remarks>
    public const ulong FeatureServerParams = 1UL << 1;

    /// <summary>
    /// Session-scoped transactions: BEGIN, the statements, and COMMIT/ROLLBACK sent as
    /// separate requests on one connection, with a real rollback boundary between them.
    /// </summary>
    /// <remarks>
    /// Granted per connection: a sharded or forwarding node may withhold it, and a server
    /// too old to negotiate never grants it. <see cref="BeginAsync"/> refuses by name when
    /// it was not granted, rather than sending a BEGIN the server would run as a
    /// one-statement autocommit script.
    /// </remarks>
    public const ulong FeatureSessionTxn = 1UL << 2;

    /// <summary>
    /// Optional protocol capabilities this build understands, sent in HELLO as a
    /// bitmap (V2 P2). The server replies with the subset it granted; a server
    /// too old to negotiate omits the field, which reads as 0.
    /// </summary>
    public const ulong Features = FeatureCorrelationId | FeatureServerParams | FeatureSessionTxn;

    /// <summary>
    /// The capabilities this connection negotiated at HELLO. Zero before a
    /// handshake, and zero against a server too old to negotiate.
    /// </summary>
    public ulong GrantedFeatures { get; private set; }

    /// <summary>
    /// Whether the server granted <see cref="FeatureSessionTxn"/> on this connection —
    /// the bit <see cref="BeginAsync"/> requires.
    /// </summary>
    public bool SessionTxnGranted => (GrantedFeatures & FeatureSessionTxn) != 0;

    /// <summary>
    /// Whether the server granted <see cref="FeatureServerParams"/> on this connection —
    /// the bit the argument-taking <c>ExecuteAsync</c>/<c>QueryAsync</c> overloads require.
    /// </summary>
    /// <remarks>
    /// False against a server too old to negotiate. Those overloads refuse by name in that
    /// case rather than rendering the values into the statement text, so a caller never
    /// binds server-side on one connection and client-side on the next without being told.
    /// </remarks>
    public bool ServerParamsGranted => (GrantedFeatures & FeatureServerParams) != 0;

    /// <summary>
    /// True while a <see cref="BeginAsync"/> block is open on this connection.
    /// </summary>
    /// <remarks>
    /// A closed or poisoned connection has no transaction: the server rolls one back the
    /// moment the socket goes.
    /// </remarks>
    public bool InTransaction => _txnOpen && !_closed;

    // Wire tags — see WIRE_REFERENCE.md. Note the asymmetry: HELLO (0) is answered by
    // HELLO_OK (8), not HELLO; AUTH (1) by AUTH_OK (9). Waiting for the same tag you sent hangs.
    private const byte TagHello = 0;
    private const byte TagAuth = 1;
    private const byte TagRequest = 2;
    private const byte TagResponse = 3;
    private const byte TagPing = 4;
    private const byte TagPong = 5;
    private const byte TagError = 6;
    private const byte TagClose = 7;
    private const byte TagHelloOk = 8;
    private const byte TagAuthOk = 9;
    private const byte TagBye = 10;
    private const byte TagCancel = 11;
    private const byte TagCancelOk = 12;

    // [version: u8][tag: u8][payload_len: u32 big-endian] = 6 bytes.
    private const int HeaderSize = 6;

    /// <summary>
    /// The protocol's payload ceilings, mirroring
    /// <c>crates/tricore_protocol/src/constants/mod.rs</c>.
    /// </summary>
    /// <remarks>
    /// A driver that trusts a declared length is six header bytes away from an
    /// unbounded allocation: the length is a <c>u32</c>, so any peer — hostile,
    /// broken, or simply the wrong port — could commit this client to a 4 GiB
    /// buffer and a read that never ends. The server refuses these lengths on its
    /// side; the client must refuse them on its own, because the server is not the
    /// only thing this socket can be connected to.
    ///
    /// Control frames take the far tighter ceiling (C-40): every frame that is not
    /// <c>REQUEST</c>/<c>RESPONSE</c> carries a small JSON document or nothing, and
    /// the two an <i>unauthenticated</i> peer may send are both in that set.
    /// </remarks>
    public const int MaxFrameSize = 16 * 1024 * 1024;

    /// <inheritdoc cref="MaxFrameSize"/>
    public const int MaxControlFrameSize = 64 * 1024;

    /// <summary>
    /// The highest frame-header format version this driver can read.
    /// </summary>
    /// <remarks>
    /// The header's first byte is a format version. It used to be read and thrown
    /// away here, so a peer speaking a future frame layout was parsed as though it
    /// spoke this one. The server refuses an unknown version by name; so does this.
    /// </remarks>
    public const byte MaxSupportedFrameVersion = 1;

    /// <summary>How long <see cref="DisposeAsync"/> waits for a BYE before dropping the socket.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The default budget for the connect phase (TCP, TLS, HELLO, AUTH).</summary>
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The payload ceiling for one tag. An <b>unknown</b> tag takes the tighter
    /// ceiling deliberately: a tag this build cannot name is a tag whose payload
    /// size it cannot vouch for, and the safe direction to be wrong in is "too small".
    /// </summary>
    private static int MaxPayloadFor(byte tag) =>
        tag == TagRequest || tag == TagResponse ? MaxFrameSize : MaxControlFrameSize;

    /// <summary>
    /// A <c>request_id</c> prefix unique to one connection.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a bare counter is a correctness bug, not a cosmetic one.</b> Ids used
    /// to be <c>dotnet-1</c>, <c>dotnet-2</c>, … restarting at zero <b>per connection</b>.
    /// The server's cancel registry is keyed by <c>request_id</c> scoped to the
    /// <i>principal</i>, and <c>ExecutionRegistry::cancel</c>
    /// (<c>crates/tricore_core/src/execution/registry.rs</c>) stops <b>every</b> entry
    /// that matches — so a <see cref="Pool"/>, whose connections all authenticate as one
    /// principal, issued concurrent <c>dotnet-1</c>s and one <c>CancelAsync("dotnet-1")</c>
    /// stopped all of them. The Rust client and the Node and Python drivers carried the
    /// same defect and were fixed the same way.</para>
    ///
    /// <para>Uniqueness is the requirement, not unguessability: the registry treats the id
    /// as guessable by construction and scopes every lookup to the principal, so secrecy
    /// buys nothing. A pid, a process-start stamp and a monotonic counter give uniqueness
    /// across connections in a process and across processes on a host.</para>
    /// </remarks>
    private static readonly string ProcessStamp =
        (((ulong)DateTime.UtcNow.Ticks << 12) ^ (ulong)System.Diagnostics.Stopwatch.GetTimestamp()).ToString("x");

    private static int _connectionSeq;

    private static string NextConnectionPrefix() =>
        $"{Environment.ProcessId:x}{ProcessStamp}{Interlocked.Increment(ref _connectionSeq):x}";

    private readonly TcpClient _tcp;
    // Stream, not NetworkStream: over TLS this is an SslStream wrapping the socket.
    // Every read and write below goes through it, so the framing code is identical
    // either way.
    private readonly Stream _stream;
    private long _requestCounter;
    private readonly string _ridPrefix = NextConnectionPrefix();
    private bool _closed;

    // True from a successful BeginAsync until the block ends. See InTransaction.
    private bool _txnOpen;

    /// <summary>
    /// Set once this connection can no longer be trusted to be frame-aligned.
    /// </summary>
    /// <remarks>
    /// Throwing alone is not enough after a refused frame: the bytes the header
    /// declared are still queued behind it, and a length-prefixed stream has no
    /// resynchronisation point once the length is untrustworthy. So the socket is
    /// dropped and every later use fails with the same error.
    /// </remarks>
    private Exception? _fatal;

    // 1 between sending a frame and reading its reply. See ExchangeAsync.
    private int _inFlight;

    /// <summary>True when this connection negotiated TLS.</summary>
    public bool IsSecure { get; }

    /// <summary>
    /// True once a frame this driver refused, a read deadline, or a caller's own
    /// cancellation left the stream un-resynchronisable. A poisoned connection is
    /// closed and is never handed back to a <see cref="Pool"/>.
    /// </summary>
    public bool IsPoisoned => _fatal is not null;

    /// <summary>
    /// A client-side deadline on each steady-state read, or null (the default) for none.
    /// </summary>
    /// <remarks>
    /// Distinct from the connect budget, which bounds TCP + TLS + HELLO + AUTH and is
    /// cleared once the handshake completes — conflating the two is how the Python driver
    /// turned a documented 10 s <i>connect</i> timeout into an undocumented 10 s deadline
    /// on every query it would ever run. Distinct also from
    /// <see cref="RequestTimeout"/>, which makes the <b>server</b> stop; this one only
    /// stops the wait. Exceeding it raises <see cref="TriCoreTimeoutException"/> and
    /// poisons the connection.
    /// </remarks>
    public TimeSpan? ReadTimeout { get; set; }

    /// <summary>Set once AUTH_OK is received; null until then / if no auth was performed.</summary>
    public string? SessionId { get; private set; }

    private TriCoreClient(TcpClient tcp, Stream stream, bool isSecure)
    {
        _tcp = tcp;
        _stream = stream;
        IsSecure = isSecure;
    }

    // -- lifecycle -----------------------------------------------------------------------

    /// <summary>Connect, handshake, and (when <paramref name="user"/> is given) authenticate.
    /// <paramref name="tls"/> is null for plain TCP — on which <paramref name="secret"/> crosses
    /// the wire in the clear — or a <see cref="TlsOptions"/> to negotiate TLS first.</summary>
    /// <param name="connectTimeout">Bounds the whole connect phase — TCP, TLS, HELLO and
    /// AUTH — and is then <b>cleared</b>. Defaults to 10 s; <see cref="TimeSpan.Zero"/> or
    /// a negative value disables it. Without it an unroutable or black-holed address hangs
    /// this call for as long as the OS is willing to wait.</param>
    /// <param name="readTimeout">The steady-state read deadline armed once the handshake
    /// completes. Null (the default) means none — see <see cref="ReadTimeout"/>.</param>
    /// <param name="features">The capability bitmap announced in HELLO. Null (the default)
    /// means <see cref="Features"/>, everything this build understands. Mask a bit out to
    /// opt out of one — the server grants only what was asked for, so
    /// <c>Features &amp; ~FeatureSessionTxn</c> produces a connection on which
    /// <see cref="BeginAsync"/> refuses by name.</param>
    public static async Task<TriCoreClient> ConnectAsync(
        string host = "127.0.0.1",
        int port = DefaultPort,
        string? user = null,
        string? secret = null,
        string clientName = "tricoredb-dotnet",
        CancellationToken cancellationToken = default,
        TlsOptions? tls = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null,
        ulong? features = null)
    {
        // Two different deadlines, and they must not become one by accident. This budget
        // covers the phase the server itself bounds (its pre-auth read/write deadline) and
        // is gone by the time the caller runs a statement; `readTimeout` is the separate,
        // opt-in, steady-state one.
        var budget = connectTimeout ?? DefaultConnectTimeout;
        CancellationTokenSource? budgetCts = null;
        var token = cancellationToken;
        if (budget > TimeSpan.Zero)
        {
            budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(budget);
            token = budgetCts.Token;
        }

        try
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, token).ConfigureAwait(false);
            tcp.NoDelay = true; // small frames (PING, cache ops) must not sit behind Nagle's timer

            // Handshake before the first frame: HELLO and the AUTH secret below must ride
            // inside the TLS session, not ahead of it.
            Stream stream = tcp.GetStream();
            if (tls is not null)
            {
                try
                {
                    stream = await tls.WrapAsync((NetworkStream)stream, token).ConfigureAwait(false);
                }
                catch
                {
                    tcp.Dispose();
                    throw;
                }
            }

            var client = new TriCoreClient(tcp, stream, tls is not null);
            try
            {
                await client.HelloAsync(clientName, features ?? Features, token).ConfigureAwait(false);
                if (user is not null)
                    await client.AuthAsync(user, secret ?? "", token).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }
            // Armed only now, so the connect budget never leaks onto a running statement.
            client.ReadTimeout = readTimeout;
            return client;
        }
        catch (OperationCanceledException) when (budgetCts is not null
                                                 && budgetCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            throw new TriCoreTimeoutException(
                $"connecting to {host}:{port} did not complete within {budget.TotalMilliseconds}ms " +
                "(TCP, TLS, HELLO and AUTH share this budget; raise connectTimeout, or pass " +
                "TimeSpan.Zero to disable it)");
        }
        finally
        {
            budgetCts?.Dispose();
        }
    }

    /// <summary>End the session politely, then drop the socket.
    /// A failure to say goodbye is not worth raising over during teardown — the socket
    /// closes either way — but the socket is always released.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        // Bounded: the goodbye is optional, the teardown is not. A peer that accepts
        // CLOSE and answers nothing used to hold this open for ever — and DisposeAsync is
        // what an `await using` block and the pool's retire path both call, so the hang
        // landed in the application's cleanup.
        using var bye = new CancellationTokenSource(CloseTimeout);
        try
        {
            await SendAsync(TagClose, null, bye.Token).ConfigureAwait(false);
            await RecvAsync(bye.Token).ConfigureAwait(false);
        }
        catch
        {
            // best-effort BYE; socket is dropped in the finally regardless
        }
        finally
        {
            _closed = true;
            _stream.Dispose();
            _tcp.Dispose();
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // -- operations ------------------------------------------------------------------------

    /// <summary>
    /// A PING/PONG liveness round trip. Routed through <see cref="ExchangeAsync"/> like
    /// every other frame pair: it used to call Send/Recv directly, so a ping issued
    /// beside a request bypassed the in-flight guard this driver enforces everywhere else
    /// and consumed the request's reply.
    /// </summary>
    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        var (tag, _) = await ExchangeAsync(TagPing, null, cancellationToken).ConfigureAwait(false);
        if (tag != TagPong)
            throw new ProtocolException($"expected PONG, got tag {tag}");
    }

    /// <summary>
    /// A server-side deadline applied to every request from this client, or null for none.
    ///
    /// Distinct from a <see cref="CancellationToken"/>: cancelling a token abandons the
    /// read on this side while the server keeps working, whereas this rides in the request
    /// and makes the <b>server</b> stop. Set both when you want the work to actually end.
    /// </summary>
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>The <c>request_id</c> most recently sent. Pass it to
    /// <see cref="CancelAsync"/> from a second connection to stop a running statement.</summary>
    public string? LastRequestId { get; private set; }

    /// <summary>
    /// Ask the server to stop one of <b>this principal's</b> running statements, named by
    /// the <c>request_id</c> it was sent with.
    /// </summary>
    /// <remarks>
    /// This must be sent on a <b>second connection</b>. The connection running the
    /// statement is blocked reading its reply and is not reading anything else, so a
    /// cancel can never reach it there.
    ///
    /// Scoped by the authenticated principal: you cannot stop — or learn about —
    /// somebody else's statement. An unknown id returns 0 rather than failing.
    /// </remarks>
    /// <returns>how many of the caller's executions were asked to stop.</returns>
    public async Task<int> CancelAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject { ["request_id"] = Wire.Require(requestId, nameof(requestId)) };
        var (tag, body) = await ExchangeAsync(TagCancel, payload, cancellationToken).ConfigureAwait(false);
        if (tag == TagError) throw new TriCoreException(ErrorText(body));
        if (tag != TagCancelOk) throw new ProtocolException($"expected CANCEL_OK, got tag {tag}");
        if (body is null) throw new ProtocolException("CANCEL_OK frame carried no payload");
        return (int)Wire.NumOr(body.Value, "cancelled", 0);
    }

    /// <summary>Send a raw operation. The typed helpers below all funnel through here.</summary>
    public async Task<Response> RequestAsync(JsonNode op, string database = "main", CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _requestCounter);
        // See NextConnectionPrefix: the prefix is what keeps a CANCEL naming one
        // statement rather than every connection's Nth statement.
        var requestId = $"dotnet-{_ridPrefix}-{id}";
        LastRequestId = requestId;
        var payload = new JsonObject
        {
            ["request_id"] = requestId,
            ["database"] = database,
            ["op"] = op,
        };
        if (RequestTimeout is { } timeout)
            payload["options"] = new JsonObject { ["timeout_ms"] = (long)timeout.TotalMilliseconds };
        var (tag, body) = await ExchangeAsync(TagRequest, payload, cancellationToken).ConfigureAwait(false);
        if (tag == TagError) throw new TriCoreException(ErrorText(body));
        if (tag != TagResponse) throw new ProtocolException($"expected RESPONSE, got tag {tag}");
        if (body is null) throw new ProtocolException("RESPONSE frame carried no payload");

        var resp = new Response(body.Value);
        // Anything that is not `ok` means the server did not complete the operation, and
        // must not reach a caller wearing a success's clothes.
        //
        // Refusing only `error` used to be the whole check, and the kernel has three
        // statuses: `ok`, `error`, and `not_implemented` — "a recognized hook that was NOT
        // run" (crates/tricore_core/src/response/mod.rs). It is live on a default node:
        // Admin::RebalanceStatus without [sharding], Admin::RaftMessage without [raft].
        // Every typed helper funnels through here and several of them unwrap the `Message`
        // payload such a response carries, so the refusal text was returned as the result.
        //
        // Compared against `ok` rather than a list of bad statuses, so a status added to
        // the kernel later fails closed.
        if (resp.Status != "ok")
            // The code and the leader hint ride the exception so an application branches on
            // them instead of substring-matching the prose. A leader redirect is the case
            // that makes this load bearing: `not_leader` says the request was valid and went
            // to the wrong node, and it is not deducible from the message.
            throw new TriCoreException(
                $"server returned status `{resp.Status}`: {MessageOf(resp.Data) ?? "request failed"}",
                resp.ErrorCode, resp.LeaderHint);
        return resp;
    }

    /// <summary>Run a write (CREATE/INSERT/UPDATE/DELETE/BEGIN…COMMIT).</summary>
    public Task<Response> ExecuteAsync(string sql, string database = "main", CancellationToken cancellationToken = default)
    {
        var op = new JsonObject { ["Sql"] = new JsonObject { ["Exec"] = new JsonObject { ["sql"] = sql } } };
        return RequestAsync(op, database, cancellationToken);
    }

    /// <summary>Run a read and return its rows. A write sent here is refused by the server.</summary>
    public async Task<Rows> QueryAsync(string sql, string database = "main", CancellationToken cancellationToken = default)
    {
        var op = new JsonObject { ["Sql"] = new JsonObject { ["Query"] = new JsonObject { ["sql"] = sql } } };
        var resp = await RequestAsync(op, database, cancellationToken).ConfigureAwait(false);
        return RowsOf(resp);
    }

    /// <summary>Decode a Rows payload. Shared by both QueryAsync overloads so the two
    /// cannot drift in what they accept.</summary>
    internal static Rows RowsOf(Response resp)
    {
        if (resp.Data.ValueKind == JsonValueKind.Object && resp.Data.TryGetProperty("Rows", out var r))
        {
            var columns = new List<string>();
            if (r.TryGetProperty("columns", out var c))
                foreach (var col in c.EnumerateArray())
                    columns.Add(col.GetString() ?? "");

            var rows = new List<IReadOnlyList<string>>();
            if (r.TryGetProperty("rows", out var rr))
            {
                foreach (var row in rr.EnumerateArray())
                {
                    var values = new List<string>();
                    foreach (var cell in row.EnumerateArray())
                        values.Add(cell.GetString() ?? "");
                    rows.Add(values);
                }
            }
            return new Rows(columns, rows);
        }
        throw new ProtocolException($"expected Rows, got {Kind(resp.Data)}");
    }

    // -- transport -------------------------------------------------------------------------

    private async Task HelloAsync(string clientName, ulong features, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["protocol"] = Protocol,
            ["version"] = new JsonObject { ["major"] = ProtocolVersion, ["minor"] = 0 },
            ["client"] = clientName,
            ["features"] = features,
        };
        var (tag, body) = await ExchangeAsync(TagHello, payload, cancellationToken).ConfigureAwait(false);
        if (tag == TagError) throw new ProtocolException(ErrorText(body));
        if (tag != TagHelloOk) throw new ProtocolException($"expected HELLO_OK, got tag {tag}");
        if (!GetBool(body, "ok")) throw new ProtocolException(GetString(body, "message") ?? "handshake refused");
        // A server too old to negotiate sends no `features` field at all, which
        // leaves this at 0 — the same as a server that granted nothing. Both mean
        // "use no optional capability", which is what a caller needs to know.
        GrantedFeatures = GetUInt64(body, "features");
    }

    private async Task AuthAsync(string user, string secret, CancellationToken cancellationToken)
    {
        // secret travels as a byte array, not a string — see WIRE_REFERENCE.md's AUTH section.
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var secretArray = new JsonArray();
        foreach (var b in secretBytes) secretArray.Add(b);

        var payload = new JsonObject { ["username"] = user, ["secret"] = secretArray };
        var (tag, body) = await ExchangeAsync(TagAuth, payload, cancellationToken).ConfigureAwait(false);
        if (tag == TagError) throw new AuthException(ErrorText(body));
        if (tag != TagAuthOk) throw new ProtocolException($"expected AUTH_OK, got tag {tag}");
        // An AUTH_OK frame carrying ok=false is still a refusal.
        if (!GetBool(body, "ok")) throw new AuthException(GetString(body, "message") ?? "authentication refused");
        SessionId = GetString(body, "session_id");
    }

    /// <summary>
    /// Send one frame and read its reply, holding the connection for the duration.
    /// </summary>
    /// <remarks>
    /// A connection is one request/response stream, and <c>RecvAsync</c> reads a 6-byte
    /// header followed by exactly that many body bytes. Two overlapping exchanges — two
    /// tasks sharing one client — both read a header: the first gets the header, the
    /// <b>second gets the first frame's body</b> and mis-parses it. From there the stream
    /// is unrecoverable and the driver awaits bytes that never arrive.
    ///
    /// Refusing the second caller turns that hang into an exception naming the mistake and
    /// the fix. Deliberately a guard rather than a queue: silently serialising would make
    /// an unsafe pattern appear to work while giving the caller no concurrency at all —
    /// use <see cref="Pool"/> for that.
    /// </remarks>
    private async Task<(byte tag, JsonElement? body)> ExchangeAsync(byte tag, JsonNode? payload, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            // RequestNotSentException, not a bare TriCoreException: nothing was written, so a
            // caller — TxnControlAsync above all — can tell "the server never saw this" from
            // "the server refused it".
            throw new RequestNotSentException(
                "a request is already in flight on this connection. A TriCoreDB connection is a " +
                "single request/response stream: overlapping requests interleave frames and " +
                "deadlock. Await each call before the next, or use a Pool for real concurrency.");
        }
        try
        {
            await SendAsync(tag, payload, cancellationToken).ConfigureAwait(false);
            return await RecvAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private async Task SendAsync(byte tag, JsonNode? payload, CancellationToken cancellationToken)
    {
        ThrowIfUnusable();
        byte[] body = payload is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(payload.ToJsonString());

        // Mirrors tricore_protocol::write_frame. A control frame over the ceiling is
        // refused locally and named; an oversized REQUEST is still written, so the
        // server's own `frame_too_large` stays the diagnosis for the case an operator
        // will actually meet.
        if (tag != TagRequest && body.Length > MaxControlFrameSize)
            throw new ProtocolException(
                $"a {body.Length}-byte payload for tag {tag} exceeds the protocol's ceiling " +
                $"for control frames at {MaxControlFrameSize} bytes");

        // Single buffer, single write: header and body must land as one frame from the
        // server's point of view, and concatenating avoids a torn write across two calls.
        var frame = new byte[HeaderSize + body.Length];
        frame[0] = ProtocolVersion;
        frame[1] = tag;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2, 4), (uint)body.Length);
        body.CopyTo(frame.AsSpan(HeaderSize));

        await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(byte tag, JsonElement? body)> RecvAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnusable();

        var header = new byte[HeaderSize];
        await ReadFrameChunkAsync(header, cancellationToken).ConfigureAwait(false);

        byte version = header[0];
        if (version > MaxSupportedFrameVersion)
            throw Poison(new ProtocolException(
                $"frame header version {version} is newer than this driver can read " +
                $"(max {MaxSupportedFrameVersion})"));

        byte tag = header[1];
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2, 4));

        // Checked BEFORE a payload byte is asked for. Six header bytes must never be
        // able to commit this process to an arbitrary allocation.
        int limit = MaxPayloadFor(tag);
        if (length > (uint)limit)
            throw Poison(new ProtocolException(
                $"frame payload of {length} bytes for tag {tag} exceeds the protocol " +
                $"ceiling of {limit} bytes"));

        if (length == 0) return (tag, null);

        var body = new byte[length];
        await ReadFrameChunkAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            // Clone: the JsonElement must outlive the JsonDocument once it's disposed above.
            return (tag, doc.RootElement.Clone());
        }
        catch (JsonException e)
        {
            throw Poison(new ProtocolException($"malformed frame payload for tag {tag}: {e.Message}"));
        }
    }

    /// <summary>Refuse any use of a connection that is closed or poisoned.</summary>
    private void ThrowIfUnusable()
    {
        if (_fatal is not null)
            throw new ProtocolException(
                "this connection was closed after a protocol failure and cannot be reused: " +
                _fatal.Message);
        if (_closed) throw new TriCoreException("connection is closed");
    }

    /// <summary>Record <paramref name="e"/> as fatal, drop the socket, and hand it back
    /// so a call site can read <c>throw Poison(new ...)</c>.</summary>
    private Exception Poison(Exception e)
    {
        MarkFatal(e);
        return e;
    }

    private void MarkFatal(Exception e)
    {
        _fatal ??= e;
        // Closed here rather than left open: there is no resynchronisation point in a
        // length-prefixed stream once the length is untrustworthy, and `_closed` keeps
        // DisposeAsync from trying a CLOSE/BYE exchange on it.
        if (_closed) return;
        _closed = true;
        try { _stream.Dispose(); } catch { /* teardown */ }
        try { _tcp.Dispose(); } catch { /* teardown */ }
    }

    /// <summary>Read exactly buffer.Length bytes.
    /// TCP is a stream, not a message queue: a single ReadAsync may return a partial frame
    /// (or several). ReadExactlyAsync is what keeps the driver correct once payloads outgrow
    /// one segment — a local test with small values would never fragment and would happily
    /// hide the bug (this is why the E2E test uses a 100 KB cache value).</summary>
    private async Task ReadFrameChunkAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var budget = ReadTimeout;
        CancellationTokenSource? cts = null;
        var token = cancellationToken;
        if (budget is { } b && b > TimeSpan.Zero)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(b);
            token = cts.Token;
        }
        try
        {
            await _stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts is not null
                                                 && cts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            throw Poison(new TriCoreTimeoutException(
                $"no reply within the connection's ReadTimeout of {budget!.Value.TotalMilliseconds}ms"));
        }
        catch (EndOfStreamException)
        {
            throw Poison(new ProtocolException("connection closed mid-frame by the server"));
        }
        catch (Exception e)
        {
            // Includes the caller's own cancellation: an abandoned read leaves the stream
            // mid-frame, so this connection can never be trusted again either.
            MarkFatal(e);
            throw;
        }
        finally
        {
            cts?.Dispose();
        }
    }

    // -- JSON helpers ------------------------------------------------------------------------

    private static bool GetBool(JsonElement? body, string prop) =>
        body.HasValue && body.Value.ValueKind == JsonValueKind.Object &&
        body.Value.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>A u64 property, or 0 when absent or not a number (V2 P2).</summary>
    /// <remarks>
    /// Absent reads as 0 on purpose: that is what a server too old to negotiate
    /// sends, and "granted nothing" is the correct reading of it.
    /// </remarks>
    private static ulong GetUInt64(JsonElement? body, string prop) =>
        body.HasValue && body.Value.ValueKind == JsonValueKind.Object &&
        body.Value.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetUInt64(out var n)
            ? n
            : 0UL;

    private static string? GetString(JsonElement? body, string prop) =>
        body.HasValue && body.Value.ValueKind == JsonValueKind.Object &&
        body.Value.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string ErrorText(JsonElement? body)
    {
        if (body.HasValue && body.Value.ValueKind == JsonValueKind.Object)
        {
            var msg = GetString(body, "message") ?? GetString(body, "error");
            if (msg is not null) return msg;
            return body.Value.GetRawText();
        }
        return body.HasValue ? body.Value.GetRawText() : "unknown error";
    }

    private static string? MessageOf(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString();
            if (data.TryGetProperty("Json", out var j))
                return j.GetRawText();
        }
        return null;
    }

    private static string Kind(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var prop in data.EnumerateObject())
                return prop.Name;
        return data.ValueKind == JsonValueKind.Undefined ? "<none>" : data.GetRawText();
    }
}
