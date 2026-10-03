# Issues & Modifications Log

Running log of problems found and changes made after the initial build. Newest first.
Status: `open` · `fixed` · `wontfix`

| # | Status | Summary |
|---|---|---|
| 9 | fixed | After updating, browsers could keep showing the old UI (no login screen): the page had no `Cache-Control` |
| 8 | open | Global dedup leaks chunk existence via negotiate; no proof-of-possession on commit (gap S3, S4) |
| 7 | open | Deleting files/folders never frees chunk storage: no garbage collector |
| 6 | open | Measured: registering chunk rows in SQLite costs ~107 ms per insert under 4 concurrent workers (gap L1) |
| 5 | fixed | No way to see where upload latency goes: per-phase timings, Server-Timing, route metrics, Performance page |
| 4 | fixed | Identity was a spoofable `X-User-Id` header: real accounts, sessions and API tokens |
| 3 | fixed | Flat file list only: folders, sharing, public links, folder upload, search, previews, Drive-style UI |
| 2 | fixed | No visual overview of the whole project: added `docs/VISUALIZATION.md` (14 Mermaid diagrams) |
| 1 | fixed | Chunk sizes not visible in the UI; no way to download an individual chunk |

Full analysis of what remains versus a real product: [`docs/GAP_ANALYSIS.md`](GAP_ANALYSIS.md).

---

## #9 Old UI kept showing after an update

**Reported:** after pulling the login/folders update there was no "Create account" option.

**Cause:** the new page does have it (Sign in + Create account), but `index.html` was served with only
`Last-Modified`/`ETag` and no `Cache-Control`. Browsers then apply heuristic freshness and may reuse the cached
old page (which had no login) without asking the server.

**Fix:** static files are served with `Cache-Control: no-cache`, so the browser revalidates on every load (a cheap
304 when unchanged). Test: `Root_serves_the_web_ui` asserts the header.

---

## #6 / #7 / #8 Open items found while measuring and reviewing

- **#6** The new timings show chunk-row registration dominating a 500 MB upload (13.3 s summed across 4 workers).
  WAL with `synchronous=FULL` fsyncs every commit and the workers queue on SQLite's single writer. Fix options in
  gap L1 (`synchronous=NORMAL` or one batched transaction per upload). Left open: it is a durability trade-off to choose.
- **#7** `DELETE` on files and folders removes catalog rows only. Needs mark-and-sweep GC with a grace period so an
  in-flight upload that deduped against a chunk can't lose it.
- **#8** See gap analysis S3 and S4.

---

## #3 / #4 / #5 Drive features, real login, latency measurement

**Request:** make it Google-Drive-like (folders, files inside folders or at the root, sharing, folder upload,
search, image/PDF preview, proper frontend), fix the weak security, and show where latency goes. Code written in
"ponytail" style: reuse what exists, standard library and platform features first, fewest files.

**Changes**
- Schema v2 via `PRAGMA user_version`: `Users`, `ApiTokens`, `Folders` (self-referencing `ParentId`), `Shares`,
  and `Files.FolderId` (NULL = root). Existing databases upgrade in place; legacy files stay at the owner's root.
- `SqliteDrive`: users, tokens, folders, sharing and search, with recursive CTEs for paths and inherited access.
  Every access decision goes through `AccessAsync`.
- Auth: PBKDF2 passwords, HttpOnly SameSite=Strict cookie, hashed bearer tokens; `X-User-Id` removed. CLI gains `login`.
- Endpoints: folders CRUD + move (cycle-safe), file rename/move/delete, upload into a folder or a `path`,
  shares + public links, search, `?inline=true` preview (images and PDF only), `/api/metrics`.
- Upload timings per phase; per-route latency recorder; Performance page.
- New single-page UI (no build step).

**Verification:** 28 tests (8 new: auth and spoofing, folders, sharing roles and inheritance, public-link scope,
search scope, preview headers, timings, migration of a pre-folder database). A headless-browser run as owner,
shared user and anonymous link visitor. `scripts/acceptance.sh` at 500 MB through the gRPC worker still passes.

---

## #2 No visual overview of the project

**Request:** a full visualization of the project in Mermaid.

**Change:** `docs/VISUALIZATION.md` with 14 diagrams: architecture, project dependencies, classes, DB schema, the
upload / download / delta-sync / gRPC sequences, the pipeline and backpressure, the dedup decision, crash
consistency, fixed-vs-CDC chunking, on-disk layout and a concept map. GitHub renders them inline.

**Verification:** every block was extracted and rendered with Mermaid 11 in headless Chromium (14/14 render, none
with a syntax error). Diagrams were derived from the code, not from memory of the design: type names, endpoints,
queue capacities and error codes match the source.

---

## #1 Chunk sizes not visible; no per-chunk download

**Reported:** chunk sizes vary (always with FastCDC, and the last chunk of any file), but the Chunks view only
showed coloured squares. Each chunk's size was buried in a hover tooltip, and chunks could not be downloaded
individually.

**Fix**
- UI (`src/CloudSync.Api/wwwroot/index.html`): the Chunks view now has a summary line (count, min / avg / max size)
  and a scrollable table with one row per chunk: index, size (human + exact bytes), a bar proportional to the
  largest chunk, short hash, shared/unique, and a **Download** button. The browser re-hashes the downloaded chunk
  and compares it with the manifest hash before saving it as `<name>.chunk<N>`.
- API: `GET /api/files/{fileId}/chunks/{index}`. Scoped to the file, so it uses the same ownership check as a
  full download (404 for other users). It reads the chunk, verifies SHA-256 *before* sending headers (so a
  corrupt blob returns a clean 500, not a truncated 200), and sets `X-Chunk-SHA256`.
- Service: `FileService.ReadChunkAsync` returns a verified, pooled `RawChunk`.
- Tests: `ApiTests` covers content/hash match, owner-only access, out-of-range index, and corrupt-blob handling.

**Design note:** deliberately *not* `GET /api/chunks/{hash}`. A hash-addressed read would let any user who knows
(or guesses) a hash fetch another user's data, the same confirmation-of-file leak discussed in the privacy module.
