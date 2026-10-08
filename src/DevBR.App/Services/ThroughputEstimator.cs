namespace DevBR.App.Services;

/// <param name="BytesPerSecond">Measured over the recent window, not since the start.</param>
/// <param name="Remaining">Null when the total is unknown or the work is not moving.</param>
public sealed record ThroughputEstimate(double BytesPerSecond, TimeSpan? Remaining);

/// <summary>
/// Throughput and ETA from measured progress only. Nothing is estimated until there are enough samples
/// over a long enough span, the rate is taken from a sliding window (so it follows slow or fast phases),
/// and an ETA is produced only for determinate work with a known total. Call <see cref="Reset"/> when the
/// stage changes; going backwards resets automatically.
/// </summary>
public sealed class ThroughputEstimator(TimeSpan? window = null, int minimumSamples = 5, TimeSpan? minimumSpan = null)
{
    /// <summary>ETAs beyond this are not meaningful enough to show.</summary>
    public static readonly TimeSpan MaximumEta = TimeSpan.FromDays(2);

    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(10);
    private readonly TimeSpan _minimumSpan = minimumSpan ?? TimeSpan.FromSeconds(2);
    private readonly LinkedList<(TimeSpan At, long Bytes)> _samples = new();

    public void Reset() => _samples.Clear();

    /// <summary>Records <paramref name="processed"/> bytes at <paramref name="at"/>; returns an estimate once meaningful.</summary>
    public ThroughputEstimate? Add(TimeSpan at, long processed, long? total)
    {
        if (_samples.Last is { } last && (processed < last.Value.Bytes || at < last.Value.At))
        {
            Reset();
        }

        if (_samples.Last is { } latest && latest.Value.At == at)
        {
            _samples.RemoveLast();
        }

        _samples.AddLast((at, processed));
        while (_samples.Count > 2 && at - _samples.First!.Next!.Value.At >= _window)
        {
            _samples.RemoveFirst();
        }

        var first = _samples.First!.Value;
        var span = at - first.At;
        if (_samples.Count < minimumSamples || span < _minimumSpan)
        {
            return null;
        }

        var rate = (processed - first.Bytes) / span.TotalSeconds;
        if (rate <= 0)
        {
            return null; // stalled: no throughput to show and no ETA to promise
        }

        TimeSpan? remaining = null;
        if (total is > 0 and var t && t >= processed)
        {
            var seconds = (t - processed) / rate;
            remaining = seconds <= MaximumEta.TotalSeconds ? TimeSpan.FromSeconds(Math.Ceiling(seconds)) : null;
        }

        return new ThroughputEstimate(rate, remaining);
    }
}
