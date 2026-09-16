using System.Text.Json;

namespace TriCoreDb;

// Administrative reads. Both require the Admin permission — the server's default for
// every AdminOp variant — so an unprivileged caller gets a permission error, not
// silently empty data.

public sealed partial class TriCoreClient
{
    /// <summary>
    /// Round-trip a request through the full pipeline.
    ///
    /// Distinct from <see cref="PingAsync"/>, which exchanges a PING frame and never
    /// reaches a module. This one proves auth, routing and dispatch are working, which
    /// is what a readiness check actually wants to know.
    /// </summary>
    public Task<Response> AdminPingAsync(string database = "main", CancellationToken cancellationToken = default) =>
        RequestAsync(Wire.UnitOp("Admin", "Ping"), database, cancellationToken);

    /// <summary>Server status as reported by the cluster core.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> AdminStatusAsync(
        string database = "main", CancellationToken cancellationToken = default)
    {
        var resp = await RequestAsync(Wire.UnitOp("Admin", "Status"), database, cancellationToken).ConfigureAwait(false);
        // Stub cores answer with a Message rather than structured JSON; surface that
        // under a known key instead of throwing, so a status call never hard-fails.
        if (resp.Data.ValueKind == JsonValueKind.Object && resp.Data.TryGetProperty("Message", out var m))
            return new Dictionary<string, object?> { ["message"] = m.GetString() };
        return Wire.ToDictionary(Wire.Json(resp));
    }
}
