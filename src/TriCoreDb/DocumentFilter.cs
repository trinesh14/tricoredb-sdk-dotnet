using System.Text.Json.Nodes;

namespace TriCoreDb;

/// <summary>
/// A document query filter.
///
/// Every <c>field</c> accepts dot notation (<c>"a.b.c"</c>) resolving into nested JSON
/// objects; a missing path never matches, <see cref="Ne"/> included. Comparisons across
/// mismatched types never match.
///
/// <code>
/// var f = DocumentFilter.And(DocumentFilter.Gt("age", 30), DocumentFilter.Eq("city", "Pune"));
/// await db.DocumentFindAsync("users", f, limit: 10);
/// </code>
///
/// This is deliberately <b>not</b> a MongoDB query language: the server has no <c>Or</c>,
/// no <c>Not</c> and no regex, so neither does this type. A builder that accepted them
/// would have to translate into something the server cannot evaluate.
/// </summary>
public sealed class DocumentFilter
{
    private readonly JsonNode _wire;

    private DocumentFilter(JsonNode wire) => _wire = wire;

    /// <summary>Match every document.</summary>
    public static DocumentFilter All() => new(JsonValue.Create("All")!);

    /// <summary>Match documents whose <paramref name="field"/> equals <paramref name="value"/>.</summary>
    public static DocumentFilter Eq(string field, object? value) => Comparison("Eq", field, value);

    /// <summary>Match documents whose <paramref name="field"/> exists and differs.</summary>
    public static DocumentFilter Ne(string field, object? value) => Comparison("Ne", field, value);

    public static DocumentFilter Gt(string field, object? value) => Comparison("Gt", field, value);

    public static DocumentFilter Gte(string field, object? value) => Comparison("Gte", field, value);

    public static DocumentFilter Lt(string field, object? value) => Comparison("Lt", field, value);

    public static DocumentFilter Lte(string field, object? value) => Comparison("Lte", field, value);

    /// <summary>Match documents whose <paramref name="field"/> equals any of the values.</summary>
    public static DocumentFilter In(string field, IEnumerable<object?> values)
    {
        RequireField(field);
        Wire.RequireNotNull(values, nameof(values));
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(Wire.ToNode(v));
        return Tagged("In", new JsonObject { ["field"] = field, ["values"] = arr });
    }

    /// <summary>Substring match on a string field, or element match on an array field.</summary>
    public static DocumentFilter Contains(string field, object? value) => Comparison("Contains", field, value);

    /// <summary>Match documents satisfying every sub-filter. An empty list matches everything.</summary>
    public static DocumentFilter And(params DocumentFilter[] filters) => And((IEnumerable<DocumentFilter>)filters);

    public static DocumentFilter And(IEnumerable<DocumentFilter> filters)
    {
        Wire.RequireNotNull(filters, nameof(filters));
        var arr = new JsonArray();
        foreach (var f in filters)
        {
            if (f is null) throw new ArgumentException("filter list must not contain null", nameof(filters));
            arr.Add(f._wire.DeepClone());
        }
        return Tagged("And", arr);
    }

    private static DocumentFilter Comparison(string tag, string field, object? value)
    {
        RequireField(field);
        return Tagged(tag, new JsonObject { ["field"] = field, ["value"] = Wire.ToNode(value) });
    }

    private static DocumentFilter Tagged(string tag, JsonNode body) => new(new JsonObject { [tag] = body });

    private static void RequireField(string field)
    {
        if (string.IsNullOrEmpty(field))
            throw new ArgumentException("field must not be null or empty", nameof(field));
    }

    /// <summary>The externally tagged form the server's enum expects.</summary>
    internal JsonNode ToWire() => _wire.DeepClone();

    public override string ToString() => $"DocumentFilter{_wire.ToJsonString()}";
}
