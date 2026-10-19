# CloudSync

Distributed chunk-based file storage and sync engine in C# / .NET 8: streaming ingestion, fixed-size and
content-defined (FastCDC) chunking, SHA-256 content addressing, block-level dedup, SQLite catalog,
gRPC storage workers and a delta-sync CLI.

Design: [`docs/LLD.md`](docs/LLD.md) · Diagrams: [`docs/VISUALIZATION.md`](docs/VISUALIZATION.md) · Gaps vs a real product: [`docs/GAP_ANALYSIS.md`](docs/GAP_ANALYSIS.md) · Change log: [`docs/ISSUES.md`](docs/ISSUES.md)

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
dotnet run --project src/CloudSync.Api                  # :5000 → open http://localhost:5000 and create an account
```

On a machine with only a newer .NET runtime, set `DOTNET_ROLL_FORWARD=Major` first.

Config (`src/CloudSync.Api/appsettings.json`, or env vars like `CloudSync__Storage__Mode=Remote`):
`Chunking:Algorithm` = `FixedSize` | `FastCdc`, `Storage:Mode` = `Local` | `Remote`, `Pipeline:Workers`.

## Web UI

A Drive-style single page (`src/CloudSync.Api/wwwroot/index.html`, no build step): sign in / create account,
My Drive with folders and breadcrumbs, drag-and-drop of files **and whole folders**, rename / move / delete,
sharing with other users as viewer or editor (inherited by everything inside a folder), public "anyone with the
link" URLs that work without an account, Shared with me, Recent, Starred, Trash (30 days, Undo), version history,
instant copies of files and whole folders, folder download as zip, search, list/grid view, image / PDF / audio /
video preview with seeking, resumable uploads, a details panel with activity, a per-file chunk view with verified
single-chunk download, and a Performance page showing where each upload's time went. Light and dark themes; the
design system lives in `docs/design-system/` and `node scripts/contrast.js` checks text contrast.

## CLI (delta sync)

```bash
dotnet run --project src/CloudSync.Cli -- login alice          # saves a token to ~/.cloudsync/token
dotnet run --project src/CloudSync.Cli -- push big.iso         # sends only chunks the server lacks
dotnet run --project src/CloudSync.Cli -- pull <fileId> out.iso
```

## API

All `/api` routes need a session cookie or `Authorization: Bearer <token>`, except `/api/auth/*` and `/api/public/*`.

| Method | Path | |
|---|---|---|
| POST | `/api/auth/register` · `/login` · `/logout` · `/token` | Accounts; `token` returns a CLI bearer token |
| GET | `/api/folders/root/children` · `/api/folders/{id}/children` | Folder listing with breadcrumbs and your role |
| POST · PATCH · DELETE | `/api/folders` · `/api/folders/{id}` | Create; rename / move (`{name, moveTo}`); move to trash with contents |
| POST | `/api/files?fileName=&folderId=&path=` | Streaming upload into a folder; `path=a/b` creates folders |
| GET | `/api/files/{id}[?inline=true]` | Download (supports `Range`), or preview inline (images, PDF, audio, video) |
| PATCH · DELETE | `/api/files/{id}` | Rename / move; move to trash |
| POST | `/api/files/{id}/copy` · `/api/folders/{id}/copy` | Instant copy (shares chunks, 0 bytes); folders copy the whole tree |
| GET | `/api/folders/{id}/zip` | Folder as a streamed .zip |
| POST · PUT | `/api/uploads` · `/api/uploads/{id}` | Resumable upload: create session, send `Content-Range` slices (`308` until done) |
| GET · POST | `/api/files/{id}/versions[/{vid}]` · `…/{vid}/restore` | Version history; download or restore an old version |
| GET · POST · DELETE | `/api/trash` · `/api/trash/{type}/{id}/restore` · `/api/trash/{type}/{id}` · `/api/trash` | List, restore, delete forever, empty |
| PUT · DELETE · GET | `/api/stars/{type}/{id}` · `/api/starred` · `/api/recent` | Star / unstar; Starred and Recent views |
| GET | `/api/activity/{type}/{id}` | Activity; for a folder includes everything inside it |
| GET | `/api/files/{id}/manifest` · `/chunks/{n}` | Ordered chunk list; one verified chunk |
| POST · GET · DELETE | `/api/shares` · `/api/shares/{type}/{id}` · `/api/shares/{shareId}` | Share with a user or create a public link; list; revoke |
| GET | `/api/shared-with-me` · `/api/search?q=` | Items shared with you; name search |
| GET | `/api/public/{token}/[?folderId=]` · `/files/{id}` · `/zip[?folderId=]` | Public link browsing, download and zip, no login |
| POST · PUT | `/api/sync/negotiate` · `/api/chunks/{hash}` · `/api/sync/commit` | Delta sync |
| GET | `/api/stats` · `/api/metrics` | Dedup totals; per-route latency p50 / p95 / max |

## Test

```bash
dotnet test                                  # unit + in-process integration (incl. gRPC worker)
SIZE_MB=500 MODE=Remote scripts/acceptance.sh  # PRD definition of done, real processes + sha256sum
```
