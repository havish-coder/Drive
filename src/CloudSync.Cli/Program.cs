using CloudSync.Cli;
using CloudSync.Core.Chunking;

const string Usage = """
    Usage:
      cloudsync push <path>              [--server URL] [--user ID] [--chunker fastcdc|fixed] [--avg BYTES]
      cloudsync pull <fileId> <outPath>  [--server URL] [--user ID]
    """;

var positional = new List<string>();
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--") && i + 1 < args.Length) options[args[i][2..]] = args[++i];
    else positional.Add(args[i]);
}

var server = options.GetValueOrDefault("server", "http://localhost:5000");
var user = options.GetValueOrDefault("user", "cli-user");
using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
var client = new SyncClient(http, user);

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
