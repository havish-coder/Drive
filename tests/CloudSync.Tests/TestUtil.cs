using System.IO.Pipelines;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CloudSync.Api;
using CloudSync.Core.Chunking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CloudSync.Tests;

public sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("cloudsync-test-").FullName;

    public string Path(string name) => System.IO.Path.Combine(Root, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public static class TestUtil
{
    public static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Small pipe segments on purpose, to exercise multi-segment sequences.</summary>
    public static async Task<List<(string Hash, byte[] Data)>> ChunkAllAsync(IChunker chunker, byte[] data)
    {
        var reader = PipeReader.Create(new MemoryStream(data), new StreamPipeReaderOptions(bufferSize: 4096));
        var chunks = new List<(string, byte[])>();
        await foreach (var chunk in chunker.ChunkAsync(reader))
        {
            using (chunk) chunks.Add((Core.ContentHash.Compute(chunk.Span), chunk.Span.ToArray()));
        }
        return chunks;
    }

    public static int CountBlobs(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}.tmp"))
            : 0;

    /// <summary>Registers the user and returns a client holding their session cookie.</summary>
    public static async Task<HttpClient> ClientForAsync(WebApplicationFactory<ApiMarker> api, string user)
    {
        // No auto-redirect: a resumable upload's 308 "Resume Incomplete" has no Location and must reach the caller.
        var http = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await http.PostAsJsonAsync("/api/auth/register", new { userName = user, password = "password123" })).EnsureSuccessStatusCode();
        return http;
    }

    public static WebApplicationFactory<ApiMarker> CreateApi(
        TempDir dir, int chunkSize = 1024 * 1024, Action<IServiceCollection>? services = null) =>
        new WebApplicationFactory<ApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CloudSync:Storage:Mode", "Local");
            builder.UseSetting("CloudSync:Storage:RootPath", dir.Path("blobs"));
            builder.UseSetting("CloudSync:Storage:UploadsPath", dir.Path("uploads"));
            builder.UseSetting("CloudSync:Database:ConnectionString", $"Data Source={dir.Path("meta.db")}");
            builder.UseSetting("CloudSync:Chunking:Algorithm", "FixedSize");
            builder.UseSetting("CloudSync:Chunking:ChunkSize", chunkSize.ToString());
            if (services is not null) builder.ConfigureServices(services);
        });
}
