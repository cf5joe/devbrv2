using System.Collections.Concurrent;
using DevBR.Domain;

namespace DevBR.Discovery.Support;

/// <summary>
/// Thread-safe accumulation of what a provider looked at, skipped, and could not read. Repeated
/// exclusions (thousands of node_modules folders) are aggregated per reason with a few sample paths.
/// </summary>
public sealed class CoverageBuilder(string source, string? volume = null)
{
    private const int SamplesPerReason = 10;
    private const int MaxInaccessible = 500;

    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<string, (long Count, ConcurrentQueue<string> Samples)> _exclusions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<InaccessibleScope> _inaccessible = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private long _inaccessibleCount;
    private long _directories;
    private long _files;

    public long Directories => Interlocked.Read(ref _directories);

    public long Files => Interlocked.Read(ref _files);

    public void CountDirectory() => Interlocked.Increment(ref _directories);

    public void CountFiles(long count) => Interlocked.Add(ref _files, count);

    public void Exclude(string path, string reason)
    {
        var entry = _exclusions.AddOrUpdate(reason,
            _ => (1, new ConcurrentQueue<string>([path])),
            (_, existing) =>
            {
                if (existing.Samples.Count < SamplesPerReason)
                {
                    existing.Samples.Enqueue(path);
                }

                return (existing.Count + 1, existing.Samples);
            });
        _ = entry;
    }

    public void Inaccessible(string path, string reason)
    {
        if (Interlocked.Increment(ref _inaccessibleCount) <= MaxInaccessible)
        {
            _inaccessible.Enqueue(new InaccessibleScope(path, reason));
        }
    }

    public void Error(string message) => _errors.Enqueue(message);

    /// <summary>Folds another provider's coverage into this one (used to report all adapters together).</summary>
    public void Absorb(DiscoveryCoverage other)
    {
        Interlocked.Add(ref _directories, other.ScannedDirectories);
        Interlocked.Add(ref _files, other.ScannedFiles);
        foreach (var excluded in other.Exclusions)
        {
            Exclude(excluded.Path, excluded.Reason);
        }

        foreach (var inaccessible in other.Inaccessible)
        {
            Inaccessible(inaccessible.Path, inaccessible.Reason);
        }

        foreach (var error in other.Errors)
        {
            Error(error);
        }
    }

    public DiscoveryCoverage Build()
    {
        var exclusions = new List<ExcludedScope>();
        foreach (var (reason, (count, samples)) in _exclusions.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            exclusions.AddRange(samples.Select(path => new ExcludedScope(path, reason)));
            if (count > samples.Count)
            {
                exclusions.Add(new ExcludedScope($"…and {count - samples.Count:N0} more", reason));
            }
        }

        var inaccessible = _inaccessible.ToList();
        var extra = Interlocked.Read(ref _inaccessibleCount) - inaccessible.Count;
        if (extra > 0)
        {
            inaccessible.Add(new InaccessibleScope($"…and {extra:N0} more", "Further inaccessible locations were not listed individually."));
        }

        return new DiscoveryCoverage(source, volume, _started, DateTimeOffset.UtcNow, Directories, Files, exclusions, inaccessible, [.. _errors]);
    }
}
