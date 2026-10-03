using System.Diagnostics;
using System.Text.Json.Serialization;
using CloudSync.Api;
using CloudSync.Core.Metadata;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCloudSync().AddCloudSyncAuth();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

await app.Services.GetRequiredService<IMetadataRepository>().InitializeAsync();
app.UseDefaultFiles();
app.UseStaticFiles();

var metrics = app.Services.GetRequiredService<RouteMetrics>();
app.Use(async (ctx, next) =>
{
    var start = Stopwatch.GetTimestamp();
    await next();
    if (ctx.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route } && route.StartsWith("/api"))
        metrics.Record($"{ctx.Request.Method} {route}", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
});

app.UseAuthentication();
app.UseAuthorization();
app.MapCloudSyncEndpoints();
app.MapAuthEndpoints();
app.MapDriveEndpoints();

app.Run();

namespace CloudSync.Api
{
    /// <summary>Entry-point marker for WebApplicationFactory.</summary>
    public sealed class ApiMarker;
}
