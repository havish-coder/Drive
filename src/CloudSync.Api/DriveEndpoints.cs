using CloudSync.Core.Services;
using CloudSync.Infrastructure.Metadata;

namespace CloudSync.Api;

public sealed record NewFolder(string Name, Guid? ParentId);
/// <summary>Rename and/or move. <c>MoveTo</c> is a folder id or "root".</summary>
public sealed record ItemPatch(string? Name, string? MoveTo);
public sealed record NewShare(string ItemType, Guid ItemId, string? UserName, string? Role, bool Link = false);

public static class DriveEndpoints
{
    public static void MapDriveEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/folders/root/children", async (HttpContext ctx, SqliteDrive drive) => Results.Ok(await drive.ListAsync(ctx.UserId(), null)));
        api.MapGet("/folders/{id:guid}/children", async (Guid id, HttpContext ctx, SqliteDrive drive) =>
            await drive.ListAsync(ctx.UserId(), id) is { } listing ? Results.Ok(listing) : Results.NotFound());

        api.MapPost("/folders", (NewFolder f, HttpContext ctx, SqliteDrive drive) => Endpoints.Guarded(async () =>
            await WritableOwnerAsync(ctx, drive, f.ParentId) is { } owner
                ? Results.Ok(await drive.CreateFolderAsync(owner, f.ParentId, f.Name ?? ""))
                : Results.StatusCode(403)));

        api.MapPatch("/folders/{id:guid}", (Guid id, ItemPatch p, HttpContext ctx, SqliteDrive drive) => Endpoints.Guarded(async () =>
        {
            var path = await drive.PathAsync(id);
            if (path.Count == 0) return Results.NotFound();
            var (role, _) = await drive.AccessAsync(ctx.UserId(), path[^1].OwnerId, path);
            if (role < Role.Editor || (p.MoveTo is not null && role != Role.Owner)) return Results.StatusCode(403);
            if (p.Name is not null) await drive.RenameFolderAsync(id, p.Name);
            if (p.MoveTo is not null) await drive.MoveFolderAsync(id, await MoveTargetAsync(drive, p.MoveTo, ctx.UserId()));
            return Results.Ok(await drive.GetFolderAsync(id));
        }));

        api.MapDelete("/folders/{id:guid}", async (Guid id, HttpContext ctx, SqliteDrive drive) =>
        {
            var path = await drive.PathAsync(id);
            if (path.Count == 0) return Results.NotFound();
            if ((await drive.AccessAsync(ctx.UserId(), path[^1].OwnerId, path)).Role < Role.Editor) return Results.StatusCode(403);
            await drive.DeleteFolderAsync(id);
            return Results.NoContent();
        });

        api.MapPatch("/files/{id:guid}", (Guid id, ItemPatch p, HttpContext ctx, SqliteDrive drive) => Endpoints.Guarded(async () =>
        {
            if (await drive.GetFileAsync(id) is not { } file) return Results.NotFound();
            var role = await drive.FileRoleAsync(ctx.UserId(), file);
            if (role < Role.Editor || (p.MoveTo is not null && role != Role.Owner)) return Results.StatusCode(403);
            if (p.Name is not null) await drive.RenameFileAsync(id, p.Name);
            if (p.MoveTo is not null) await drive.MoveFileAsync(id, await MoveTargetAsync(drive, p.MoveTo, ctx.UserId()));
            return Results.Ok(await drive.GetFileAsync(id));
        }));

        api.MapDelete("/files/{id:guid}", async (Guid id, HttpContext ctx, SqliteDrive drive) =>
        {
            if (await drive.GetFileAsync(id) is not { } file) return Results.NotFound();
            if (await drive.FileRoleAsync(ctx.UserId(), file) < Role.Editor) return Results.StatusCode(403);
            await drive.DeleteFileAsync(id);
            return Results.NoContent();
        });

        api.MapGet("/search", async (string? q, HttpContext ctx, SqliteDrive drive) =>
            Results.Ok(string.IsNullOrWhiteSpace(q) ? [] : await drive.SearchAsync(ctx.UserId(), q.Trim())));
        api.MapGet("/shared-with-me", async (HttpContext ctx, SqliteDrive drive) => Results.Ok(await drive.SharedWithMeAsync(ctx.UserId())));

        // Only the owner can see, add or remove shares.
        api.MapGet("/shares/{itemType}/{itemId:guid}", async (string itemType, Guid itemId, HttpContext ctx, SqliteDrive drive) =>
            await OwnerOfAsync(drive, itemType, itemId) == ctx.UserId() ? Results.Ok(await drive.SharesAsync(itemId)) : Results.NotFound());

        api.MapPost("/shares", async (NewShare s, HttpContext ctx, SqliteDrive drive) =>
        {
            var owner = ctx.UserId();
            if (await OwnerOfAsync(drive, s.ItemType, s.ItemId) != owner) return Results.NotFound();
            if (s.Link) return Results.Ok(new { token = await drive.LinkAsync(owner, s.ItemType, s.ItemId) });
            var grantee = (s.UserName ?? "").Trim().ToLowerInvariant();
            if (grantee == owner || !await drive.UserExistsAsync(grantee)) return Results.BadRequest(new { error = "No such user." });
            await drive.ShareWithUserAsync(owner, s.ItemType, s.ItemId, grantee, s.Role == "editor" ? "editor" : "viewer");
            return Results.Ok(await drive.SharesAsync(s.ItemId));
        });

        api.MapDelete("/shares/{shareId:guid}", async (Guid shareId, HttpContext ctx, SqliteDrive drive) =>
            await drive.DeleteShareAsync(shareId, ctx.UserId()) ? Results.NoContent() : Results.NotFound());

        // Public links: no login; the unguessable token is the permission, scoped to the shared item and what's inside it.
        var pub = app.MapGroup("/api/public/{token}");
        pub.MapGet("/", async (string token, Guid? folderId, SqliteDrive drive) =>
        {
            if (await drive.ShareByTokenAsync(token) is not { } share) return Results.NotFound();
            if (share.ItemType == "file")
                return await drive.GetFileAsync(share.ItemId) is { } file ? Results.Ok(new { file }) : Results.NotFound();
            var path = await drive.PathAsync(folderId ?? share.ItemId);
            var from = path.FindIndex(f => f.Id == share.ItemId);
            return from < 0 ? Results.NotFound() : Results.Ok(await drive.ChildrenAsync(path, Role.Viewer, from));
        });
        pub.MapGet("/files/{fileId:guid}", async (string token, Guid fileId, bool? inline, HttpContext ctx, SqliteDrive drive,
            FileService files, ILoggerFactory logs) =>
        {
            var share = await drive.ShareByTokenAsync(token);
            var file = await drive.GetFileAsync(fileId);
            var inScope = share is not null && file is not null && (share.ItemType == "file"
                ? share.ItemId == fileId
                : (await drive.PathAsync(file.FolderId)).Any(f => f.Id == share.ItemId));
            if (!inScope) ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            else await Endpoints.SendFileAsync(ctx, files, logs, fileId, inline == true);
        });
    }

    /// <summary>Owner of the folder if the caller may write into it (null folder = caller's own root); otherwise null.</summary>
    internal static async Task<string?> WritableOwnerAsync(HttpContext ctx, SqliteDrive drive, Guid? folderId)
    {
        if (folderId is null) return ctx.UserId();
        var path = await drive.PathAsync(folderId);
        if (path.Count == 0) return null;
        return (await drive.AccessAsync(ctx.UserId(), path[^1].OwnerId, path)).Role >= Role.Editor ? path[^1].OwnerId : null;
    }

    // Moves stay inside the owner's own tree; moving across owners would mean transferring ownership.
    private static async Task<Guid?> MoveTargetAsync(SqliteDrive drive, string moveTo, string owner)
    {
        if (moveTo == "root") return null;
        return Guid.TryParse(moveTo, out var id) && await drive.GetFolderAsync(id) is { } target && target.OwnerId == owner
            ? id
            : throw new ArgumentException("Destination folder not found.");
    }

    private static async Task<string?> OwnerOfAsync(SqliteDrive drive, string itemType, Guid id) => itemType switch
    {
        "file" => (await drive.GetFileAsync(id))?.UserId,
        "folder" => (await drive.GetFolderAsync(id))?.OwnerId,
        _ => null,
    };
}
