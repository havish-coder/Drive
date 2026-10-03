# CloudSync vs a real cloud drive: gap analysis

Where this project stands against a production service like Google Drive or Dropbox, what was measured, what was
fixed, and what each remaining gap would take. Items are ordered by impact within each section.

## 1. Latency: measured, not guessed

Every streaming upload now reports per-phase timings (`timings` in the JSON response, a `Server-Timing` header for
browser DevTools, and the **Performance** page in the UI). `GET /api/metrics` gives p50 / p95 / max per API route.

**500 MB upload through the gRPC storage worker** (4 workers, fixed 4 MB chunks, `scripts/acceptance.sh`):

| Phase | Time | Reading it |
|---|---|---|
| Total, wall clock | 7.9 s | ~63 MB/s end to end |
| Producer waiting on workers (backpressure) | 4.0 s | Half the upload, the reader sat idle: **the workers are the bottleneck**, not the network |
| Chunk writes (gRPC + worker fsync), summed over 4 workers | 14.6 s | ~117 ms per 4 MB chunk |
| Register chunk rows in SQLite, summed over 4 workers | 13.3 s | **~107 ms per one-row INSERT**: the surprise |
| Read request + chunk | 3.3 s | |
| SHA-256, summed | 1.7 s | Hashing is cheap (~290 MB/s per core) |
| Dedup lookups | 0.04 s | Negligible with the primary-key index |
| Save file manifest | 0.01 s | One transaction |

### Findings and fixes

| # | Gap | Fix |
|---|---|---|
| L1 | **SQLite commit cost dominates chunk registration.** WAL mode with the default `synchronous=FULL` fsyncs every commit, and 4 workers queue for SQLite's single writer. | `PRAGMA synchronous=NORMAL` (safe against app crashes; power loss can drop the last commits, but blobs are fsynced and rows self-heal on the next upload), or batch the chunk inserts per upload into one transaction. Expect this phase to drop by an order of magnitude. |
| L2 | One fsync per 4 MB blob | Group commit: fsync a batch, or rely on replication (L-durability below) instead of per-write fsync. |
| L3 | One DB round-trip per chunk for dedup | Batch existence checks per N chunks, plus an in-memory hash cache or Bloom filter in front of the DB. Small today (0.04 s), large when the catalog is remote. |
| L4 | No HTTP `Range` support | Required for resumable downloads and seeking in audio/video. Map a byte range to chunks via cumulative offsets. |
| L5 | No caching | Chunks are immutable, so they are perfect for `Cache-Control: immutable` + `ETag: <hash>` and a CDN. |
| L6 | Browser hashing runs on the main thread | Move delta-sync hashing into a Web Worker. |
| L7 | Single process metrics | Export to OpenTelemetry (traces + histograms) once there is more than one node. |

## 2. Durability, availability and scale

| Gap | Real products | Fix |
|---|---|---|
| One copy of every chunk | 3 replicas or erasure coding (e.g. Reed-Solomon 6+3), "11 nines" durability | Replicate chunk writes to N workers; read from any healthy replica; background scrubbing re-verifies hashes. |
| One SQLite file, one writer | Sharded SQL or a distributed KV store (Spanner, MySQL shards, Edgestore at Dropbox) | The `IMetadataRepository` boundary already exists: a Postgres repository, then sharding by user. |
| Single API process | Stateless gateways behind a load balancer | Move session state to tokens (already supported) so any node can serve a request. |
| No garbage collection | Mark-and-sweep with a grace period | Deleting a file or folder removes rows only; chunks stay on disk. Needs GC that respects in-flight uploads (see the GC race in the course). |
| No change feed | Clients long-poll a cursor of changes | A `Changes` journal table + `GET /api/changes?cursor=` lets clients sync without rescanning. |
| No backups / point-in-time restore | Continuous backups of metadata | Snapshot the SQLite file (or use Postgres PITR). |

## 3. Security

### Fixed in this pass

| Was | Now |
|---|---|
| Identity was an `X-User-Id` header: anyone could be anyone | Real accounts. PBKDF2-SHA256 (600k iterations, per-user salt, constant-time compare); HttpOnly `SameSite=Strict` session cookie; hashed bearer tokens for the CLI. A test proves the old header is ignored. |
| Login timing revealed which usernames exist | Unknown users are verified against a dummy hash, so both cases take the same time and return the same 401. |
| Any file could be served inline | Preview is inline only for raster images and PDF, always with `X-Content-Type-Options: nosniff`. HTML and SVG are never rendered on our origin (stored XSS). |
| No authorization model | One choke point (`SqliteDrive.AccessAsync`): owner / editor / viewer, inherited down the folder tree, applied to every file, folder, chunk and manifest endpoint. Public links are scoped to the shared item's subtree. |

### Still open

| # | Gap | Risk | Fix |
|---|---|---|---|
| S1 | No rate limiting or lockout | Password guessing, API abuse | ASP.NET Core's built-in `RateLimiter` on `/api/auth/*` and uploads. |
| S2 | No quotas | One user can fill the disk | Sum `TotalSize` per user at upload time; reject over quota. |
| S3 | Cross-user dedup side channel | `negotiate` reveals whether *anyone* stored a chunk (confirmation-of-file attack) | Only report chunks the caller already references; still dedup silently on the server after upload. |
| S4 | No proof of possession on commit | Knowing a chunk hash is enough to claim its content (Dropbox "Dropship", 2011) | For chunks the caller never uploaded, require upload or a challenge (hash of chunk + server nonce). |
| S5 | gRPC worker is unauthenticated, plaintext h2c | Anyone on the network can read or write blobs | mTLS between gateway and workers, or a shared-secret interceptor; private network only. |
| S6 | No encryption at rest | Disk theft exposes data | Volume encryption, or per-chunk convergent encryption (keeps dedup, with its own leaks). |
| S7 | User content served from the app origin | Any future preview type is an XSS risk | Serve downloads from a separate, cookieless domain (Google uses googleusercontent.com). |
| S8 | No HTTPS in dev, no HSTS | Cookies and tokens readable on the wire | TLS termination + `UseHsts()`; mark cookies `Secure`. |
| S9 | No audit log, no malware scanning | No forensics; shared files can spread malware | Append-only audit table; scan on upload. |
| S10 | Tokens never expire, can't be listed or revoked | A leaked CLI token is valid forever | Expiry + a token management screen. |

## 4. Product features

Done now: folders (create, rename, move, delete, breadcrumbs), whole-folder upload, sharing with users (viewer /
editor, inherited), public links, "Shared with me", search, image + PDF preview, a Drive-style UI.

| Missing | Notes |
|---|---|
| Trash + restore | Soft delete with `TrashedAt`; pairs with GC. |
| File versions | Re-upload of the same name becomes a new version; nearly free with dedup since only changed chunks are new. |
| Audio / video playback, Office previews | Needs `Range` (L4); Office needs a converter. |
| Download a folder as .zip | Stream a zip built from chunks on the fly. |
| Starred, recent, sorting, grid view, thumbnails | UI work; thumbnails need a background job. |
| Multiple owners per folder tree | Today items created in a shared folder belong to the folder's owner, and moves across owners are rejected. |
