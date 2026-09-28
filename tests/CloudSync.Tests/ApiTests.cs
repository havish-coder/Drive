using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudSync.Cli;
using CloudSync.Core;
using CloudSync.Core.Chunking;
using CloudSync.Core.Metadata;
using CloudSync.Core.Sync;
using static CloudSync.Tests.TestUtil;

namespace CloudSync.Tests;

public class ApiTests
{
    private const int MB = 1024 * 1024;

    private static async Task<UploadResult> UploadRawAsync(HttpClient http, byte[] data, string name, string user = "alice")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/files?fileName={name}")
        {
            Content = new StreamContent(new MemoryStream(data)),
        };
        request.Headers.Add("X-User-Id", user);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UploadResult>())!;
    }

    private static async Task<HttpResponseMessage> DownloadAsync(HttpClient http, Guid id, string user = "alice")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{id}");
        request.Headers.Add("X-User-Id", user);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task Upload_reconstructs_exactly_and_second_upload_writes_zero_chunks()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = api.CreateClient();
        var data = RandomBytes(24 * MB, seed: 20);

        // 1. End-to-end upload
        var first = await UploadRawAsync(http, data, "video.bin");
        Assert.Equal(24, first.ChunkCount);
        Assert.Equal(24, first.NewChunks);
        Assert.Equal(Sha256(data), first.Sha256);
        Assert.Equal(24, CountBlobs(dir.Path("blobs")));

        // 2. Dedup: identical file → 0 physical writes
        var second = await UploadRawAsync(http, data, "copy-of-video.bin");
        Assert.Equal(0, second.NewChunks);
        Assert.Equal(24, second.DedupedChunks);
        Assert.Equal(24, CountBlobs(dir.Path("blobs")));

        // 3. Reconstruction integrity
        using var download = await DownloadAsync(http, first.FileId);
        download.EnsureSuccessStatusCode();
        Assert.Equal(Sha256(data), Sha256(await download.Content.ReadAsByteArrayAsync()));

        var stats = await http.GetFromJsonAsync<StorageStats>("/api/stats");
        Assert.Equal(48L * MB, stats!.LogicalBytes);
        Assert.Equal(24L * MB, stats.PhysicalBytes);
    }

    [Fact]
    public async Task Overlapping_file_only_writes_the_new_chunks()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = api.CreateClient();
        var a = RandomBytes(12 * MB, seed: 21);
        var b = a.Take(8 * MB).Concat(RandomBytes(4 * MB, seed: 22)).ToArray();

        await UploadRawAsync(http, a, "a.bin");
        var result = await UploadRawAsync(http, b, "b.bin");

        Assert.Equal(4, result.NewChunks);
        Assert.Equal(8, result.DedupedChunks);
    }

    [Fact]
    public async Task Multipart_upload_streams_and_dedups_against_raw_upload()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = api.CreateClient();
        var data = RandomBytes(5 * MB + 123, seed: 23);
        await UploadRawAsync(http, data, "raw.bin");

        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(data);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "form.bin");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        request.Headers.Add("X-User-Id", "alice");
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<UploadResult>())!;
        Assert.Equal("form.bin", result.FileName);
        Assert.Equal(0, result.NewChunks);
        Assert.Equal(Sha256(data), result.Sha256);
    }

    [Fact]
    public async Task Files_are_private_to_their_owner()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = api.CreateClient();
        var uploaded = await UploadRawAsync(http, RandomBytes(1000, seed: 24), "secret.txt", user: "alice");

        using var response = await DownloadAsync(http, uploaded.FileId, user: "mallory");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Corrupted_blob_aborts_the_download_instead_of_serving_bad_bytes()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = api.CreateClient();
        var data = RandomBytes(3 * MB, seed: 25);
        var uploaded = await UploadRawAsync(http, data, "fragile.bin");

        var victim = Directory.EnumerateFiles(dir.Path("blobs"), "*", SearchOption.AllDirectories).First(f => !f.Contains(".tmp"));
        var bytes = await File.ReadAllBytesAsync(victim);
        bytes[100] ^= 0xFF;
        await File.WriteAllBytesAsync(victim, bytes);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var response = await DownloadAsync(http, uploaded.FileId);
            await response.Content.ReadAsByteArrayAsync();
        });
    }

    [Fact]
    public async Task Delta_sync_uploads_only_changed_chunks()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var client = new SyncClient(api.CreateClient(), "bob");
        var chunker = new FastCdcChunker(64 * 1024, 256 * 1024, 1024 * 1024);

        var original = RandomBytes(16 * MB, seed: 26);
        var path = dir.Path("doc.bin");
        await File.WriteAllBytesAsync(path, original);
        var first = await client.PushAsync(path, chunker);
        Assert.Equal(first.TotalBytes, first.UploadedBytes);

        // Insert 100 bytes in the middle: fixed-size chunking would resend ~half the file.
        var edited = original.Take(8 * MB).Concat(RandomBytes(100, seed: 27)).Concat(original.Skip(8 * MB)).ToArray();
        await File.WriteAllBytesAsync(path, edited);
        var second = await client.PushAsync(path, chunker);

        Assert.True(second.UploadedBytes < second.TotalBytes / 10,
            $"sent {second.UploadedBytes:N0} of {second.TotalBytes:N0} bytes");
        Assert.Equal(Sha256(edited), await client.PullAsync(second.FileId, dir.Path("restored.bin")));
    }

    [Fact]
    public async Task Server_rejects_chunks_whose_bytes_do_not_match_the_claimed_hash()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = api.CreateClient();
        var liar = ContentHash.Compute("popular content"u8);

        using var response = await http.PutAsync($"/api/chunks/{liar}", new ByteArrayContent("garbage"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, CountBlobs(dir.Path("blobs")));
    }

    [Fact]
    public async Task Commit_referencing_unknown_chunks_is_rejected()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = api.CreateClient();
        var unknown = ContentHash.Compute("never uploaded"u8);

        using var response = await http.PostAsJsonAsync("/api/sync/commit", new CommitRequest("x.bin", [unknown], null));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
