using System.Text.Json.Nodes;
using TriCoreDb.Tests.Support;

namespace TriCoreDb.Tests;

/// <summary>
/// Server-side parameters and session transactions are negotiated at HELLO. When a bit is
/// not granted the driver refuses by name and sends nothing; it never falls back.
/// </summary>
public class FeatureNegotiationTests
{
    [Fact]
    public async Task Hello_announces_every_feature_this_build_understands()
    {
        JsonNode? hello = null;
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            hello = await p.HandshakeAsync(s, TriCoreClient.Features);
            await p.DrainAsync(s);
        });
        await using (var db = await peer.ConnectAsync())
        {
            Assert.Equal(TriCoreClient.Features, db.GrantedFeatures);
            Assert.True(db.ServerParamsGranted);
            Assert.True(db.SessionTxnGranted);
        }
        Assert.Equal(7UL, TriCoreClient.Features);
        Assert.Equal(TriCoreClient.Features, hello!["features"]!.GetValue<ulong>());
    }

    [Fact]
    public async Task A_masked_feature_is_not_asked_for()
    {
        JsonNode? hello = null;
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            hello = await p.HandshakeAsync(s, TriCoreClient.FeatureCorrelationId);
            await p.DrainAsync(s);
        });
        var mask = TriCoreClient.Features & ~TriCoreClient.FeatureSessionTxn;
        await using (var db = await peer.ConnectAsync(features: mask))
        {
            Assert.False(db.SessionTxnGranted);
        }
        Assert.Equal(mask, hello!["features"]!.GetValue<ulong>());
    }

    [Fact]
    public async Task Parameters_without_SERVER_PARAMS_fail_by_name_and_send_nothing()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await p.HandshakeAsync(s, TriCoreClient.FeatureCorrelationId | TriCoreClient.FeatureSessionTxn);
            await p.DrainAsync(s);
        });
        await using (var db = await peer.ConnectAsync())
        {
            Assert.False(db.ServerParamsGranted);
            var exec = await Assert.ThrowsAsync<TriCoreException>(() =>
                db.ExecuteAsync("INSERT INTO t VALUES (?)", new object?[] { 1 }));
            Assert.Contains("SERVER_PARAMS", exec.Message);
            Assert.Contains("will not silently render", exec.Message);
            var query = await Assert.ThrowsAsync<TriCoreException>(() =>
                db.QueryAsync("SELECT * FROM t WHERE id = ?", new object?[] { 1 }));
            Assert.Contains("SERVER_PARAMS", query.Message);
            var tx = await Assert.ThrowsAsync<TriCoreException>(() =>
                db.TransactionAsync(new SqlStatement("INSERT INTO t VALUES (?)", 1)));
            Assert.Contains("SERVER_PARAMS", tx.Message);
        }
        await peer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(ScriptedPeer.TagRequest, peer.TagsReceived());
    }

    [Fact]
    public async Task Begin_without_SESSION_TXN_fails_by_name_and_sends_nothing()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await p.HandshakeAsync(s, TriCoreClient.FeatureCorrelationId | TriCoreClient.FeatureServerParams);
            await p.DrainAsync(s);
        });
        await using (var db = await peer.ConnectAsync())
        {
            Assert.False(db.SessionTxnGranted);
            var e = await Assert.ThrowsAsync<TriCoreException>(() => db.BeginAsync());
            Assert.Contains("did not grant session transactions", e.Message);
            Assert.Contains("TransactionAsync(...)", e.Message);
            await Assert.ThrowsAsync<TriCoreException>(() => db.WithTransactionAsync(_ => Task.CompletedTask));
            await Assert.ThrowsAsync<TriCoreException>(() => db.BeginTransactionAsync());
            Assert.False(db.InTransaction);
        }
        await peer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(ScriptedPeer.TagRequest, peer.TagsReceived());
    }

    [Fact]
    public async Task A_server_that_sends_no_features_field_grants_nothing()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteFrameAsync(s, ScriptedPeer.TagHelloOk, new { ok = true });
            await p.DrainAsync(s);
        });
        await using var db = await peer.ConnectAsync();
        Assert.Equal(0UL, db.GrantedFeatures);
        await Assert.ThrowsAsync<TriCoreException>(() => db.ExecuteAsync("SELECT ?", new object?[] { 1 }));
        await Assert.ThrowsAsync<TriCoreException>(() => db.BeginAsync());
    }

    [Fact]
    public async Task Zero_arguments_need_no_capability()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await p.HandshakeAsync(s, 0);
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteFrameAsync(s, ScriptedPeer.TagResponse,
                """{"request_id":"x","status":"ok","data":{"Rows":{"columns":["one"],"rows":[["1"]]}}}""");
            await p.DrainAsync(s);
        });
        await using var db = await peer.ConnectAsync();
        var rows = await db.QueryAsync("SELECT 1", Array.Empty<object?>());
        Assert.Equal(new[] { "one" }, rows.Columns);
        Assert.Equal("1", rows[0][0]);
    }

    [Fact]
    public async Task Bound_parameters_reach_the_wire_in_their_documented_spellings()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await p.HandshakeAsync(s, TriCoreClient.Features);
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteFrameAsync(s, ScriptedPeer.TagResponse,
                """{"request_id":"x","status":"ok","data":{"Json":{"rows_affected":1}}}""");
            await p.DrainAsync(s);
        });
        await using (var db = await peer.ConnectAsync())
        {
            await db.ExecuteAsync("INSERT INTO t VALUES (?, ?, ?, ?, ?)",
                new object?[] { 12345678901234567890.123456789m, new byte[] { 0, 0xff }, (ReadOnlyMemory<byte>)new byte[] { 0xab }, ulong.MaxValue, null });
        }
        await peer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var request = peer.Received.First(f => f.Tag == ScriptedPeer.TagRequest);
        var body = JsonNode.Parse(request.Body)!;
        var exec = body["op"]!["Sql"]!["Exec"]!;
        Assert.Equal("INSERT INTO t VALUES (?, ?, ?, ?, ?)", exec["sql"]!.GetValue<string>());
        Assert.Equal("[\"12345678901234567890.123456789\",\"0x00ff\",\"0xab\",18446744073709551615,null]",
            exec["params"]!.ToJsonString());
    }
}
