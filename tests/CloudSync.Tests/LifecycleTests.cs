using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudSync.Core.Storage;
using CloudSync.Infrastructure.Metadata;
using Microsoft.Extensions.DependencyInjection;
using static CloudSync.Tests.DriveTests;
using static CloudSync.Tests.TestUtil;

namespace CloudSync.Tests;

/// <summary>Trash + GC, versions, copy, stars/recent, zip, Range, resumable uploads, activity.</summary>
public class LifecycleTests
{
    private const int MB = 1024 * 1024;

    private static Task<CloudSync.Core.Sync.UploadResult> Upload(HttpClient http, byte[] data, string name, Guid? folder = null) =>
        ApiTests.UploadRawAsync(http, data, name, folder is null ? "" : $"&folderId={folder}");

    private static Task<int> CollectAllGarbageAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<CloudSync.Api.ApiMarker> api) =>
        api.Services.GetRequiredService<SqliteDrive>().CollectGarbageAsync(api.Services.GetRequiredService<IBlobStore>(), TimeSpan.Zero);

    private static async Task<List<string>> NamesOf(HttpClient http, string url) =>
        (await http.GetFromJsonAsync<JsonElement>(url)).EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();

    [Fact]
    public async Task Trash_hides_a_subtree_restore_returns_it_and_gc_frees_only_unreferenced_chunks()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = await ClientForAsync(api, "alice");
        var album = await FolderAsync(http, "Album");
        var inner = await FolderAsync(http, "Inner", album);
        var shared = RandomBytes(MB, 50);
        await Upload(http, [.. shared, .. RandomBytes(MB, 51)], "photo.raw", inner); // 2 chunks
        await Upload(http, shared, "keep.bin");                                         // reuses the first one
        Assert.Equal(2, CountBlobs(dir.Path("blobs")));

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/folders/{album}")).StatusCode);
        Assert.DoesNotContain("Album", Names(await ChildrenAsync(http, null), "folders"));
        Assert.Empty(await NamesOf(http, "/api/search?q=photo"));
        Assert.Equal(["Album"], await NamesOf(http, "/api/trash")); // only the top-level item, not its contents

        (await http.PostAsync($"/api/trash/folder/{album}/restore", null)).EnsureSuccessStatusCode();
        Assert.Contains("photo.raw", Names(await ChildrenAsync(http, inner), "files"));
        Assert.Empty(await NamesOf(http, "/api/trash"));

        await http.DeleteAsync($"/api/folders/{album}");
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/trash/folder/{album}")).StatusCode);
        Assert.Equal(1, await CollectAllGarbageAsync(api));
        Assert.Equal(1, CountBlobs(dir.Path("blobs"))); // the chunk keep.bin still uses survives
        var keep = Names(await ChildrenAsync(http, null), "files").Single();
        Assert.Equal("keep.bin", keep);
    }

    [Fact]
    public async Task Reupload_makes_a_version_restore_keeps_history_and_gc_spares_versions()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = await ClientForAsync(api, "alice");
        byte[] v1 = RandomBytes(MB + 10, 60), v2 = RandomBytes(MB + 10, 61);

        var first = await Upload(http, v1, "report.docx");
        var second = await Upload(http, v2, "report.docx");
        Assert.Equal(first.FileId, second.FileId); // same file, new content
        Assert.Single(Names(await ChildrenAsync(http, null), "files"));
        Assert.Equal(Sha256(v2), Sha256(await http.GetByteArrayAsync($"/api/files/{first.FileId}")));

        var versions = (await http.GetFromJsonAsync<JsonElement>($"/api/files/{first.FileId}/versions")).EnumerateArray().ToList();
        var old = versions.Single().GetProperty("versionId").GetGuid();
        Assert.Equal(Sha256(v1), Sha256(await http.GetByteArrayAsync($"/api/files/{first.FileId}/versions/{old}")));

        (await http.PostAsync($"/api/files/{first.FileId}/versions/{old}/restore", null)).EnsureSuccessStatusCode();
        Assert.Equal(Sha256(v1), Sha256(await http.GetByteArrayAsync($"/api/files/{first.FileId}")));
        Assert.Equal(2, (await http.GetFromJsonAsync<JsonElement>($"/api/files/{first.FileId}/versions")).GetArrayLength());

        Assert.Equal(0, await CollectAllGarbageAsync(api)); // every chunk is still referenced by the file or a version
        Assert.Equal(4, CountBlobs(dir.Path("blobs")));
    }

    [Fact]
    public async Task Copying_files_and_whole_folders_writes_no_bytes()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = await ClientForAsync(api, "alice");
        var trip = await FolderAsync(http, "Trip");
        var day = await FolderAsync(http, "Day1", trip);
        var a = RandomBytes(3 * MB, 62);
        await Upload(http, a, "a.bin", day);
        var b = await Upload(http, RandomBytes(MB, 63), "b.bin", trip);
        var blobs = CountBlobs(dir.Path("blobs"));

        var fileCopy = (await (await http.PostAsync($"/api/files/{b.FileId}/copy", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Contains("Copy of b.bin", Names(await ChildrenAsync(http, trip), "files"));
        Assert.Equal(await http.GetByteArrayAsync($"/api/files/{b.FileId}"), await http.GetByteArrayAsync($"/api/files/{fileCopy}"));

        var tripCopy = (await (await http.PostAsync($"/api/folders/{trip}/copy", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var copied = await ChildrenAsync(http, tripCopy);
        Assert.Equal(["Day1"], Names(copied, "folders"));
        Assert.Equal(["b.bin", "Copy of b.bin"], Names(copied, "files").Order());
        var dayCopy = copied.GetProperty("folders")[0].GetProperty("id").GetGuid();
        var aCopy = (await ChildrenAsync(http, dayCopy)).GetProperty("files")[0].GetProperty("fileId").GetGuid();
        Assert.Equal(Sha256(a), Sha256(await http.GetByteArrayAsync($"/api/files/{aCopy}")));

        Assert.Equal(blobs, CountBlobs(dir.Path("blobs"))); // nothing new on disk
        await http.PostAsync($"/api/folders/{trip}/copy", null);
        Assert.Contains("Copy of Trip (2)", Names(await ChildrenAsync(http, null), "folders"));
    }

    [Fact]
    public async Task Stars_are_per_user_and_recent_lists_newest_first()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var alice = await ClientForAsync(api, "alice");
        var bob = await ClientForAsync(api, "bob");
        var file = await Upload(alice, RandomBytes(100, 64), "notes.txt");
        var folder = await FolderAsync(alice, "Projects");

        Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsync($"/api/stars/file/{file.FileId}", null)).StatusCode);
        await alice.PutAsync($"/api/stars/folder/{folder}", null);
        Assert.Equal(["Projects", "notes.txt"], await NamesOf(alice, "/api/starred"));
        await alice.DeleteAsync($"/api/stars/file/{file.FileId}");
        Assert.Equal(["Projects"], await NamesOf(alice, "/api/starred"));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsync($"/api/stars/file/{file.FileId}", null)).StatusCode);

        Assert.Equal(["Projects", "notes.txt"], await NamesOf(alice, "/api/recent"));
    }

    [Fact]
    public async Task Folder_downloads_as_a_zip_with_its_tree()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = await ClientForAsync(api, "alice");
        var root = await FolderAsync(http, "Export");
        var sub = await FolderAsync(http, "sub", root);
        byte[] top = RandomBytes(2 * MB + 5, 65), inner = RandomBytes(300, 66);
        await Upload(http, top, "top.bin", root);
        await Upload(http, inner, "inner.txt", sub);

        using var response = await http.GetAsync($"/api/folders/{root}/zip");
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["sub/inner.txt", "top.bin"], zip.Entries.Select(e => e.FullName));
        var bytes = new MemoryStream();
        await zip.GetEntry("top.bin")!.Open().CopyToAsync(bytes);
        Assert.Equal(Sha256(top), Sha256(bytes.ToArray()));
    }

    [Fact]
    public async Task Range_requests_return_206_across_a_chunk_boundary()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir); // 1 MB chunks
        var http = await ClientForAsync(api, "alice");
        var data = RandomBytes(3 * MB, 70);
        var song = await Upload(http, data, "song.mp3");

        using (var full = await http.GetAsync($"/api/files/{song.FileId}"))
            Assert.Equal("bytes", full.Headers.AcceptRanges.Single());

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{song.FileId}?inline=true");
        request.Headers.Range = new RangeHeaderValue(MB - 100, MB + 99);
        using var partial = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("audio/mpeg", partial.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"bytes {MB - 100}-{MB + 99}/{3 * MB}", partial.Content.Headers.ContentRange!.ToString());
        Assert.Equal(data.AsSpan(MB - 100, 200).ToArray(), await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Resumable_upload_survives_an_interruption_and_only_its_creator_can_resume()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var http = await ClientForAsync(api, "alice");
        var bob = await ClientForAsync(api, "bob");
        var data = RandomBytes(2 * MB + 123, 80);

        var created = await http.PostAsJsonAsync("/api/uploads", new { fileName = "big.iso", size = data.Length });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var session = created.Headers.Location!.ToString();

        async Task<HttpResponseMessage> Put(HttpClient client, int from, int to)
        {
            var content = new ByteArrayContent(from <= to ? data[from..(to + 1)] : []);
            content.Headers.ContentRange = from <= to ? new ContentRangeHeaderValue(from, to, data.Length) : new ContentRangeHeaderValue(data.Length);
            return await client.PutAsync(session, content);
        }
        async Task<long> Received(HttpResponseMessage r)
        {
            Assert.Equal((HttpStatusCode)308, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("received").GetInt64();
        }

        Assert.Equal(MB, await Received(await Put(http, 0, MB - 1)));       // first slice
        Assert.Equal(MB, await Received(await Put(http, 1, 0)));            // status query ("bytes */total")
        Assert.Equal(MB, await Received(await Put(http, 5, 10)));           // wrong offset: told where to resume
        Assert.Equal(HttpStatusCode.NotFound, (await Put(bob, MB, data.Length - 1)).StatusCode);

        using var done = await Put(http, MB, data.Length - 1);
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        var file = (await done.Content.ReadFromJsonAsync<CloudSync.Core.Sync.UploadResult>())!;
        Assert.Equal(Sha256(data), file.Sha256);
        Assert.Equal(Sha256(data), Sha256(await http.GetByteArrayAsync($"/api/files/{file.FileId}")));
        Assert.Empty(Directory.GetFiles(dir.Path("uploads"))); // temp file cleaned up
    }

    [Fact]
    public async Task Activity_records_who_did_what_and_folders_include_their_contents()
    {
        using var dir = new TempDir();
        await using var api = CreateApi(dir);
        var alice = await ClientForAsync(api, "alice");
        var bob = await ClientForAsync(api, "bob");
        var folder = await FolderAsync(alice, "Team");
        var file = await Upload(alice, RandomBytes(100, 81), "a.txt", folder);
        await alice.PatchAsJsonAsync($"/api/files/{file.FileId}", new { name = "b.txt" });
        await alice.PostAsJsonAsync("/api/shares", new { itemType = "folder", itemId = folder, userName = "bob" });

        async Task<List<string>> Actions(HttpClient http, string type, Guid id) =>
            (await http.GetFromJsonAsync<JsonElement>($"/api/activity/{type}/{id}")).EnumerateArray().Select(a => a.GetProperty("action").GetString()!).ToList();

        Assert.Equal(["renamed", "uploaded"], await Actions(alice, "file", file.FileId));
        Assert.Equal(["shared", "renamed", "uploaded", "created"], await Actions(alice, "folder", folder));
        Assert.Contains("shared", await Actions(bob, "folder", folder)); // bob can now see the folder's history

        var carol = await ClientForAsync(api, "carol");
        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync($"/api/activity/folder/{folder}")).StatusCode);
    }
}
