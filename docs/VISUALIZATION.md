# CloudSync: Visual Guide

Every diagram here is [Mermaid](https://mermaid.js.org). GitHub renders them in place. Locally, use the VS Code
"Markdown Preview Mermaid Support" extension, or paste a block into <https://mermaid.live>.

| # | Diagram | Answers |
|---|---|---|
| 1 | System architecture | What runs where, and how does data flow? |
| 2 | Solution layout | Which project depends on which? |
| 3 | Class diagram | What are the types and patterns? |
| 4 | Database schema | What is stored in the catalog? |
| 5 | Upload sequence | What happens on `POST /api/files`? |
| 6 | Upload pipeline | How is memory kept bounded? |
| 7 | Dedup decision | How is a chunk written exactly once? |
| 8 | Crash consistency | What if the server dies mid-write? |
| 9 | Download sequence | How is a file rebuilt and verified? |
| 10 | Delta sync | How does an edit upload only changed chunks? |
| 11 | gRPC storage worker | How does the gateway talk to a worker? |
| 12 | Chunking: fixed vs CDC | Why content-defined chunking survives inserts |
| 13 | On-disk layout | Where do the bytes live? |
| 14 | Concept map | How do all the ideas fit together? |
| 15 | Authorization | Who may do what to which file or folder? |
| 16 | Trash, versions and GC | When do bytes actually get freed? |

---

## 1. System architecture

```mermaid
flowchart LR
  subgraph Clients
    UI["Web UI<br/>wwwroot/index.html"]
    CLI["CloudSync.Cli<br/>push / pull"]
    CURL["curl or any HTTP client"]
  end

  subgraph Gateway["CloudSync.Api  :5000"]
    EP["Endpoints<br/>REST + static UI"]
    FS["FileService<br/>facade"]
    PL["UploadPipeline<br/>producer-consumer"]
    CK["IChunker<br/>Fixed 4MB or FastCDC"]
    CS["ChunkStore<br/>dedup + single-flight"]
    AU["Auth<br/>cookie + bearer token"]
    DRV["SqliteDrive<br/>folders, shares, access checks"]
  end

  DB[("SQLite catalog<br/>Users, Folders, Shares,<br/>Files, Chunks, File_Chunks")]

  subgraph Stores["IBlobStore, chosen by config"]
    LOCAL["LocalFileSystemBlobStore"]
    GRPC["GrpcBlobStore<br/>gRPC client"]
  end

  subgraph Worker["CloudSync.StorageWorker  :5100"]
    SVC["BlobStorageService<br/>re-hashes every blob"]
    WLOCAL["LocalFileSystemBlobStore"]
  end

  DISK[("Disk<br/>ab/cd/hash")]
  WDISK[("Worker disk<br/>ab/cd/hash")]

  UI --> EP
  CLI --> EP
  CURL --> EP
  EP --> AU
  EP --> DRV
  DRV --> DB
  EP --> FS
  FS --> PL
  PL --> CK
  PL --> CS
  FS --> DB
  CS --> DB
  CS --> LOCAL
  CS --> GRPC
  LOCAL --> DISK
  GRPC -- "HTTP/2 gRPC" --> SVC
  SVC --> WLOCAL
  WLOCAL --> WDISK
  CFG["appsettings.json<br/>CloudSync:Storage:Mode"] -. "Local" .-> LOCAL
  CFG -. "Remote" .-> GRPC
```

---

## 2. Solution layout

Arrows point from a project to what it depends on. `Core` depends on nothing concrete.

```mermaid
flowchart TB
  Api["CloudSync.Api<br/>REST gateway + UI"]
  Worker["CloudSync.StorageWorker<br/>gRPC node"]
  Cli["CloudSync.Cli<br/>delta-sync client"]
  Infra["CloudSync.Infrastructure<br/>SQLite, filesystem, gRPC client"]
  Contracts["CloudSync.Contracts<br/>blob_storage.proto"]
  Core["CloudSync.Core<br/>chunkers, ChunkStore, pipeline, FileService"]
  Tests["CloudSync.Tests<br/>xUnit"]

  Api --> Infra
  Worker --> Infra
  Cli --> Core
  Infra --> Core
  Infra --> Contracts
  Tests --> Api
  Tests --> Worker
  Tests --> Cli
```

---

## 3. Class diagram

```mermaid
classDiagram
  direction LR

  class IChunker {
    <<interface>>
    +Name string
    +MaxChunkSize int
    +ChunkAsync(PipeReader source) IAsyncEnumerable~RawChunk~
  }
  class ChunkerBase {
    <<abstract>>
    +ChunkAsync(PipeReader source) IAsyncEnumerable~RawChunk~
    #FindCutPoint(ReadOnlySequence buffer, bool isFinal) int
  }
  class FixedSizeChunker {
    cut every N bytes
  }
  class FastCdcChunker {
    Gear rolling hash
    min / avg / max sizes
  }
  class ChunkerFactory {
    +Create(ChunkingOptions) IChunker
  }
  class RawChunk {
    +SequenceIndex int
    +Offset long
    +Length int
    +Dispose() returns pooled buffer
  }

  class IBlobStore {
    <<interface>>
    +GetStorageKey(hash) string
    +PutAsync(hash, data) bool
    +ReadAsync(hash, destination) int
    +ExistsAsync(hash) bool
  }
  class LocalFileSystemBlobStore {
    temp file, fsync, atomic move
  }
  class GrpcBlobStore {
    remote proxy over gRPC
  }
  class BlobStorageService {
    gRPC server in the worker
  }

  class IMetadataRepository {
    <<interface>>
    +ChunkExistsAsync(hash) bool
    +GetChunkSizesAsync(hashes) Dictionary
    +TryAddChunkAsync(chunk) bool
    +SaveFileAsync(file, chunks) void
    +GetManifestAsync(fileId) FileManifest
    +GetStatsAsync() StorageStats
  }
  class SqliteMetadataRepository

  class ChunkStore {
    +StoreAsync(hash, data) ChunkStoreOutcome
    in-flight map = single-flight
  }
  class UploadPipeline {
    +RunAsync(chunker, reader) PipelineResult
    bounded Channel + N workers
  }
  class FileService {
    +UploadAsync()
    +DownloadAsync()
    +ReadChunkAsync()
    +FindMissingAsync()
    +PutChunkAsync()
    +CommitAsync()
  }

  ChunkerBase ..|> IChunker
  FixedSizeChunker --|> ChunkerBase
  FastCdcChunker --|> ChunkerBase
  ChunkerFactory ..> IChunker : creates
  IChunker ..> RawChunk : yields

  LocalFileSystemBlobStore ..|> IBlobStore
  GrpcBlobStore ..|> IBlobStore
  BlobStorageService --> LocalFileSystemBlobStore : wraps
  GrpcBlobStore ..> BlobStorageService : gRPC
  SqliteMetadataRepository ..|> IMetadataRepository

  ChunkStore --> IBlobStore
  ChunkStore --> IMetadataRepository
  UploadPipeline --> ChunkStore
  UploadPipeline ..> IChunker
  FileService --> UploadPipeline
  FileService --> ChunkStore
  FileService --> IBlobStore
  FileService --> IMetadataRepository
  FileService --> IChunker

  class SqliteDrive {
    +AccessAsync(user, owner, path, fileId) Role
    +ListAsync(user, folderId) Listing
    +EnsurePathAsync(owner, parent, path) Guid
    +ShareWithUserAsync() / LinkAsync()
    +SearchAsync(user, query)
  }
  SqliteDrive --> SqliteMetadataRepository : shares connection
```

Patterns: **Strategy** (`IChunker`, `IBlobStore`), **Template Method** (`ChunkerBase`), **Repository**
(`IMetadataRepository`), **Remote Proxy** (`GrpcBlobStore`), **Facade** (`FileService`), **Factory**
(`ChunkerFactory`), **Producer-Consumer** (`UploadPipeline`), **Single-flight** (`ChunkStore`), **Object Pool**
(`ArrayPool` behind `RawChunk`).

---

## 4. Database schema

```mermaid
erDiagram
  Users ||--o{ ApiTokens : "has"
  Users ||--o{ Shares : "receives"
  Folders ||--o{ Folders : "contains"
  Folders ||--o{ Files : "contains"
  Files ||--o{ File_Chunks : "has ordered"
  Chunks ||--o{ File_Chunks : "referenced by"
  Files ||--o{ Versions : "history"
  Versions ||--o{ Version_Chunks : "has ordered"
  Chunks ||--o{ Version_Chunks : "referenced by"
  Users ||--o{ Stars : "stars"
  Users ||--o{ Uploads : "resumes"

  Users {
    string UserId PK "lowercase username"
    string PasswordHash "PBKDF2-SHA256"
    string CreatedAt
  }
  ApiTokens {
    string TokenHash PK "SHA-256 of the token"
    string UserId FK
    string CreatedAt
  }
  Folders {
    string FolderId PK
    string UserId "owner"
    string ParentId FK "null = root"
    string Name "unique per parent among live folders"
    string CreatedAt
    string UpdatedAt
    string TrashedAt "null = live"
  }
  Files {
    string FileId PK
    string UserId "owner"
    string FolderId FK "null = root"
    string FileName
    int TotalSize
    string Sha256 "null for delta-synced files"
    string Chunker
    string CreatedAt
    string UpdatedAt
    string TrashedAt "null = live"
  }
  Versions {
    string VersionId PK
    string FileId FK
    int TotalSize
    string Sha256
    string Chunker
    string CreatedAt
  }
  Version_Chunks {
    string VersionId PK, FK
    int SequenceIndex PK
    string ChunkHash FK
  }
  Stars {
    string UserId PK
    string ItemId PK
    string ItemType
  }
  Activity {
    int Id PK
    string ItemType
    string ItemId
    string UserId
    string Action
    string Detail
    string At
  }
  Uploads {
    string UploadId PK "bytes so far = size of the .part file"
    string UserId FK
    string OwnerId
    string FolderId
    string FileName
    int TotalSize
    string CreatedAt
  }
  Shares {
    string ShareId PK
    string ItemType "file or folder"
    string ItemId
    string OwnerId
    string GranteeId FK "null for public links"
    string LinkToken "null for user shares"
    string Role "viewer or editor"
  }
  Chunks {
    string ChunkHash PK "SHA-256 hex"
    string StoragePath "ab/cd/hash"
    int ByteSize
    string CreatedAt
  }
  File_Chunks {
    string FileId PK, FK
    int SequenceIndex PK "position in file"
    string ChunkHash FK
  }
```

Folders form a tree through `ParentId`; deleting a folder cascades to its subfolders and files. A file with
`FolderId` NULL sits at its owner's root ("My Drive"). `File_Chunks` is the junction table. Position lives there because the same chunk can sit at different positions in
different files. The foreign key on `ChunkHash` means a manifest can never reference a chunk that was not stored.

---

## 5. Upload sequence (`POST /api/files`)

```mermaid
sequenceDiagram
  autonumber
  participant C as Client
  participant E as Endpoints
  participant F as FileService
  participant P as UploadPipeline
  participant K as Chunker (producer)
  participant W as Workers x4
  participant S as ChunkStore
  participant B as IBlobStore
  participant D as SQLite

  C->>E: POST /api/files (raw or multipart stream)
  E->>F: UploadAsync(user, name, PipeReader)
  F->>P: RunAsync(chunker, reader)
  par chunking
    loop every chunk
      K->>K: cut chunk, copy to pooled buffer, update file SHA-256
      K->>W: write to bounded channel (waits when full)
    end
  and hashing and storing
    W->>W: SHA-256 of the chunk
    W->>S: StoreAsync(hash, bytes)
    S->>D: ChunkExists?
    alt already stored
      S-->>W: Deduplicated (no disk write)
    else new chunk
      S->>B: PutAsync(hash, bytes)
      B-->>S: written
      S->>D: insert chunk row (ON CONFLICT DO NOTHING)
      S-->>W: Written
    end
  end
  P-->>F: ordered results, total size, file SHA-256
  F->>D: SaveFileAsync (one transaction)
  F-->>E: UploadResult
  E-->>C: 201 Created (chunkCount, newChunks, dedupedChunks)
```

---

## 6. Upload pipeline and backpressure

```mermaid
flowchart TB
  BODY["HTTP request body"] --> PR["PipeReader<br/>1 MB buffer"]
  PR --> CH["Chunker<br/>single producer<br/>(cut points depend on earlier bytes)"]
  CH --> FH["Running SHA-256<br/>of the whole file"]
  CH --> Q[["Bounded channel<br/>capacity 4"]]
  Q --> W1["Worker 1"]
  Q --> W2["Worker 2"]
  Q --> W3["Worker 3"]
  Q --> W4["Worker 4"]
  W1 --> CS["ChunkStore"]
  W2 --> CS
  W3 --> CS
  W4 --> CS
  CS --> R["Results keyed by SequenceIndex"]
  R --> SORT["Sort, then check there are no gaps"]
  SORT --> TX[("SaveFileAsync<br/>one transaction")]

  Q -. "channel full: producer waits" .-> PR
  PR -. "stops reading socket,<br/>TCP slows the client" .-> BODY
```

Peak memory = (channel 4 + workers 4 + 1 in the chunker) x 4 MB, about 36 MB per upload, whatever the file size.

---

## 7. Dedup decision (`ChunkStore.StoreAsync`)

```mermaid
flowchart TD
  A["StoreAsync(hash, bytes)"] --> B{"Chunk row exists?"}
  B -- yes --> DUP1(["Deduplicated<br/>no IO"])
  B -- no --> C{"Another task in this process<br/>already writing this hash?"}
  C -- "yes (layer 1: single-flight)" --> WAIT["await that task"] --> DUP2(["Deduplicated"])
  C -- "no, I am the owner" --> D["PutAsync: temp file, fsync,<br/>no-overwrite move"]
  D --> E{"Move won the race?<br/>(layer 2: atomic publish)"}
  E -- "no, file already there" --> F["delete temp"] --> G
  E -- yes --> G["INSERT chunk row<br/>ON CONFLICT DO NOTHING<br/>(layer 3: primary key)"]
  G --> H["remove from in-flight map"]
  H --> OUT(["Written, or Deduplicated if another<br/>process published first"])
```

---

## 8. Crash consistency: what if the server dies here?

```mermaid
flowchart LR
  S1["1. Write blob<br/>temp, fsync, atomic move"] --> S2["2. Insert Chunks row"] --> S3["3. One transaction:<br/>Files + File_Chunks"] --> OK(["File visible"])

  S1 -. "crash" .-> X1["Orphan blob, nothing points to it.<br/>Harmless. Next upload of that chunk<br/>finds the blob and adds the row."]
  S2 -. "crash" .-> X2["Chunk registered, no file uses it yet.<br/>Harmless. Garbage collection can reclaim it."]
  S3 -. "crash" .-> X3["Transaction rolls back.<br/>The file never becomes visible."]

  BAD["Wrong order: row first, then blob"] -. "crash between" .-> X4["Row claims bytes exist, but they do not.<br/>Later uploads dedup against it.<br/>Silent permanent corruption."]
```

Rule: write the thing being pointed to before the thing that points to it.

---

## 9. Download sequence (`GET /api/files/:id`)

```mermaid
sequenceDiagram
  autonumber
  participant C as Client
  participant E as Endpoints
  participant F as FileService
  participant D as SQLite
  participant R as Read-ahead task
  participant B as IBlobStore

  C->>E: GET /api/files/ID  (X-User-Id header)
  E->>F: GetManifestAsync(id)
  F->>D: file row + ordered chunks
  D-->>E: manifest
  alt not found or not the owner
    E-->>C: 404
  else owner
    E-->>C: 200 headers (Content-Length = TotalSize)
    E->>F: DownloadAsync(manifest, response body)
    F->>R: start reader
    loop each chunk in SequenceIndex order
      R->>B: ReadAsync(hash)
      B-->>R: bytes
      R->>R: verify size and SHA-256
      alt hash mismatch
        R-->>E: ChunkIntegrityException
        E-->>C: abort connection (truncated body = failure)
      else verified
        R->>F: chunk into channel (capacity 2)
        F-->>C: write bytes to socket
      end
    end
  end
```

Reading chunk N+1 overlaps with sending chunk N. Headers are already sent when a bad chunk is found, so the only
honest signal left is to cut the connection.

---

## 10. Delta sync

```mermaid
sequenceDiagram
  autonumber
  participant L as Client (browser or CLI)
  participant A as API
  participant F as FileService
  participant D as SQLite
  participant S as ChunkStore

  L->>L: chunk the file locally and SHA-256 each chunk
  L->>A: POST /api/sync/negotiate (all hashes)
  A->>F: FindMissingAsync
  F->>D: which hashes are stored?
  D-->>F: known sizes
  F-->>L: missing hashes only

  loop each missing chunk
    L->>A: PUT /api/chunks/HASH (bytes)
    A->>F: PutChunkAsync(claimedHash, bytes)
    F->>F: re-hash the body (never trust the client)
    alt body does not match claimed hash
      F-->>L: 400 Bad Request
    else matches
      F->>S: StoreAsync
      S-->>L: 200 written
    end
  end

  L->>A: POST /api/sync/commit (file name, ordered hashes)
  A->>F: CommitAsync
  F->>D: are all hashes stored? sizes come from the catalog
  alt some chunk is missing
    F-->>L: 409 Conflict (list of missing)
  else all present
    F->>D: SaveFileAsync (one transaction)
    F-->>L: 201 Created
  end
```

Only the changed chunks cross the network. Dedup works only when client and server chunk the same way.

---

## 11. gRPC storage worker

```mermaid
sequenceDiagram
  autonumber
  participant G as GrpcBlobStore (gateway)
  participant W as BlobStorageService (worker)
  participant S as LocalFileSystemBlobStore

  Note over G,W: PutBlob is client-streaming over HTTP/2
  G->>W: header message (hash, size)
  loop frames of at most 1 MB
    G->>W: data frame
  end
  G->>W: complete request stream
  W->>W: size matches the header?
  W->>W: SHA-256 of assembled bytes equals hash?
  alt mismatch
    W-->>G: RpcException DataLoss
  else ok
    W->>S: PutAsync(hash, bytes)
    S-->>W: created true or false
    W-->>G: PutBlobReply(created)
  end

  Note over G,W: ReadBlob is server-streaming
  G->>W: ReadBlobRequest(hash)
  loop 1 MB frames
    W-->>G: BlobFrame
  end
  Note over G: gateway verifies SHA-256 again on download
```

The dedup check happens in the gateway before this call, so duplicate bytes never cross the network.

---

## 12. Chunking: fixed vs content-defined after a 1-byte insert

```mermaid
flowchart TB
  subgraph Before["Original file"]
    direction LR
    A1["AAAA"] --- A2["BBBB"] --- A3["CCCC"] --- A4["DDDD"]
  end

  subgraph Fixed["Fixed-size, after inserting X at the start"]
    direction LR
    F1["XAAA"] --- F2["ABBB"] --- F3["BCCC"] --- F4["CDDD"] --- F5["D"]
  end

  subgraph CDC["Content-defined (FastCDC), after the same insert"]
    direction LR
    C1["XAAAA"] --- C2["BBBB"] --- C3["CCCC"] --- C4["DDDD"]
  end

  Before --> Fixed
  Before --> CDC
  Fixed --> FR(["Every chunk hash changed:<br/>re-upload everything"])
  CDC --> CR(["Cut points depend on local content,<br/>so only 1 chunk changed"])
```

FastCDC cuts where a rolling Gear hash of the last ~64 bytes has its top bits equal to zero, with a minimum size, a
maximum size, and a stricter mask before the average size (normalized chunking).

---

## 13. On-disk layout

```mermaid
flowchart TD
  ROOT["data/blobs/"] --> TMP[".tmp/<br/>in-progress writes"]
  ROOT --> L1["9f/ first 2 hex chars"]
  ROOT --> L1B["a3/ ..."]
  L1 --> L2["86/ next 2 hex chars"]
  L1 --> L2B["c0/ ..."]
  L2 --> BLOB["9f86d081884c7d65...<br/>file name = SHA-256 of its content"]
  DBF["data/cloudsync.db<br/>SQLite catalog, WAL mode"]
```

Two prefix levels give 65,536 directories, so no single directory ever holds millions of files.

---

## 14. Concept map

```mermaid
mindmap
  root((CloudSync))
    Chunking
      Fixed size 4 MB
      FastCDC
        Gear rolling hash
        min avg max sizes
        normalized masks
    Content addressing
      SHA-256 as the name
      Dedup by reference
      Integrity check on every read
      Immutable blobs
    Streaming
      PipeReader
      Bounded channel
      Backpressure
      ArrayPool buffers
    Concurrency
      Single-flight
      Atomic temp then move
      DB primary key
      Blob then row then manifest
      SequenceIndex ordering
    Storage
      SQLite WAL
      Files Chunks File_Chunks
      Hash prefix directories
    Distributed
      gRPC client streaming
      Worker re-hashes
      Dedup in the gateway
    Delta sync
      Chunk on the client
      Negotiate
      PUT missing chunks
      Commit manifest
    Drive
      Folder tree by ParentId
      Shares inherit downwards
      Public link tokens
      Login PBKDF2 + cookie
    Trust and safety
      Server re-hashes uploads
      Owner-only downloads
      Verified before sending
```

---

## 15. Authorization

```mermaid
flowchart TD
  R["Request to /api/..."] --> A{"Session cookie or<br/>bearer token valid?"}
  A -- no --> U(["401 Unauthorized"])
  A -- yes --> I["Load the item and its folder path<br/>(recursive CTE, root first)"]
  I --> O{"Caller owns it?"}
  O -- yes --> OWN(["Owner: everything,<br/>including share and move"])
  O -- no --> S{"Share on the item or on<br/>any folder above it?"}
  S -- no --> N(["404, as if it did not exist"])
  S -- editor --> E(["Editor: read, upload,<br/>new folder, rename, delete"])
  S -- viewer --> V(["Viewer: list, download, preview"])
  P["Public link: /api/public/TOKEN"] --> T{"Token exists and the item is<br/>inside the shared subtree?"}
  T -- yes --> V
  T -- no --> N
```

Every file, folder, manifest and chunk endpoint goes through the same check (`SqliteDrive.AccessAsync`), so a
new endpoint cannot accidentally skip it by re-implementing the rules.

---

## 16. Trash, versions and GC

```mermaid
flowchart TD
  D["DELETE /api/files|folders/{id}"] --> T["Stamp one TrashedAt on the item and its whole subtree"]
  T --> H["Hidden from listings, search, shares, public links"]
  H -->|"Restore (clears that timestamp)"| L["Live again, same ids"]
  H -->|"Delete forever / Empty trash / 30 days (Janitor)"| R["Rows deleted; File_Chunks and Version_Chunks cascade"]
  U["Upload with an existing name"] --> V["Old manifest copied to Versions + Version_Chunks"]
  V --> L
  R --> G{"Hourly GC: chunk older than 1 h and referenced by no file and no version?"}
  G -->|"yes: DELETE ... RETURNING ChunkHash"| B["Delete blob (local disk or gRPC DeleteBlob)"]
  G -->|"no: still shared by a copy, a version or another user"| K["Keep"]
```

Copies and versions only add rows that point at existing chunks, so a chunk is freed only when the last file or
version that references it is gone. The 1-hour grace keeps chunks of an upload still in flight; if an upload dedups
against a chunk GC deletes at the same moment, the `File_Chunks` foreign key makes that commit fail instead of
saving a manifest with a missing chunk.
