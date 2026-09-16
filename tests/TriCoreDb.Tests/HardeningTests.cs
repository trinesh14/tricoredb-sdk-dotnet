using System.Diagnostics;
using TriCoreDb.Tests.Support;

namespace TriCoreDb.Tests;

/// <summary>Frame ceilings, header versions, deadlines and handshake integrity, each against
/// a peer that misbehaves on a real socket.</summary>
public class HardeningTests
{
    private static Task Grant(ScriptedPeer p, System.Net.Sockets.NetworkStream s) =>
        p.HandshakeAsync(s, TriCoreClient.FeatureCorrelationId);

    [Fact(Timeout = 20_000)]
    public async Task A_response_declaring_4_GiB_is_refused_before_allocation()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteHeaderAsync(s, 1, ScriptedPeer.TagResponse, uint.MaxValue);
            await Task.Delay(TimeSpan.FromMinutes(1));
        });
        var db = await peer.ConnectAsync();
        var e = await Assert.ThrowsAsync<ProtocolException>(() => db.ExecuteAsync("SELECT 1"));
        Assert.Contains("exceeds the protocol ceiling", e.Message);
        Assert.True(db.IsPoisoned);
    }

    [Fact(Timeout = 20_000)]
    public async Task A_control_frame_takes_the_64_KiB_ceiling()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteHeaderAsync(s, 1, ScriptedPeer.TagPong, 1024 * 1024);
            await Task.Delay(TimeSpan.FromMinutes(1));
        });
        var db = await peer.ConnectAsync();
        var e = await Assert.ThrowsAsync<ProtocolException>(() => db.PingAsync());
        Assert.Contains("65536", e.Message);
    }

    [Fact(Timeout = 20_000)]
    public async Task An_unknown_frame_version_is_refused_and_the_connection_stays_poisoned()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteHeaderAsync(s, 99, ScriptedPeer.TagPong, 0);
            await Task.Delay(TimeSpan.FromMinutes(1));
        });
        var db = await peer.ConnectAsync();
        var first = await Assert.ThrowsAsync<ProtocolException>(() => db.PingAsync());
        Assert.Contains("version 99", first.Message);
        var again = await Assert.ThrowsAsync<ProtocolException>(() => db.PingAsync());
        Assert.Contains("cannot be reused", again.Message);
    }

    [Fact(Timeout = 20_000)]
    public async Task A_2_MiB_response_arrives_whole()
    {
        var big = new string('a', 2 * 1024 * 1024);
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteFrameAsync(s, ScriptedPeer.TagResponse,
                new { request_id = "x", status = "ok", data = new { Message = big } });
            await p.DrainAsync(s);
        });
        await using var db = await peer.ConnectAsync();
        var resp = await db.ExecuteAsync("SELECT 1");
        Assert.Equal("ok", resp.Status);
        Assert.Equal(big.Length, resp.Data.GetProperty("Message").GetString()!.Length);
    }

    [Fact(Timeout = 20_000)]
    public async Task A_status_this_build_does_not_know_fails_closed()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteFrameAsync(s, ScriptedPeer.TagResponse,
                new { request_id = "x", status = "degraded", data = new { Message = "partially applied" } });
            await p.DrainAsync(s);
        });
        await using var db = await peer.ConnectAsync();
        var e = await Assert.ThrowsAsync<TriCoreException>(() => db.ExecuteAsync("SELECT 1"));
        Assert.Contains("degraded", e.Message);
        Assert.Contains("partially applied", e.Message);
    }

    [Fact(Timeout = 20_000)]
    public async Task Dispose_is_bounded_when_the_peer_never_answers_bye()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await Task.Delay(TimeSpan.FromMinutes(1));
        });
        var db = await peer.ConnectAsync();
        var sw = Stopwatch.StartNew();
        await db.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"DisposeAsync took {sw.ElapsedMilliseconds}ms");
    }

    [Fact(Timeout = 20_000)]
    public async Task A_read_deadline_is_a_typed_fatal_timeout()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await Task.Delay(TimeSpan.FromMinutes(1));
        });
        var db = await peer.ConnectAsync(readTimeout: TimeSpan.FromMilliseconds(300));
        var e = await Assert.ThrowsAsync<TriCoreTimeoutException>(() => db.PingAsync());
        Assert.IsAssignableFrom<TriCoreException>(e);
        Assert.True(db.IsPoisoned);
    }

    [Fact(Timeout = 20_000)]
    public async Task The_connect_budget_does_not_become_a_query_deadline()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await Task.Delay(900);
            await ScriptedPeer.WriteFrameAsync(s, ScriptedPeer.TagResponse,
                new { request_id = "x", status = "ok", data = new { Message = "slow" } });
            await p.DrainAsync(s);
        });
        await using var db = await peer.ConnectAsync(connectTimeout: TimeSpan.FromMilliseconds(400));
        var resp = await db.ExecuteAsync("SELECT 1");
        Assert.Equal("slow", resp.Data.GetProperty("Message").GetString());
    }

    [Fact(Timeout = 20_000)]
    public async Task Hello_ok_with_ok_false_is_a_refusal()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteFrameAsync(s, ScriptedPeer.TagHelloOk, new { ok = false, message = "this node is draining" });
        });
        var e = await Assert.ThrowsAsync<ProtocolException>(() => peer.ConnectAsync());
        Assert.Contains("draining", e.Message);
    }

    [Fact(Timeout = 20_000)]
    public async Task Auth_ok_with_ok_false_is_a_refusal_that_does_not_echo_the_secret()
    {
        const string secret = "s3cr3t-do-not-echo";
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await ScriptedPeer.WriteFrameAsync(s, ScriptedPeer.TagAuthOk, new { ok = false, message = "bad credentials" });
        });
        var e = await Assert.ThrowsAsync<AuthException>(() => peer.ConnectAsync(user: "admin", secret: secret));
        Assert.Contains("bad credentials", e.Message);
        Assert.DoesNotContain(secret, e.ToString());
    }

    [Fact(Timeout = 20_000)]
    public async Task A_concurrent_ping_cannot_steal_a_requests_reply()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await p.ExpectFrameAsync(s);
            await Task.Delay(TimeSpan.FromMinutes(1));
        });
        var db = await peer.ConnectAsync(readTimeout: TimeSpan.FromSeconds(3));
        var inflight = db.ExecuteAsync("SELECT 1");
        await Task.Delay(200);
        var e = await Assert.ThrowsAsync<RequestNotSentException>(() => db.PingAsync());
        Assert.Contains("already in flight", e.Message);
        await Assert.ThrowsAsync<TriCoreTimeoutException>(() => inflight);
    }

    [Fact(Timeout = 20_000)]
    public async Task An_outbound_control_frame_over_64_KiB_is_refused_locally()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await Grant(p, s);
            await Task.Delay(TimeSpan.FromMinutes(1));
        });
        var db = await peer.ConnectAsync();
        var e = await Assert.ThrowsAsync<ProtocolException>(() => db.CancelAsync(new string('k', 70 * 1024)));
        Assert.Contains("control frames", e.Message);
    }

    [Fact(Timeout = 20_000)]
    public async Task Connecting_to_a_black_hole_is_bounded_by_the_connect_budget()
    {
        await using var peer = ScriptedPeer.Start(async (p, s) =>
        {
            await p.ExpectFrameAsync(s);
            await Task.Delay(TimeSpan.FromMinutes(1));
        });
        var sw = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<TriCoreTimeoutException>(() =>
            peer.ConnectAsync(connectTimeout: TimeSpan.FromMilliseconds(500)));
        Assert.Contains("did not complete within", e.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }
}
