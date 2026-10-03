using System.Collections.Concurrent;

namespace CloudSync.Api;

/// <summary>Per-route latency over the last 1000 requests. ponytail: in-memory, per process; export to OpenTelemetry when there's more than one node.</summary>
public sealed class RouteMetrics
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _samples = new();

    public void Record(string route, double milliseconds)
    {
        var queue = _samples.GetOrAdd(route, _ => new());
        queue.Enqueue(milliseconds);
        while (queue.Count > 1000) queue.TryDequeue(out _);
    }

    public IEnumerable<object> Snapshot() => _samples.OrderBy(r => r.Key).Select(r =>
    {
        var s = r.Value.ToArray();
        Array.Sort(s);
        return new { route = r.Key, samples = s.Length, p50 = At(s, 0.5), p95 = At(s, 0.95), max = Math.Round(s[^1], 1) };
    });

    private static double At(double[] sorted, double q) => Math.Round(sorted[(int)Math.Ceiling(q * sorted.Length) - 1], 1);
}
