using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriCoreDb.Tests.Support;

/// <summary>
/// A raw TCP peer that speaks real frame bytes on a real socket and misbehaves on
/// purpose. Nothing is mocked: the driver's own read and write paths are under test.
/// </summary>
internal sealed class ScriptedPeer : IAsyncDisposable
{
    public const byte TagHello = 0;
    public const byte TagAuth = 1;
    public const byte TagRequest = 2;
    public const byte TagResponse = 3;
    public const byte TagPing = 4;
    public const byte TagPong = 5;
    public const byte TagClose = 7;
    public const byte TagHelloOk = 8;
    public const byte TagAuthOk = 9;

    private readonly TcpListener _listener;
    private readonly Task _script;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Every frame the driver sent, in order, as (tag, body).</summary>
    public List<(byte Tag, byte[] Body)> Received { get; } = new();

    private ScriptedPeer(TcpListener listener, Func<ScriptedPeer, NetworkStream, Task> script)
    {
        _listener = listener;
        _script = Task.Run(async () =>
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                client.NoDelay = true;
                await using var stream = client.GetStream();
                await script(this, stream);
            }
            catch
            {
                // The driver hanging up mid-script is the normal end of a case.
            }
        });
    }

    public static ScriptedPeer Start(Func<ScriptedPeer, NetworkStream, Task> script)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new ScriptedPeer(listener, script);
    }

    public static ScriptedPeer Start(Func<NetworkStream, Task> script) => Start((_, s) => script(s));

    public Task<TriCoreClient> ConnectAsync(
        string? user = null, string? secret = null,
        TimeSpan? readTimeout = null, TimeSpan? connectTimeout = null, ulong? features = null) =>
        TriCoreClient.ConnectAsync("127.0.0.1", Port, user, secret, "scripted-peer-test",
            default, null, connectTimeout, readTimeout, features);

    /// <summary>Answer HELLO with HELLO_OK granting <paramref name="features"/>.</summary>
    public async Task<JsonNode> HandshakeAsync(NetworkStream s, ulong features)
    {
        var hello = await ExpectFrameAsync(s);
        await WriteFrameAsync(s, TagHelloOk, $$"""{"ok":true,"server_version":{"major":1,"minor":0},"features":{{features}}}""");
        return JsonNode.Parse(hello.Body)!;
    }

    /// <summary>Wait until the script has finished, for tests that inspect <see cref="Received"/>.</summary>
    public Task Completion => _script;

    public static async Task WriteHeaderAsync(Stream s, byte version, byte tag, uint length)
    {
        var header = new byte[6];
        header[0] = version;
        header[1] = tag;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(2, 4), length);
        await s.WriteAsync(header);
        await s.FlushAsync();
    }

    public static async Task WriteFrameAsync(Stream s, byte tag, object payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        await WriteHeaderAsync(s, 1, tag, (uint)body.Length);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    public static async Task WriteFrameAsync(Stream s, byte tag, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        await WriteHeaderAsync(s, 1, tag, (uint)body.Length);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    /// <summary>Read one whole frame from the driver and record it.</summary>
    public async Task<(byte Tag, byte[] Body)> ExpectFrameAsync(Stream s)
    {
        var header = new byte[6];
        await s.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2, 4));
        var body = new byte[length];
        if (length > 0) await s.ReadExactlyAsync(body);
        lock (Received) Received.Add((header[1], body));
        return (header[1], body);
    }

    /// <summary>Read frames until the driver closes the socket.</summary>
    public async Task DrainAsync(Stream s)
    {
        while (true) await ExpectFrameAsync(s);
    }

    public byte[] TagsReceived()
    {
        lock (Received) return Received.Select(f => f.Tag).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await Task.WhenAny(_script, Task.Delay(2000));
    }
}
