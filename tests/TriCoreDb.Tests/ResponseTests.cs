using System.Text.Json;

namespace TriCoreDb.Tests;

public class ResponseTests
{
    private static Response Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new Response(doc.RootElement.Clone());
    }

    [Fact]
    public void Not_leader_with_a_hint_is_readable_from_the_response()
    {
        var r = Parse("""{"request_id":"r1","status":"error","data":{"Message":"not the raft leader"},"diagnostics":{"error_code":"not_leader","leader_hint":"10.9.9.7:8427"}}""");
        Assert.Equal("not_leader", r.ErrorCode);
        Assert.True(r.IsNotLeader);
        Assert.Equal("10.9.9.7:8427", r.LeaderHint);
        Assert.Equal("r1", r.RequestId);
        Assert.Equal("error", r.Status);
    }

    [Fact]
    public void Not_leader_without_a_hint_keeps_the_code_and_a_null_hint()
    {
        var r = Parse("""{"request_id":"r1","status":"error","diagnostics":{"error_code":"not_leader"}}""");
        Assert.True(r.IsNotLeader);
        Assert.Null(r.LeaderHint);
    }

    [Fact]
    public void Another_code_is_not_a_redirect()
    {
        var r = Parse("""{"request_id":"r1","status":"error","diagnostics":{"error_code":"request.invalid"}}""");
        Assert.False(r.IsNotLeader);
        Assert.Equal("request.invalid", r.ErrorCode);
        Assert.Null(r.LeaderHint);
    }

    [Fact]
    public void An_ok_response_has_no_code_and_carries_its_diagnostics()
    {
        var r = Parse("""{"request_id":"r2","status":"ok","data":{"Json":{"rows_affected":3}},"diagnostics":{"route":"local","elapsed_ms":4,"warnings":["w1"]}}""");
        Assert.Null(r.ErrorCode);
        Assert.False(r.IsNotLeader);
        Assert.Equal("local", r.Route);
        Assert.Equal(4, r.ElapsedMs);
        Assert.Equal(new[] { "w1" }, r.Warnings);
        Assert.Equal(3, r.Data.GetProperty("Json").GetProperty("rows_affected").GetInt64());
    }

    [Fact]
    public void Exception_carries_code_hint_and_redirect_flag()
    {
        var e = new TriCoreException("refused", TriCoreException.ErrorCodeNotLeader, "db2:8427");
        Assert.True(e.IsNotLeader);
        Assert.Equal("db2:8427", e.LeaderHint);
        Assert.False(new TriCoreException("plain").IsNotLeader);
        Assert.Null(new TriCoreException("plain").ErrorCode);
    }
}
