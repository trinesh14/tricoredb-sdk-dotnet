using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TriCoreDb.Tests.Support;

/// <summary>
/// A private tricore-server on an ephemeral port with its own temporary data directory.
/// </summary>
/// <remarks>
/// The binary comes from <c>TRICORE_SERVER_BIN</c>, or a <c>tricore-db</c> checkout next to
/// this repository's parent (<c>../../tricore/tricore-db/target/{release,debug}</c>). The
/// suite never builds the server. It is stopped through its own <see cref="Process"/>
/// handle, so only the instance this test started is ever killed.
/// </remarks>
public sealed class TriCoreServer : IAsyncDisposable
{
    public const string User = "admin";
    public const string Secret = "pw";

    private static readonly Regex Listening = new(@"^listening on\s+([0-9.]+):(\d+)", RegexOptions.Compiled);

    private readonly Process _proc;
    private readonly string _dir;
    private readonly StringBuilder _log;

    public string Host { get; }
    public int Port { get; }
    public int Pid => _proc.Id;
    public string Directory => _dir;

    private TriCoreServer(Process proc, string dir, StringBuilder log, string host, int port)
    {
        _proc = proc;
        _dir = dir;
        _log = log;
        Host = host;
        Port = port;
    }

    public string Log
    {
        get { lock (_log) return _log.ToString(); }
    }

    public Task<TriCoreClient> ConnectAsync(ulong? features = null, TlsOptions? tls = null, string clientName = "tricoredb-dotnet-tests") =>
        TriCoreClient.ConnectAsync(Host, Port, User, Secret, clientName, default, tls, features: features);

    public Pool NewPool(int size, TlsOptions? tls = null) =>
        new(Host, Port, User, Secret, size, "tricoredb-dotnet-tests-pool", tls);

    public static string FindBinary() =>
        TryFindBinary(out var path, out var why) ? path : throw new InvalidOperationException(why);

    /// <summary>Why the live tests cannot run, or <c>null</c> when they can.</summary>
    public static string? SkipReason => TryFindBinary(out _, out var why) ? null : why;

    /// <summary>Locate the server binary without throwing when there is none.</summary>
    public static bool TryFindBinary(out string path, out string why)
    {
        var exe = OperatingSystem.IsWindows() ? "tricore-server.exe" : "tricore-server";
        var env = Environment.GetEnvironmentVariable("TRICORE_SERVER_BIN");
        if (!string.IsNullOrEmpty(env))
        {
            if (File.Exists(env))
            {
                path = env;
                why = "";
                return true;
            }

            path = "";
            why = $"TRICORE_SERVER_BIN points at `{env}`, which does not exist";
            return false;
        }

        var candidates = new List<string>();
        var root = RepoRoot();
        if (root is not null)
        {
            foreach (var profile in new[] { "release", "debug" })
                candidates.Add(Path.GetFullPath(Path.Combine(root, "..", "..", "tricore", "tricore-db", "target", profile, exe)));
        }
        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;
            path = c;
            why = "";
            return true;
        }

        path = "";
        why = "no tricore-server binary: set TRICORE_SERVER_BIN, or run one from the Docker image (see the README). Looked in:\n  "
            + string.Join("\n  ", candidates);
        return false;
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TriCoreDb.sln")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>Start a server. <paramref name="tlsSection"/> replaces the default
    /// <c>[tls] enabled = false</c> block.</summary>
    public static async Task<TriCoreServer> StartAsync(string nodeId, string? tlsSection = null, string? dir = null)
    {
        var bin = FindBinary();
        dir ??= System.IO.Directory.CreateTempSubdirectory("tricore-dotnet-sdk-").FullName;
        var data = Path.Combine(dir, "data").Replace('\\', '/');
        var cfg = Path.Combine(dir, "tricore.toml");
        await File.WriteAllTextAsync(cfg, $"""
            [server]
            host = "127.0.0.1"
            port = 0
            node_id = "{nodeId}"
            region_id = "sdk-dotnet-tests"
            shutdown_grace_secs = 1

            [modules]
            sql = true
            document = true
            cache = true
            vector = true
            graph = true
            llm = true
            cluster = true

            [storage]
            data_dir = "{data}"
            fsync = false

            [security]
            auth_mode = "password"
            dev_auth = true

            {tlsSection ?? "[tls]\nenabled = false"}

            [observability]
            port = 0
            """);

        var psi = new ProcessStartInfo(bin)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = dir,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(cfg);

        var log = new StringBuilder();
        var bound = new TaskCompletionSource<(string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) log.AppendLine(e.Data);
            var m = Listening.Match(e.Data.Trim());
            if (m.Success) bound.TrySetResult((m.Groups[1].Value, int.Parse(m.Groups[2].Value)));
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) log.AppendLine(e.Data);
        };
        proc.Exited += (_, _) => bound.TrySetException(new InvalidOperationException("tricore-server exited during startup"));
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var winner = await Task.WhenAny(bound.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (winner != bound.Task || bound.Task.IsFaulted)
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { /* already gone */ }
            string text;
            lock (log) text = log.ToString();
            throw new InvalidOperationException($"tricore-server ({bin}) did not report a listen address:\n{text}");
        }
        var (host, port) = await bound.Task;
        return new TriCoreServer(proc, dir, log, host, port);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_proc.HasExited) _proc.Kill(true);
            await _proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch
        {
            // already gone
        }
        _proc.Dispose();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.IO.Directory.Delete(_dir, recursive: true);
                break;
            }
            catch
            {
                await Task.Delay(200);
            }
        }
    }
}

/// <summary>One shared server for every test in the <c>live</c> collection.</summary>
public sealed class LiveServer : IAsyncLifetime
{
    private TriCoreServer? _server;

    public TriCoreServer Server => _server ?? throw new InvalidOperationException("server not started");

    public async Task InitializeAsync()
    {
        // Nothing to start when there is no binary: every test that needs one is
        // skipped, so this must not throw and fail the whole collection.
        if (TriCoreServer.SkipReason is not null) return;
        _server = await TriCoreServer.StartAsync("sdk-dotnet-tests");
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
    }
}

[CollectionDefinition("live")]
public sealed class LiveCollection : ICollectionFixture<LiveServer>
{
}

internal static class Names
{
    public static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];
}
