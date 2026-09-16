using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriCoreDb.Tests;

public class SqlParamsTests
{
    private static JsonValueKind Kind(JsonNode? n) => n is null ? JsonValueKind.Null : n.GetValueKind();

    [Theory]
    [InlineData("1234.5678")]
    [InlineData("0.000000000000000001")]
    [InlineData("-12345678901234567890.12345678")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("1.10")]
    public void Decimal_goes_out_as_invariant_text_with_every_digit(string text)
    {
        var value = decimal.Parse(text, CultureInfo.InvariantCulture);
        var node = SqlParams.Param(value);
        Assert.Equal(JsonValueKind.String, Kind(node));
        Assert.Equal(text, node!.GetValue<string>());
    }

    [Fact]
    public void Decimal_text_ignores_a_comma_decimal_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", SqlParams.Param(1.5m)!.GetValue<string>());
            Assert.Equal("[\"-0.25\"]", SqlParams.Params(new object?[] { -0.25m }).ToJsonString());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Byte_array_goes_out_as_0x_hex()
    {
        byte[] blob = { 0x00, 0x01, 0xff, 0xfe, (byte)'\'', (byte)'\\', 0x00 };
        var node = SqlParams.Param(blob);
        Assert.Equal(JsonValueKind.String, Kind(node));
        Assert.Equal("0x0001fffe275c00", node!.GetValue<string>());
        Assert.Equal("0x", SqlParams.Param(Array.Empty<byte>())!.GetValue<string>());
    }

    [Fact]
    public void ReadOnlyMemory_of_bytes_goes_out_as_0x_hex()
    {
        var backing = new byte[] { 0xaa, 0xde, 0xad, 0xbe, 0xef, 0xbb };
        ReadOnlyMemory<byte> slice = backing.AsMemory(1, 4);
        var node = SqlParams.Param(slice);
        Assert.Equal("0xdeadbeef", node!.GetValue<string>());
    }

    [Fact]
    public void Ulong_past_long_max_stays_an_exact_positive_json_number()
    {
        var justPast = SqlParams.Param((ulong)long.MaxValue + 1);
        Assert.Equal(JsonValueKind.Number, Kind(justPast));
        Assert.Equal("9223372036854775808", justPast!.ToJsonString());

        var max = SqlParams.Param(ulong.MaxValue);
        Assert.Equal(JsonValueKind.Number, Kind(max));
        Assert.Equal("18446744073709551615", max!.ToJsonString());

        Assert.Equal("[18446744073709551615,-1]", SqlParams.Params(new object?[] { ulong.MaxValue, -1L }).ToJsonString());
    }

    [Fact]
    public void Integral_bool_null_and_text_pass_through_unchanged()
    {
        var arr = SqlParams.Params(new object?[] { 42, long.MinValue, (byte)7, true, null, DBNull.Value, "O'Hara", 'x' });
        var wire = JsonDocument.Parse(arr.ToJsonString()).RootElement;
        Assert.Equal(8, wire.GetArrayLength());
        Assert.Equal("42", wire[0].GetRawText());
        Assert.Equal("-9223372036854775808", wire[1].GetRawText());
        Assert.Equal("7", wire[2].GetRawText());
        Assert.Equal(JsonValueKind.True, wire[3].ValueKind);
        Assert.Equal(JsonValueKind.Null, wire[4].ValueKind);
        Assert.Equal(JsonValueKind.Null, wire[5].ValueKind);
        Assert.Equal("O'Hara", wire[6].GetString());
        Assert.Equal("x", wire[7].GetString());
    }

    [Fact]
    public void Temporal_and_guid_values_use_their_documented_text()
    {
        Assert.Equal("2026-09-14 13:05:07.250",
            SqlParams.Param(new DateTime(2026, 9, 14, 13, 5, 7, 250))!.GetValue<string>());
        var g = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", SqlParams.Param(g)!.GetValue<string>());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_doubles_are_refused(double d)
    {
        Assert.Throws<ArgumentException>(() => SqlParams.Param(d));
    }

    [Fact]
    public void An_unsupported_type_is_refused_by_name()
    {
        var e = Assert.Throws<ArgumentException>(() => SqlParams.Param(new Uri("http://example.com")));
        Assert.Contains("System.Uri", e.Message);
    }

    [Fact]
    public void Bind_doubles_quotes_and_leaves_placeholders_inside_literals_alone()
    {
        var sql = SqlParams.Bind("SELECT * FROM t WHERE note = 'why?' AND name = ? AND n = ?", "O'Hara", 1.5m);
        Assert.Equal("SELECT * FROM t WHERE note = 'why?' AND name = 'O''Hara' AND n = 1.5", sql);
    }

    [Fact]
    public void Bind_refuses_a_count_mismatch_in_either_direction()
    {
        Assert.Throws<ArgumentException>(() => SqlParams.Bind("SELECT ?, ?", 1));
        Assert.Throws<ArgumentException>(() => SqlParams.Bind("SELECT ?", 1, 2));
        Assert.Throws<ArgumentException>(() => SqlParams.Bind("SELECT 'unterminated"));
    }

    [Fact]
    public void Literal_has_no_byte_array_form()
    {
        Assert.Throws<ArgumentException>(() => SqlParams.Literal(new byte[] { 1 }));
    }
}
