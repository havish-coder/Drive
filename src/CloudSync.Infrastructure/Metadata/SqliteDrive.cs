using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CloudSync.Core;
using CloudSync.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace CloudSync.Infrastructure.Metadata;

public enum Role { None, Viewer, Editor, Owner }

public sealed record FolderItem(Guid Id, string Name, string OwnerId, Guid? ParentId, DateTimeOffset CreatedAt);
public sealed record Listing(FolderItem? Folder, Role Role, IReadOnlyList<FolderItem> Path, IReadOnlyList<FolderItem> Folders, IReadOnlyList<FileRecord> Files);
public sealed record ShareInfo(Guid ShareId, string ItemType, Guid ItemId, string OwnerId, string? Grantee, string? LinkToken, string Role);
public sealed record DriveItem(string Type, Guid Id, string Name, string OwnerId, string? Role, long? Size);

/// <summary>Users, folders, sharing and search. Every access decision goes through <see cref="AccessAsync"/>.</summary>
public sealed class SqliteDrive(SqliteMetadataRepository db)
{
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
        (await QueryAsync("SELECT FolderId, Name, UserId, ParentId, CreatedAt FROM Folders WHERE FolderId = $id", ReadFolder, ("$id", id.ToString()))).FirstOrDefault();

    public async Task<FileRecord?> GetFileAsync(Guid id) =>
        (await QueryAsync($"{SqliteMetadataRepository.SelectFile} WHERE FileId = $id", SqliteMetadataRepository.ReadFile, ("$id", id.ToString()))).FirstOrDefault();

    /// <summary>Ancestors of a folder including itself, root first.</summary>
    public Task<List<FolderItem>> PathAsync(Guid? folderId) => folderId is null
        ? Task.FromResult(new List<FolderItem>())
        : QueryAsync("""
            WITH RECURSIVE anc(FolderId, Name, UserId, ParentId, CreatedAt, Depth) AS (
                SELECT FolderId, Name, UserId, ParentId, CreatedAt, 0 FROM Folders WHERE FolderId = $id
                UNION ALL
                SELECT f.FolderId, f.Name, f.UserId, f.ParentId, f.CreatedAt, a.Depth + 1 FROM Folders f JOIN anc a ON f.FolderId = a.ParentId)
            SELECT FolderId, Name, UserId, ParentId, CreatedAt FROM anc ORDER BY Depth DESC
            """, ReadFolder, ("$id", folderId.ToString()));

    /// <summary>
    /// Owner, or the strongest share on the item or any folder above it (shares inherit downwards).
    /// <c>From</c> is the index in <paramref name="path"/> where the caller's visibility starts (for breadcrumbs).
    /// </summary>
    public async Task<(Role Role, int From)> AccessAsync(string userId, string ownerId, List<FolderItem> path, Guid? fileId = null)
    {
        if (userId == ownerId) return (Role.Owner, 0);
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
        (await AccessAsync(userId, file.UserId, await PathAsync(file.FolderId), file.FileId)).Role;

    // ---- folders ----

    /// <summary>Null folder = the caller's own root ("My Drive").</summary>
    public async Task<Listing?> ListAsync(string userId, Guid? folderId)
    {
        if (folderId is null)
            return new Listing(null, Role.Owner, [],
                await QueryAsync("SELECT FolderId, Name, UserId, ParentId, CreatedAt FROM Folders WHERE UserId = $u AND ParentId IS NULL ORDER BY Name COLLATE NOCASE", ReadFolder, ("$u", userId)),
                await QueryAsync($"{SqliteMetadataRepository.SelectFile} WHERE UserId = $u AND FolderId IS NULL ORDER BY FileName COLLATE NOCASE", SqliteMetadataRepository.ReadFile, ("$u", userId)));

        var path = await PathAsync(folderId);
        if (path.Count == 0) return null;
        var (role, from) = await AccessAsync(userId, path[^1].OwnerId, path);
        return role == Role.None ? null : await ChildrenAsync(path, role, from);
    }

    public async Task<Listing> ChildrenAsync(List<FolderItem> path, Role role, int from)
    {
        var id = path[^1].Id.ToString();
        return new Listing(path[^1], role, path[from..],
            await QueryAsync("SELECT FolderId, Name, UserId, ParentId, CreatedAt FROM Folders WHERE ParentId = $id ORDER BY Name COLLATE NOCASE", ReadFolder, ("$id", id)),
            await QueryAsync($"{SqliteMetadataRepository.SelectFile} WHERE FolderId = $id ORDER BY FileName COLLATE NOCASE", SqliteMetadataRepository.ReadFile, ("$id", id)));
    }

    /// <summary>Throws SqliteException (unique constraint) if the parent already has a folder with that name.</summary>
    public async Task<FolderItem> CreateFolderAsync(string ownerId, Guid? parentId, string name)
    {
        var folder = new FolderItem(Guid.NewGuid(), CleanName(name), ownerId, parentId, DateTimeOffset.UtcNow);
        await ExecAsync("INSERT INTO Folders (FolderId, UserId, ParentId, Name, CreatedAt) VALUES ($id, $u, $p, $n, $at)",
            ("$id", folder.Id.ToString()), ("$u", ownerId), ("$p", parentId?.ToString()), ("$n", folder.Name), ("$at", Now()));
        return folder;
    }

    /// <summary>Get-or-create each segment of "a/b/c" under the parent; race-safe via the unique index. Returns the last folder.</summary>
    public async Task<Guid?> EnsurePathAsync(string ownerId, Guid? parentId, string path)
    {
        foreach (var segment in path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment is "." or "..") continue;
            var name = CleanName(segment);
            await ExecAsync("INSERT INTO Folders (FolderId, UserId, ParentId, Name, CreatedAt) VALUES ($id, $u, $p, $n, $at) ON CONFLICT DO NOTHING",
                ("$id", Guid.NewGuid().ToString()), ("$u", ownerId), ("$p", parentId?.ToString()), ("$n", name), ("$at", Now()));
            parentId = Guid.Parse((await QueryAsync(
                "SELECT FolderId FROM Folders WHERE UserId = $u AND IFNULL(ParentId, '') = $p AND Name = $n COLLATE NOCASE",
                r => r.GetString(0), ("$u", ownerId), ("$p", parentId?.ToString() ?? ""), ("$n", name)))[0]);
        }
        return parentId;
    }

    public Task RenameFolderAsync(Guid id, string name) =>
        ExecAsync("UPDATE Folders SET Name = $n WHERE FolderId = $id", ("$n", CleanName(name)), ("$id", id.ToString()));

    public async Task MoveFolderAsync(Guid id, Guid? parentId)
    {
        if ((await PathAsync(parentId)).Any(f => f.Id == id))
            throw new ArgumentException("A folder can't be moved into itself or one of its subfolders.");
        await ExecAsync("UPDATE Folders SET ParentId = $p WHERE FolderId = $id", ("$p", parentId?.ToString()), ("$id", id.ToString()));
    }

    // ponytail: deleting rows only; chunks stay on disk until a garbage collector exists (see docs/GAP_ANALYSIS.md).
    public Task DeleteFolderAsync(Guid id) => ExecAsync("DELETE FROM Folders WHERE FolderId = $id", ("$id", id.ToString()));

    public Task RenameFileAsync(Guid id, string name) =>
        ExecAsync("UPDATE Files SET FileName = $n WHERE FileId = $id", ("$n", CleanName(name)), ("$id", id.ToString()));

    public Task MoveFileAsync(Guid id, Guid? folderId) =>
        ExecAsync("UPDATE Files SET FolderId = $f WHERE FileId = $id", ("$f", folderId?.ToString()), ("$id", id.ToString()));

    public Task DeleteFileAsync(Guid id) => ExecAsync("DELETE FROM Files WHERE FileId = $id", ("$id", id.ToString()));

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

    public async Task<bool> DeleteShareAsync(Guid shareId, string ownerId) =>
        await ExecAsync("DELETE FROM Shares WHERE ShareId = $s AND OwnerId = $o", ("$s", shareId.ToString()), ("$o", ownerId)) == 1;

    public Task<List<DriveItem>> SharedWithMeAsync(string userId) => QueryAsync("""
        SELECT s.ItemType, s.ItemId, COALESCE(f.FileName, d.Name), COALESCE(f.UserId, d.UserId), s.Role, f.TotalSize
        FROM Shares s
        LEFT JOIN Files f ON s.ItemType = 'file' AND f.FileId = s.ItemId
        LEFT JOIN Folders d ON s.ItemType = 'folder' AND d.FolderId = s.ItemId
        WHERE s.GranteeId = $u AND COALESCE(f.FileId, d.FolderId) IS NOT NULL
        ORDER BY s.ItemType DESC, 3 COLLATE NOCASE
        """, ReadItem, ("$u", userId));

    /// <summary>Name search over your own items, items shared with you, and everything inside folders shared with you.</summary>
    public Task<List<DriveItem>> SearchAsync(string userId, string query) => QueryAsync("""
        WITH RECURSIVE vis(id) AS (
            SELECT ItemId FROM Shares WHERE GranteeId = $u AND ItemType = 'folder'
            UNION SELECT f.FolderId FROM Folders f JOIN vis ON f.ParentId = vis.id)
        SELECT 'folder', FolderId, Name, UserId, NULL, NULL FROM Folders
            WHERE Name LIKE $q ESCAPE '\' AND (UserId = $u OR FolderId IN (SELECT id FROM vis))
        UNION ALL
        SELECT 'file', FileId, FileName, UserId, NULL, TotalSize FROM Files
            WHERE FileName LIKE $q ESCAPE '\' AND (UserId = $u OR FolderId IN (SELECT id FROM vis)
                OR FileId IN (SELECT ItemId FROM Shares WHERE GranteeId = $u))
        LIMIT 100
        """, ReadItem, ("$u", userId),
        ("$q", "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));

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
        r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)), DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture));

    private static DriveItem ReadItem(SqliteDataReader r) => new(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2),
        r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt64(5));

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

    private async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = await db.OpenAsync();
        await using var cmd = Command(connection, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
