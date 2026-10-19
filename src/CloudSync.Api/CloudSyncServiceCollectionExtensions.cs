using CloudSync.Contracts;
using CloudSync.Core.Chunking;
using CloudSync.Core.Metadata;
using CloudSync.Core.Services;
using CloudSync.Core.Storage;
using CloudSync.Infrastructure.Metadata;
using CloudSync.Infrastructure.Storage;
using Grpc.Net.Client;

namespace CloudSync.Api;

public sealed class StorageOptions
{
    /// <summary>"Local" (blobs on this node) or "Remote" (gRPC storage worker).</summary>
    public string Mode { get; set; } = "Local";
    public string RootPath { get; set; } = "data/blobs";
    public string WorkerUrl { get; set; } = "http://localhost:5100";

    /// <summary>Where in-progress resumable uploads are kept until they complete.</summary>
    public string UploadsPath { get; set; } = "data/uploads";
}

/// <summary>Composition root: the only place that knows which concrete strategies are in use.</summary>
public static class CloudSyncServiceCollectionExtensions
{
    public static IServiceCollection AddCloudSync(this IServiceCollection services)
    {
        // Resolved lazily so test hosts can override configuration.
        services.AddSingleton(sp => Section(sp, "Chunking").Get<ChunkingOptions>() ?? new ChunkingOptions());
        services.AddSingleton(sp => Section(sp, "Pipeline").Get<PipelineOptions>() ?? new PipelineOptions());
        services.AddSingleton(sp => Section(sp, "Storage").Get<StorageOptions>() ?? new StorageOptions());

        services.AddSingleton(sp => ChunkerFactory.Create(sp.GetRequiredService<ChunkingOptions>()));
        services.AddSingleton(sp => new SqliteMetadataRepository(
            Section(sp, "Database")["ConnectionString"] ?? "Data Source=data/cloudsync.db"));
        services.AddSingleton<IMetadataRepository>(sp => sp.GetRequiredService<SqliteMetadataRepository>());
        services.AddSingleton<SqliteDrive>();
        services.AddSingleton<RouteMetrics>();
        services.AddHostedService<Janitor>();

        services.AddSingleton<IBlobStore>(sp =>
        {
            var storage = sp.GetRequiredService<StorageOptions>();
            return storage.Mode.Equals("Remote", StringComparison.OrdinalIgnoreCase)
                ? new GrpcBlobStore(new BlobStorage.BlobStorageClient(GrpcChannel.ForAddress(storage.WorkerUrl)))
                : new LocalFileSystemBlobStore(storage.RootPath);
        });

        services.AddSingleton<ChunkStore>();
        services.AddSingleton<UploadPipeline>();
        services.AddSingleton<FileService>();
        return services;
    }

    private static IConfigurationSection Section(IServiceProvider sp, string name) =>
        sp.GetRequiredService<IConfiguration>().GetSection($"CloudSync:{name}");
}
