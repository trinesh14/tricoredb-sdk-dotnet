using System.Globalization;
using TriCoreDb.Tests.Support;

namespace TriCoreDb.Tests.Live;

/// <summary>Bound parameters against a real server: every case reads the value back.</summary>
[Collection("live")]
[Trait("Category", "Live")]
public class ParameterRoundTripTests
{
    private readonly TriCoreServer _server;

    public ParameterRoundTripTests(LiveServer live) => _server = live.Server;

    private static async Task<string?> OneAsync(TriCoreClient db, string sql, params object?[] args)
    {
        var rows = await db.QueryAsync(sql, args);
        return Assert.Single(rows)[0];
    }

    [LiveFact]
    public async Task Text_null_bool_float_and_injection_round_trip_exactly()
    {
        await using var db = await _server.ConnectAsync();
        var t = Names.Unique("prm");
        var victim = Names.Unique("victim");
        await db.ExecuteAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, s TEXT, f DOUBLE, k BOOL)");
        await db.ExecuteAsync($"CREATE TABLE {victim} (id INT PRIMARY KEY)");

        const string quote = "O'Hara said 'hi'";
        const string back = @"C:\Users\a\'b";
        var inject = $"'; DROP TABLE {victim}; --";
        await db.ExecuteAsync($"INSERT INTO {t} (id, s) VALUES (?, ?)", new object?[] { 1, quote });
        await db.ExecuteAsync($"INSERT INTO {t} (id, s) VALUES (?, ?)", new object?[] { 2, back });
        await db.ExecuteAsync($"INSERT INTO {t} (id, s) VALUES (?, ?)", new object?[] { 3, null });
        await db.ExecuteAsync($"INSERT INTO {t} (id, s) VALUES (?, ?)", new object?[] { 4, inject });
        await db.ExecuteAsync($"INSERT INTO {t} (id, f, k) VALUES (?, ?, ?)", new object?[] { 5, -0.125d, true });

        Assert.Equal(quote, await OneAsync(db, $"SELECT s FROM {t} WHERE id = ?", 1));
        Assert.Equal(back, await OneAsync(db, $"SELECT s FROM {t} WHERE id = ?", 2));
        Assert.Equal("1", await OneAsync(db, $"SELECT COUNT(*) FROM {t} WHERE id = ? AND s IS NULL", 3));
        Assert.Equal(inject, await OneAsync(db, $"SELECT s FROM {t} WHERE id = ?", 4));
        Assert.Equal("-0.125", await OneAsync(db, $"SELECT f FROM {t} WHERE id = ?", 5));
        Assert.Equal("true", await OneAsync(db, $"SELECT k FROM {t} WHERE id = ?", 5));
        Assert.Equal("1", await OneAsync(db, $"SELECT COUNT(*) FROM {t} WHERE k = ?", true));
        Assert.Empty(await db.QueryAsync($"SELECT id FROM {victim}"));
    }

    [LiveFact]
    public async Task A_byte_array_and_a_memory_slice_round_trip_through_a_blob_column()
    {
        await using var db = await _server.ConnectAsync();
        var t = Names.Unique("blob");
        await db.ExecuteAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, b BLOB)");
        byte[] blob = { 0x00, 0x01, 0xff, 0xfe, (byte)'\'', (byte)'\\', (byte)'h', (byte)'i', 0x00 };
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (?, ?)", new object?[] { 1, blob });
        ReadOnlyMemory<byte> slice = new byte[] { 0x11, 0xde, 0xad, 0xbe, 0xef, 0x22 }.AsMemory(1, 4);
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (?, ?)", new object?[] { 2, slice });

        Assert.Equal("0x0001fffe275c686900", await OneAsync(db, $"SELECT b FROM {t} WHERE id = ?", 1));
        Assert.Equal("0xdeadbeef", await OneAsync(db, $"SELECT b FROM {t} WHERE id = ?", 2));
    }

    [LiveTheory]
    [InlineData("1234.5678")]
    [InlineData("-98765432109876543.21")]
    [InlineData("0.000001")]
    public async Task A_decimal_keeps_every_digit_in_a_decimal_column(string text)
    {
        await using var db = await _server.ConnectAsync();
        var t = Names.Unique("dec");
        await db.ExecuteAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, d DECIMAL)");
        var value = decimal.Parse(text, CultureInfo.InvariantCulture);
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (?, ?)", new object?[] { 1, value });

        Assert.Equal(text, await OneAsync(db, $"SELECT d FROM {t} WHERE id = ?", 1));
        Assert.Equal("1", await OneAsync(db, $"SELECT COUNT(*) FROM {t} WHERE d = ?", value));
    }

    [LiveFact]
    public async Task A_decimal_bound_into_a_double_column_fails_by_name_and_writes_nothing()
    {
        await using var db = await _server.ConnectAsync();
        var t = Names.Unique("dbl");
        await db.ExecuteAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, f DOUBLE)");

        var e = await Assert.ThrowsAsync<TriCoreException>(() =>
            db.ExecuteAsync($"INSERT INTO {t} VALUES (?, ?)", new object?[] { 1, 1.5m }));
        Assert.Contains("DOUBLE", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("0", await OneAsync(db, $"SELECT COUNT(*) FROM {t}"));

        await db.ExecuteAsync($"INSERT INTO {t} VALUES (?, ?)", new object?[] { 1, 1.5d });
        Assert.Equal("1.5", await OneAsync(db, $"SELECT f FROM {t} WHERE id = ?", 1));
    }

    [LiveFact]
    public async Task A_ulong_past_long_max_is_stored_exactly()
    {
        await using var db = await _server.ConnectAsync();
        var t = Names.Unique("u64");
        await db.ExecuteAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, d DECIMAL)");
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (?, ?)", new object?[] { 1, ulong.MaxValue });
        await db.ExecuteAsync($"INSERT INTO {t} VALUES (?, ?)", new object?[] { 2, (ulong)long.MaxValue + 1 });

        Assert.Equal("18446744073709551615", await OneAsync(db, $"SELECT d FROM {t} WHERE id = ?", 1));
        Assert.Equal("9223372036854775808", await OneAsync(db, $"SELECT d FROM {t} WHERE id = ?", 2));
    }
}
