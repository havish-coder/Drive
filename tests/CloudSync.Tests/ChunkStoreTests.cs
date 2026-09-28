using CloudSync.Core;
using CloudSync.Core.Services;
using CloudSync.Infrastructure.Metadata;
using CloudSync.Infrastructure.Storage;
using static CloudSync.Tests.TestUtil;

namespace CloudSync.Tests;

public class ChunkStoreTests
{
    private static async Task<(ChunkStore Store, SqliteMetadataRepository Repo)> CreateAsync(TempDir dir)
    {
        var repo = new SqliteMetadataRepository($"Data Source={dir.Path("meta.db")}");
        await repo.InitializeAsync();
        return (new ChunkStore(new LocalFileSystemBlobStore(dir.Path("blobs")), repo), repo);
    }

    [Fact]
    public async Task Concurrent_writers_of_the_same_chunk_write_it_once()
    {
        using var dir = new TempDir();
        var (store, repo) = await CreateAsync(dir);
        var data = RandomBytes(512 * 1024, seed: 10);
        var hash = ContentHash.Compute(data);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => store.StoreAsync(hash, data))));

        Assert.Equal(1, outcomes.Count(o => o == ChunkStoreOutcome.Written));
        Assert.Equal(1, CountBlobs(dir.Path("blobs")));
        Assert.Equal(1, (await repo.GetStatsAsync()).ChunkCount);
    }

    [Fact]
    public async Task Two_nodes_sharing_disk_and_db_still_write_once()
    {
        using var dir = new TempDir();
        var (nodeA, _) = await CreateAsync(dir);
        var (nodeB, repo) = await CreateAsync(dir); // separate in-flight maps, like separate processes
        var data = RandomBytes(256 * 1024, seed: 11);
        var hash = ContentHash.Compute(data);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(i => Task.Run(() => (i % 2 == 0 ? nodeA : nodeB).StoreAsync(hash, data))));

        Assert.Equal(1, outcomes.Count(o => o == ChunkStoreOutcome.Written));
        Assert.Equal(1, CountBlobs(dir.Path("blobs")));
        Assert.Equal(1, (await repo.GetStatsAsync()).ChunkCount);
    }

    [Fact]
    public async Task Blob_on_disk_without_row_is_healed_without_rewriting()
    {
        using var dir = new TempDir();
        var (store, repo) = await CreateAsync(dir);
        var data = RandomBytes(4096, seed: 12);
        var hash = ContentHash.Compute(data);

        // Simulates a crash after the blob write but before the Chunks insert.
        await new LocalFileSystemBlobStore(dir.Path("blobs")).PutAsync(hash, data);

        Assert.Equal(ChunkStoreOutcome.Deduplicated, await store.StoreAsync(hash, data));
        Assert.True(await repo.ChunkExistsAsync(hash));
    }
}
