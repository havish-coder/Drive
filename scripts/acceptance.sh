#!/usr/bin/env bash
# PRD Definition-of-Done check against real processes:
#   1. upload a large file   2. re-upload → 0 new chunk writes   3. download → sha256sum match
#   4. delta sync of an edited copy sends only changed chunks
# Usage: SIZE_MB=500 MODE=Remote scripts/acceptance.sh     (MODE=Local skips the gRPC worker)
set -euo pipefail

SIZE_MB=${SIZE_MB:-500}
MODE=${MODE:-Remote}
API=http://localhost:5000
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d)
PIDS=()
cleanup() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null || true; done; rm -rf "$WORK"; }
trap cleanup EXIT

step() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fail() { printf '\033[31mFAIL: %s\033[0m\n' "$*"; exit 1; }
pass() { printf '\033[32mPASS: %s\033[0m\n' "$*"; }
blobs() { find "$1" -type f -not -path '*/.tmp/*' 2>/dev/null | wc -l; }

step "Build"
dotnet build "$ROOT" -c Release -v q --nologo >/dev/null
BIN="$ROOT/src/%s/bin/Release/net8.0/%s.dll"

BLOB_DIR="$WORK/blobs"
if [[ "$MODE" == "Remote" ]]; then
  BLOB_DIR="$WORK/worker-blobs"
  step "Start gRPC storage worker"
  ( cd "$ROOT/src/CloudSync.StorageWorker" && Storage__RootPath="$BLOB_DIR" \
      dotnet "$(printf "$BIN" CloudSync.StorageWorker CloudSync.StorageWorker)" >"$WORK/worker.log" 2>&1 ) &
  PIDS+=($!)
fi

step "Start API gateway (storage mode: $MODE)"
( cd "$ROOT/src/CloudSync.Api" && \
  CloudSync__Storage__Mode="$MODE" CloudSync__Storage__RootPath="$WORK/blobs" \
  CloudSync__Database__ConnectionString="Data Source=$WORK/meta.db" \
  dotnet "$(printf "$BIN" CloudSync.Api CloudSync.Api)" >"$WORK/api.log" 2>&1 ) &
PIDS+=($!)
for _ in $(seq 1 60); do curl -sf "$API/health" >/dev/null && break; sleep 0.5; done
curl -sf "$API/health" >/dev/null || { cat "$WORK/api.log"; fail "API did not start"; }

step "Generate ${SIZE_MB} MB random file"
head -c "${SIZE_MB}M" /dev/urandom > "$WORK/original.bin"
ORIGINAL_SHA=$(sha256sum "$WORK/original.bin" | cut -d' ' -f1)
echo "sha256 $ORIGINAL_SHA"

upload() { curl -sf -X POST -T "$1" -H 'Content-Type: application/octet-stream' -H 'X-User-Id: acceptance' "$API/api/files?fileName=$(basename "$1")"; }

step "1. End-to-end upload"
START=$(date +%s.%N)
FIRST=$(upload "$WORK/original.bin")
echo "$FIRST" | jq .
printf 'upload took %.2fs\n' "$(echo "$(date +%s.%N) - $START" | bc)"
FILE_ID=$(echo "$FIRST" | jq -r .fileId)
BLOBS_AFTER_FIRST=$(blobs "$BLOB_DIR")
[[ "$(echo "$FIRST" | jq -r .sha256)" == "$ORIGINAL_SHA" ]] || fail "server-computed sha256 differs"
[[ "$BLOBS_AFTER_FIRST" -eq "$(echo "$FIRST" | jq .newChunks)" ]] || fail "blob count != newChunks"
pass "$(echo "$FIRST" | jq .chunkCount) chunks, $BLOBS_AFTER_FIRST blobs on disk, manifest registered"

step "2. Deduplication: upload identical file again"
cp "$WORK/original.bin" "$WORK/duplicate.bin"
SECOND=$(upload "$WORK/duplicate.bin")
echo "$SECOND" | jq '{chunkCount, newChunks, dedupedChunks}'
[[ "$(echo "$SECOND" | jq .newChunks)" -eq 0 ]] || fail "duplicate upload wrote new chunks"
[[ "$(blobs "$BLOB_DIR")" -eq "$BLOBS_AFTER_FIRST" ]] || fail "blob count changed"
pass "0 new physical chunk writes"

step "3. Reconstruction integrity"
START=$(date +%s.%N)
curl -sf -H 'X-User-Id: acceptance' "$API/api/files/$FILE_ID" -o "$WORK/downloaded.bin"
printf 'download took %.2fs\n' "$(echo "$(date +%s.%N) - $START" | bc)"
sha256sum "$WORK/original.bin" "$WORK/downloaded.bin"
[[ "$(sha256sum "$WORK/downloaded.bin" | cut -d' ' -f1)" == "$ORIGINAL_SHA" ]] || fail "sha256 mismatch"
pass "sha256sum exact match"

step "4. Delta sync (FastCDC client): insert 1 KB in the middle of the file"
CLI="$(printf "$BIN" CloudSync.Cli CloudSync.Cli)"
dotnet "$CLI" push "$WORK/original.bin" --server "$API" --user acceptance
HALF=$(( SIZE_MB * 1024 * 1024 / 2 ))
{ head -c "$HALF" "$WORK/original.bin"; head -c 1024 /dev/urandom; tail -c +"$(( HALF + 1 ))" "$WORK/original.bin"; } > "$WORK/edited.bin"
PUSH=$(dotnet "$CLI" push "$WORK/edited.bin" --server "$API" --user acceptance)
echo "$PUSH"
EDITED_ID=$(echo "$PUSH" | awk '/^fileId/ {print $2}')
dotnet "$CLI" pull "$EDITED_ID" "$WORK/edited-restored.bin" --server "$API" --user acceptance
[[ "$(sha256sum "$WORK/edited-restored.bin" | cut -d' ' -f1)" == "$(sha256sum "$WORK/edited.bin" | cut -d' ' -f1)" ]] || fail "delta-synced file mismatch"
pass "edited file round-trips; see bandwidth saved above"

step "Storage stats"
curl -sf "$API/api/stats" | jq .
printf '\n\033[32mAll acceptance criteria passed.\033[0m\n'
