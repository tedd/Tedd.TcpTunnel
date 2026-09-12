namespace Tedd.TcpTunnel.Management;

public sealed record TrafficRate(double UncompressedMBps, double EncodedMBps, double CompressedMBps)
{
    public double UncompressedPayloadMBps => Math.Max(0, EncodedMBps - CompressedMBps);
    public double? CompressionRatio => EncodedMBps > 0 ? UncompressedMBps / EncodedMBps : null;
}

public sealed record TrafficSample(DateTimeOffset Time, TrafficRate Outbound, TrafficRate Inbound);

/// <summary>Computes deltas using the daemon's monotonic clock, without bridging restarts or dropped samples.</summary>
public sealed class TelemetryHistory(int capacity = 120)
{
    private DaemonStatus? _previous;
    private readonly Dictionary<string, List<TrafficSample>> _samples = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<TrafficSample> For(string name) => _samples.TryGetValue(name, out var values) ? values : [];
    public void Reset() { _previous = null; _samples.Clear(); }

    public const string AllForwards = "All forwards";
    public void Add(DaemonStatus current)
    {
        static TrafficTotals Sum(IEnumerable<TrafficTotals> values)
        {
            var totals = values.ToArray();
            return new(totals.Sum(t => t.UncompressedBytes), totals.Sum(t => t.EncodedBytes), totals.Sum(t => t.CompressedBytes));
        }
        var aggregate = new ForwardTelemetry(AllForwards, "Aggregate", "", "", current.Forwards.Sum(f => f.ActiveConnections),
            Sum(current.Forwards.Select(f => f.Outbound)), Sum(current.Forwards.Select(f => f.Inbound)));
        current = current with { Forwards = new[] { aggregate }.Concat(current.Forwards).ToArray() };
        if (_previous is null || _previous.InstanceId != current.InstanceId || current.SampleSeconds <= _previous.SampleSeconds)
        {
            Reset(); _previous = current; return;
        }
        var seconds = current.SampleSeconds - _previous.SampleSeconds;
        if (seconds > 5) { Reset(); _previous = current; return; }
        foreach (var forward in current.Forwards)
        {
            var before = _previous.Forwards.FirstOrDefault(f => f.Name == forward.Name);
            if (before is null) continue;
            if (!_samples.TryGetValue(forward.Name, out var values)) _samples[forward.Name] = values = [];
            values.Add(new(current.StartedAt.AddSeconds(current.SampleSeconds), Rate(before.Outbound, forward.Outbound, seconds),
                Rate(before.Inbound, forward.Inbound, seconds)));
            if (values.Count > capacity) values.RemoveRange(0, values.Count - capacity);
        }
        _previous = current;
    }

    private static TrafficRate Rate(TrafficTotals before, TrafficTotals after, double seconds) => new(
        Math.Max(0, after.UncompressedBytes - before.UncompressedBytes) / seconds / 1_000_000,
        Math.Max(0, after.EncodedBytes - before.EncodedBytes) / seconds / 1_000_000,
        Math.Max(0, after.CompressedBytes - before.CompressedBytes) / seconds / 1_000_000);
}
