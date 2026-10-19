using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using CloudSync.Core;
using CloudSync.Core.Metadata;
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
                var name = SqliteDrive.CleanName(request.FileName ?? "");
                return Created(ctx, await SaveAsync(ctx, drive, owner, folder, name, replace =>
                    files.CommitAsync(owner, request with { FolderId = folder, FileName = name }, ctx.RequestAborted, replace)));
            }));

        // Resumable uploads, following Google Drive's protocol: create a session, PUT byte ranges, resume after a drop.
        api.MapPost("/uploads", (NewUpload u, HttpContext ctx, SqliteDrive drive, StorageOptions storage) => Guarded(async () =>
        {
            if (u.Size < 0) return Results.BadRequest(new { error = "Size must be zero or more." });
            if (await DriveEndpoints.WritableOwnerAsync(ctx, drive, u.FolderId) is not { } owner) return Results.StatusCode(403);
            var folder = u.Path is { Length: > 0 } path ? await drive.EnsurePathAsync(owner, u.FolderId, path) : u.FolderId;
            var session = new UploadSession(Guid.NewGuid(), ctx.UserId(), owner, folder, SqliteDrive.CleanName(u.FileName ?? ""), u.Size);
            await drive.CreateUploadAsync(session);
            Directory.CreateDirectory(storage.UploadsPath);
            File.Create(PartPath(storage, session.UploadId)).Dispose();
            return Results.Created($"/api/uploads/{session.UploadId}", new { uploadId = session.UploadId });
        }));
        api.MapPut("/uploads/{id:guid}", ResumeUploadAsync);
    }

    internal static string PartPath(StorageOptions storage, Guid id) => Path.Combine(storage.UploadsPath, $"{id}.part");

    /// <summary>
    /// <c>Content-Range: bytes a-b/total</c> appends a slice (a must equal the bytes already received);
    /// <c>bytes */total</c> asks for status. Incomplete → 308 with <c>Range: bytes=0-n</c>; complete → the file.
    /// ponytail: the slices land in a temp file and are read again by the chunker (bytes hit the disk twice).
    /// </summary>
    private static async Task<IResult> ResumeUploadAsync(Guid id, HttpContext ctx, SqliteDrive drive, FileService files, StorageOptions storage)
    {
        if (await drive.GetUploadAsync(id) is not { } session || session.UserId != ctx.UserId()) return Results.NotFound();
        if (!ContentRangeHeaderValue.TryParse(ctx.Request.Headers.ContentRange.ToString(), out var range) || range.Length != session.TotalSize)
            return Results.BadRequest(new { error = "Content-Range must be 'bytes a-b/total' or 'bytes */total' with the session's total size." });

        var part = PartPath(storage, id);
        var received = new FileInfo(part).Length;
        if (range.HasRange)
        {
            if (range.From != received) return Incomplete(ctx, received);
            DisableBodyLimit(ctx);
            FileStream file;
            try { file = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None); }
            catch (IOException) { return Results.Conflict(new { error = "Another request is writing to this upload." }); }
            await using (file)
            {
                try { await ctx.Request.Body.CopyToAsync(file, ctx.RequestAborted); }
                catch when (ctx.RequestAborted.IsCancellationRequested) { return Results.Empty; } // what arrived is kept; the client resumes
            }
            received = new FileInfo(part).Length;
            if (received > session.TotalSize)
            {
                await drive.DeleteUploadAsync(id);
                File.Delete(part);
                return Results.BadRequest(new { error = "More bytes than the declared size; upload discarded." });
            }
        }
        if (received < session.TotalSize) return Incomplete(ctx, received);

        UploadResult result;
        await using (var stream = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
        {
            var source = PipeReader.Create(stream, ReaderOptions);
            result = await SaveAsync(ctx, drive, session.OwnerId, session.FolderId, session.FileName, replace =>
                files.UploadAsync(session.OwnerId, session.FileName, source, ctx.RequestAborted, session.FolderId, replace));
            await source.CompleteAsync();
        }
        await drive.DeleteUploadAsync(id);
        File.Delete(part);
        return Created(ctx, result);
    }

    private static IResult Incomplete(HttpContext ctx, long received)
    {
        // 308 = "Resume Incomplete" in Google's protocol. Range is the standard signal; the JSON body is for clients
        // (like .NET's HttpClient) that drop a Range header on responses.
        if (received > 0) ctx.Response.Headers.Range = $"bytes=0-{received - 1}";
        return Results.Json(new { received }, statusCode: StatusCodes.Status308PermanentRedirect);
    }

    /// <summary>Every way of storing a file ends here: same name in the same folder becomes a new version, and it's logged.</summary>
    internal static async Task<UploadResult> SaveAsync(HttpContext ctx, SqliteDrive drive, string owner, Guid? folder, string name,
        Func<Guid?, Task<UploadResult>> store)
    {
        var existing = await drive.FindFileAsync(owner, folder, name);
        var result = await store(existing);
        await drive.TouchAsync(folder);
        await drive.LogAsync(ctx.UserId(), "file", result.FileId, existing is null ? "uploaded" : "uploaded a new version", name);
        return result;
    }

    private static IResult Created(HttpContext ctx, UploadResult result)
    {
        if (result.Timings is { } timings) ctx.Response.Headers["Server-Timing"] = string.Join(", ", timings.Select(t => $"{t.Key};dur={t.Value}"));
        return Results.Created($"/api/files/{result.FileId}", result);
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
            var clean = SqliteDrive.CleanName(Path.GetFileName(name));
            var result = await SaveAsync(ctx, drive, owner, folder, clean, replace =>
                files.UploadAsync(owner, clean, source, ctx.RequestAborted, folder, replace));
            await source.CompleteAsync();
            return Created(ctx, result);
        }
    }

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private static async Task<bool> CanReadAsync(HttpContext ctx, SqliteDrive drive, Guid fileId) =>
        await drive.GetFileAsync(fileId) is { } file && await drive.FileRoleAsync(ctx.UserId(), file) >= Role.Viewer;

    internal static async Task SendFileAsync(HttpContext ctx, FileService files, ILoggerFactory logs, Guid fileId, bool inline)
    {
        if (await files.GetManifestAsync(fileId, ctx.RequestAborted) is not { } manifest)
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        else
            await SendManifestAsync(ctx, files, logs, manifest, inline);
    }

    /// <summary>
    /// Streams a file (or a past version). Inline only for raster images, PDF, audio and video: an HTML or SVG file
    /// shown inline would run its scripts on our origin (stored XSS), so everything else is always a download.
    /// A <c>Range</c> request (media seeking, resumed download) is served from a seekable view over the chunks.
    /// </summary>
    internal static async Task SendManifestAsync(HttpContext ctx, FileService files, ILoggerFactory logs, FileManifest manifest, bool inline)
    {
        var previewable = ContentTypes.TryGetContentType(manifest.File.FileName, out var type) &&
            (type is "application/pdf" || type.StartsWith("audio/") || type.StartsWith("video/") ||
             (type.StartsWith("image/") && type != "image/svg+xml"));
        inline &= previewable;
        ctx.Response.ContentType = inline ? type! : "application/octet-stream";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment")
        {
            FileNameStar = manifest.File.FileName,
        }.ToString();
        if (manifest.File.Sha256 is { } sha) ctx.Response.Headers["X-Content-SHA256"] = sha;

        if (ctx.Request.Headers.Range.Count > 0)
        {
            await Results.Stream(files.OpenRead(manifest), ctx.Response.ContentType, enableRangeProcessing: true).ExecuteAsync(ctx);
            return;
        }

        ctx.Response.Headers.AcceptRanges = "bytes";
        ctx.Response.ContentLength = manifest.File.TotalSize;
        try
        {
            await files.DownloadAsync(manifest, ctx.Response.Body, ctx.RequestAborted);
        }
        catch (ChunkIntegrityException ex)
        {
            // Headers are already sent; cutting the connection is the only honest signal left.
            logs.CreateLogger("CloudSync.Download").LogError(ex, "Aborting download of {FileId}", manifest.File.FileId);
            ctx.Abort();
        }
    }

    /// <summary>A folder as a zip, built on the fly from verified chunks: no temp file, nothing buffered whole.</summary>
    internal static async Task SendZipAsync(HttpContext ctx, SqliteDrive drive, FileService files, FolderItem folder)
    {
        // ZipArchive writes entry headers and the central directory synchronously; allow it for this response only.
        if (ctx.Features.Get<IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = $"{folder.Name}.zip" }.ToString();
        using var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (path, fileId) in await drive.ZipEntriesAsync(folder.Id))
        {
            if (await files.GetManifestAsync(fileId, ctx.RequestAborted) is not { } manifest) continue;
            await using var entry = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
            await files.DownloadAsync(manifest, entry, ctx.RequestAborted);
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

public sealed record NewUpload(string? FileName, long Size, Guid? FolderId, string? Path);
