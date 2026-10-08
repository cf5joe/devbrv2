using DevBR.App.Services;

namespace DevBR.Tests.App;

public sealed class ProgressTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Estimator_stays_silent_until_enough_samples_over_enough_time()
    {
        var estimator = new ThroughputEstimator(minimumSamples: 5, minimumSpan: S(2));

        // Four samples, then a fifth that is still under two seconds from the first.
        Assert.Null(estimator.Add(S(0.0), 0, 1000));
        Assert.Null(estimator.Add(S(0.5), 50, 1000));
        Assert.Null(estimator.Add(S(1.0), 100, 1000));
        Assert.Null(estimator.Add(S(1.5), 150, 1000));
        Assert.Null(estimator.Add(S(1.9), 190, 1000));

        var estimate = estimator.Add(S(2.0), 200, 1000);
        Assert.NotNull(estimate);
        Assert.Equal(100, estimate.BytesPerSecond, precision: 6);
        Assert.Equal(S(8), estimate.Remaining);
    }

    [Fact]
    public void Estimator_gives_throughput_but_no_eta_without_a_total()
    {
        var estimator = new ThroughputEstimator(minimumSamples: 2, minimumSpan: S(1));
        estimator.Add(S(0), 0, null);

        var estimate = estimator.Add(S(2), 4096, null);

        Assert.NotNull(estimate);
        Assert.Equal(2048, estimate.BytesPerSecond, precision: 6);
        Assert.Null(estimate.Remaining);
    }

    [Fact]
    public void Estimator_follows_the_recent_rate_not_the_average_since_the_start()
    {
        var estimator = new ThroughputEstimator(window: S(4), minimumSamples: 2, minimumSpan: S(1));
        long bytes = 0;
        ThroughputEstimate? estimate = null;

        // Ten slow seconds at 10 B/s, then ten fast seconds at 1000 B/s.
        for (var t = 0; t <= 10; t++)
        {
            estimate = estimator.Add(S(t), bytes, 100_000);
            bytes += 10;
        }

        Assert.Equal(10, estimate!.BytesPerSecond, precision: 6);

        bytes -= 10;
        for (var t = 11; t <= 20; t++)
        {
            bytes += 1000;
            estimate = estimator.Add(S(t), bytes, 100_000);
        }

        Assert.Equal(1000, estimate!.BytesPerSecond, precision: 6);
        Assert.Equal(S(Math.Ceiling((100_000 - bytes) / 1000.0)), estimate.Remaining);
    }

    [Fact]
    public void Estimator_shows_nothing_while_stalled()
    {
        var estimator = new ThroughputEstimator(minimumSamples: 2, minimumSpan: S(1));
        estimator.Add(S(0), 500, 1000);

        Assert.Null(estimator.Add(S(5), 500, 1000));
    }

    [Fact]
    public void Estimator_restarts_when_progress_goes_backwards()
    {
        var estimator = new ThroughputEstimator(minimumSamples: 2, minimumSpan: S(1));
        estimator.Add(S(0), 0, 1000);
        Assert.NotNull(estimator.Add(S(2), 800, 1000));

        // A new stage starts counting from zero: no estimate until it has its own measurements.
        Assert.Null(estimator.Add(S(3), 0, 1000));
        var estimate = estimator.Add(S(5), 100, 1000);
        Assert.Equal(50, estimate!.BytesPerSecond, precision: 6);
    }

    [Fact]
    public void Estimator_does_not_promise_absurd_etas()
    {
        var estimator = new ThroughputEstimator(minimumSamples: 2, minimumSpan: S(1));
        estimator.Add(S(0), 0, long.MaxValue / 2);

        var estimate = estimator.Add(S(10), 10, long.MaxValue / 2);

        Assert.NotNull(estimate);
        Assert.Null(estimate.Remaining);
    }

    [Fact]
    public void Gate_allows_one_update_per_interval()
    {
        var gate = new UpdateGate(TimeSpan.FromMilliseconds(100));

        Assert.True(gate.TryPass(TimeSpan.FromMilliseconds(0)));
        Assert.False(gate.TryPass(TimeSpan.FromMilliseconds(40)));
        Assert.Equal(TimeSpan.FromMilliseconds(60), gate.Remaining(TimeSpan.FromMilliseconds(40)));
        Assert.False(gate.TryPass(TimeSpan.FromMilliseconds(99)));
        Assert.True(gate.TryPass(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(TimeSpan.Zero, gate.Remaining(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void Gate_limits_a_flood_of_reports_to_about_ten_per_second()
    {
        var gate = new UpdateGate(TimeSpan.FromMilliseconds(100));
        var passed = 0;

        // 10,000 reports spread over one second.
        for (var i = 0; i < 10_000; i++)
        {
            if (gate.TryPass(TimeSpan.FromTicks(TimeSpan.TicksPerSecond * i / 10_000)))
            {
                passed++;
            }
        }

        Assert.Equal(10, passed);
    }

    [Fact]
    public async Task Throttled_progress_coalesces_reports_and_always_delivers_the_last_value()
    {
        var delivered = new List<int>();
        var done = new TaskCompletionSource();
        using var progress = new ThrottledProgress<int>(value =>
        {
            lock (delivered)
            {
                delivered.Add(value);
            }

            if (value == 999)
            {
                done.TrySetResult();
            }
        }, TimeSpan.FromMilliseconds(50));

        for (var i = 0; i < 1000; i++)
        {
            progress.Report(i);
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        lock (delivered)
        {
            Assert.InRange(delivered.Count, 1, 3);
            Assert.Equal(999, delivered[^1]);
            Assert.Equal(delivered.Order(), delivered);
        }
    }

    [Fact]
    public async Task Throttled_progress_delivers_nothing_after_dispose()
    {
        var delivered = 0;
        var progress = new ThrottledProgress<int>(_ => Interlocked.Increment(ref delivered), TimeSpan.FromMilliseconds(100));
        progress.Report(1); // delivered at once
        progress.Report(2); // waits for the interval

        progress.Dispose();
        progress.Report(3);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref delivered));
    }
}
