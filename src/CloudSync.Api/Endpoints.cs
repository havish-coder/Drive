using System.Buffers;
using System.IO.Pipelines;
using CloudSync.Core;
using CloudSync.Core.Services;
using CloudSync.Core.Sync;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace CloudSync.Api;

public static class Endpoints
{
    public const int MaxChunkBytes = 64 * 1024 * 1024;

    // Decouples chunking from Kestrel's request buffer: Kestrel pauses reading once ~1 MB is
    // unconsumed, and a chunker holding a 4 MB window would otherwise stall the connection.
    private static readonly StreamPipeReaderOptions ReaderOptions =
        new(bufferSize: 1024 * 1024, minimumReadSize: 64 * 1024, leaveOpen: true);

    public static void MapCloudSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok("ok"));

        var api = app.MapGroup("/api");
        api.MapPost("/files", UploadAsync);
        api.MapGet("/files", async (HttpContext ctx, FileService files) =>
            Results.Ok(await files.ListFilesAsync(UserId(ctx), ctx.RequestAborted)));
        api.MapGet("/files/{fileId:guid}", DownloadAsync);
        api.MapGet("/files/{fileId:guid}/manifest", async (Guid fileId, HttpContext ctx, FileService files) =>
        {
            var manifest = await files.GetManifestAsync(fileId, ctx.RequestAborted);
            return manifest is null || manifest.File.UserId != UserId(ctx) ? Results.NotFound() : Results.Ok(manifest);
        });
        api.MapGet("/stats", async (HttpContext ctx, FileService files) =>
            Results.Ok(await files.GetStatsAsync(ctx.RequestAborted)));

        api.MapPost("/sync/negotiate", (NegotiateRequest request, HttpContext ctx, FileService files) =>
            Guarded(async () => Results.Ok(new NegotiateResponse(await files.FindMissingAsync(request.Hashes, ctx.RequestAborted)))));
        api.MapPut("/chunks/{hash}", PutChunkAsync);
        api.MapPost("/sync/commit", (CommitRequest request, HttpContext ctx, FileService files) =>
            Guarded(async () =>
            {
                var result = await files.CommitAsync(UserId(ctx), request, ctx.RequestAborted);
                return Results.Created($"/api/files/{result.FileId}", result);
            }));
    }

    private static async Task<IResult> UploadAsync(HttpContext ctx, FileService files, string? fileName)
    {
        DisableBodyLimit(ctx);
        var request = ctx.Request;

        if (request.HasFormContentType &&
            MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) &&
            HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is { Length: > 0 } boundary)
        {
            // Stream the first file part; never buffer the form (IFormFile would spool the whole body first).
            var reader = new MultipartReader(boundary, request.Body);
            while (await reader.ReadNextSectionAsync(ctx.RequestAborted) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    !disposition.IsFileDisposition())
                    continue;

                var name = fileName ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value ?? "upload.bin";
                return await StoreAsync(PipeReader.Create(section.Body, ReaderOptions), name);
            }
            return Results.BadRequest("No file part in multipart body.");
        }

        return await StoreAsync(PipeReader.Create(request.Body, ReaderOptions), fileName ?? "upload.bin");

        async Task<IResult> StoreAsync(PipeReader source, string name)
        {
            var result = await files.UploadAsync(UserId(ctx), Path.GetFileName(name), source, ctx.RequestAborted);
            await source.CompleteAsync();
            return Results.Created($"/api/files/{result.FileId}", result);
        }
    }

    private static async Task DownloadAsync(Guid fileId, HttpContext ctx, FileService files, ILoggerFactory logs)
    {
        var manifest = await files.GetManifestAsync(fileId, ctx.RequestAborted);
        if (manifest is null || manifest.File.UserId != UserId(ctx))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = manifest.File.TotalSize;
        ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = manifest.File.FileName,
        }.ToString();
        if (manifest.File.Sha256 is { } sha) ctx.Response.Headers["X-Content-SHA256"] = sha;

        try
        {
            await files.DownloadAsync(manifest, ctx.Response.Body, ctx.RequestAborted);
        }
        catch (ChunkIntegrityException ex)
        {
            // Headers are already sent; cutting the connection is the only honest signal left.
            logs.CreateLogger("CloudSync.Download").LogError(ex, "Aborting download of {FileId}", fileId);
            ctx.Abort();
        }
    }

    private static async Task<IResult> PutChunkAsync(string hash, HttpContext ctx, FileService files)
    {
        if (!ContentHash.IsValid(hash)) return Results.BadRequest("Hash must be lowercase SHA-256 hex.");
        if (ctx.Request.ContentLength is not { } length) return Results.StatusCode(StatusCodes.Status411LengthRequired);
        if (length is <= 0 or > MaxChunkBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        DisableBodyLimit(ctx);
        var size = (int)length;
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            await ctx.Request.Body.ReadExactlyAsync(buffer.AsMemory(0, size), ctx.RequestAborted);
            return await Guarded(async () =>
            {
                var outcome = await files.PutChunkAsync(hash, buffer.AsMemory(0, size), ctx.RequestAborted);
                return Results.Ok(new PutChunkResult(hash, outcome == ChunkStoreOutcome.Written));
            });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MissingChunksException ex)
        {
            return Results.Conflict(new { error = ex.Message, missing = ex.Missing });
        }
        catch (Exception ex) when (ex is ChunkIntegrityException or ArgumentException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    // Stand-in for real authentication.
    private static string UserId(HttpContext ctx) =>
        ctx.Request.Headers["X-User-Id"].FirstOrDefault() is { Length: > 0 } user ? user : "anonymous";

    private static void DisableBodyLimit(HttpContext ctx)
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = null;
    }
}
