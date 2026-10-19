using CloudSync.Core;
using CloudSync.Core.Storage;

namespace CloudSync.Infrastructure.Storage;

/// <summary>
/// Hierarchical content-addressable store: <c>root/ab/cd/abcd…</c>. Writes go to a temp file on the same
/// volume, are flushed, then published with a no-overwrite move (atomic link on Unix), so a reader never
/// sees a partial blob and concurrent writers of the same hash can't corrupt each other.
/// </summary>
public sealed class LocalFileSystemBlobStore : IBlobStore
{
    private readonly string _root;
    private readonly string _temp;
    private readonly bool _durable;

    public LocalFileSystemBlobStore(string rootPath, bool durable = true)
    {
        _root = Path.GetFullPath(rootPath);
        _temp = Path.Combine(_root, ".tmp");
        _durable = durable;
        Directory.CreateDirectory(_temp);
    }

    public string GetStorageKey(string hash) => BlobKey.For(hash);

    public Task<bool> ExistsAsync(string hash, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(PathFor(hash)));

    public async Task<bool> PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var target = PathFor(hash);
        if (File.Exists(target)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = Path.Combine(_temp, $"{hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                PreallocationSize = data.Length,
            }))
            {
                await stream.WriteAsync(data, ct);
                if (_durable) stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, target, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(target))
            {
                return false; // lost the race; identical bytes are already published
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<int> ReadAsync(string hash, Memory<byte> destination, CancellationToken ct = default)
    {
        await using var stream = OpenRead(hash);
        if (stream.Length > destination.Length)
            throw new ChunkIntegrityException(hash, $"blob is {stream.Length} bytes, expected at most {destination.Length}");

        var total = 0;
        int read;
        while (total < destination.Length && (read = await stream.ReadAsync(destination[total..], ct)) > 0)
            total += read;
        return total;
    }

    public Task<bool> DeleteAsync(string hash, CancellationToken ct = default)
    {
        var path = PathFor(hash);
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <summary>Raw stream access for the storage worker's streaming reads.</summary>
    public FileStream OpenRead(string hash) =>
        new(PathFor(hash), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private string PathFor(string hash) => Path.Combine(_root, BlobKey.For(hash));
}
