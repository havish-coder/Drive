using CloudSync.Contracts;
using CloudSync.Core;
using CloudSync.Core.Storage;
using Google.Protobuf;
using Grpc.Core;

namespace CloudSync.Infrastructure.Storage;

/// <summary>Remote proxy: a storage worker reached over gRPC, presented as a plain <see cref="IBlobStore"/>.</summary>
public sealed class GrpcBlobStore(BlobStorage.BlobStorageClient client) : IBlobStore
{
    public const int FrameSize = 1024 * 1024;

    public string GetStorageKey(string hash) => BlobKey.For(hash);

    public async Task<bool> PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        using var call = client.PutBlob(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new PutBlobRequest { Header = new BlobHeader { Hash = hash, Size = data.Length } }, ct);

        for (var offset = 0; offset < data.Length; offset += FrameSize)
        {
            // Zero-copy: the frame wraps the pooled chunk buffer, which outlives the call.
            var frame = data.Slice(offset, Math.Min(FrameSize, data.Length - offset));
            await call.RequestStream.WriteAsync(new PutBlobRequest { Data = UnsafeByteOperations.UnsafeWrap(frame) }, ct);
        }

        await call.RequestStream.CompleteAsync();
        return (await call.ResponseAsync).Created;
    }

    public async Task<int> ReadAsync(string hash, Memory<byte> destination, CancellationToken ct = default)
    {
        using var call = client.ReadBlob(new ReadBlobRequest { Hash = hash }, cancellationToken: ct);
        var total = 0;
        await foreach (var frame in call.ResponseStream.ReadAllAsync(ct))
        {
            if (total + frame.Data.Length > destination.Length)
                throw new ChunkIntegrityException(hash, $"worker sent more than {destination.Length} bytes");
            frame.Data.Span.CopyTo(destination.Span[total..]);
            total += frame.Data.Length;
        }
        return total;
    }

    public async Task<bool> ExistsAsync(string hash, CancellationToken ct = default) =>
        (await client.BlobExistsAsync(new BlobExistsRequest { Hash = hash }, cancellationToken: ct)).Exists;
}
