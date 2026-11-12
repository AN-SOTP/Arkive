using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Diagnostics;

/// <summary>
/// One timed step of a launch or of the launch benchmark. Byte counts come from Windows' per-process I/O counters, so
/// they include every file the process read in the step, cached or not, and work other threads did at the same time.
/// </summary>
/// <param name="HeapBytes">Managed heap after the step; with forced collections, what Arkive still holds.</param>
/// <param name="UiBlockedSeconds">How long the window's thread could not answer during the step (waits over 0.1 s).</param>
public sealed record LaunchStepResult(
    string Id,
    double Seconds,
    double CpuSeconds,
    long BytesRead,
    long ReadOperations,
    long BytesWritten,
    long HeapBytes,
    double UiBlockedSeconds,
    double UiLongestBlockSeconds,
    bool Skipped,
    string? Note,
    string? Error)
{
    /// <summary>How many times the step walked each store of working files or package projects (see StoreWalks).</summary>
    public IReadOnlyDictionary<string, int>? StoreWalks { get; init; }

    /// <summary>The line normal launches write to the session log for this step.</summary>
    public string LogLine => Skipped
        ? $"Startup step: id={Id}, skipped ({Note})."
        : FormattableString.Invariant(
              $"Startup step: id={Id}, seconds={Seconds:0.00}, cpu={CpuSeconds:0.00}, readMB={BytesRead / 1048576d:0.0}, ") +
          FormattableString.Invariant(
              $"writeMB={BytesWritten / 1048576d:0.0}, heapMB={HeapBytes / 1048576d:0}, uiBlocked={UiBlockedSeconds:0.00}, ") +
          FormattableString.Invariant($"uiLongest={UiLongestBlockSeconds:0.00}") +
          (StoreWalks is null ? string.Empty : ", walks=" + string.Join(" ", StoreWalks.Select(pair => $"{pair.Key}:{pair.Value}"))) +
          (Note is null ? string.Empty : $", note={Note}") + (Error is null ? string.Empty : $", error={Error}") + ".";
}

public sealed record LaunchMeasurementTotals(
    double Seconds,
    double StepSeconds,
    double CpuSeconds,
    long BytesRead,
    long BytesWritten,
    double UiBlockedSeconds,
    long HeapBytesAfter);

/// <summary>What --measure-startup and launch-benchmark write: one record per step, in the order they ran.</summary>
public sealed record LaunchMeasurementReport(
    string Kind,
    string ArkiveVersion,
    string? Commit,
    string DataRoot,
    DateTimeOffset StartedAt,
    bool ForcedGc,
    IReadOnlyList<LaunchStepResult> Steps,
    LaunchMeasurementTotals Totals,
    long PeakWorkingSetBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long FilesHashed,
    long BytesHashed,
    string? Error)
{
    /// <summary>The store walks of every step added up: how often the whole run read each store.</summary>
    public IReadOnlyDictionary<string, int>? StoreWalks { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Write(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, full, overwrite: true);
    }

    public static LaunchMeasurementReport Read(string path) =>
        JsonSerializer.Deserialize<LaunchMeasurementReport>(File.ReadAllText(path), JsonOptions) ??
        throw new InvalidDataException("The measurement file was empty.");
}

/// <summary>
/// Runs steps one after another and records, for each, wall time, process CPU time, bytes read and written, the managed
/// heap afterwards and (when a monitor is given) how long the window's thread was blocked. A step that throws is
/// recorded with its error and the exception is passed on unchanged, so callers behave as they did without it.
/// </summary>
public sealed class LaunchStepRecorder
{
    private readonly List<LaunchStepResult> _steps = [];
    private readonly Func<long, long, (double Total, double Longest)>? _blocked;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <param name="forcedGc">Collect fully after each step, so the heap column is what is still held.</param>
    /// <param name="blocked">How long the window's thread was blocked between two Stopwatch timestamps.</param>
    public LaunchStepRecorder(bool forcedGc = false, Func<long, long, (double Total, double Longest)>? blocked = null)
    {
        ForcedGc = forcedGc;
        _blocked = blocked;
        StartedAt = DateTimeOffset.Now;
    }

    public bool ForcedGc { get; }

    public DateTimeOffset StartedAt { get; }

    public IReadOnlyList<LaunchStepResult> Steps
    {
        get { lock (_steps) return _steps.ToArray(); }
    }

    /// <summary>Called with each step as it finishes.</summary>
    public Action<LaunchStepResult>? Completed { get; set; }

    public void Run(string id, Action body)
    {
        var start = Begin();
        try
        {
            body();
        }
        catch (Exception exception)
        {
            End(id, start, null, exception);
            throw;
        }
        End(id, start, null, null);
    }

    public async Task RunAsync(string id, Func<Task> body)
    {
        var start = Begin();
        try
        {
            await body();
        }
        catch (Exception exception)
        {
            End(id, start, null, exception);
            throw;
        }
        End(id, start, null, null);
    }

    public async Task<T> RunAsync<T>(string id, Func<Task<T>> body, Func<T, string?>? note = null)
    {
        var start = Begin();
        T result;
        try
        {
            result = await body();
        }
        catch (Exception exception)
        {
            End(id, start, null, exception);
            throw;
        }
        End(id, start, note?.Invoke(result), null);
        return result;
    }

    /// <summary>Records a step that did not run here (a measuring run skips the ones that write user state).</summary>
    public void Skip(string id, string reason) =>
        Add(new LaunchStepResult(id, 0, 0, 0, 0, 0, GC.GetTotalMemory(false), 0, 0, true, reason, null));

    public LaunchMeasurementReport Report(string kind, string dataRoot, string? error)
    {
        var steps = Steps;
        var (version, commit) = ProcessCounters.ArkiveVersion();
        using var process = Process.GetCurrentProcess();
        return new LaunchMeasurementReport(
            kind, version, commit, dataRoot, StartedAt, ForcedGc, steps,
            new LaunchMeasurementTotals(
                _clock.Elapsed.TotalSeconds,
                steps.Sum(step => step.Seconds),
                steps.Sum(step => step.CpuSeconds),
                steps.Sum(step => step.BytesRead),
                steps.Sum(step => step.BytesWritten),
                steps.Sum(step => step.UiBlockedSeconds),
                steps.Count == 0 ? GC.GetTotalMemory(false) : steps[^1].HeapBytes),
            process.PeakWorkingSet64,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            Mods.ArkiveModEditSnapshot.FilesHashed, Mods.ArkiveModEditSnapshot.BytesHashed, error)
        {
            StoreWalks = steps.Where(step => step.StoreWalks is not null).SelectMany(step => step.StoreWalks!)
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value), StringComparer.Ordinal),
        };
    }

    private (ProcessSample Sample, IReadOnlyDictionary<string, int> Walks) Begin() =>
        (ProcessCounters.Sample(), Diagnostics.StoreWalks.Current());

    private void End(string id, (ProcessSample Sample, IReadOnlyDictionary<string, int> Walks) begun, string? note, Exception? exception)
    {
        var (start, walksBefore) = begun;
        var walks = Diagnostics.StoreWalks.Since(walksBefore);
        var end = ProcessCounters.Sample();
        var blocked = _blocked?.Invoke(start.Timestamp, end.Timestamp) ?? (0, 0);
        if (ForcedGc)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        Add(new LaunchStepResult(
            id,
            (end.Timestamp - start.Timestamp) / (double)Stopwatch.Frequency,
            end.CpuSeconds - start.CpuSeconds,
            end.ReadBytes - start.ReadBytes,
            end.ReadOperations - start.ReadOperations,
            end.WriteBytes - start.WriteBytes,
            GC.GetTotalMemory(false),
            blocked.Total,
            blocked.Longest,
            false,
            note,
            exception is null ? null : $"{exception.GetType().Name}: {exception.Message}")
        {
            StoreWalks = walks,
        });
    }

    private void Add(LaunchStepResult step)
    {
        lock (_steps) _steps.Add(step);
        Completed?.Invoke(step);
    }
}

public readonly record struct ProcessSample(long Timestamp, double CpuSeconds, long ReadBytes, long ReadOperations, long WriteBytes);

public static class ProcessCounters
{
    public static ProcessSample Sample()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var cpu = Environment.CpuUsage.TotalTime.TotalSeconds;
        IoCounters io = default;
        if (OperatingSystem.IsWindows()) GetProcessIoCounters(GetCurrentProcess(), out io);
        return new ProcessSample(timestamp, cpu, (long)io.ReadTransferCount, (long)io.ReadOperationCount,
            (long)io.WriteTransferCount);
    }

    /// <summary>Arkive's version and the commit it was built from, when the build recorded one.</summary>
    public static (string Version, string? Commit) ArkiveVersion()
    {
        var informational = typeof(ProcessCounters).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = informational.IndexOf('+');
        return plus < 0 ? (informational, null) : (informational[..plus], informational[(plus + 1)..]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
