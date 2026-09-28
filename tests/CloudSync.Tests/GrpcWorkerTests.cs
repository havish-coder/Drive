using System.Net.Http.Json;
using CloudSync.Contracts;
using CloudSync.Core;
using CloudSync.Core.Storage;
using CloudSync.Core.Sync;
using CloudSync.Infrastructure.Storage;
using CloudSync.StorageWorker;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static CloudSync.Tests.TestUtil;

namespace CloudSync.Tests;

public class GrpcWorkerTests
{
    private static (WebApplicationFactory<WorkerMarker> Worker, BlobStorage.BlobStorageClient Client) StartWorker(TempDir dir)
    {
        var worker = new WebApplicationFactory<WorkerMarker>().WithWebHostBuilder(b =>
            b.UseSetting("Storage:RootPath", dir.Path("worker-blobs")));
        var channel = GrpcChannel.ForAddress(worker.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = worker.Server.CreateHandler(),
        });
        return (worker, new BlobStorage.BlobStorageClient(channel));
    }

    [Fact]
    public async Task Gateway_stores_and_reconstructs_through_the_grpc_worker()
    {
        using var dir = new TempDir();
        var (worker, client) = StartWorker(dir);
        await using var _ = worker;
        await using var api = CreateApi(dir, services: s => s.AddSingleton<IBlobStore>(new GrpcBlobStore(client)));
        var http = api.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", "carol");
        var data = RandomBytes(6 * 1024 * 1024 + 7, seed: 30);

        var first = await (await http.PostAsync("/api/files?fileName=remote.bin", new ByteArrayContent(data)))
            .Content.ReadFromJsonAsync<UploadResult>();
        Assert.Equal(7, first!.NewChunks);
        Assert.Equal(7, CountBlobs(dir.Path("worker-blobs")));
        Assert.Equal(0, CountBlobs(dir.Path("blobs")));

        var second = await (await http.PostAsync("/api/files?fileName=again.bin", new ByteArrayContent(data)))
            .Content.ReadFromJsonAsync<UploadResult>();
        Assert.Equal(0, second!.NewChunks);

        var downloaded = await http.GetByteArrayAsync($"/api/files/{first.FileId}");
        Assert.Equal(Sha256(data), Sha256(downloaded));
    }

    [Fact]
    public async Task Worker_rejects_a_payload_that_does_not_match_its_hash()
    {
        using var dir = new TempDir();
        var (worker, client) = StartWorker(dir);
        await using var _ = worker;

        using var call = client.PutBlob();
        await call.RequestStream.WriteAsync(new PutBlobRequest
        {
            Header = new BlobHeader { Hash = ContentHash.Compute("expected"u8), Size = 8 },
        });
        await call.RequestStream.WriteAsync(new PutBlobRequest { Data = ByteString.CopyFromUtf8("tampered") });
        await call.RequestStream.CompleteAsync();

        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
        Assert.Equal(StatusCode.DataLoss, error.StatusCode);
        Assert.Equal(0, CountBlobs(dir.Path("worker-blobs")));
    }
}
