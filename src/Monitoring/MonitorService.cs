using System.Diagnostics;
using PMON.Models;

namespace PMON.Monitoring;

/// <summary>Samples system and matching-process metrics on a dedicated thread and publishes snapshots.</summary>
public sealed class MonitorService : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private ProcessMatcher _matcher = ProcessMatcher.None;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private int _topCount;
    private Thread? _thread;

    public event Action<Snapshot>? SnapshotReady;

    public void Configure(IEnumerable<TrackingRule> rules, TimeSpan interval, int topCount)
    {
        lock (_gate)
        {
            _matcher = new ProcessMatcher(rules);
            _interval = interval;
            _topCount = topCount;
        }

        _wake.Set();
    }

    public void Start()
    {
        if (_thread is not null)
            return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PMON sampler",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _stop.Dispose();
        _wake.Dispose();
    }

    private void Run()
    {
        using var system = new SystemSampler();
        var processes = new ProcessSampler();
        var token = _stop.Token;

        // First pass only primes the delta-based counters.
        var (matcher, topCount) = GetProcessSettings();
        processes.Sample(DateTime.Now, matcher, topCount);
        system.Sample(DateTime.Now);
        WaitHandle.WaitAny([token.WaitHandle], TimeSpan.FromMilliseconds(500));

        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            var time = DateTime.Now;
            (matcher, topCount) = GetProcessSettings();
            var (tracked, hogs) = processes.Sample(time, matcher, topCount);
            var snapshot = new Snapshot(time, system.Sample(time), tracked, hogs);
            SnapshotReady?.Invoke(snapshot);

            var remaining = GetInterval() - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                continue;

            WaitHandle.WaitAny([token.WaitHandle, _wake], remaining);
        }
    }

    private (ProcessMatcher Matcher, int TopCount) GetProcessSettings()
    {
        lock (_gate)
            return (_matcher, _topCount);
    }

    private TimeSpan GetInterval()
    {
        lock (_gate)
            return _interval;
    }
}
