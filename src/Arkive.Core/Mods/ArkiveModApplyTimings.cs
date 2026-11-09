using System.Collections.Concurrent;
using System.Diagnostics;

namespace Arkive.Core.Mods;

/// <summary>
/// Where an Apply spent its time, added up per step across every package and asset. Apply starts one for its own
/// work; the services it calls add to it through <see cref="Measure"/>, which does nothing outside an Apply. It only
/// reads the clock, so it never changes what Apply does.
/// </summary>
public sealed class ArkiveModApplyTimings
{
    private static readonly AsyncLocal<ArkiveModApplyTimings?> CurrentTimings = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _order;

    private sealed class Entry(int order)
    {
        public int Order { get; } = order;
        public long Ticks;
        public int Count;
    }

    /// <summary>The timings of the Apply running on this call path, if any.</summary>
    public static ArkiveModApplyTimings? Current => CurrentTimings.Value;

    /// <summary>Makes these timings current for this call path and the tasks it starts, until disposed.</summary>
    public IDisposable Begin()
    {
        var previous = CurrentTimings.Value;
        CurrentTimings.Value = this;
        return new Scope(() => CurrentTimings.Value = previous);
    }

    /// <summary>Adds the time until disposal to <paramref name="step"/> of the current Apply, if one is running.</summary>
    public static IDisposable Measure(string step) => Current?.Start(step) ?? Scope.Empty;

    public IDisposable Start(string step)
    {
        var started = Stopwatch.GetTimestamp();
        return new Scope(() => Add(step, Stopwatch.GetElapsedTime(started)));
    }

    public void Add(string step, TimeSpan elapsed)
    {
        var entry = _entries.GetOrAdd(step, _ => new Entry(Interlocked.Increment(ref _order)));
        Interlocked.Add(ref entry.Ticks, elapsed.Ticks);
        Interlocked.Increment(ref entry.Count);
    }

    /// <summary>
    /// One build's steps, timed from each progress report to the next. Steps of builds that run at the same time add
    /// up, so their total can exceed the time the builds took together.
    /// </summary>
    public BuildStepTimer BuildSteps(string prefix) => new(this, prefix);

    /// <summary>Lines such as "setup.mesh-recon: 412.3 s over 474 (870 ms each)", in the order the steps first ran.</summary>
    public IReadOnlyList<string> Describe() => _entries
        .OrderBy(item => item.Value.Order)
        .Select(item =>
        {
            var total = TimeSpan.FromTicks(Interlocked.Read(ref item.Value.Ticks));
            var count = item.Value.Count;
            return count <= 1
                ? $"{item.Key}: {total.TotalSeconds:N1} s"
                : $"{item.Key}: {total.TotalSeconds:N1} s over {count:N0} ({total.TotalMilliseconds / count:N0} ms each)";
        })
        .ToArray();

    /// <summary>
    /// Times a run of steps one after another: each <see cref="LapTimer.Next"/> ends the step before it. It does
    /// nothing outside an Apply.
    /// </summary>
    public static LapTimer Laps(string prefix) => new(Current, prefix);

    public sealed class LapTimer(ArkiveModApplyTimings? timings, string prefix) : IDisposable
    {
        private string? _step;
        private long _started;

        public void Next(string step)
        {
            if (timings is null) return;
            var now = Stopwatch.GetTimestamp();
            if (_step is not null) timings.Add($"{prefix}.{_step}", Stopwatch.GetElapsedTime(_started, now));
            _step = step;
            _started = now;
        }

        public void Dispose()
        {
            if (timings is null || _step is null) return;
            timings.Add($"{prefix}.{_step}", Stopwatch.GetElapsedTime(_started));
            _step = null;
        }
    }

    /// <summary>Times one build's steps from its progress reports; disposing it ends the last step.</summary>
    public sealed class BuildStepTimer(ArkiveModApplyTimings timings, string prefix) : IProgress<UnifiedPackageBuildProgress>, IDisposable
    {
        private string? _stage;
        private long _started;

        public void Report(UnifiedPackageBuildProgress value)
        {
            var now = Stopwatch.GetTimestamp();
            if (_stage is not null) timings.Add($"{prefix}.{_stage}", Stopwatch.GetElapsedTime(_started, now));
            _stage = value.Stage;
            _started = now;
        }

        public void Dispose()
        {
            if (_stage is null) return;
            timings.Add($"{prefix}.{_stage}", Stopwatch.GetElapsedTime(_started));
            _stage = null;
        }
    }

    private sealed class Scope(Action? end) : IDisposable
    {
        public static readonly IDisposable Empty = new Scope(null);
        private Action? _end = end;

        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }
}
