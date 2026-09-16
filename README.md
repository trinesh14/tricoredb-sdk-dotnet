# TriCoreDb for .NET

Official .NET client for [TriCoreDB](https://hub.docker.com/r/trinesh14/tricoredb), speaking
the native `tricore` wire protocol. It covers SQL (with server-side parameters and session
transactions), documents, vectors, graphs, cache and LLM context export over one
connection.

[![NuGet](https://img.shields.io/nuget/v/TriCoreDb?logo=nuget&label=nuget&color=blue&cacheSeconds=1800)](https://www.nuget.org/packages/TriCoreDb)
[![Downloads](https://img.shields.io/nuget/dt/TriCoreDb?label=downloads&color=blue&cacheSeconds=1800)](https://www.nuget.org/packages/TriCoreDb)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue?cacheSeconds=86400)](LICENSE)

- **No dependencies** beyond the .NET base class library.
- **Async throughout**, with `CancellationToken` on every call.
- **Server-side parameters.** Values never become part of the SQL text.
- **Transactions, connection pooling, TLS and mutual TLS.**

## Requirements

- .NET **8.0** or later
- A TriCoreDB server speaking protocol 1.0 (`tricore-server` 0.1.0-rc.1 or later).
  See [Running a server](#running-a-server).

## Installation

```
dotnet add package TriCoreDb
```

```csharp
using TriCoreDb;
```

## Running a server

The quickest way is the official Docker image,
[`trinesh14/tricoredb`](https://hub.docker.com/r/trinesh14/tricoredb).

**Local development** (no TLS and no encryption, for this machine only). Set
`TRICORE_ADMIN_PASSWORD` in your shell first. Then create the admin and start the server:

```bash
docker run --rm -v tricoredb-dev:/var/lib/tricoredb -e TRICORE_ADMIN_PASSWORD --entrypoint /usr/local/bin/tricore trinesh14/tricoredb:0.1.0-rc.1-r2 auth init-admin --user admin --password-env TRICORE_ADMIN_PASSWORD --data-dir /var/lib/tricoredb/data
docker run -d --name tricoredb-dev -p 127.0.0.1:8427:8427 -e TRICORE_TLS=off -e TRICORE_ENCRYPTION=off -e TRICORE_MODULES=all -v tricoredb-dev:/var/lib/tricoredb trinesh14/tricoredb:0.1.0-rc.1-r2
```

**Anything else:** by default the image runs with **TLS on** and an **encrypted data
volume**. Follow the quick start on the
[Docker Hub page](https://hub.docker.com/r/trinesh14/tricoredb) to create the certificate
and key, then connect with TLS as shown below.

`TRICORE_MODULES=all` enables every data model. The image's default is `sql`, `document`
and `cache`; a call to a disabled model throws a `TriCoreException` whose `ErrorCode` is
`engine.disabled`.

## Connect

```csharp
await using var db = await TriCoreClient.ConnectAsync("127.0.0.1", 8427, "admin", "pw");
```

`8427` is the server's default port. TLS stays **off** until you pass a `TlsOptions`. On a
plaintext connection the secret crosses the wire in the clear.

```csharp
var tls = new TlsOptions
{
    CaFile = "/etc/tricore/ca.pem",   // the PEM bundle that signed the server certificate
    ServerName = "db.internal",       // SNI, and the name checked in the certificate
};
await using var db = await TriCoreClient.ConnectAsync("db.internal", 8427, "admin", "pw", tls: tls);
```

When TLS is on, the driver verifies the chain against `CaFile` **alone** and checks the
hostname. It does not fall back to the OS root store, so leaving out `CaFile` gives an
empty trust store and the connection fails closed. `DangerAcceptInvalidCerts = true` turns
verification off and is meant for development only. For mTLS, set both `ClientCertFile`
and `ClientKeyFile`. A `Pool` applies the same `TlsOptions` to every connection it opens.

| Property | Default | Meaning |
| --- | --- | --- |
| `CaFile` | `null` (empty trust store) | PEM bundle used to verify the server |
| `ServerName` | `"localhost"` | Expected server name (SNI and certificate check) |
| `ClientCertFile` | `null` | PEM client certificate chain (mTLS) |
| `ClientKeyFile` | `null` | PEM private key for that certificate (mTLS) |
| `DangerAcceptInvalidCerts` | `false` | **Development only.** Skips all verification |

`ConnectAsync` also accepts `connectTimeout`, which bounds TCP, TLS, HELLO and AUTH, and
`readTimeout`, which bounds each reply. They are separate deadlines, so a short connect
budget never turns into a short query deadline.

## Quick start

```csharp
using System.Text;
using TriCoreDb;

await using var db = await TriCoreClient.ConnectAsync("127.0.0.1", 8427, "admin", "pw");

await db.CacheSetAsync("app", "greeting", Encoding.UTF8.GetBytes("world"));
string? hello = await db.CacheGetTextAsync("app", "greeting");

await db.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, name TEXT)");
await db.ExecuteAsync("INSERT INTO t VALUES (?, ?)", new object?[] { 1, "ada" });
Rows rows = await db.QueryAsync("SELECT id, name FROM t WHERE id = ?", new object?[] { 1 });
Console.WriteLine($"{rows.Columns[1]} = {rows[0][1]}");
```

## SQL

`QueryAsync` runs `SELECT` only. It is a read-only boundary, so a caller that holds only
read permission can never write through it. `ExecuteAsync` runs writes and DDL.

### Parameters are bound on the server

Each `?` placeholder is bound **server-side**. The values travel beside the statement as a
typed JSON array, so a value can never turn into syntax. This needs the `SERVER_PARAMS`
feature bit, which is negotiated at HELLO (`db.ServerParamsGranted`). If the server did not
grant it, the overloads that take arguments **throw by name and send nothing**. They never
fall back to pasting values into the SQL text. If you really want client-side rendering,
call `SqlParams.Bind(sql, args)` yourself.

How .NET values go on the wire:

| .NET value | Wire form | Notes |
| --- | --- | --- |
| `int`, `long`, other integers | JSON number | |
| `ulong` above `long.MaxValue` | exact JSON number | Never narrowed. The server binds it as a decimal and the column decides whether it fits. |
| `double`, `float` | JSON number | NaN and infinities are refused |
| `decimal` | **text**, `ToString(CultureInfo.InvariantCulture)` | Every digit survives |
| `byte[]`, `ReadOnlyMemory<byte>` | `"0x"` + lowercase hex | For BLOB columns |
| `bool`, `null`, `DBNull` | JSON `true`/`false`/`null` | |
| `string`, `char`, `Guid`, `DateTime` | JSON string | |

**The decimal trade-off.** A `decimal` goes out as text because a JSON number is read
through a double, which would lose the digits a `decimal` exists to keep. The server
accepts text for a `DECIMAL` column but not for a `DOUBLE` one, so a `decimal` bound into a
`DOUBLE` column **fails by name**. Pass a `double` for a `DOUBLE` column.

### Transactions

The two shapes below behave differently.

- `TransactionAsync(params SqlStatement[])` sends a whole `BEGIN … COMMIT` script as **one
  request**. It works on every node, including sharded and forwarding nodes.
- `BeginAsync` / `CommitAsync` / `RollbackAsync` keep a rollback boundary open **across
  requests on this connection**. They need the `SESSION_TXN` feature bit
  (`db.SessionTxnGranted`). Without it `BeginAsync` throws by name before sending anything,
  and never falls back to silent autocommit.

```csharp
await db.WithTransactionAsync(async tx =>          // commits on return
{
    await tx.ExecuteAsync("UPDATE accounts SET balance = balance - 10 WHERE id = ?", new object?[] { 1 });
    await tx.ExecuteAsync("UPDATE accounts SET balance = balance + 10 WHERE id = ?", new object?[] { 2 });
});                                                // rolls back and rethrows on any exception

await using (var tx = await db.BeginTransactionAsync())   // disposing rolls back unless committed
{
    await db.ExecuteAsync("INSERT INTO t VALUES (5, 'eve')");
    await tx.CommitAsync();
}
```

A block belongs to **its connection's socket**. The server refuses a `COMMIT` sent from any
other connection, and a dropped socket rolls the block back. `Pool.UseAsync` never returns
a connection to the pool with a block still open: it rolls the block back first and then
throws at the callback that left it open.

`ConnectAsync(features:)` overrides the feature bitmap sent in HELLO. For example,
`TriCoreClient.Features & ~TriCoreClient.FeatureSessionTxn` masks one bit out.

## Pool

```csharp
await using var pool = new Pool("127.0.0.1", 8427, "admin", "pw", size: 8);
long n = await pool.UseAsync(async c => long.Parse((await c.QueryAsync("SELECT COUNT(*) FROM t"))[0][0]));
```

The pool fills lazily and stays bounded. When it is exhausted, a caller waits until its
timeout and then gets `PoolTimeoutException`; the pool never grows. A server-side error
leaves the connection in the pool, while a poisoned or timed-out connection is retired.

## Other families

Every method takes an optional trailing `database` (default `"main"`) and a
`CancellationToken`.

- **Cache**: keys, TTLs and counters (`CacheSetAsync`, `CacheGetAsync`, `CacheIncrAsync`,
  `CacheSetNxAsync`, `CacheKeysAsync`, …), lists (`CacheLPushAsync` … `CacheLIndexAsync`),
  sets (`CacheSAddAsync` … `CacheSMembersAsync`), hashes (`CacheHSetAsync` …
  `CacheHLenAsync`) and streams (`CacheXAddAsync` … `CacheXTrimAsync`). Values are `byte[]`,
  and the `*Text` overloads handle UTF-8. A miss returns `null`, which is distinct from an
  empty value.
- **Documents**: collections, `DocumentInsertAsync`, `DocumentFindAsync` with
  `DocumentFilter`, `DocumentUpdateManyAsync` with `DocumentUpdate`, secondary indexes, and
  `DocumentAggregateAsync` with `AggregateStage`.
- **Vectors**: `VectorCreateCollectionAsync` (cosine, dot or l2), upsert, get, delete,
  `VectorSearchAsync` with metadata filters, describe and list.
- **Graph**: nodes, edges, neighbours, traversal, `GraphShortestPathAsync`,
  `GraphWeightedShortestPathAsync`, degree, paging, and `GraphQueryAsync` for the read-only
  Cypher subset.
- **LLM**: `LlmContextAsync` and `LlmSchemaAsync` render read-only context as TOON, JSON,
  Markdown or native output.
- **Admin**: `AdminPingAsync` and `AdminStatusAsync` (these need `[modules] cluster = true`).
  `PingAsync` is a transport-level liveness check.

`RequestAsync(JsonObject op)` sends any raw operation.

## Errors

Every failure the driver raises derives from `TriCoreException`. The subtypes are
`AuthException`, `ProtocolException`, `PoolTimeoutException`, `TriCoreTimeoutException`
(fatal to its connection) and `RequestNotSentException`.

`ErrorCode` holds the server's `diagnostics.error_code`. Branch on that code, not on the
message text.

### Leader redirects

When a write reaches a Raft follower, the server refuses it with `not_leader`:

```csharp
catch (TriCoreException ex) when (ex.IsNotLeader)
{
    // ex.ErrorCode == "not_leader"
    // ex.LeaderHint is "host:port", or null when the leader is unknown (mid-election)
}
```

`Response` exposes the same `ErrorCode`, `LeaderHint` and `IsNotLeader`. **The driver does
not follow the redirect.** Sending a write to another address is a policy decision
(reachability, credentials, whether the operation is safe to repeat), so it is left to the
application. A null `LeaderHint` means the destination is unknown. It does not mean the
error wasn't a redirect.

## Hardening

- Inbound frames are capped at 64 KiB for control frames and a protocol ceiling for
  responses. The cap is checked before any buffer is allocated.
- A frame with an unknown version is rejected.
- Overlapping requests on one connection are refused with `RequestNotSentException`, so
  one request can never read another's reply.
- A connection whose stream can no longer be trusted is marked poisoned and refuses
  further use.

## Building from source

```
scripts/build.ps1         # or scripts/build.sh
scripts/test.ps1          # xUnit; live tests start a private tricore-server
scripts/test.ps1 -Offline # only the tests that need no server
scripts/conformance.ps1   # cross-SDK conformance matrix (needs Node and the spec repo)
scripts/pack.ps1          # nupkg + snupkg into ./artifacts
```

Live tests look for the server binary in `TRICORE_SERVER_BIN`, then in a sibling
`tricore/tricore-db/target/{release,debug}` checkout. Each run starts its own server on an
ephemeral port with a temporary data directory and stops that process when it finishes.
With no binary to start, those tests are **skipped** rather than failed, so `dotnet test`
is green on a machine that has no server.

`examples/TriCoreDb.Examples` is a console app with one subcommand per family (`basic`,
`sql`, `nosql`, `vector`, `graph`, `cache`, `errors`, `concurrency`, `all`). It reads
`TRICOREDB_HOST`, `TRICOREDB_PORT`, `TRICOREDB_USER` and `TRICOREDB_PASSWORD`.

## License

Apache-2.0. See [LICENSE](LICENSE).
