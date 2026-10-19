using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudSync.Core.Sync;
using Microsoft.Data.Sqlite;
using static CloudSync.Tests.TestUtil;

namespace CloudSync.Tests;

public class DriveTests
{
    internal static async Task<Guid> FolderAsync(HttpClient http, string name, Guid? parent = null)
    {
        var response = await http.PostAsJsonAsync("/api/folders", new { name, parentId = parent });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    internal static async Task<JsonElement> ChildrenAsync(HttpClient http, Guid? folder) =>
        await http.GetFromJsonAsync<JsonElement>($"/api/folders/{folder?.ToString() ?? "root"}/children");

    internal static IEnumerable<string> Names(JsonElement listing, string kind) =>
        listing.GetProperty(kind).EnumerateArray().Select(e => e.GetProperty(kind == "files" ? "fileName" : "name").GetString()!);

    internal static Task<UploadResult> UploadAsync(HttpClient http, string name, Guid? folder = null, string? path = null) =>
        ApiTests.UploadRawAsync(http, RandomBytes(5000, name.GetHashCode()), name,
            (folder is null ? "" : $"&folderId={folder}") + (path is null ? "" : $"&path={path}"));

    [Fact]
    public async Task Login_is_required_identity_cannot_be_spoofed_and_passwords_are_hashed()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var anon = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/folders/root/children")).StatusCode);
        anon.DefaultRequestHeaders.Add("X-User-Id", "alice"); // the old trust-the-header identity
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/folders/root/children")).StatusCode);

        await ClientForAsync(api, "alice");
        var fresh = api.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict, (await fresh.PostAsJsonAsync("/api/auth/register", new { userName = "ALICE", password = "password123" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "wrong-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/auth/login", new { userName = "nobody", password = "password123" })).StatusCode);

        // CLI-style bearer token.
        var token = (await (await fresh.PostAsJsonAsync("/api/auth/token", new { userName = "alice", password = "password123" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var cli = api.CreateClient();
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal("alice", (await cli.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("userName").GetString());
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "forged");
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli.GetAsync("/api/auth/me")).StatusCode);

        await using var db = new SqliteConnection($"Data Source={dir.Path("meta.db")}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT PasswordHash FROM Users WHERE UserId = 'alice'";
        var stored = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.DoesNotContain("password123", stored);
        Assert.StartsWith("600000.", stored);
    }

    [Fact]
    public async Task Folders_nest_rename_move_upload_by_path_and_delete_with_contents()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = await ClientForAsync(api, "alice");

        var a = await FolderAsync(http, "A");
        var b = await FolderAsync(http, "B", a);
        var file = await UploadAsync(http, "notes.txt", b);

        var inB = await ChildrenAsync(http, b);
        Assert.Equal(["notes.txt"], Names(inB, "files"));
        Assert.Equal(["A", "B"], inB.GetProperty("path").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal("Owner", inB.GetProperty("role").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/folders", new { name = "a" })).StatusCode); // case-insensitive clash
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync($"/api/folders/{a}", new { moveTo = b.ToString() })).StatusCode); // cycle
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/folders", new { name = "x/y" })).StatusCode);

        (await http.PatchAsJsonAsync($"/api/folders/{b}", new { name = "Docs", moveTo = "root" })).EnsureSuccessStatusCode();
        Assert.Equal(["A", "Docs"], Names(await ChildrenAsync(http, null), "folders"));

        // Folder upload: the path's folders are created once and then reused.
        await UploadAsync(http, "1.jpg", path: "Photos/2024");
        await UploadAsync(http, "2.jpg", path: "Photos/2024");
        var photos = Names(await ChildrenAsync(http, null), "folders").Count(n => n == "Photos");
        Assert.Equal(1, photos);

        // Delete = move to trash; deleting forever from the trash removes the folder and its contents.
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/folders/{b}")).StatusCode);
        Assert.DoesNotContain("Docs", Names(await ChildrenAsync(http, null), "folders"));
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/trash/folder/{b}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/files/{file.FileId}")).StatusCode); // cascaded
    }

    [Fact]
    public async Task Shares_inherit_into_subfolders_and_roles_are_enforced()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var alice = await ClientForAsync(api, "alice");
        var bob = await ClientForAsync(api, "bob");
        var carol = await ClientForAsync(api, "carol");

        var team = await FolderAsync(alice, "Team");
        var deep = await FolderAsync(alice, "Deep", team);
        var file = await UploadAsync(alice, "plan.pdf", deep);

        (await alice.PostAsJsonAsync("/api/shares", new { itemType = "folder", itemId = team, userName = "bob", role = "viewer" })).EnsureSuccessStatusCode();

        // Viewer: can see and download two levels down, cannot write.
        var asBob = await ChildrenAsync(bob, deep);
        Assert.Equal("Viewer", asBob.GetProperty("role").GetString());
        Assert.Equal(["Team", "Deep"], asBob.GetProperty("path").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/files/{file.FileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsync($"/api/files?fileName=x.txt&folderId={deep}", new ByteArrayContent([1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/files/{file.FileId}")).StatusCode);
        Assert.Contains("Team", (await bob.GetFromJsonAsync<JsonElement>("/api/shared-with-me")).EnumerateArray().Select(i => i.GetProperty("name").GetString()));

        // Only the owner can share.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync("/api/shares", new { itemType = "folder", itemId = team, userName = "carol" })).StatusCode);

        // Upgrade to editor: bob can now upload, and the file lands in alice's tree.
        (await alice.PostAsJsonAsync("/api/shares", new { itemType = "folder", itemId = team, userName = "bob", role = "editor" })).EnsureSuccessStatusCode();
        await UploadAsync(bob, "from-bob.txt", deep);
        Assert.Contains("from-bob.txt", Names(await ChildrenAsync(alice, deep), "files"));

        // A stranger sees nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync($"/api/folders/{deep}/children")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync($"/api/files/{file.FileId}")).StatusCode);

        // Revoke.
        var shares = await alice.GetFromJsonAsync<JsonElement>($"/api/shares/folder/{team}");
        var bobShare = shares.EnumerateArray().Single(s => s.GetProperty("grantee").GetString() == "bob").GetProperty("shareId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/shares/{bobShare}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/folders/{deep}/children")).StatusCode);
    }

    [Fact]
    public async Task Public_link_works_without_login_only_inside_the_shared_folder()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var alice = await ClientForAsync(api, "alice");
        var shared = await FolderAsync(alice, "Public");
        var sub = await FolderAsync(alice, "Sub", shared);
        var inside = await UploadAsync(alice, "inside.txt", sub);
        var other = await FolderAsync(alice, "Private");
        var outside = await UploadAsync(alice, "outside.txt", other);

        var token = (await (await alice.PostAsJsonAsync("/api/shares", new { itemType = "folder", itemId = shared, link = true }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var anon = api.CreateClient();

        Assert.Equal(["Sub"], Names(await anon.GetFromJsonAsync<JsonElement>($"/api/public/{token}/"), "folders"));
        Assert.Equal(["inside.txt"], Names(await anon.GetFromJsonAsync<JsonElement>($"/api/public/{token}/?folderId={sub}"), "files"));
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/public/{token}/files/{inside.FileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/{token}/files/{outside.FileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/{token}/?folderId={other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/not-a-token/")).StatusCode);

        var link = (await alice.GetFromJsonAsync<JsonElement>($"/api/shares/folder/{shared}")).EnumerateArray().Single().GetProperty("shareId").GetGuid();
        await alice.DeleteAsync($"/api/shares/{link}");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/{token}/files/{inside.FileId}")).StatusCode);
    }

    [Fact]
    public async Task Search_finds_own_and_shared_items_but_not_other_peoples()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var alice = await ClientForAsync(api, "alice");
        var bob = await ClientForAsync(api, "bob");
        await UploadAsync(alice, "report-alice.txt");
        await UploadAsync(bob, "report-bob-private.txt");
        var bobShared = await FolderAsync(bob, "Shared");
        await UploadAsync(bob, "report-bob-shared.txt", await FolderAsync(bob, "Nested", bobShared));
        await bob.PostAsJsonAsync("/api/shares", new { itemType = "folder", itemId = bobShared, userName = "alice" });

        var hits = (await alice.GetFromJsonAsync<JsonElement>("/api/search?q=REPORT")).EnumerateArray().Select(h => h.GetProperty("name").GetString()).ToList();
        Assert.Equal(["report-alice.txt", "report-bob-shared.txt"], hits.Order());
        Assert.Empty((await alice.GetFromJsonAsync<JsonElement>("/api/search?q=%25")).EnumerateArray()); // '%' is literal, not a wildcard
    }

    [Fact]
    public async Task Preview_is_inline_only_for_images_and_pdf()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = await ClientForAsync(api, "alice");
        var png = await UploadAsync(http, "cat.png");
        var html = await UploadAsync(http, "evil.html");

        using var image = await http.GetAsync($"/api/files/{png.FileId}?inline=true");
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", image.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());

        using var page = await http.GetAsync($"/api/files/{html.FileId}?inline=true");
        Assert.Equal("application/octet-stream", page.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", page.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task Uploads_report_phase_timings_and_routes_are_measured()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = await ClientForAsync(api, "alice");

        using var response = await http.PostAsync("/api/files?fileName=t.bin", new ByteArrayContent(RandomBytes(3 * 1024 * 1024, seed: 40)));
        var result = (await response.Content.ReadFromJsonAsync<UploadResult>())!;
        Assert.Superset(new HashSet<string> { "read_chunk", "hash", "dedup_lookup", "blob_write", "db_register", "manifest_save", "total" },
            result.Timings!.Keys.ToHashSet());
        Assert.Contains("total;dur=", response.Headers.GetValues("Server-Timing").Single());

        var routes = (await http.GetFromJsonAsync<JsonElement>("/api/metrics")).EnumerateArray().Select(r => r.GetProperty("route").GetString());
        Assert.Contains("POST /api/files", routes);
    }

    [Fact]
    public async Task Database_from_before_folders_is_upgraded_and_keeps_its_files()
    {
        using var dir = new TempDir();
        await using (var db = new SqliteConnection($"Data Source={dir.Path("meta.db")}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Files (FileId TEXT PRIMARY KEY, UserId TEXT NOT NULL, FileName TEXT NOT NULL, TotalSize INTEGER NOT NULL,
                                    Sha256 TEXT NULL, Chunker TEXT NOT NULL, CreatedAt TEXT NOT NULL);
                INSERT INTO Files VALUES ('6f9619ff-8b86-d011-b42d-00c04fc964ff', 'legacy', 'old.txt', 3, NULL, 'fixed', '2026-01-01T00:00:00+00:00');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        await using var api = CreateApi(dir);
        var http = await ClientForAsync(api, "legacy"); // registering the old header name adopts its files
        Assert.Equal(["old.txt"], Names(await ChildrenAsync(http, null), "files"));
    }
}
