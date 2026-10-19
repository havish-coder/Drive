using CloudSync.Core.Metadata;
using CloudSync.Core.Services;
using CloudSync.Core.Storage;
using CloudSync.Infrastructure.Metadata;

namespace CloudSync.Api;

public sealed record NewFolder(string Name, Guid? ParentId);
/// <summary>Rename and/or move. <c>MoveTo</c> is a folder id or "root".</summary>
public sealed record ItemPatch(string? Name, string? MoveTo);
public sealed record NewShare(string ItemType, Guid ItemId, string? UserName, string? Role, bool Link = false);

/// <summary>A file or folder as the caller sees it.</summary>
internal sealed record Item(string Type, Guid Id, string Name, string OwnerId, Guid? ParentId, bool Trashed, Role Role);

public static class DriveEndpoints
{
    public static void MapDriveEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/folders/root/children", async (HttpContext ctx, SqliteDrive drive) => Results.Ok(await drive.ListAsync(ctx.UserId(), null)));
        api.MapGet("/folders/{id:guid}/children", async (Guid id, HttpContext ctx, SqliteDrive drive) =>
            await drive.ListAsync(ctx.UserId(), id) is { } listing ? Results.Ok(listing) : Results.NotFound());

        api.MapPost("/folders", (NewFolder f, HttpContext ctx, SqliteDrive drive) => Endpoints.Guarded(async () =>
        {
            if (await WritableOwnerAsync(ctx, drive, f.ParentId) is not { } owner) return Results.StatusCode(403);
            var folder = await drive.CreateFolderAsync(owner, f.ParentId, f.Name ?? "");
            await drive.LogAsync(ctx.UserId(), "folder", folder.Id, "created", folder.Name);
            return Results.Ok(folder);
        }));

        // Rename, move, trash and copy behave the same for files and folders.
        foreach (var (route, type) in new[] { ("files", "file"), ("folders", "folder") })
        {
            api.MapPatch($"/{route}/{{id:guid}}", (Guid id, ItemPatch p, HttpContext ctx, SqliteDrive drive) =>
                Endpoints.Guarded(() => PatchAsync(ctx, drive, type, id, p)));
            api.MapDelete($"/{route}/{{id:guid}}", (Guid id, HttpContext ctx, SqliteDrive drive) => TrashAsync(ctx, drive, type, id));
            api.MapPost($"/{route}/{{id:guid}}/copy", (Guid id, HttpContext ctx, SqliteDrive drive) =>
                Endpoints.Guarded(() => CopyAsync(ctx, drive, type, id)));
        }

        api.MapGet("/folders/{id:guid}/zip", async (Guid id, HttpContext ctx, SqliteDrive drive, FileService files) =>
        {
            if (await ItemAsync(ctx, drive, "folder", id) is not { Role: > Role.None }) ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            else await Endpoints.SendZipAsync(ctx, drive, files, (await drive.GetFolderAsync(id))!);
        });

        // ---- trash ----
        api.MapGet("/trash", async (HttpContext ctx, SqliteDrive drive) => Results.Ok(await drive.TrashAsync(ctx.UserId())));
        api.MapPost("/trash/{type}/{id:guid}/restore", (string type, Guid id, HttpContext ctx, SqliteDrive drive) => Endpoints.Guarded(async () =>
        {
            if (await ItemAsync(ctx, drive, type, id) is not { Trashed: true, Role: Role.Owner } item) return Results.NotFound();
            await drive.RestoreAsync(type, id);
            await drive.LogAsync(ctx.UserId(), type, id, "restored from trash", item.Name);
            return Results.NoContent();
        }));
        api.MapDelete("/trash/{type}/{id:guid}", async (string type, Guid id, HttpContext ctx, SqliteDrive drive, IBlobStore blobs) =>
        {
            if (await ItemAsync(ctx, drive, type, id) is not { Trashed: true, Role: Role.Owner } item) return Results.NotFound();
            await drive.DeleteForeverAsync(type, id);
            await drive.LogAsync(ctx.UserId(), type, id, "deleted forever", item.Name);
            await drive.CollectGarbageAsync(blobs, Janitor.GcGrace);
            return Results.NoContent();
        });
        api.MapDelete("/trash", async (HttpContext ctx, SqliteDrive drive, IBlobStore blobs) =>
        {
            await drive.EmptyTrashAsync(ctx.UserId());
            await drive.CollectGarbageAsync(blobs, Janitor.GcGrace);
            return Results.NoContent();
        });

        // ---- versions ----
        api.MapGet("/files/{id:guid}/versions", async (Guid id, HttpContext ctx, SqliteDrive drive) =>
            await ItemAsync(ctx, drive, "file", id) is { Role: > Role.None } ? Results.Ok(await drive.VersionsAsync(id)) : Results.NotFound());
        api.MapGet("/files/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, bool? inline, HttpContext ctx,
            SqliteDrive drive, FileService files, ILoggerFactory logs) =>
        {
            var manifest = await ItemAsync(ctx, drive, "file", id) is { Role: > Role.None } ? await drive.VersionManifestAsync(id, versionId) : null;
            if (manifest is null) ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            else await Endpoints.SendManifestAsync(ctx, files, logs, manifest, inline == true);
        });
        api.MapPost("/files/{id:guid}/versions/{versionId:guid}/restore", async (Guid id, Guid versionId, HttpContext ctx,
            SqliteDrive drive, IMetadataRepository metadata) =>
        {
            var item = await ItemAsync(ctx, drive, "file", id);
            if (item is null || item.Role == Role.None || item.Trashed) return Results.NotFound();
            if (item.Role < Role.Editor) return Results.StatusCode(403);
            if (await drive.VersionManifestAsync(id, versionId) is not { } version) return Results.NotFound();
            // The current content becomes a version too, so restoring never loses history.
            await metadata.SaveVersionAsync(version.File, version.Chunks.Select(c => new FileChunkRecord(c.SequenceIndex, c.ChunkHash)).ToList());
            await drive.LogAsync(ctx.UserId(), "file", id, "restored a previous version", item.Name);
            return Results.Ok(await drive.GetFileAsync(id));
        });

        // ---- stars, recent, activity ----
        api.MapPut("/stars/{type}/{id:guid}", (string type, Guid id, HttpContext ctx, SqliteDrive drive) => StarAsync(ctx, drive, type, id, true));
        api.MapDelete("/stars/{type}/{id:guid}", (string type, Guid id, HttpContext ctx, SqliteDrive drive) => StarAsync(ctx, drive, type, id, false));
        api.MapGet("/starred", async (HttpContext ctx, SqliteDrive drive) =>
        {
            var visible = new List<DriveItem>();
            foreach (var item in await drive.StarredAsync(ctx.UserId()))
                if (item.OwnerId == ctx.UserId() || await ItemAsync(ctx, drive, item.Type, item.Id) is { Role: > Role.None })
                    visible.Add(item);
            return Results.Ok(visible);
        });
        api.MapGet("/recent", async (HttpContext ctx, SqliteDrive drive) => Results.Ok(await drive.RecentAsync(ctx.UserId())));
        api.MapGet("/activity/{type}/{id:guid}", async (string type, Guid id, HttpContext ctx, SqliteDrive drive) =>
            await ItemAsync(ctx, drive, type, id) is { Role: > Role.None } ? Results.Ok(await drive.ActivityAsync(type, id)) : Results.NotFound());

        api.MapGet("/search", async (string? q, HttpContext ctx, SqliteDrive drive) =>
            Results.Ok(string.IsNullOrWhiteSpace(q) ? [] : await drive.SearchAsync(ctx.UserId(), q.Trim())));
        api.MapGet("/shared-with-me", async (HttpContext ctx, SqliteDrive drive) => Results.Ok(await drive.SharedWithMeAsync(ctx.UserId())));

        // ---- sharing (owner only) ----
        api.MapGet("/shares/{itemType}/{itemId:guid}", async (string itemType, Guid itemId, HttpContext ctx, SqliteDrive drive) =>
            await ItemAsync(ctx, drive, itemType, itemId) is { Role: Role.Owner } ? Results.Ok(await drive.SharesAsync(itemId)) : Results.NotFound());

        api.MapPost("/shares", async (NewShare s, HttpContext ctx, SqliteDrive drive) =>
        {
            var owner = ctx.UserId();
            if (await ItemAsync(ctx, drive, s.ItemType, s.ItemId) is not { Role: Role.Owner, Trashed: false }) return Results.NotFound();
            if (s.Link)
            {
                var token = await drive.LinkAsync(owner, s.ItemType, s.ItemId);
                await drive.LogAsync(owner, s.ItemType, s.ItemId, "shared", "anyone with the link can view");
                return Results.Ok(new { token });
            }
            var grantee = (s.UserName ?? "").Trim().ToLowerInvariant();
            if (grantee == owner || !await drive.UserExistsAsync(grantee)) return Results.BadRequest(new { error = "No such user." });
            var role = s.Role == "editor" ? "editor" : "viewer";
            await drive.ShareWithUserAsync(owner, s.ItemType, s.ItemId, grantee, role);
            await drive.LogAsync(owner, s.ItemType, s.ItemId, "shared", $"with {grantee} as {role}");
            return Results.Ok(await drive.SharesAsync(s.ItemId));
        });

        api.MapDelete("/shares/{shareId:guid}", async (Guid shareId, HttpContext ctx, SqliteDrive drive) =>
        {
            if (await drive.DeleteShareAsync(shareId, ctx.UserId()) is not { } share) return Results.NotFound();
            await drive.LogAsync(ctx.UserId(), share.ItemType, share.ItemId, "stopped sharing",
                share.Grantee is null ? "public link removed" : $"with {share.Grantee}");
            return Results.NoContent();
        });

        // ---- public links: no login; the unguessable token is the permission, scoped to the shared item's subtree ----
        var pub = app.MapGroup("/api/public/{token}");
        pub.MapGet("/", async (string token, Guid? folderId, SqliteDrive drive) =>
        {
            if (await drive.ShareByTokenAsync(token) is not { } share) return Results.NotFound();
            if (share.ItemType == "file")
                return await drive.GetFileAsync(share.ItemId) is { TrashedAt: null } file ? Results.Ok(new { file }) : Results.NotFound();
            return await PublicScopeAsync(drive, share, folderId) is { } scope
                ? Results.Ok(await drive.ChildrenAsync(scope.Path, Role.Viewer, scope.From))
                : Results.NotFound();
        });
        pub.MapGet("/files/{fileId:guid}", async (string token, Guid fileId, bool? inline, HttpContext ctx, SqliteDrive drive,
            FileService files, ILoggerFactory logs) =>
        {
            var share = await drive.ShareByTokenAsync(token);
            var file = await drive.GetFileAsync(fileId);
            var inScope = share is not null && file is { TrashedAt: null } && (share.ItemType == "file"
                ? share.ItemId == fileId
                : file.FolderId is { } folder && await PublicScopeAsync(drive, share, folder) is not null);
            if (!inScope) ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            else await Endpoints.SendFileAsync(ctx, files, logs, fileId, inline == true);
        });
        pub.MapGet("/zip", async (string token, Guid? folderId, HttpContext ctx, SqliteDrive drive, FileService files) =>
        {
            var share = await drive.ShareByTokenAsync(token);
            if (share is not { ItemType: "folder" } || await PublicScopeAsync(drive, share, folderId) is not { } scope)
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            else await Endpoints.SendZipAsync(ctx, drive, files, scope.Path[^1]);
        });
    }

    private static async Task<IResult> PatchAsync(HttpContext ctx, SqliteDrive drive, string type, Guid id, ItemPatch p)
    {
        if (await ItemAsync(ctx, drive, type, id) is not { Trashed: false } item || item.Role == Role.None) return Results.NotFound();
        if (item.Role < Role.Editor || (p.MoveTo is not null && item.Role != Role.Owner)) return Results.StatusCode(403);
        if (p.Name is not null && p.Name.Trim() != item.Name)
        {
            await (type == "file" ? drive.RenameFileAsync(id, p.Name) : drive.RenameFolderAsync(id, p.Name));
            await drive.LogAsync(ctx.UserId(), type, id, "renamed", $"{item.Name} → {p.Name.Trim()}");
        }
        if (p.MoveTo is not null)
        {
            var target = await MoveTargetAsync(drive, p.MoveTo, ctx.UserId());
            await (type == "file" ? drive.MoveFileAsync(id, target) : drive.MoveFolderAsync(id, target));
            await drive.LogAsync(ctx.UserId(), type, id, "moved", target is null ? "to My Drive" : $"to {(await drive.GetFolderAsync(target.Value))!.Name}");
        }
        return Results.Ok(type == "file" ? (object?)await drive.GetFileAsync(id) : await drive.GetFolderAsync(id));
    }

    private static async Task<IResult> TrashAsync(HttpContext ctx, SqliteDrive drive, string type, Guid id)
    {
        if (await ItemAsync(ctx, drive, type, id) is not { Trashed: false } item || item.Role == Role.None) return Results.NotFound();
        if (item.Role < Role.Editor) return Results.StatusCode(403);
        await drive.TrashAsync(type, id);
        await drive.LogAsync(ctx.UserId(), type, id, "moved to trash", item.Name);
        return Results.NoContent();
    }

    /// <summary>Instant copy (no bytes duplicated). Lands next to the original if the caller can write there, else in their root.</summary>
    private static async Task<IResult> CopyAsync(HttpContext ctx, SqliteDrive drive, string type, Guid id)
    {
        if (await ItemAsync(ctx, drive, type, id) is not { Trashed: false } item || item.Role == Role.None) return Results.NotFound();
        var owner = await WritableOwnerAsync(ctx, drive, item.ParentId);
        var parent = owner is null ? null : item.ParentId;
        owner ??= ctx.UserId();
        var copy = type == "file"
            ? await drive.CopyFileAsync(id, owner, parent, $"Copy of {item.Name}")
            : await drive.CopyFolderAsync(id, owner, parent, $"Copy of {item.Name}");
        if (type == "file") await drive.TouchAsync(parent);
        await drive.LogAsync(ctx.UserId(), type, copy, "created as a copy", $"of {item.Name}");
        return Results.Ok(new { id = copy });
    }

    private static async Task<IResult> StarAsync(HttpContext ctx, SqliteDrive drive, string type, Guid id, bool starred)
    {
        if (await ItemAsync(ctx, drive, type, id) is not { Role: > Role.None }) return Results.NotFound();
        await drive.StarAsync(ctx.UserId(), type, id, starred);
        return Results.NoContent();
    }

    internal static async Task<Item?> ItemAsync(HttpContext ctx, SqliteDrive drive, string type, Guid id)
    {
        if (type == "file")
            return await drive.GetFileAsync(id) is { } f
                ? new Item("file", id, f.FileName, f.UserId, f.FolderId, f.TrashedAt is not null, await drive.FileRoleAsync(ctx.UserId(), f))
                : null;
        if (type != "folder") return null;
        var path = await drive.PathAsync(id);
        if (path.Count == 0) return null;
        var folder = path[^1];
        return new Item("folder", id, folder.Name, folder.OwnerId, folder.ParentId, folder.TrashedAt is not null,
            (await drive.AccessAsync(ctx.UserId(), folder.OwnerId, path)).Role);
    }

    /// <summary>Owner of the folder if the caller may write into it (null folder = caller's own root); otherwise null.</summary>
    internal static async Task<string?> WritableOwnerAsync(HttpContext ctx, SqliteDrive drive, Guid? folderId)
    {
        if (folderId is null) return ctx.UserId();
        var path = await drive.PathAsync(folderId);
        if (path.Count == 0 || path.Any(f => f.TrashedAt is not null)) return null;
        return (await drive.AccessAsync(ctx.UserId(), path[^1].OwnerId, path)).Role >= Role.Editor ? path[^1].OwnerId : null;
    }

    private static async Task<(List<FolderItem> Path, int From)?> PublicScopeAsync(SqliteDrive drive, ShareInfo share, Guid? folderId)
    {
        var path = await drive.PathAsync(folderId ?? share.ItemId);
        var from = path.FindIndex(f => f.Id == share.ItemId);
        return from < 0 || path.Any(f => f.TrashedAt is not null) ? null : (path, from);
    }

    // Moves stay inside the owner's own tree; moving across owners would mean transferring ownership.
    private static async Task<Guid?> MoveTargetAsync(SqliteDrive drive, string moveTo, string owner)
    {
        if (moveTo == "root") return null;
        return Guid.TryParse(moveTo, out var id) && await drive.GetFolderAsync(id) is { TrashedAt: null } target && target.OwnerId == owner
            ? id
            : throw new ArgumentException("Destination folder not found.");
    }
}
