using System.Buffers;
using CloudSync.Contracts;
using CloudSync.Core;
using CloudSync.Infrastructure.Storage;
using Google.Protobuf;
using Grpc.Core;

namespace CloudSync.StorageWorker;

/// <summary>gRPC storage node. Re-verifies every blob it receives before publishing it.</summary>
public sealed class BlobStorageService(LocalFileSystemBlobStore store) : BlobStorage.BlobStorageBase
{
    public const int MaxBlobSize = 64 * 1024 * 1024;

    public override async Task<PutBlobReply> PutBlob(IAsyncStreamReader<PutBlobRequest> requestStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (!await requestStream.MoveNext(ct) || requestStream.Current.PayloadCase != PutBlobRequest.PayloadOneofCase.Header)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "First message must be a header."));

        var header = requestStream.Current.Header;
        if (!ContentHash.IsValid(header.Hash) || header.Size is < 0 or > MaxBlobSize)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid hash or size."));

        var size = (int)header.Size;
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var filled = 0;
            while (await requestStream.MoveNext(ct))
            {
                var data = requestStream.Current.Data;
                if (filled + data.Length > size)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "More data than declared size."));
                data.Span.CopyTo(buffer.AsSpan(filled));
                filled += data.Length;
            }

            if (filled != size)
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Expected {size} bytes, got {filled}."));
            if (ContentHash.Compute(buffer.AsSpan(0, size)) != header.Hash)
                throw new RpcException(new Status(StatusCode.DataLoss, "Payload does not match hash."));

            return new PutBlobReply { Created = await store.PutAsync(header.Hash, buffer.AsMemory(0, size), ct) };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override async Task ReadBlob(ReadBlobRequest request, IServerStreamWriter<BlobFrame> responseStream, ServerCallContext context)
    {
        if (!ContentHash.IsValid(request.Hash))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid hash."));

        FileStream stream;
        try
        {
            stream = store.OpenRead(request.Hash);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Blob {request.Hash} not found."));
        }

        var buffer = ArrayPool<byte>.Shared.Rent(GrpcBlobStore.FrameSize);
        try
        {
            await using (stream)
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, GrpcBlobStore.FrameSize), context.CancellationToken)) > 0)
                    await responseStream.WriteAsync(new BlobFrame { Data = ByteString.CopyFrom(buffer, 0, read) });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override async Task<BlobExistsReply> BlobExists(BlobExistsRequest request, ServerCallContext context)
    {
        if (!ContentHash.IsValid(request.Hash))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid hash."));
        return new BlobExistsReply { Exists = await store.ExistsAsync(request.Hash, context.CancellationToken) };
    }
}
