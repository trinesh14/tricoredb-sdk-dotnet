using System.Text.Json;

namespace TriCoreDb;

/// <summary>A server response: typed data plus how it was produced.</summary>
public sealed class Response
{
    public string RequestId { get; }
    public string Status { get; }

    /// <summary>Externally tagged result payload, e.g. {"Rows": {...}} or {"CacheValue": [...]}.
    /// ValueKind is Undefined if the server sent no "data" field.</summary>
    public JsonElement Data { get; }

    private readonly JsonElement _diagnostics;

    internal Response(JsonElement raw)
    {
        RequestId = raw.TryGetProperty("request_id", out var rid) ? rid.GetString() ?? "" : "";
        Status = raw.TryGetProperty("status", out var st) ? st.GetString() ?? "error" : "error";
        Data = raw.TryGetProperty("data", out var d) ? d : default;
        _diagnostics = raw.TryGetProperty("diagnostics", out var diag) ? diag : default;
    }

    /// <summary>Non-fatal warnings. Not decorative: a broadcast DDL that could not reach every
    /// shard reports it here while still returning status "ok" — a caller that ignores this
    /// can miss a divergent cluster.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            if (_diagnostics.ValueKind == JsonValueKind.Object &&
                _diagnostics.TryGetProperty("warnings", out var w) &&
                w.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in w.EnumerateArray())
                    list.Add(item.GetString() ?? "");
                return list;
            }
            return Array.Empty<string>();
        }
    }

    public string Route =>
        _diagnostics.ValueKind == JsonValueKind.Object &&
        _diagnostics.TryGetProperty("route", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() ?? ""
            : "";

    public long ElapsedMs =>
        _diagnostics.ValueKind == JsonValueKind.Object &&
        _diagnostics.TryGetProperty("elapsed_ms", out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt64()
            : 0;

    /// <summary>The server's machine-readable reason a request failed
    /// (<c>diagnostics.error_code</c>), or null when it did not fail or the server sent
    /// none.</summary>
    /// <remarks>
    /// This is the field to branch on. The message is prose and stays free to be reworded;
    /// substring-matching it is how an authorization denial once reached clients as something
    /// else entirely, which is the failure the code was added to end.
    /// </remarks>
    public string? ErrorCode =>
        _diagnostics.ValueKind == JsonValueKind.Object &&
        _diagnostics.TryGetProperty("error_code", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

    /// <summary>A <c>host:port</c> this request should have gone to, carried only with an
    /// <see cref="ErrorCode"/> of <see cref="TriCoreException.ErrorCodeNotLeader"/>.</summary>
    /// <remarks>
    /// An address a client can dial, resolved by the server from <c>[[raft.peers]].address</c>,
    /// not a node id. Null means "no address was named", which is <b>not</b> the same as
    /// "this is not a redirect": a refusal raised mid-election, on a node without Raft, or
    /// for a leader with no <c>[[raft.peers]]</c> entry all carry the code and no hint. See
    /// <see cref="TriCoreException.LeaderHint"/>.
    /// </remarks>
    public string? LeaderHint =>
        _diagnostics.ValueKind == JsonValueKind.Object &&
        _diagnostics.TryGetProperty("leader_hint", out var h) && h.ValueKind == JsonValueKind.String
            ? h.GetString()
            : null;

    /// <summary>Whether this response is a leader redirect (<see cref="ErrorCode"/> is
    /// <see cref="TriCoreException.ErrorCodeNotLeader"/>). The driver never follows it.</summary>
    public bool IsNotLeader => ErrorCode == TriCoreException.ErrorCodeNotLeader;

    public override string ToString() => $"Response(status={Status}, route={Route})";
}
