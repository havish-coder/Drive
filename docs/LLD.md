# CloudSync — Low-Level Design

## 1. Requirements recap

| Functional | Non-functional |
|---|---|
| Streaming upload (raw body or multipart) | Bounded memory per upload (no whole-file buffering) |
| Fixed-size chunking + Content-Defined Chunking (FastCDC) | Thread-safe concurrent writes (no races on disk or DB) |
| SHA-256 per chunk, dedup **before** write | Gateway ↔ storage worker over gRPC |
| Content-addressable blob store (`ab/cd/<hash>`) | Integrity: every chunk re-verified on write **and** read |
| Ordered reconstruction on download | 1 GB file chunk+hash+lookup under concurrent load |
| Delta sync: client negotiates, uploads only missing chunks | |

## 2. Solution layout

```
src/
  CloudSync.Core            domain, interfaces, chunkers, ChunkStore, UploadPipeline, FileService  (no infra deps)
  CloudSync.Infrastructure  SqliteMetadataRepository, LocalFileSystemBlobStore, GrpcBlobStore
  CloudSync.Contracts       blob_storage.proto (gRPC client + server stubs)
  CloudSync.Api             REST gateway (ASP.NET Core minimal APIs)
  CloudSync.StorageWorker   gRPC storage node wrapping LocalFileSystemBlobStore
  CloudSync.Cli             delta-sync client (push / pull)
tests/
  CloudSync.Tests           unit + in-process integration tests
```

Dependency rule (DIP): `Api/Worker/Cli → Infrastructure → Core`. Core depends on nothing concrete.

## 3. Class diagram

```
                     ┌───────────────────────────┐
  REST endpoints ───▶│ FileService  «Facade»     │
                     │ UploadAsync / DownloadAsync│
                     │ NegotiateAsync / CommitAsync│
                     └──┬──────────┬──────────┬───┘
                        │          │          │
             ┌──────────▼───┐ ┌────▼──────┐ ┌─▼─────────────────────┐
             │ IChunker     │ │UploadPipe-│ │ IMetadataRepository   │
             │ «Strategy»   │ │line «P/C» │ │ «Repository»          │
             └──────▲───────┘ └────┬──────┘ └─▲─────────────────────┘
                    │              │          │ SqliteMetadataRepository
          ChunkerBase «Template»   │          │
          ├ FixedSizeChunker       ▼          │
          └ FastCdcChunker   ┌────────────────┴─┐
                             │ ChunkStore       │  dedup + single-flight
                             └────────┬─────────┘
                                      ▼
                             ┌──────────────────┐
                             │ IBlobStore       │ «Strategy / Adapter»
                             └──▲───────────▲───┘
             LocalFileSystemBlobStore    GrpcBlobStore «Remote Proxy» ──gRPC──▶ StorageWorker
                                                                               └ LocalFileSystemBlobStore
```

### Key interfaces

```csharp
interface IChunker {
    string Name { get; }
    IAsyncEnumerable<RawChunk> ChunkAsync(PipeReader source, CancellationToken ct);
}
// RawChunk: SequenceIndex, Offset, pooled buffer (IMemoryOwner<byte>), Length — IDisposable

interface IBlobStore {
    string GetStorageKey(string hash);                               // "ab/cd/abcd…"
    Task<bool> PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct); // true = physically written
    Task<int>  ReadAsync(string hash, Memory<byte> destination, CancellationToken ct);
    Task<bool> ExistsAsync(string hash, CancellationToken ct);
}

interface IMetadataRepository {
    Task<bool> ChunkExistsAsync(string hash, CancellationToken ct);
    Task<IReadOnlyList<string>> FindMissingChunksAsync(IEnumerable<string> hashes, CancellationToken ct);
    Task<bool> TryAddChunkAsync(ChunkRecord chunk, CancellationToken ct);   // INSERT OR IGNORE
    Task SaveFileAsync(FileRecord file, IReadOnlyList<FileChunkRecord> chunks, CancellationToken ct); // 1 txn
    Task<FileManifest?> GetManifestAsync(Guid fileId, CancellationToken ct);
    Task<StorageStats> GetStatsAsync(CancellationToken ct);
}
```

### Patterns and why

| Pattern | Where | Problem it solves |
|---|---|---|
| Strategy | `IChunker`, `IBlobStore` | Swap FixedSize↔FastCDC, Local↔gRPC via config, no code edits (OCP) |
| Template Method | `ChunkerBase.ChunkAsync` → abstract `FindCutPoint` | Pipe/buffer mechanics written once; subclasses only decide *where* to cut |
| Repository | `IMetadataRepository` | SQL isolated; SQLite→Postgres is a new class |
| Remote Proxy / Adapter | `GrpcBlobStore` | Remote worker looks like a local `IBlobStore` |
| Facade | `FileService` | Endpoints call one method; orchestration hidden |
| Producer–Consumer | `UploadPipeline` (bounded `Channel<T>`) | Overlap IO + hashing; backpressure caps memory |
| Single-flight | `ChunkStore` in-flight map | N concurrent writers of the same new hash → 1 physical write |
| Object Pool | `ArrayPool<byte>.Shared` | No 4 MB LOH allocation per chunk |

## 4. Schema (SQLite, WAL, `foreign_keys=ON`, `busy_timeout=5000`)

> Schema v1 below. v2 (accounts, folders, sharing) is migrated in place via `PRAGMA user_version`; see
> `docs/VISUALIZATION.md` §4 for the full current schema and §15 for the authorization model.

```sql
CREATE TABLE Files (
  FileId TEXT PRIMARY KEY, UserId TEXT NOT NULL, FileName TEXT NOT NULL,
  TotalSize INTEGER NOT NULL, Sha256 TEXT NULL, Chunker TEXT NOT NULL, CreatedAt TEXT NOT NULL);
CREATE TABLE Chunks (
  ChunkHash TEXT PRIMARY KEY, StoragePath TEXT NOT NULL, ByteSize INTEGER NOT NULL, CreatedAt TEXT NOT NULL);
CREATE TABLE File_Chunks (
  FileId TEXT NOT NULL REFERENCES Files(FileId) ON DELETE CASCADE,
  SequenceIndex INTEGER NOT NULL,
  ChunkHash TEXT NOT NULL REFERENCES Chunks(ChunkHash),
  PRIMARY KEY (FileId, SequenceIndex));
CREATE INDEX IX_FileChunks_ChunkHash ON File_Chunks(ChunkHash);
CREATE INDEX IX_Files_UserId ON Files(UserId);
```

The DB, not the app, enforces: one row per hash (PK), manifest can't reference a missing chunk (FK),
no duplicate positions in a manifest (composite PK).

## 5. Flows

### 5.1 Streaming upload `POST /api/files?fileName=x`

```
Request.BodyReader (PipeReader)
  → IChunker.ChunkAsync            producer: cut chunk, copy into pooled buffer, feed whole-file IncrementalHash
  → Channel<RawChunk>(bounded)     backpressure: producer blocks when workers are behind
  → N workers:                     SHA256.HashData(span) → ChunkStore.StoreAsync → return buffer to pool
  → collect results by SequenceIndex
  → repo.SaveFileAsync(Files + File_Chunks) in ONE transaction
  → { fileId, totalSize, sha256, chunkCount, newChunks, dedupedChunks }
```

Peak memory ≈ `(channelCapacity + workers + 1) × chunkSize` (+ pipe buffer ≤ max chunk).

### 5.2 `ChunkStore.StoreAsync(hash, data)`

```
1. repo.ChunkExistsAsync(hash)            → yes: return Deduplicated (no IO)
2. single-flight on hash (in-process)     → another task already writing it: await it, return Deduplicated
3. blobStore.PutAsync(hash, data)         → write temp file, fsync, atomic link/rename to ab/cd/<hash>
                                            target exists → delete temp, created=false
4. repo.TryAddChunkAsync (INSERT OR IGNORE)
```

Order = **blob → chunk row → manifest**. A crash leaves at worst an orphan blob (GC-able), never a row
pointing at missing bytes. Cross-process races are settled by the filesystem (atomic link) and the PK.

### 5.3 Download `GET /api/files/{id}`

```
manifest = repo.GetManifestAsync(id)          ordered by SequenceIndex
Content-Length = TotalSize
read-ahead task (Channel cap 2): blobStore.ReadAsync(hash, pooledBuffer)
                                 verify SHA-256(buffer) == hash, else abort (never serve corrupt bytes)
writer: copy buffers to Response.Body in order
```

### 5.4 Delta sync (client-side chunking)

```
CLI: chunk + hash file locally
POST /api/sync/negotiate { hashes[] }          → { missing[] }
PUT  /api/chunks/{hash}  (only missing ones)   server RE-HASHES body, rejects mismatch
POST /api/sync/commit { fileName, chunks[] }   manifest in one txn; FK rejects unknown hashes
```

Only changed chunks cross the network. With FastCDC, an insert in the middle of a file changes
~1–2 chunks instead of every chunk after the edit.

## 6. gRPC contract (`blob_storage.proto`)

```proto
service BlobStorage {
  rpc PutBlob (stream PutBlobRequest) returns (PutBlobReply);   // header, then ≤1 MB data frames
  rpc ReadBlob (ReadBlobRequest) returns (stream BlobFrame);
  rpc BlobExists (BlobExistsRequest) returns (BlobExistsReply);
}
```

Frames are ≤ 1 MB (gRPC default max message is 4 MB). The worker re-hashes the assembled blob before
committing it. Dedup lookups happen in the **gateway**, so duplicate bytes never cross the network.

## 7. Concurrency and failure matrix

| Scenario | Handling |
|---|---|
| Two uploads, same new chunk, same process | single-flight map → one write |
| Same, different processes/nodes | temp + atomic link (FS) + `INSERT OR IGNORE` (DB) |
| Crash after blob write, before row | orphan blob; next upload of that hash re-adds row (`PutAsync` returns false) |
| Crash mid-upload | no manifest committed → file never visible; blobs are orphans |
| Client lies about a chunk hash | server re-hashes; 400 on mismatch (prevents chunk poisoning) |
| Bit rot on disk | download verifies every chunk hash; aborts instead of serving bad bytes |
| SQLite write contention | WAL + busy_timeout; writes are tiny (one row per new chunk, one txn per file) |

## 8. Known trade-offs / future work

- **GC**: unreferenced chunks need mark-and-sweep with a grace period, because an in-flight upload
  may have deduped against a chunk whose last referencing file was just deleted.
- **Cross-user dedup side channel**: `negotiate` reveals whether *someone* already has a chunk.
  Mitigation: per-user dedup scope, or always require upload for chunks the user never owned.
- **Auth**: `X-User-Id` header is a stand-in for real authentication.
- **Postgres**: add `PostgresMetadataRepository`; nothing else changes.
