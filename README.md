# CloudSync

Distributed chunk-based file storage and sync engine in C# / .NET 8: streaming ingestion, fixed-size and
content-defined (FastCDC) chunking, SHA-256 content addressing, block-level dedup, SQLite catalog,
gRPC storage workers and a delta-sync CLI.

Design: [`docs/LLD.md`](docs/LLD.md)

## Layout

| Project | Role |
|---|---|
| `CloudSync.Core` | Chunkers, `ChunkStore` (dedup + single-flight), `UploadPipeline` (producer–consumer), `FileService` |
| `CloudSync.Infrastructure` | `SqliteMetadataRepository`, `LocalFileSystemBlobStore`, `GrpcBlobStore` |
| `CloudSync.Contracts` | `blob_storage.proto` |
| `CloudSync.Api` | REST gateway |
| `CloudSync.StorageWorker` | gRPC storage node |
| `CloudSync.Cli` | Delta-sync client (`push` / `pull`) |

## Run

```bash
dotnet run --project src/CloudSync.StorageWorker        # :5100, only needed for Storage:Mode=Remote
dotnet run --project src/CloudSync.Api                  # :5000

curl -X POST -T big.iso -H 'X-User-Id: me' 'http://localhost:5000/api/files?fileName=big.iso'
curl -H 'X-User-Id: me' http://localhost:5000/api/files/<fileId> -o big.copy.iso
dotnet run --project src/CloudSync.Cli -- push big.iso --user me
```

Config (`src/CloudSync.Api/appsettings.json`, or env vars like `CloudSync__Storage__Mode=Remote`):
`Chunking:Algorithm` = `FixedSize` | `FastCdc`, `Storage:Mode` = `Local` | `Remote`, `Pipeline:Workers`.

## API

| Method | Path | |
|---|---|---|
| POST | `/api/files?fileName=` | Streaming upload (raw body or multipart) |
| GET | `/api/files/{id}` | Reconstructed download (every chunk re-verified) |
| GET | `/api/files/{id}/manifest` | Ordered chunk list |
| GET | `/api/files` | Caller's files |
| POST | `/api/sync/negotiate` | `{hashes}` → `{missing}` |
| PUT | `/api/chunks/{hash}` | Upload one chunk (server re-hashes) |
| POST | `/api/sync/commit` | `{fileName, chunks}` → file |
| GET | `/api/stats` | Logical vs physical bytes, dedup ratio |

## Test

```bash
dotnet test                                  # unit + in-process integration (incl. gRPC worker)
SIZE_MB=500 MODE=Remote scripts/acceptance.sh  # PRD definition of done, real processes + sha256sum
```
