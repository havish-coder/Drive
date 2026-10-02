# Issues & Modifications Log

Running log of problems found and changes made after the initial build. Newest first.
Status: `open` · `fixed` · `wontfix`

| # | Status | Summary |
|---|---|---|
| 1 | fixed | Chunk sizes not visible in the UI; no way to download an individual chunk |

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
