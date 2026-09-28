using CloudSync.Api;
using CloudSync.Core.Metadata;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCloudSync();

var app = builder.Build();

await app.Services.GetRequiredService<IMetadataRepository>().InitializeAsync();
app.MapCloudSyncEndpoints();

app.Run();

namespace CloudSync.Api
{
    /// <summary>Entry-point marker for WebApplicationFactory.</summary>
    public sealed class ApiMarker;
}
