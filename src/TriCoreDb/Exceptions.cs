namespace TriCoreDb;

/// <summary>Any failure from the server or the transport.</summary>
public class TriCoreException : Exception
{
    /// <summary>The server's <c>diagnostics.error_code</c> for a write or linearizable read
    /// that reached a Raft follower.</summary>
    public const string ErrorCodeNotLeader = "not_leader";

    public TriCoreException(string message) : base(message) { }

    /// <summary>Wraps an underlying failure (a TLS handshake or certificate-load error, say)
    /// while keeping the original available via <see cref="Exception.InnerException"/>.</summary>
    public TriCoreException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>A failure carrying the server's machine-readable code and, for a leader
    /// redirect, the node it named.</summary>
    public TriCoreException(string message, string? errorCode, string? leaderHint) : base(message)
    {
        ErrorCode = errorCode;
        LeaderHint = leaderHint;
    }

    /// <summary>The server's machine-readable reason (<c>diagnostics.error_code</c>), or null
    /// when it sent none — a transport failure, or a refusal this driver made before
    /// sending.</summary>
    /// <remarks>This is the property to branch on. The message is prose and stays free to be
    /// reworded.</remarks>
    public string? ErrorCode { get; }

    /// <summary>A <c>host:port</c> this request should have gone to, set only alongside an
    /// <see cref="ErrorCode"/> of <see cref="ErrorCodeNotLeader"/>.</summary>
    /// <remarks>
    /// <para>It is an <b>address a client can dial</b>, not a node id: the server resolves
    /// the leader's id through <c>[[raft.peers]].address</c>, which names that node's native
    /// protocol listener — the same endpoint this driver already speaks, not a separate
    /// consensus port.</para>
    ///
    /// <para>Null means "no address was named", which is <b>not</b> the same as "this is not
    /// a redirect". Three situations produce a code with no hint: a refusal raised
    /// mid-election, a node running without Raft, and a leader whose id has no
    /// <c>[[raft.peers]]</c> entry — where the server deliberately sends nothing rather than
    /// a bare id, because a label in an address field is a connection attempt to a host that
    /// does not exist. All three mean the same thing to a caller: wait and retry.</para>
    ///
    /// <para>The refusal's message still names the leader's <b>node id</b>, on purpose: prose
    /// is for a human reading logs and the hint is for a machine dialling. Never scrape the
    /// id out of the message and use it as a destination — that is exactly what this property
    /// replaces.</para>
    ///
    /// <para>Test <see cref="IsNotLeader"/> for the redirect and treat a null hint as an
    /// unknown destination.</para>
    /// </remarks>
    public string? LeaderHint { get; }

    /// <summary>Whether this refusal is a leader redirect: the request was valid, and this
    /// node is not the one that may serve it.</summary>
    /// <remarks>
    /// This driver deliberately does not follow the redirect itself: re-sending a write to
    /// another address is a policy decision (which endpoints are reachable, which credentials
    /// apply there, whether the operation is safe to repeat) that belongs to the caller, not
    /// to a connection object.
    /// </remarks>
    public bool IsNotLeader => ErrorCode == ErrorCodeNotLeader;
}

/// <summary>Authentication was refused (bad user/secret, or an AUTH_OK with ok=false).</summary>
public class AuthException : TriCoreException
{
    public AuthException(string message) : base(message) { }
}

/// <summary>The peer did not speak the protocol we expect (wrong tag, malformed frame, …).</summary>
public class ProtocolException : TriCoreException
{
    public ProtocolException(string message) : base(message) { }
}

/// <summary>
/// A request this driver refused <b>before writing a byte</b>, so the server never saw it
/// and the session's state is exactly what it was.
/// </summary>
/// <remarks>
/// The distinction is not cosmetic. A failed COMMIT normally means the block is over — the
/// server ends it either way, and says so. A COMMIT that was never sent means the block is
/// still open, and treating the two alike would let a connection go back to a
/// <see cref="Pool"/> carrying somebody else's uncommitted writes.
/// </remarks>
public class RequestNotSentException : TriCoreException
{
    public RequestNotSentException(string message) : base(message) { }
}

/// <summary>No pooled connection became available within the caller's timeout.</summary>
public class PoolTimeoutException : TriCoreException
{
    public PoolTimeoutException(string message) : base(message) { }
}

/// <summary>
/// A reply did not arrive within this connection's <see cref="TriCoreClient.ReadTimeout"/>,
/// or the connect phase did not finish within its budget.
/// </summary>
/// <remarks>
/// <para>Fatal to the connection, not a retryable hiccup. The reply this driver stopped
/// waiting for may still be in flight, and reusing the socket would read it as the answer
/// to the <b>next</b> request. The connection is poisoned when this is thrown, and a
/// <see cref="Pool"/> retires it.</para>
///
/// <para>Named rather than reusing <see cref="System.TimeoutException"/> so that an
/// existing <c>catch (TriCoreException)</c> sees it: every other failure this driver
/// raises is a <c>TriCoreException</c>, and a timeout that escaped the hierarchy is
/// exactly the defect the Python driver carried.</para>
///
/// <para><see cref="TriCoreClient.ReadTimeout"/> has <b>no default</b>. A read legitimately
/// blocks for exactly as long as the statement runs, the server's own
/// <c>statement_timeout_ms</c> defaults to unlimited, and any number this driver picked
/// would be a guess at a guarantee the server does not make. A caller who wants the
/// <i>server</i> to stop rather than merely stopping the wait should set
/// <see cref="TriCoreClient.RequestTimeout"/>; that is almost always the one you want.</para>
/// </remarks>
public class TriCoreTimeoutException : TriCoreException
{
    public TriCoreTimeoutException(string message) : base(message) { }
}
