using System.Collections.Concurrent;

namespace SmartMetrix.LocalPositioningService;

// A bounded live window, not historical storage. Resolved exposure poses are archived by the pipeline.
public sealed class PositioningSampleBuffer
{
    private readonly ConcurrentDictionary<string, List<PositioningSample>> _samples = new(StringComparer.Ordinal);

    public void Add(string excavatorId, IReadOnlyList<PositioningSample> samples)
    {
        if (samples.Count is 0 or > 10000) throw new ArgumentException("Supply 1..10000 positioning samples.");
        var window = _samples.GetOrAdd(excavatorId, _ => []);
        lock (window)
        {
            window.AddRange(samples);
            window.Sort((left, right) => left.HardwareTimestampNanoseconds.CompareTo(right.HardwareTimestampNanoseconds));
            if (window.Count > 10000) window.RemoveRange(0, window.Count - 10000);
        }
    }

    public IReadOnlyList<PositioningSample> Get(string excavatorId)
    {
        if (!_samples.TryGetValue(excavatorId, out var window)) return [];
        lock (window) return window.ToArray();
    }
}
