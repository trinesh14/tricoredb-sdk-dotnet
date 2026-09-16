using System.Collections;

namespace TriCoreDb;

/// <summary>A SQL result set: column names plus string-encoded row values (the wire never
/// carries typed scalars — every cell is the server's text rendering of the value).</summary>
public sealed class Rows : IReadOnlyList<IReadOnlyList<string>>
{
    public IReadOnlyList<string> Columns { get; }
    private readonly IReadOnlyList<IReadOnlyList<string>> _rows;

    public Rows(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Columns = columns;
        _rows = rows;
    }

    public int Count => _rows.Count;

    public IReadOnlyList<string> this[int index] => _rows[index];

    /// <summary>Rows as dictionaries keyed by column name.</summary>
    public IReadOnlyList<Dictionary<string, string>> Dicts()
    {
        var result = new List<Dictionary<string, string>>(_rows.Count);
        foreach (var row in _rows)
        {
            var dict = new Dictionary<string, string>();
            for (int i = 0; i < Columns.Count && i < row.Count; i++)
                dict[Columns[i]] = row[i];
            result.Add(dict);
        }
        return result;
    }

    public IEnumerator<IReadOnlyList<string>> GetEnumerator() => _rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"Rows(columns=[{string.Join(", ", Columns)}], rows={Count})";
}
