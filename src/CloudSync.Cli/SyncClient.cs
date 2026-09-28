using System.Buffers;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CloudSync.Core;
using CloudSync.Core.Chunking;
using CloudSync.Core.Sync;

namespace CloudSync.Cli;

public sealed record PushResult(
    Guid FileId, long TotalBytes, long UploadedBytes, int ChunkCount, int UploadedChunks, string Sha256);

/// <summary>
/// Delta-sync client: chunk and hash locally, ask the server which chunks it lacks,
/// upload only those, then commit the ordered manifest.
/// </summary>
public sealed class SyncClient(HttpClient http, string userId)
{
    private sealed record LocalChunk(string Hash, long Offset, int Length);

    public async Task<PushResult> PushAsync(string path, IChunker chunker, int parallelism = 4, CancellationToken ct = default)
    {
        // 1. Chunk + hash locally. Only hashes and offsets are kept; bytes are re-read on demand.
        var chunks = new List<LocalChunk>();
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var file = File.OpenRead(path))
        {
            var reader = PipeReader.Create(file, new StreamPipeReaderOptions(bufferSize: 1024 * 1024));
            await foreach (var chunk in chunker.ChunkAsync(reader, ct))
            {
                using (chunk)
                {
                    chunks.Add(new LocalChunk(ContentHash.Compute(chunk.Span), chunk.Offset, chunk.Length));
                    fileHash.AppendData(chunk.Span);
                }
            }
            await reader.CompleteAsync();
        }

        // 2. Negotiate.
        var unique = chunks.DistinctBy(c => c.Hash).ToList();
        var negotiate = await SendAsync<NegotiateResponse>(HttpMethod.Post, "api/sync/negotiate",
            JsonContent.Create(new NegotiateRequest(unique.Select(c => c.Hash).ToList())), ct);
        var missing = negotiate.Missing.ToHashSet();
        var toUpload = unique.Where(c => missing.Contains(c.Hash)).ToList();

        // 3. Upload only missing chunks.
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous))
        {
            await Parallel.ForEachAsync(toUpload, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                async (chunk, token) =>
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
                    try
                    {
                        var read = 0;
                        while (read < chunk.Length)
                        {
                            var n = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read, chunk.Length - read), chunk.Offset + read, token);
                            if (n == 0) throw new IOException($"{path} shrank while syncing.");
                            read += n;
                        }
                        var content = new ByteArrayContent(buffer, 0, chunk.Length);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        await SendAsync<PutChunkResult>(HttpMethod.Put, $"api/chunks/{chunk.Hash}", content, token);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                });
        }

        // 4. Commit manifest.
        var commit = await SendAsync<UploadResult>(HttpMethod.Post, "api/sync/commit",
            JsonContent.Create(new CommitRequest(Path.GetFileName(path), chunks.Select(c => c.Hash).ToList(), chunker.Name)), ct);

        return new PushResult(
            commit.FileId,
            chunks.Sum(c => (long)c.Length),
            toUpload.Sum(c => (long)c.Length),
            chunks.Count,
            toUpload.Count,
            Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>Downloads a file and returns the SHA-256 of what was written.</summary>
    public async Task<string> PullAsync(Guid fileId, string outputPath, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/files/{fileId}");
        request.Headers.Add("X-User-Id", userId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(outputPath);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string uri, HttpContent content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Add("X-User-Id", userId);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{method} {uri} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
