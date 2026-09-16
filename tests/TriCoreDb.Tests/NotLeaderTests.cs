using System.Net;
using System.Net.Sockets;
using TriCoreDb.Tests.Support;

namespace TriCoreDb.Tests;

/// <summary>
/// A <c>not_leader</c> refusal is typed: code, optional <c>host:port</c> hint, and a flag.
/// The driver reports it and never follows it.
/// </summary>
public class NotLeaderTests
{
    private static async Task<TriCoreException> RefusalAsync(string responseJson)
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await p.HandshakeAsync(s, TriCoreClient.Features);
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteFrameAsync(s, ScriptedPeer.TagResponse, responseJson);
            await p.DrainAsync(s);
        });
        await using var db = await peer.ConnectAsync();
        return await Assert.ThrowsAsync<TriCoreException>(() =>
            db.ExecuteAsync("INSERT INTO t VALUES (?)", new object?[] { 1 }));
    }

    [Fact]
    public async Task A_redirect_with_a_leader_carries_its_address()
    {
        var e = await RefusalAsync(
            """{"request_id":"r1","status":"error","data":{"Message":"not the raft leader — send writes to `n2`"},"diagnostics":{"error_code":"not_leader","leader_hint":"10.9.9.7:8427"}}""");
        Assert.Equal(TriCoreException.ErrorCodeNotLeader, e.ErrorCode);
        Assert.True(e.IsNotLeader);
        Assert.Equal("10.9.9.7:8427", e.LeaderHint);
        Assert.Contains("not the raft leader", e.Message);
    }

    [Fact]
    public async Task A_redirect_mid_election_has_the_code_and_no_hint()
    {
        var e = await RefusalAsync(
            """{"request_id":"r1","status":"error","data":{"Message":"not the raft leader"},"diagnostics":{"error_code":"not_leader"}}""");
        Assert.True(e.IsNotLeader);
        Assert.Null(e.LeaderHint);
    }

    [Fact]
    public async Task An_ordinary_failure_is_not_a_redirect()
    {
        var e = await RefusalAsync(
            """{"request_id":"r1","status":"error","data":{"Message":"syntax error"},"diagnostics":{"error_code":"request.invalid"}}""");
        Assert.False(e.IsNotLeader);
        Assert.Equal("request.invalid", e.ErrorCode);
        Assert.Null(e.LeaderHint);
    }

    [Fact]
    public async Task The_driver_does_not_follow_the_redirect()
    {
        var leader = new TcpListener(IPAddress.Loopback, 0);
        leader.Start();
        try
        {
            var hint = $"127.0.0.1:{((IPEndPoint)leader.LocalEndpoint).Port}";
            var accept = leader.AcceptTcpClientAsync();
            var e = await RefusalAsync(
                $$$"""{"request_id":"r1","status":"error","data":{"Message":"not the raft leader"},"diagnostics":{"error_code":"not_leader","leader_hint":"{{{hint}}}"}}""");
            Assert.Equal(hint, e.LeaderHint);
            var winner = await Task.WhenAny(accept, Task.Delay(750));
            Assert.NotSame(accept, winner);
        }
        finally
        {
            leader.Stop();
        }
    }
}
