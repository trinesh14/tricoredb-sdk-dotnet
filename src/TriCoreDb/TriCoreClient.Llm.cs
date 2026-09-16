using System.Text.Json.Nodes;

namespace TriCoreDb;

/// <summary>How the server should render an export.</summary>
public enum OutputFormat
{
    /// <summary>TriCoreDB's native representation.</summary>
    Native,
    /// <summary>Standard JSON.</summary>
    Json,
    /// <summary>TOON: a token-oriented rendering for LLM context windows.</summary>
    Toon,
    /// <summary>Human-readable Markdown.</summary>
    Markdown,
}

/// <summary>
/// Bounds and redaction for an LLM context export.
/// </summary>
/// <param name="MaxRows">cap on rows included, or null for the server's own cap.</param>
/// <param name="RedactSensitive">redact sensitive fields before export. Defaults to
/// <c>true</c>, matching the server — the safe default is the one you get by omission.</param>
/// <param name="IncludeSchema">include type information alongside the data.</param>
public readonly record struct LlmOptions(
    int? MaxRows = null,
    bool RedactSensitive = true,
    bool IncludeSchema = false)
{
    internal JsonObject ToWire() => new()
    {
        ["max_rows"] = MaxRows,
        ["redact_sensitive"] = RedactSensitive,
        ["include_schema"] = IncludeSchema,
    };
}

/// <summary>One read-only source contributing to an LLM context bundle.</summary>
public sealed class LlmSource
{
    private readonly JsonNode _wire;

    private LlmSource(JsonNode wire) => _wire = wire;

    /// <summary>A SQL <c>SELECT</c>. Requires the caller to hold SQL read permission.</summary>
    public static LlmSource Sql(string query) =>
        new(new JsonObject { ["Sql"] = new JsonObject { ["query"] = Wire.Require(query, nameof(query)) } });

    /// <summary>A document find. Requires the caller to hold document read permission.</summary>
    public static LlmSource DocumentFind(string collection, DocumentFilter? filter = null, int? limit = null) =>
        new(new JsonObject
        {
            ["DocumentFind"] = new JsonObject
            {
                ["collection"] = Wire.Require(collection, nameof(collection)),
                ["filter"] = (filter ?? DocumentFilter.All()).ToWire(),
                ["limit"] = limit,
            },
        });

    internal JsonNode ToWire() => _wire.DeepClone();
}

// The LLM family assembles read-only context bundles. No LLM operation ever writes.

public sealed partial class TriCoreClient
{
    /// <summary>
    /// Assemble a context bundle from one or more read-only sources.
    /// </summary>
    /// <returns>the rendered bundle, as text for TOON/Markdown or JSON otherwise.</returns>
    public async Task<string> LlmContextAsync(
        IEnumerable<LlmSource> sources,
        OutputFormat format = OutputFormat.Toon,
        LlmOptions? options = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var arr = new JsonArray();
        foreach (var s in Wire.RequireNotNull(sources, nameof(sources)))
            arr.Add(Wire.RequireNotNull(s, "source").ToWire());
        if (arr.Count == 0)
            throw new ArgumentException("a context bundle needs at least one source", nameof(sources));

        var body = new JsonObject
        {
            ["sources"] = arr,
            ["format"] = FormatWire(format),
            ["options"] = (options ?? new LlmOptions()).ToWire(),
        };
        var resp = await RequestAsync(Wire.Op("Llm", "Context", body), database, cancellationToken).ConfigureAwait(false);
        return Rendered(resp);
    }

    /// <summary>Export the schema catalog: SQL tables plus document collections.</summary>
    public async Task<string> LlmSchemaAsync(
        OutputFormat format = OutputFormat.Toon,
        LlmOptions? options = null,
        string database = "main",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["format"] = FormatWire(format),
            ["options"] = (options ?? new LlmOptions()).ToWire(),
        };
        var resp = await RequestAsync(Wire.Op("Llm", "Schema", body), database, cancellationToken).ConfigureAwait(false);
        return Rendered(resp);
    }

    internal static string FormatWire(OutputFormat f) => f switch
    {
        OutputFormat.Native => "native",
        OutputFormat.Json => "json",
        OutputFormat.Toon => "toon",
        OutputFormat.Markdown => "markdown",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    /// <summary>Unwrap whichever payload the requested format produced.</summary>
    private static string Rendered(Response resp)
    {
        if (resp.Data.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (resp.Data.TryGetProperty("Toon", out var t)) return t.GetString() ?? "";
            if (resp.Data.TryGetProperty("Json", out var j)) return j.GetRawText();
            if (resp.Data.TryGetProperty("Message", out var m)) return m.GetString() ?? "";
        }
        throw new ProtocolException($"expected a rendered export, got {Wire.Kind(resp.Data)}");
    }
}
