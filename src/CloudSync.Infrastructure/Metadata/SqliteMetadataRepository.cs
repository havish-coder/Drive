using System.Globalization;
using System.Text.Json;
using CloudSync.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace CloudSync.Infrastructure.Metadata;

/// <summary>
/// SQLite catalog. WAL lets readers run alongside the single writer; writes are tiny
/// (one row per new chunk, one transaction per file) so writer serialization is cheap.
/// </summary>
public sealed class SqliteMetadataRepository : IMetadataRepository
{
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS Files (
            FileId    TEXT PRIMARY KEY,
            UserId    TEXT NOT NULL,
            FileName  TEXT NOT NULL,
            TotalSize INTEGER NOT NULL,
            Sha256    TEXT NULL,
            Chunker   TEXT NOT NULL,
            CreatedAt TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Chunks (
            ChunkHash   TEXT PRIMARY KEY,
            StoragePath TEXT NOT NULL,
            ByteSize    INTEGER NOT NULL,
            CreatedAt   TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS File_Chunks (
            FileId        TEXT NOT NULL REFERENCES Files(FileId) ON DELETE CASCADE,
            SequenceIndex INTEGER NOT NULL,
            ChunkHash     TEXT NOT NULL REFERENCES Chunks(ChunkHash),
            PRIMARY KEY (FileId, SequenceIndex));
        CREATE INDEX IF NOT EXISTS IX_FileChunks_ChunkHash ON File_Chunks(ChunkHash);
        CREATE INDEX IF NOT EXISTS IX_Files_UserId ON Files(UserId);
        """;

    // v2: accounts, folders, sharing. Legacy files get FolderId NULL (= the owner's root).
    private const string SchemaV2 = """
        CREATE TABLE Users (
            UserId       TEXT PRIMARY KEY,
            PasswordHash TEXT NOT NULL,
            CreatedAt    TEXT NOT NULL);
        CREATE TABLE ApiTokens (
            TokenHash TEXT PRIMARY KEY,
            UserId    TEXT NOT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
            CreatedAt TEXT NOT NULL);
        CREATE TABLE Folders (
            FolderId  TEXT PRIMARY KEY,
            UserId    TEXT NOT NULL,
            ParentId  TEXT NULL REFERENCES Folders(FolderId) ON DELETE CASCADE,
            Name      TEXT NOT NULL,
            CreatedAt TEXT NOT NULL);
        CREATE UNIQUE INDEX UX_Folders_Name ON Folders(UserId, IFNULL(ParentId, ''), Name COLLATE NOCASE);
        CREATE INDEX IX_Folders_Parent ON Folders(ParentId);
        ALTER TABLE Files ADD COLUMN FolderId TEXT NULL REFERENCES Folders(FolderId) ON DELETE CASCADE;
        CREATE INDEX IX_Files_Folder ON Files(FolderId);
        CREATE TABLE Shares (
            ShareId   TEXT PRIMARY KEY,
            ItemType  TEXT NOT NULL CHECK (ItemType IN ('file', 'folder')),
            ItemId    TEXT NOT NULL,
            OwnerId   TEXT NOT NULL,
            GranteeId TEXT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
            LinkToken TEXT NULL UNIQUE,
            Role      TEXT NOT NULL CHECK (Role IN ('viewer', 'editor')),
            CreatedAt TEXT NOT NULL,
            CHECK ((GranteeId IS NULL) <> (LinkToken IS NULL)));
        CREATE UNIQUE INDEX UX_Shares_Grantee ON Shares(ItemId, GranteeId);
        CREATE INDEX IX_Shares_GranteeId ON Shares(GranteeId);
        PRAGMA user_version = 2;
        """;

    private readonly string _connectionString;

    public SqliteMetadataRepository(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 30,
        };
        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await ExecuteAsync(connection, Schema, ct);

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        if ((long)(await version.ExecuteScalarAsync(ct))! >= 2) return;
        await using var tx = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, SchemaV2, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> ChunkExistsAsync(string hash, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Chunks WHERE ChunkHash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<IReadOnlyDictionary<string, long>> GetChunkSizesAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        var sizes = new Dictionary<string, long>(hashes.Count);
        if (hashes.Count == 0) return sizes;

        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ChunkHash, ByteSize FROM Chunks WHERE ChunkHash IN (SELECT value FROM json_each($hashes))";
        cmd.Parameters.AddWithValue("$hashes", JsonSerializer.Serialize(hashes));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) sizes[reader.GetString(0)] = reader.GetInt64(1);
        return sizes;
    }

    public async Task<bool> TryAddChunkAsync(ChunkRecord chunk, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Chunks (ChunkHash, StoragePath, ByteSize, CreatedAt)
            VALUES ($hash, $path, $size, $at)
            ON CONFLICT (ChunkHash) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$hash", chunk.ChunkHash);
        cmd.Parameters.AddWithValue("$path", chunk.StoragePath);
        cmd.Parameters.AddWithValue("$size", chunk.ByteSize);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task SaveFileAsync(FileRecord file, IReadOnlyList<FileChunkRecord> chunks, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Files (FileId, UserId, FileName, TotalSize, Sha256, Chunker, CreatedAt, FolderId)
                VALUES ($id, $user, $name, $size, $sha, $chunker, $at, $folder)
                """;
            cmd.Parameters.AddWithValue("$id", file.FileId.ToString());
            cmd.Parameters.AddWithValue("$user", file.UserId);
            cmd.Parameters.AddWithValue("$name", file.FileName);
            cmd.Parameters.AddWithValue("$size", file.TotalSize);
            cmd.Parameters.AddWithValue("$sha", (object?)file.Sha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$chunker", file.Chunker);
            cmd.Parameters.AddWithValue("$at", file.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$folder", (object?)file.FolderId?.ToString() ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO File_Chunks (FileId, SequenceIndex, ChunkHash) VALUES ($id, $seq, $hash)";
            var id = cmd.Parameters.AddWithValue("$id", file.FileId.ToString());
            var seq = cmd.Parameters.Add("$seq", SqliteType.Integer);
            var hash = cmd.Parameters.Add("$hash", SqliteType.Text);
            cmd.Prepare();
            foreach (var chunk in chunks)
            {
                seq.Value = chunk.SequenceIndex;
                hash.Value = chunk.ChunkHash;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task<FileManifest?> GetManifestAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        FileRecord? file;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"{SelectFile} WHERE FileId = $id";
            cmd.Parameters.AddWithValue("$id", fileId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            file = await reader.ReadAsync(ct) ? ReadFile(reader) : null;
        }
        if (file is null) return null;

        var entries = new List<ManifestEntry>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fc.SequenceIndex, fc.ChunkHash, c.ByteSize
                FROM File_Chunks fc JOIN Chunks c ON c.ChunkHash = fc.ChunkHash
                WHERE fc.FileId = $id
                ORDER BY fc.SequenceIndex
                """;
            cmd.Parameters.AddWithValue("$id", fileId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                entries.Add(new ManifestEntry(reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2)));
        }
        return new FileManifest(file, entries);
    }

    public async Task<StorageStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM Files),
                   (SELECT COUNT(*) FROM Chunks),
                   (SELECT COALESCE(SUM(TotalSize), 0) FROM Files),
                   (SELECT COALESCE(SUM(ByteSize), 0) FROM Chunks)
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new StorageStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    internal const string SelectFile = "SELECT FileId, UserId, FileName, TotalSize, Sha256, Chunker, CreatedAt, FolderId FROM Files";

    internal static FileRecord ReadFile(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),
        r.GetString(1),
        r.GetString(2),
        r.GetInt64(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5),
        DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
        r.IsDBNull(7) ? null : Guid.Parse(r.GetString(7)));

    internal async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", ct);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
