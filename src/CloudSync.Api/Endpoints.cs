using System.Buffers;
using System.IO.Pipelines;
using CloudSync.Core;
using CloudSync.Core.Services;
using CloudSync.Core.Sync;
using CloudSync.Infrastructure.Metadata;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
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

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/files", UploadAsync);
        api.MapGet("/files/{fileId:guid}", async (Guid fileId, bool? inline, HttpContext ctx, FileService files, SqliteDrive drive, ILoggerFactory logs) =>
        {
            if (!await CanReadAsync(ctx, drive, fileId)) ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            else await SendFileAsync(ctx, files, logs, fileId, inline == true);
        });
        api.MapGet("/files/{fileId:guid}/manifest", async (Guid fileId, HttpContext ctx, FileService files, SqliteDrive drive) =>
            await CanReadAsync(ctx, drive, fileId) ? Results.Ok(await files.GetManifestAsync(fileId, ctx.RequestAborted)) : Results.NotFound());
        api.MapGet("/metrics", (RouteMetrics metrics) => Results.Ok(metrics.Snapshot()));
        api.MapGet("/files/{fileId:guid}/chunks/{index:int}", DownloadChunkAsync);
        api.MapGet("/stats",async (HttpContext ctx, FileService files) =>
            Results.Ok(await files.GetStatsAsync(ctx.RequestAborted)));

        api.MapPost("/sync/negotiate", (NegotiateRequest request, HttpContext ctx, FileService files) =>
            Guarded(async () => Results.Ok(new NegotiateResponse(await files.FindMissingAsync(request.Hashes, ctx.RequestAborted)))));
        api.MapPut("/chunks/{hash}", PutChunkAsync);
        api.MapPost("/sync/commit", (CommitRequest request, HttpContext ctx, FileService files, SqliteDrive drive) =>
            Guarded(async () =>
            {
                if (await DriveEndpoints.WritableOwnerAsync(ctx, drive, request.FolderId) is not { } owner) return Results.StatusCode(403);
                var folder = request.Path is { Length: > 0 } path ? await drive.EnsurePathAsync(owner, request.FolderId, path) : request.FolderId;
                var result = await files.CommitAsync(owner, request with { FolderId = folder }, ctx.RequestAborted);
                return Results.Created($"/api/files/{result.FileId}", result);
            }));
    }

    private static async Task<IResult> UploadAsync(HttpContext ctx, FileService files, SqliteDrive drive, string? fileName, Guid? folderId, string? path)
    {
        if (await DriveEndpoints.WritableOwnerAsync(ctx, drive, folderId) is not { } owner) return Results.StatusCode(403);
        var folder = path is { Length: > 0 } ? await drive.EnsurePathAsync(owner, folderId, path) : folderId;
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
            var result = await files.UploadAsync(owner, SqliteDrive.CleanName(Path.GetFileName(name)), source, ctx.RequestAborted, folder);
            await source.CompleteAsync();
            ctx.Response.Headers["Server-Timing"] = string.Join(", ", result.Timings!.Select(t => $"{t.Key};dur={t.Value}"));
            return Results.Created($"/api/files/{result.FileId}", result);
        }
    }

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private static async Task<bool> CanReadAsync(HttpContext ctx, SqliteDrive drive, Guid fileId) =>
        await drive.GetFileAsync(fileId) is { } file && await drive.FileRoleAsync(ctx.UserId(), file) >= Role.Viewer;

    /// <summary>
    /// Streams a file. Inline (preview) only for raster images and PDF: an HTML or SVG file shown inline would run its
    /// scripts on our origin (stored XSS), so everything else is always a download.
    /// </summary>
    internal static async Task SendFileAsync(HttpContext ctx, FileService files, ILoggerFactory logs, Guid fileId, bool inline)
    {
        if (await files.GetManifestAsync(fileId, ctx.RequestAborted) is not { } manifest)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var previewable = ContentTypes.TryGetContentType(manifest.File.FileName, out var type) &&
            (type == "application/pdf" || (type.StartsWith("image/") && type != "image/svg+xml"));
        inline &= previewable;
        ctx.Response.ContentType = inline ? type! : "application/octet-stream";
        ctx.Response.ContentLength = manifest.File.TotalSize;
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment")
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

    // Scoped to a file (not a bare hash) so chunks are only reachable by someone who can read a file containing them.
    private static async Task DownloadChunkAsync(Guid fileId, int index, HttpContext ctx, FileService files, SqliteDrive drive, ILoggerFactory logs)
    {
        var manifest = await CanReadAsync(ctx, drive, fileId) ? await files.GetManifestAsync(fileId, ctx.RequestAborted) : null;
        if (manifest is null ||
            manifest.Chunks.FirstOrDefault(c => c.SequenceIndex == index) is not { } entry)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        try
        {
            // Verified before any header is written, so a corrupt blob yields a real 500 rather than a cut-off 200.
            using var chunk = await files.ReadChunkAsync(entry, ctx.RequestAborted);
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = chunk.Length;
            ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = $"{manifest.File.FileName}.chunk{index}",
            }.ToString();
            ctx.Response.Headers["X-Chunk-SHA256"] = entry.ChunkHash;
            await ctx.Response.Body.WriteAsync(chunk.Memory, ctx.RequestAborted);
        }
        catch (ChunkIntegrityException ex)
        {
            logs.CreateLogger("CloudSync.Download").LogError(ex, "Chunk {Index} of {FileId} failed verification", index, fileId);
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
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

    internal static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067) // SQLITE_CONSTRAINT_UNIQUE
        {
            return Results.Conflict(new { error = "An item with that name already exists here." });
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

    private static void DisableBodyLimit(HttpContext ctx)
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = null;
    }
}
