using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TriCoreDb;

/// <summary>
/// Client-side parameter binding for SQL statements.
///
/// <b>TriCoreDB V1 has no server-side prepared statements</b>: the wire carries one
/// <c>sql</c> string and nothing else. This class therefore renders parameters into
/// that string here, in the driver, before it is sent. That is a real and useful
/// feature — it removes hand-rolled string concatenation, which is where injection
/// bugs actually come from — but it is not the same guarantee a server-side bind
/// gives you, and this file is where that distinction is documented rather than
/// quietly blurred.
///
/// What it does guarantee:
/// <list type="bullet">
///   <item>Strings are escaped by doubling every <c>'</c>, which is the only escape
///         the server's tokenizer recognises, and wrapped in single quotes. There is
///         no backslash escape to smuggle a quote past.</item>
///   <item>Numbers render invariantly, so a comma decimal separator in the ambient
///         culture cannot turn <c>1.5</c> into <c>1,5</c> and thereby into two values.</item>
///   <item>Only a closed set of CLR types is accepted. An unsupported type throws
///         rather than falling back to <c>ToString()</c>, which is how an object's
///         debug rendering ends up inside a WHERE clause.</item>
///   <item>Placeholder count must equal argument count, so a dropped argument fails
///         loudly instead of shifting every later value by one.</item>
/// </list>
///
/// What it does not do: it cannot protect an <b>identifier</b>. Table and column names
/// are not values and are not bindable; build those from a whitelist you control.
///
/// <code>
/// var rows = await db.QueryAsync(
///     SqlParams.Bind("SELECT * FROM users WHERE city = ? AND age &gt; ?", "Pune", 30));
/// </code>
/// </summary>
public static class SqlParams
{
    /// <summary>Render <paramref name="sql"/> with each <c>?</c> replaced by the
    /// corresponding argument, escaped as a SQL literal.</summary>
    /// <exception cref="ArgumentException">the placeholder and argument counts differ,
    /// or an argument has a type with no SQL literal form.</exception>
    public static string Bind(string sql, params object?[] args)
    {
        Wire.RequireNotNull(sql, nameof(sql));
        args ??= Array.Empty<object?>();

        var sb = new StringBuilder(sql.Length + args.Length * 8);
        int next = 0;
        bool inString = false;

        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];

            // A `?` inside a string literal is data, not a placeholder. Tracking quote
            // state is what stops `WHERE note = 'why?'` from consuming an argument.
            if (c == '\'')
            {
                // `''` inside a literal is an escaped quote, not the end of one.
                if (inString && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    sb.Append("''");
                    i++;
                    continue;
                }
                inString = !inString;
                sb.Append(c);
                continue;
            }

            if (c == '?' && !inString)
            {
                if (next >= args.Length)
                    throw new ArgumentException(
                        $"SQL has more `?` placeholders than the {args.Length} argument(s) supplied", nameof(args));
                sb.Append(Literal(args[next++]));
                continue;
            }

            sb.Append(c);
        }

        if (inString)
            throw new ArgumentException("SQL ends inside an unterminated string literal", nameof(sql));
        if (next != args.Length)
            throw new ArgumentException(
                $"SQL has {next} `?` placeholder(s) but {args.Length} argument(s) were supplied", nameof(args));

        return sb.ToString();
    }

    /// <summary>
    /// One CLR value as a <b>server-side</b> parameter: the JSON scalar that travels beside
    /// the statement for the server to bind.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Literal"/> has to reproduce the server's literal syntax exactly, for
    /// every type, and any mismatch is a wrong value or a parse error. A bound parameter is
    /// substituted at a value position the grammar has already fixed, so what a value
    /// contains — a quote, a backslash, a whole SQL statement — can never change what the
    /// statement means.</para>
    ///
    /// <para>The wire carries plain JSON because five SDKs build this payload; the server
    /// maps each scalar to a typed SQL value and refuses, by name and by index, anything it
    /// cannot represent. So this method's job is only the types that need a specific
    /// spelling to land in the right column type:</para>
    /// <list type="bullet">
    ///   <item><c>byte[]</c> and <c>ReadOnlyMemory&lt;byte&gt;</c> become <c>0x</c>-prefixed
    ///         hex, which is what a BLOB column parses. (Client-side rendering had no
    ///         <c>byte[]</c> case at all — it threw.)</item>
    ///   <item><c>DateTime</c>/<c>DateTimeOffset</c> become the same sortable text this
    ///         driver has always sent for a TIMESTAMP column, and <c>Guid</c> its canonical
    ///         text for a UUID column.</item>
    ///   <item><c>decimal</c> is sent as <b>text</b>. A JSON number is read through a
    ///         <c>double</c> on the way in, so every digit past a double's precision was
    ///         silently lost — the one thing a <c>decimal</c> is chosen to prevent. Text is
    ///         the only wire spelling that carries them all, and a DECIMAL column parses it
    ///         exactly. The cost: binding a <c>decimal</c> into a <b>DOUBLE</b> column now
    ///         fails by name, since the server accepts text for DECIMAL but not for DOUBLE.
    ///         Pass a <c>double</c> for a DOUBLE column.</item>
    ///   <item>NaN and infinities are refused here rather than reaching the JSON writer,
    ///         which has no spelling for them.</item>
    /// </list>
    /// <para>Everything else passes through: an integral type is exact in JSON at every width
    /// .NET has, and a string is a TEXT parameter whatever it holds.</para>
    /// </remarks>
    public static JsonNode? Param(object? value) => value switch
    {
        null or DBNull => null,
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        char c => JsonValue.Create(c.ToString()),
        byte[] bytes => JsonValue.Create(Hex(bytes)),
        ReadOnlyMemory<byte> mem => JsonValue.Create(Hex(mem.Span)),
        sbyte or byte or short or ushort or int or uint or long =>
            JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        // A ulong past long.MaxValue is still exact as a JSON number; the server binds it
        // as a decimal and the column decides whether it fits. Narrowing it to a long here
        // would wrap it into a negative, which is the one outcome nobody wants.
        ulong u => JsonValue.Create(u),
        float f => JsonValue.Create(Finite(f)),
        double d => JsonValue.Create(Finite(d)),
        // Text, not a number: a JSON number goes through a double and drops
        // every digit past its precision. `ToString(InvariantCulture)` on a
        // decimal never produces exponent form, which the server would read as
        // a DOUBLE.
        decimal m => JsonValue.Create(m.ToString(CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(g.ToString()),
        _ => throw new ArgumentException(
            $"no SQL parameter form for {value.GetType().FullName}. Convert it explicitly — " +
            "passing it through would put a JSON object or array where the server expects a scalar."),
    };

    /// <summary>Every value in <paramref name="args"/> as a server-side parameter, in order.</summary>
    public static JsonArray Params(IReadOnlyList<object?> args)
    {
        var arr = new JsonArray();
        foreach (var a in args)
            arr.Add(Param(a));
        return arr;
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(2 + bytes.Length * 2);
        sb.Append("0x");
        foreach (var b in bytes)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static double Finite(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            throw new ArgumentException($"`{d}` has no SQL parameter form");
        return d;
    }

    /// <summary>Render one CLR value as a SQL literal.</summary>
    public static string Literal(object? value) => value switch
    {
        null or DBNull => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        string s => Quote(s),
        char c => Quote(c.ToString()),
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
        float f => Real(f),
        double d => Real(d),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        // Rendered as a quoted ISO-8601 string: the server has no date type, so a
        // timestamp is stored as text and must round-trip in a sortable form.
        DateTime dt => Quote(dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => Quote(dto.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)),
        Guid g => Quote(g.ToString()),
        _ => throw new ArgumentException(
            $"no SQL literal form for {value.GetType().FullName}. Convert it explicitly — " +
            "falling back to ToString() would put an object's debug rendering into the statement."),
    };

    /// <summary>Escape and quote a string: double every <c>'</c>, which is the only
    /// escape the server's tokenizer recognises.</summary>
    public static string Quote(string s)
    {
        Wire.RequireNotNull(s, nameof(s));
        return $"'{s.Replace("'", "''")}'";
    }

    private static string Real(double d)
    {
        // The parser has no literal for these, so emitting one produces a statement the
        // server rejects with a syntax error far from the cause. Refuse it here instead.
        if (double.IsNaN(d) || double.IsInfinity(d))
            throw new ArgumentException($"`{d}` has no SQL literal form");
        // "R" round-trips; the invariant culture keeps `.` as the decimal separator.
        return d.ToString("R", CultureInfo.InvariantCulture);
    }
}
