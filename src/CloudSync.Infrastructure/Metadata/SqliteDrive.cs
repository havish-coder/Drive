using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CloudSync.Core;
using CloudSync.Core.Metadata;
using CloudSync.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CloudSync.Infrastructure.Metadata;

public enum Role { None, Viewer, Editor, Owner }

public sealed record FolderItem(Guid Id, string Name, string OwnerId, Guid? ParentId, DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null, DateTimeOffset? TrashedAt = null);
public sealed record Listing(FolderItem? Folder, Role Role, IReadOnlyList<FolderItem> Path, IReadOnlyList<FolderItem> Folders,
    IReadOnlyList<FileRecord> Files, IReadOnlyList<Guid> Starred);
public sealed record ShareInfo(Guid ShareId, string ItemType, Guid ItemId, string OwnerId, string? Grantee, string? LinkToken, string Role);
public sealed record DriveItem(string Type, Guid Id, string Name, string OwnerId, string? Role, long? Size, DateTimeOffset? Date = null);
public sealed record VersionInfo(Guid VersionId, long TotalSize, string? Sha256, DateTimeOffset CreatedAt);
public sealed record ActivityEntry(string ItemType, Guid ItemId, string? Name, string UserId, string Action, string? Detail, DateTimeOffset At);
public sealed record UploadSession(Guid UploadId, string UserId, string OwnerId, Guid? FolderId, string FileName, long TotalSize);

/// <summary>
/// Users, folders, sharing, trash, versions, stars, activity and search. Every access decision goes through
/// <see cref="AccessAsync"/>. Trash stamps one timestamp on an item and everything inside it, so every query can
/// filter with a plain <c>TrashedAt IS NULL</c> and a restore brings back exactly what was trashed together.
/// </summary>
public sealed class SqliteDrive(SqliteMetadataRepository db)
{
    private const string SelectFolder = "SELECT FolderId, Name, UserId, ParentId, CreatedAt, UpdatedAt, TrashedAt FROM Folders";

    // A folder and all folders below it, trashed or not.
    private const string Subtree = """
        WITH RECURSIVE sub(id) AS (
            SELECT $id UNION ALL SELECT f.FolderId FROM Folders f JOIN sub ON f.ParentId = sub.id)
        """;

    // ---- users & API tokens ----

    public async Task<bool> CreateUserAsync(string userId, string passwordHash) =>
        await ExecAsync("INSERT INTO Users (UserId, PasswordHash, CreatedAt) VALUES ($u, $h, $at) ON CONFLICT DO NOTHING",
            ("$u", userId), ("$h", passwordHash), ("$at", Now())) == 1;

    public async Task<string?> PasswordHashAsync(string userId) =>
        (await QueryAsync("SELECT PasswordHash FROM Users WHERE UserId = $u", r => r.GetString(0), ("$u", userId))).FirstOrDefault();

    public async Task<bool> UserExistsAsync(string userId) => await PasswordHashAsync(userId) is not null;

    /// <summary>Returns a new random token; only its hash is stored.</summary>
    public async Task<string> CreateTokenAsync(string userId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await ExecAsync("INSERT INTO ApiTokens (TokenHash, UserId, CreatedAt) VALUES ($h, $u, $at)",
            ("$h", HashToken(token)), ("$u", userId), ("$at", Now()));
        return token;
    }

    public async Task<string?> UserForTokenAsync(string token) =>
        (await QueryAsync("SELECT UserId FROM ApiTokens WHERE TokenHash = $h", r => r.GetString(0), ("$h", HashToken(token)))).FirstOrDefault();

    private static string HashToken(string token) => ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(token));

    // ---- access control (the one choke point) ----

    public async Task<FolderItem?> GetFolderAsync(Guid id) =>
        (await QueryAsync($"{SelectFolder} WHERE FolderId = $id", ReadFolder, ("$id", id.ToString()))).FirstOrDefault();

    public async Task<FileRecord?> GetFileAsync(Guid id) =>
        (await QueryAsync($"{SqliteMetadataRepository.SelectFile} WHERE FileId = $id", SqliteMetadataRepository.ReadFile, ("$id", id.ToString()))).FirstOrDefault();

    /// <summary>Ancestors of a folder including itself, root first.</summary>
    public Task<List<FolderItem>> PathAsync(Guid? folderId) => folderId is null
        ? Task.FromResult(new List<FolderItem>())
        : QueryAsync("""
            WITH RECURSIVE anc(FolderId, Name, UserId, ParentId, CreatedAt, UpdatedAt, TrashedAt, Depth) AS (
                SELECT FolderId, Name, UserId, ParentId, CreatedAt, UpdatedAt, TrashedAt, 0 FROM Folders WHERE FolderId = $id
                UNION ALL
                SELECT f.FolderId, f.Name, f.UserId, f.ParentId, f.CreatedAt, f.UpdatedAt, f.TrashedAt, a.Depth + 1
                FROM Folders f JOIN anc a ON f.FolderId = a.ParentId)
            SELECT FolderId, Name, UserId, ParentId, CreatedAt, UpdatedAt, TrashedAt FROM anc ORDER BY Depth DESC
            """, ReadFolder, ("$id", folderId.ToString()));

    /// <summary>
    /// Owner, or the strongest share on the item or any folder above it (shares inherit downwards). Trashed items
    /// are visible to their owner only. <c>From</c> is where the caller's visibility starts in <paramref name="path"/>.
    /// </summary>
    public async Task<(Role Role, int From)> AccessAsync(string userId, string ownerId, List<FolderItem> path, Guid? fileId = null, bool fileTrashed = false)
    {
        if (userId == ownerId) return (Role.Owner, 0);
        if (fileTrashed || path.Any(f => f.TrashedAt is not null)) return (Role.None, 0);
        var ids = path.Select(f => f.Id.ToString()).ToList();
        if (fileId is not null) ids.Add(fileId.ToString()!);
        var shares = await QueryAsync(
            "SELECT ItemId, Role FROM Shares WHERE GranteeId = $u AND ItemId IN (SELECT value FROM json_each($ids))",
            r => (Id: r.GetString(0), Role: r.GetString(1)), ("$u", userId), ("$ids", JsonSerializer.Serialize(ids)));
        if (shares.Count == 0) return (Role.None, 0);
        var from = path.FindIndex(f => shares.Any(s => s.Id == f.Id.ToString()));
        return (shares.Any(s => s.Role == "editor") ? Role.Editor : Role.Viewer, from < 0 ? path.Count : from);
    }

    public async Task<Role> FileRoleAsync(string userId, FileRecord file) =>
        (await AccessAsync(userId, file.UserId, await PathAsync(file.FolderId), file.FileId, file.TrashedAt is not null)).Role;

    // ---- folders ----

    /// <summary>Null folder = the caller's own root ("My Drive").</summary>
    public async Task<Listing?> ListAsync(string userId, Guid? folderId)
    {
        if (folderId is null)
            return new Listing(null, Role.Owner, [],
                await QueryAsync($"{SelectFolder} WHERE UserId = $u AND ParentId IS NULL AND TrashedAt IS NULL ORDER BY Name COLLATE NOCASE", ReadFolder, ("$u", userId)),
                await QueryAsync($"{SqliteMetadataRepository.SelectFile} WHERE UserId = $u AND FolderId IS NULL AND TrashedAt IS NULL ORDER BY FileName COLLATE NOCASE",
                    SqliteMetadataRepository.ReadFile, ("$u", userId)),
                await StarIdsAsync(userId));

        var path = await PathAsync(folderId);
        if (path.Count == 0) return null;
        var (role, from) = await AccessAsync(userId, path[^1].OwnerId, path);
        return role == Role.None ? null : await ChildrenAsync(path, role, from, userId);
    }

    public async Task<Listing> ChildrenAsync(List<FolderItem> path, Role role, int from, string? userId = null)
    {
        var id = path[^1].Id.ToString();
        return new Listing(path[^1], role, path[from..],
            await QueryAsync($"{SelectFolder} WHERE ParentId = $id AND TrashedAt IS NULL ORDER BY Name COLLATE NOCASE", ReadFolder, ("$id", id)),
            await QueryAsync($"{SqliteMetadataRepository.SelectFile} WHERE FolderId = $id AND TrashedAt IS NULL ORDER BY FileName COLLATE NOCASE",
                SqliteMetadataRepository.ReadFile, ("$id", id)),
            userId is null ? [] : await StarIdsAsync(userId));
    }

    /// <summary>Throws SqliteException (unique constraint) if the parent already has a live folder with that name.</summary>
    public async Task<FolderItem> CreateFolderAsync(string ownerId, Guid? parentId, string name)
    {
        var folder = new FolderItem(Guid.NewGuid(), CleanName(name), ownerId, parentId, DateTimeOffset.UtcNow);
        await ExecAsync("INSERT INTO Folders (FolderId, UserId, ParentId, Name, CreatedAt, UpdatedAt) VALUES ($id, $u, $p, $n, $at, $at)",
            ("$id", folder.Id.ToString()), ("$u", ownerId), ("$p", parentId?.ToString()), ("$n", folder.Name), ("$at", Now()));
        await TouchAsync(parentId);
        return folder;
    }

    /// <summary>Get-or-create each segment of "a/b/c" under the parent; race-safe via the unique index. Returns the last folder.</summary>
    public async Task<Guid?> EnsurePathAsync(string ownerId, Guid? parentId, string path)
    {
        foreach (var segment in path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment is "." or "..") continue;
            var name = CleanName(segment);
            await ExecAsync("INSERT INTO Folders (FolderId, UserId, ParentId, Name, CreatedAt, UpdatedAt) VALUES ($id, $u, $p, $n, $at, $at) ON CONFLICT DO NOTHING",
                ("$id", Guid.NewGuid().ToString()), ("$u", ownerId), ("$p", parentId?.ToString()), ("$n", name), ("$at", Now()));
            parentId = Guid.Parse((await QueryAsync(
                "SELECT FolderId FROM Folders WHERE UserId = $u AND IFNULL(ParentId, '') = $p AND Name = $n COLLATE NOCASE AND TrashedAt IS NULL",
                r => r.GetString(0), ("$u", ownerId), ("$p", parentId?.ToString() ?? ""), ("$n", name)))[0]);
        }
        return parentId;
    }

    /// <summary>Marks a folder as changed (for Recent and "modified" sorting).</summary>
    public Task TouchAsync(Guid? folderId) => folderId is null
        ? Task.CompletedTask
        : ExecAsync("UPDATE Folders SET UpdatedAt = $at WHERE FolderId = $id", ("$at", Now()), ("$id", folderId.ToString()));

    public Task RenameFolderAsync(Guid id, string name) =>
        ExecAsync("UPDATE Folders SET Name = $n, UpdatedAt = $at WHERE FolderId = $id", ("$n", CleanName(name)), ("$at", Now()), ("$id", id.ToString()));

    public async Task MoveFolderAsync(Guid id, Guid? parentId)
    {
        if ((await PathAsync(parentId)).Any(f => f.Id == id))
            throw new ArgumentException("A folder can't be moved into itself or one of its subfolders.");
        await ExecAsync("UPDATE Folders SET ParentId = $p, UpdatedAt = $at WHERE FolderId = $id", ("$p", parentId?.ToString()), ("$at", Now()), ("$id", id.ToString()));
        await TouchAsync(parentId);
    }

    public Task RenameFileAsync(Guid id, string name) =>
        ExecAsync("UPDATE Files SET FileName = $n WHERE FileId = $id", ("$n", CleanName(name)), ("$id", id.ToString()));

    public async Task MoveFileAsync(Guid id, Guid? folderId)
    {
        await ExecAsync("UPDATE Files SET FolderId = $f WHERE FileId = $id", ("$f", folderId?.ToString()), ("$id", id.ToString()));
        await TouchAsync(folderId);
    }

    /// <summary>The live file with this exact name in the folder, if any (a re-upload becomes a new version of it).</summary>
    public async Task<Guid?> FindFileAsync(string ownerId, Guid? folderId, string name) =>
        (await QueryAsync("SELECT FileId FROM Files WHERE UserId = $u AND IFNULL(FolderId, '') = $f AND FileName = $n AND TrashedAt IS NULL LIMIT 1",
            r => (Guid?)Guid.Parse(r.GetString(0)), ("$u", ownerId), ("$f", folderId?.ToString() ?? ""), ("$n", name))).FirstOrDefault();

    // ---- copy (instant: new rows point at the same chunks) ----

    public async Task<Guid> CopyFileAsync(Guid fileId, string ownerId, Guid? folderId, string name)
    {
        var id = Guid.NewGuid();
        await ExecAsync(CopyFileSql, ("$src", fileId.ToString()), ("$new", id.ToString()), ("$o", ownerId), ("$n", CleanName(name)),
            ("$f", folderId?.ToString()), ("$at", Now()));
        return id;
    }

    /// <summary>Deep copy of a folder's live subtree in one transaction. Zero chunk bytes are copied.</summary>
    public async Task<Guid> CopyFolderAsync(Guid sourceId, string ownerId, Guid? parentId, string name)
    {
        var folders = await QueryAsync("""
            WITH RECURSIVE sub(id, depth) AS (
                SELECT $id, 0 UNION ALL
                SELECT f.FolderId, sub.depth + 1 FROM Folders f JOIN sub ON f.ParentId = sub.id WHERE f.TrashedAt IS NULL)
            SELECT d.FolderId, d.ParentId, d.Name FROM sub JOIN Folders d ON d.FolderId = sub.id ORDER BY sub.depth
            """, r => (Id: r.GetString(0), Parent: r.IsDBNull(1) ? null : r.GetString(1), Name: r.GetString(2)), ("$id", sourceId.ToString()));
        var files = await QueryAsync($"""
            {Subtree}
            SELECT FileId, FolderId, FileName FROM Files WHERE FolderId IN (SELECT id FROM sub) AND TrashedAt IS NULL
            """, r => (Id: r.GetString(0), Folder: r.GetString(1), Name: r.GetString(2)), ("$id", sourceId.ToString()));

        var map = folders.ToDictionary(f => f.Id, _ => Guid.NewGuid().ToString());
        var at = Now();
        var rootName = await FreeFolderNameAsync(ownerId, parentId, CleanName(name));
        var batch = folders.Select(f => ("INSERT INTO Folders (FolderId, UserId, ParentId, Name, CreatedAt, UpdatedAt) VALUES ($id, $o, $p, $n, $at, $at)",
            new (string, object?)[] { ("$id", map[f.Id]), ("$o", ownerId), ("$at", at),
                ("$p", f.Id == sourceId.ToString() ? parentId?.ToString() : map[f.Parent!]),
                ("$n", f.Id == sourceId.ToString() ? rootName : f.Name) })).ToList();
        batch.AddRange(files.Select(f => (CopyFileSql, new (string, object?)[] {
            ("$src", f.Id), ("$new", Guid.NewGuid().ToString()), ("$o", ownerId), ("$n", f.Name), ("$f", map[f.Folder]), ("$at", at) })));
        await BatchAsync(batch);
        await TouchAsync(parentId);
        return Guid.Parse(map[sourceId.ToString()]);
    }

    private const string CopyFileSql = """
        INSERT INTO Files (FileId, UserId, FileName, TotalSize, Sha256, Chunker, CreatedAt, FolderId, UpdatedAt)
            SELECT $new, $o, $n, TotalSize, Sha256, Chunker, $at, $f, $at FROM Files WHERE FileId = $src;
        INSERT INTO File_Chunks (FileId, SequenceIndex, ChunkHash)
            SELECT $new, SequenceIndex, ChunkHash FROM File_Chunks WHERE FileId = $src;
        """;

    private async Task<string> FreeFolderNameAsync(string ownerId, Guid? parentId, string name)
    {
        var taken = (await QueryAsync("SELECT Name FROM Folders WHERE UserId = $u AND IFNULL(ParentId, '') = $p AND TrashedAt IS NULL",
            r => r.GetString(0), ("$u", ownerId), ("$p", parentId?.ToString() ?? ""))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var i = 2; taken.Contains(candidate); i++) candidate = $"{name} ({i})";
        return candidate;
    }

    // ---- trash & garbage collection ----

    public Task TrashAsync(string type, Guid id)
    {
        var args = new (string, object?)[] { ("$id", id.ToString()), ("$t", Now()) };
        return type == "file"
            ? ExecAsync("UPDATE Files SET TrashedAt = $t WHERE FileId = $id AND TrashedAt IS NULL", args)
            : ExecAsync($"""
                {Subtree} UPDATE Folders SET TrashedAt = $t WHERE FolderId IN (SELECT id FROM sub) AND TrashedAt IS NULL;
                {Subtree} UPDATE Files SET TrashedAt = $t WHERE FolderId IN (SELECT id FROM sub) AND TrashedAt IS NULL;
                """, args);
    }

    /// <summary>Restores what was trashed together with the item. If its parent is still in the trash it returns to the root.</summary>
    public Task RestoreAsync(string type, Guid id) => type == "file"
        ? ExecAsync("""
            UPDATE Files SET TrashedAt = NULL,
                FolderId = CASE WHEN FolderId IN (SELECT FolderId FROM Folders WHERE TrashedAt IS NOT NULL) THEN NULL ELSE FolderId END
            WHERE FileId = $id
            """, ("$id", id.ToString()))
        : ExecAsync($"""
            {Subtree} UPDATE Files SET TrashedAt = NULL
                WHERE FolderId IN (SELECT id FROM sub) AND TrashedAt = (SELECT TrashedAt FROM Folders WHERE FolderId = $id);
            {Subtree} UPDATE Folders SET TrashedAt = NULL
                WHERE FolderId IN (SELECT id FROM sub) AND FolderId <> $id AND TrashedAt = (SELECT TrashedAt FROM Folders WHERE FolderId = $id);
            UPDATE Folders SET TrashedAt = NULL,
                ParentId = CASE WHEN ParentId IN (SELECT FolderId FROM Folders WHERE TrashedAt IS NOT NULL) THEN NULL ELSE ParentId END
            WHERE FolderId = $id;
            """, ("$id", id.ToString()));

    /// <summary>Top-level trashed items of an owner (not the contents of a trashed folder).</summary>
    public Task<List<DriveItem>> TrashAsync(string ownerId) => QueryAsync("""
        SELECT 'folder', d.FolderId, d.Name, d.UserId, NULL, NULL, d.TrashedAt FROM Folders d LEFT JOIN Folders p ON p.FolderId = d.ParentId
            WHERE d.UserId = $u AND d.TrashedAt IS NOT NULL AND (p.TrashedAt IS NULL OR p.TrashedAt <> d.TrashedAt)
        UNION ALL
        SELECT 'file', f.FileId, f.FileName, f.UserId, NULL, f.TotalSize, f.TrashedAt FROM Files f LEFT JOIN Folders p ON p.FolderId = f.FolderId
            WHERE f.UserId = $u AND f.TrashedAt IS NOT NULL AND (p.TrashedAt IS NULL OR p.TrashedAt <> f.TrashedAt)
        ORDER BY 7 DESC
        """, ReadItem, ("$u", ownerId));

    /// <summary>Only items already in the trash can be deleted forever. Rows only; <see cref="CollectGarbageAsync"/> frees bytes.</summary>
    public async Task<bool> DeleteForeverAsync(string type, Guid id) => type == "file"
        ? await ExecAsync("DELETE FROM Files WHERE FileId = $id AND TrashedAt IS NOT NULL", ("$id", id.ToString())) > 0
        : await ExecAsync("DELETE FROM Folders WHERE FolderId = $id AND TrashedAt IS NOT NULL", ("$id", id.ToString())) > 0;

    public Task EmptyTrashAsync(string ownerId) => ExecAsync("""
        DELETE FROM Files WHERE UserId = $u AND TrashedAt IS NOT NULL;
        DELETE FROM Folders WHERE UserId = $u AND TrashedAt IS NOT NULL;
        """, ("$u", ownerId));

    /// <summary>Drive empties items that have been in the trash for 30 days.</summary>
    public Task PurgeTrashAsync(DateTimeOffset olderThan) => ExecAsync("""
        DELETE FROM Files WHERE TrashedAt < $c;
        DELETE FROM Folders WHERE TrashedAt < $c;
        """, ("$c", olderThan.ToString("O")));

    /// <summary>
    /// Mark-and-sweep in one atomic statement: delete chunk rows nothing references (no file, no version), then their
    /// blobs. The grace period spares chunks an in-flight upload has written but not yet committed. If an upload
    /// deduplicated against a chunk that is collected before its manifest commits, the File_Chunks foreign key makes
    /// that commit fail loudly instead of saving a file with a missing chunk. A crash between the two steps leaves an
    /// orphan blob, which is harmless.
    /// </summary>
    public async Task<int> CollectGarbageAsync(IBlobStore blobs, TimeSpan grace)
    {
        var freed = await QueryAsync("""
            DELETE FROM Chunks WHERE CreatedAt < $cutoff
                AND NOT EXISTS (SELECT 1 FROM File_Chunks fc WHERE fc.ChunkHash = Chunks.ChunkHash)
                AND NOT EXISTS (SELECT 1 FROM Version_Chunks vc WHERE vc.ChunkHash = Chunks.ChunkHash)
            RETURNING ChunkHash
            """, r => r.GetString(0), ("$cutoff", (DateTimeOffset.UtcNow - grace).ToString("O")));
        foreach (var hash in freed) await blobs.DeleteAsync(hash);
        return freed.Count;
    }

    // ---- versions ----

    public Task<List<VersionInfo>> VersionsAsync(Guid fileId) =>
        QueryAsync("SELECT VersionId, TotalSize, Sha256, CreatedAt FROM Versions WHERE FileId = $id ORDER BY CreatedAt DESC",
            r => new VersionInfo(Guid.Parse(r.GetString(0)), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), Date(r, 3)!.Value),
            ("$id", fileId.ToString()));

    /// <summary>A past version presented as a manifest: same file record, the version's size, hash and chunks.</summary>
    public async Task<FileManifest?> VersionManifestAsync(Guid fileId, Guid versionId)
    {
        if (await GetFileAsync(fileId) is not { } file) return null;
        var version = (await QueryAsync("SELECT TotalSize, Sha256, Chunker FROM Versions WHERE VersionId = $v AND FileId = $id",
            r => (Size: r.GetInt64(0), Sha: r.IsDBNull(1) ? null : r.GetString(1), Chunker: r.GetString(2)),
            ("$v", versionId.ToString()), ("$id", fileId.ToString()))).FirstOrDefault();
        if (version == default) return null;
        var chunks = await QueryAsync("""
            SELECT vc.SequenceIndex, vc.ChunkHash, c.ByteSize FROM Version_Chunks vc JOIN Chunks c ON c.ChunkHash = vc.ChunkHash
            WHERE vc.VersionId = $v ORDER BY vc.SequenceIndex
            """, r => new ManifestEntry(r.GetInt32(0), r.GetString(1), r.GetInt64(2)), ("$v", versionId.ToString()));
        return new FileManifest(file with { TotalSize = version.Size, Sha256 = version.Sha, Chunker = version.Chunker }, chunks);
    }

    // ---- stars, recent ----

    public Task StarAsync(string userId, string type, Guid id, bool starred) => starred
        ? ExecAsync("INSERT INTO Stars (UserId, ItemId, ItemType, CreatedAt) VALUES ($u, $id, $t, $at) ON CONFLICT DO NOTHING",
            ("$u", userId), ("$id", id.ToString()), ("$t", type), ("$at", Now()))
        : ExecAsync("DELETE FROM Stars WHERE UserId = $u AND ItemId = $id", ("$u", userId), ("$id", id.ToString()));

    private Task<List<Guid>> StarIdsAsync(string userId) =>
        QueryAsync("SELECT ItemId FROM Stars WHERE UserId = $u", r => Guid.Parse(r.GetString(0)), ("$u", userId));

    /// <summary>Live starred items; callers still filter by access, since a share may have been revoked since.</summary>
    public Task<List<DriveItem>> StarredAsync(string userId) => QueryAsync("""
        SELECT s.ItemType, s.ItemId, COALESCE(f.FileName, d.Name), COALESCE(f.UserId, d.UserId), NULL, f.TotalSize,
               COALESCE(f.UpdatedAt, f.CreatedAt, d.UpdatedAt, d.CreatedAt)
        FROM Stars s
        LEFT JOIN Files f ON s.ItemType = 'file' AND f.FileId = s.ItemId AND f.TrashedAt IS NULL
        LEFT JOIN Folders d ON s.ItemType = 'folder' AND d.FolderId = s.ItemId AND d.TrashedAt IS NULL
        WHERE s.UserId = $u AND COALESCE(f.FileId, d.FolderId) IS NOT NULL
        ORDER BY s.CreatedAt DESC
        """, ReadItem, ("$u", userId));

    public Task<List<DriveItem>> RecentAsync(string userId) => QueryAsync("""
        SELECT 'folder', FolderId, Name, UserId, NULL, NULL, COALESCE(UpdatedAt, CreatedAt) FROM Folders WHERE UserId = $u AND TrashedAt IS NULL
        UNION ALL
        SELECT 'file', FileId, FileName, UserId, NULL, TotalSize, COALESCE(UpdatedAt, CreatedAt) FROM Files WHERE UserId = $u AND TrashedAt IS NULL
        ORDER BY 7 DESC LIMIT 50
        """, ReadItem, ("$u", userId));

    // ---- activity ----

    public Task LogAsync(string userId, string type, Guid id, string action, string? detail = null) =>
        ExecAsync("INSERT INTO Activity (ItemType, ItemId, UserId, Action, Detail, At) VALUES ($t, $id, $u, $a, $d, $at)",
            ("$t", type), ("$id", id.ToString()), ("$u", userId), ("$a", action), ("$d", detail), ("$at", Now()));

    /// <summary>A file's own history, or for a folder: the folder plus everything that is or was inside it.</summary>
    public Task<List<ActivityEntry>> ActivityAsync(string type, Guid id) => QueryAsync($"""
        {Subtree}
        SELECT a.ItemType, a.ItemId, COALESCE(f.FileName, d.Name), a.UserId, a.Action, a.Detail, a.At
        FROM Activity a
        LEFT JOIN Files f ON a.ItemType = 'file' AND f.FileId = a.ItemId
        LEFT JOIN Folders d ON a.ItemType = 'folder' AND d.FolderId = a.ItemId
        WHERE a.ItemId = $id OR ($type = 'folder' AND (a.ItemId IN (SELECT id FROM sub)
            OR a.ItemId IN (SELECT FileId FROM Files WHERE FolderId IN (SELECT id FROM sub))))
        ORDER BY a.Id DESC LIMIT 200
        """, r => new ActivityEntry(r.GetString(0), Guid.Parse(r.GetString(1)), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
            r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), Date(r, 6)!.Value), ("$id", id.ToString()), ("$type", type));

    // ---- resumable upload sessions ----

    public Task CreateUploadAsync(UploadSession s) => ExecAsync("""
        INSERT INTO Uploads (UploadId, UserId, OwnerId, FolderId, FileName, TotalSize, CreatedAt) VALUES ($id, $u, $o, $f, $n, $size, $at)
        """, ("$id", s.UploadId.ToString()), ("$u", s.UserId), ("$o", s.OwnerId), ("$f", s.FolderId?.ToString()), ("$n", s.FileName),
        ("$size", s.TotalSize), ("$at", Now()));

    public async Task<UploadSession?> GetUploadAsync(Guid id) => (await QueryAsync(
        "SELECT UploadId, UserId, OwnerId, FolderId, FileName, TotalSize FROM Uploads WHERE UploadId = $id",
        r => new UploadSession(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)),
            r.GetString(4), r.GetInt64(5)), ("$id", id.ToString()))).FirstOrDefault();

    public Task DeleteUploadAsync(Guid id) => ExecAsync("DELETE FROM Uploads WHERE UploadId = $id", ("$id", id.ToString()));

    public Task<List<Guid>> DeleteStaleUploadsAsync(DateTimeOffset olderThan) =>
        QueryAsync("DELETE FROM Uploads WHERE CreatedAt < $c RETURNING UploadId", r => Guid.Parse(r.GetString(0)), ("$c", olderThan.ToString("O")));

    // ---- sharing ----

    public Task ShareWithUserAsync(string ownerId, string itemType, Guid itemId, string granteeId, string role) =>
        ExecAsync("""
            INSERT INTO Shares (ShareId, ItemType, ItemId, OwnerId, GranteeId, Role, CreatedAt) VALUES ($s, $t, $i, $o, $g, $r, $at)
            ON CONFLICT (ItemId, GranteeId) DO UPDATE SET Role = excluded.Role
            """, ("$s", Guid.NewGuid().ToString()), ("$t", itemType), ("$i", itemId.ToString()), ("$o", ownerId), ("$g", granteeId), ("$r", role), ("$at", Now()));

    /// <summary>"Anyone with the link can view". Reuses the item's existing link if there is one.</summary>
    public async Task<string> LinkAsync(string ownerId, string itemType, Guid itemId)
    {
        var existing = (await SharesAsync(itemId)).FirstOrDefault(s => s.LinkToken is not null);
        if (existing is not null) return existing.LinkToken!;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await ExecAsync("INSERT INTO Shares (ShareId, ItemType, ItemId, OwnerId, LinkToken, Role, CreatedAt) VALUES ($s, $t, $i, $o, $k, 'viewer', $at)",
            ("$s", Guid.NewGuid().ToString()), ("$t", itemType), ("$i", itemId.ToString()), ("$o", ownerId), ("$k", token), ("$at", Now()));
        return token;
    }

    public Task<List<ShareInfo>> SharesAsync(Guid itemId) =>
        QueryAsync($"{SelectShare} WHERE ItemId = $i ORDER BY CreatedAt", ReadShare, ("$i", itemId.ToString()));

    public async Task<ShareInfo?> ShareByTokenAsync(string token) =>
        (await QueryAsync($"{SelectShare} WHERE LinkToken = $k", ReadShare, ("$k", token))).FirstOrDefault();

    public async Task<ShareInfo?> DeleteShareAsync(Guid shareId, string ownerId) => (await QueryAsync(
        "DELETE FROM Shares WHERE ShareId = $s AND OwnerId = $o RETURNING ShareId, ItemType, ItemId, OwnerId, GranteeId, LinkToken, Role",
        ReadShare, ("$s", shareId.ToString()), ("$o", ownerId))).FirstOrDefault();

    public Task<List<DriveItem>> SharedWithMeAsync(string userId) => QueryAsync("""
        SELECT s.ItemType, s.ItemId, COALESCE(f.FileName, d.Name), COALESCE(f.UserId, d.UserId), s.Role, f.TotalSize,
               COALESCE(f.UpdatedAt, f.CreatedAt, d.UpdatedAt, d.CreatedAt)
        FROM Shares s
        LEFT JOIN Files f ON s.ItemType = 'file' AND f.FileId = s.ItemId AND f.TrashedAt IS NULL
        LEFT JOIN Folders d ON s.ItemType = 'folder' AND d.FolderId = s.ItemId AND d.TrashedAt IS NULL
        WHERE s.GranteeId = $u AND COALESCE(f.FileId, d.FolderId) IS NOT NULL
        ORDER BY s.ItemType DESC, 3 COLLATE NOCASE
        """, ReadItem, ("$u", userId));

    /// <summary>Name search over your own items, items shared with you, and everything inside folders shared with you.</summary>
    public Task<List<DriveItem>> SearchAsync(string userId, string query) => QueryAsync("""
        WITH RECURSIVE vis(id) AS (
            SELECT ItemId FROM Shares WHERE GranteeId = $u AND ItemType = 'folder'
            UNION SELECT f.FolderId FROM Folders f JOIN vis ON f.ParentId = vis.id)
        SELECT 'folder', FolderId, Name, UserId, NULL, NULL, COALESCE(UpdatedAt, CreatedAt) FROM Folders
            WHERE Name LIKE $q ESCAPE '\' AND TrashedAt IS NULL AND (UserId = $u OR FolderId IN (SELECT id FROM vis))
        UNION ALL
        SELECT 'file', FileId, FileName, UserId, NULL, TotalSize, COALESCE(UpdatedAt, CreatedAt) FROM Files
            WHERE FileName LIKE $q ESCAPE '\' AND TrashedAt IS NULL AND (UserId = $u OR FolderId IN (SELECT id FROM vis)
                OR FileId IN (SELECT ItemId FROM Shares WHERE GranteeId = $u))
        LIMIT 100
        """, ReadItem, ("$u", userId),
        ("$q", "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));

    // ---- zip ----

    /// <summary>(relative path, fileId) for every live file below a folder, for streaming it as a zip.</summary>
    public Task<List<(string Path, Guid FileId)>> ZipEntriesAsync(Guid folderId) => QueryAsync("""
        WITH RECURSIVE t(id, path) AS (
            SELECT $id, '' UNION ALL
            SELECT f.FolderId, t.path || f.Name || '/' FROM Folders f JOIN t ON f.ParentId = t.id WHERE f.TrashedAt IS NULL)
        SELECT t.path || fi.FileName, fi.FileId FROM t JOIN Files fi ON fi.FolderId = t.id WHERE fi.TrashedAt IS NULL ORDER BY 1
        """, r => (r.GetString(0), Guid.Parse(r.GetString(1))), ("$id", folderId.ToString()));

    // ---- helpers ----

    public static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 255 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Names must be 1-255 characters and can't contain / or \\.");
        return name;
    }

    private const string SelectShare = "SELECT ShareId, ItemType, ItemId, OwnerId, GranteeId, LinkToken, Role FROM Shares";

    private static ShareInfo ReadShare(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(1), Guid.Parse(r.GetString(2)),
        r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6));

    private static FolderItem ReadFolder(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)), Date(r, 4)!.Value, Date(r, 5), Date(r, 6));

    private static DriveItem ReadItem(SqliteDataReader r) => new(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2),
        r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt64(5), Date(r, 6));

    private static DateTimeOffset? Date(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    private async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        await using var connection = await db.OpenAsync();
        await using var cmd = Command(connection, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }

    // Statements run in one transaction, so a multi-statement change (trash a subtree, copy a file) is all-or-nothing.
    private Task<int> ExecAsync(string sql, params (string Name, object? Value)[] args) => BatchAsync([(sql, args)]);

    private async Task<int> BatchAsync(IEnumerable<(string Sql, (string Name, object? Value)[] Args)> statements)
    {
        await using var connection = await db.OpenAsync();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();
        var changed = 0;
        foreach (var (sql, args) in statements)
        {
            await using var cmd = Command(connection, sql, args);
            cmd.Transaction = tx;
            changed += await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return changed;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
