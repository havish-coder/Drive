using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudSync.Cli;
using CloudSync.Core.Chunking;

const string Usage = """
    Usage:
      cloudsync login <username>         [--server URL]
      cloudsync push <path>              [--server URL] [--chunker fastcdc|fixed] [--avg BYTES]
      cloudsync pull <fileId> <outPath>  [--server URL]
    """;

var positional = new List<string>();
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--") && i + 1 < args.Length) options[args[i][2..]] = args[++i];
    else positional.Add(args[i]);
}

var server = options.GetValueOrDefault("server", "http://localhost:5000");
var tokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudsync", "token");
using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };

if (positional is ["login", var userName])
{
    var password = options.GetValueOrDefault("password") ?? ReadHidden("Password: ");
    var response = await http.PostAsJsonAsync("api/auth/token", new { userName, password });
    if (!response.IsSuccessStatusCode) { Console.Error.WriteLine("Login failed."); return 1; }
    Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
    await File.WriteAllTextAsync(tokenFile, (await response.Content.ReadFromJsonAsync<TokenReply>())!.Token);
    Console.WriteLine($"Logged in. Token saved to {tokenFile}");
    return 0;
}

var token = options.GetValueOrDefault("token") ?? (File.Exists(tokenFile) ? File.ReadAllText(tokenFile).Trim() : null);
if (token is null) { Console.Error.WriteLine("Not logged in. Run: cloudsync login <username>"); return 1; }
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
var client = new SyncClient(http);

switch (positional)
{
    case ["push", var path]:
    {
        var avg = int.Parse(options.GetValueOrDefault("avg", (4 * 1024 * 1024).ToString()));
        var chunker = ChunkerFactory.Create(new ChunkingOptions
        {
            Algorithm = options.GetValueOrDefault("chunker", "fastcdc"),
            ChunkSize = avg,
            MinSize = avg / 4,
            AvgSize = avg,
            MaxSize = avg * 4,
        });
        var result = await client.PushAsync(path, chunker);
        var saved = result.TotalBytes == 0 ? 0 : 100.0 * (result.TotalBytes - result.UploadedBytes) / result.TotalBytes;
        Console.WriteLine($"fileId        {result.FileId}");
        Console.WriteLine($"sha256        {result.Sha256}");
        Console.WriteLine($"chunks        {result.ChunkCount} total, {result.UploadedChunks} uploaded");
        Console.WriteLine($"bytes         {result.TotalBytes:N0} total, {result.UploadedBytes:N0} sent ({saved:F1}% bandwidth saved)");
        return 0;
    }
    case ["pull", var id, var output]:
    {
        var sha = await client.PullAsync(Guid.Parse(id), output);
        Console.WriteLine($"{sha}  {output}");
        return 0;
    }
    default:
        Console.Error.WriteLine(Usage);
        return 1;
}

static string ReadHidden(string prompt)
{
    Console.Write(prompt);
    var chars = new List<char>();
    for (ConsoleKeyInfo key; (key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter;)
    {
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
        else chars.Add(key.KeyChar);
    }
    Console.WriteLine();
    return new string(chars.ToArray());
}

internal sealed record TokenReply(string Token);
