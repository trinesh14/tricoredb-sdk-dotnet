# Changelog

All notable changes to the `TriCoreDb` NuGet package are recorded here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses
[Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-09-16

First standalone release, ported from the in-tree driver at
`tricore-db/sdk/dotnet/TriCoreDb`.

### Added

- `TriCoreClient`: the native `tricore` wire protocol over TCP or TLS (TLS 1.2/1.3,
  custom CA, hostname check, mTLS), HELLO feature negotiation, and password AUTH.
- SQL: `ExecuteAsync`/`QueryAsync`, with `?` parameters bound **server-side** when the
  server grants `FeatureServerParams`. The overloads fail by name when it is not granted;
  they never fall back to client-side rendering.
- Transactions: one-request `TransactionAsync(...)`, and session transactions
  (`BeginAsync`/`CommitAsync`/`RollbackAsync`, `WithTransactionAsync`,
  `BeginTransactionAsync`) gated on `FeatureSessionTxn`, which also fail by name when the
  bit is not granted.
- Documents, vectors, graphs, cache (strings, counters, lists, sets, hashes, streams), LLM
  context/schema export and admin ping/status: all 82 client operations of the
  conformance scenario.
- `Pool`: a bounded, lazily-filled connection pool that never returns a connection
  mid-transaction.
- Errors: a `TriCoreException` hierarchy with `ErrorCode`, `LeaderHint` and `IsNotLeader`,
  mirrored on `Response`. Leader redirects are reported, never followed.
- Parameter encoding: `decimal` goes out as invariant-culture text, `byte[]` and
  `ReadOnlyMemory<byte>` as `0x` hex, and a `ulong` above `long.MaxValue` as an exact
  JSON number.
- Hardening: frame-size ceilings, frame-version check, an in-flight guard against
  overlapping requests, separate connect and read deadlines, and poisoning of a connection
  whose stream can no longer be trusted.

### Changed from the in-tree driver

- `Response.IsNotLeader` added, matching `TriCoreException.IsNotLeader`.
- Package metadata, SourceLink, a symbol package, and a README written for NuGet.
