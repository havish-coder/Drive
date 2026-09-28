using CloudSync.Infrastructure.Storage;
using CloudSync.StorageWorker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new LocalFileSystemBlobStore(config["Storage:RootPath"] ?? "data/worker-blobs");
});

var app = builder.Build();

app.MapGrpcService<BlobStorageService>();
app.MapGet("/", () => "CloudSync storage worker (gRPC).");

app.Run();

namespace CloudSync.StorageWorker
{
    /// <summary>Entry-point marker for WebApplicationFactory.</summary>
    public sealed class WorkerMarker;
}
