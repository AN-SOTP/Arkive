using System.Diagnostics;
using System.Windows.Threading;
using Arkive.Core.Diagnostics;

namespace Arkive.App;

/// <summary>
/// Runs the startup loading steps one at a time and records each (time, CPU, bytes read, memory, how long the window
/// could not answer), so slow launches can be compared step by step. Every finished step is written to the session log.
/// </summary>
internal sealed class StartupSteps
{
    private readonly DispatcherStallMonitor _monitor;

    public StartupSteps(Dispatcher dispatcher, bool forcedGc = false)
    {
        _monitor = new DispatcherStallMonitor(dispatcher);
        Recorder = new LaunchStepRecorder(forcedGc, _monitor.Blocked) { Completed = step => Log?.Invoke(step.LogLine) };
    }

    public LaunchStepRecorder Recorder { get; }

    public Action<string>? Log { get; set; }

    public void Run(string id, Action body) => Recorder.Run(id, body);

    public Task RunAsync(string id, Func<Task> body) => Recorder.RunAsync(id, body);

    public Task<T> RunAsync<T>(string id, Func<Task<T>> body, Func<T, string?> note) => Recorder.RunAsync(id, body, note);

    public void Skip(string id, string reason) => Recorder.Skip(id, reason);

    /// <summary>Stops watching the window's thread; called once loading is over.</summary>
    public void Stop() => _monitor.Dispose();
}

/// <summary>
/// Measures how long the window's thread is blocked: every 50 ms a background thread asks it to run an empty piece of
/// work at the highest priority and keeps the waits longer than 0.1 s (shorter ones are not noticeable). A wait is
/// shared between the steps it overlaps, so a long step is not blamed on the next one.
/// </summary>
internal sealed class DispatcherStallMonitor : IDisposable
{
    private static readonly long Noticeable = Stopwatch.Frequency / 10;
    private readonly object _sync = new();
    private readonly List<(long Start, long End)> _waits = [];
    private long _pendingStart;
    private volatile bool _stopped;

    public DispatcherStallMonitor(Dispatcher dispatcher)
    {
        new Thread(() =>
        {
            while (!_stopped)
            {
                var start = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _pendingStart, start);
                try
                {
                    dispatcher.Invoke(static () => { }, DispatcherPriority.Send);
                }
                catch (Exception exception) when (exception is TaskCanceledException or OperationCanceledException or
                                                      InvalidOperationException)
                {
                    return;
                }
                var end = Stopwatch.GetTimestamp();
                lock (_sync)
                {
                    Interlocked.Exchange(ref _pendingStart, 0);
                    if (end - start > Noticeable) _waits.Add((start, end));
                }
                Thread.Sleep(50);
            }
        })
        {
            IsBackground = true,
            Name = "Arkive startup responsiveness monitor",
        }.Start();
    }

    /// <summary>The blocked time between two Stopwatch timestamps, in total and the longest single wait.</summary>
    public (double Total, double Longest) Blocked(long from, long to)
    {
        double total = 0, longest = 0;
        lock (_sync)
        {
            var pending = Interlocked.Read(ref _pendingStart);
            var waits = pending != 0 && to - pending > Noticeable ? _waits.Append((pending, to)) : _waits;
            foreach (var (start, end) in waits)
            {
                var overlap = (Math.Min(end, to) - Math.Max(start, from)) / (double)Stopwatch.Frequency;
                if (overlap <= 0) continue;
                total += overlap;
                longest = Math.Max(longest, overlap);
            }
            // Steps run one after another, so waits that ended before this one began are not needed again.
            _waits.RemoveAll(wait => wait.End < from);
        }
        return (total, longest);
    }

    public void Dispose() => _stopped = true;
}