using System.Globalization;
using Arkive.SmokeTests;

/// <summary>launch-fixture and launch-benchmark: synthetic data folders and timed engine loads for performance work.</summary>
internal static class PerformanceCommands
{
    public static int LaunchFixture(string root, IReadOnlyList<string> arguments)
    {
        var defaults = new LaunchFixtureOptions();
        var options = new LaunchFixtureOptions(
            Count(arguments, "--mesh-edits", defaults.MeshEdits),
            Count(arguments, "--textures", defaults.Textures),
            Count(arguments, "--effects", defaults.Effects),
            Count(arguments, "--materials", defaults.Materials),
            Count(arguments, "--packages", defaults.Packages),
            Count(arguments, "--builds-per-package", defaults.BuildsPerPackage),
            Count(arguments, "--mods", defaults.Mods),
            Share(arguments, "--enabled-share", defaults.EnabledShare),
            Share(arguments, "--bound-share", defaults.BoundShare),
            Count(arguments, "--seed", defaults.Seed));
        if (options.Packages == 0) throw new ArgumentException("--packages must be at least 1.");
        var summary = Arkive.SmokeTests.LaunchFixture.CreateAsync(root, options, new Progress(Console.Error.WriteLine))
            .GetAwaiter().GetResult();
        Console.WriteLine(Arkive.SmokeTests.LaunchFixture.Describe(summary));
        return 0;
    }

    public static int LaunchBenchmark(string dataRoot, string? jsonPath, bool forcedGc)
    {
        var report = Arkive.Core.Diagnostics.LaunchBenchmark.RunAsync(dataRoot, forcedGc).GetAwaiter().GetResult();
        if (jsonPath is not null) report.Write(jsonPath);
        Console.WriteLine($"Launch benchmark of {report.DataRoot} (Arkive {report.ArkiveVersion}{(forcedGc ? ", forced collections" : string.Empty)}):");
        Console.WriteLine($"  {"step",-20} {"seconds",8} {"cpu s",8} {"read MB",9} {"heap MB",8}  note");
        foreach (var step in report.Steps)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {step.Id,-20} {step.Seconds,8:0.00} {step.CpuSeconds,8:0.00} {step.BytesRead / 1048576d,9:0.0} {step.HeapBytes / 1048576d,8:0}  " +
                $"{(step.Skipped ? "skipped: " : string.Empty)}{step.Note}{(step.Error is null ? string.Empty : " ERROR " + step.Error)}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {"total",-20} {report.Totals.Seconds,8:0.00} {report.Totals.CpuSeconds,8:0.00} {report.Totals.BytesRead / 1048576d,9:0.0} " +
            $"{report.Totals.HeapBytesAfter / 1048576d,8:0}  peak working set {report.PeakWorkingSetBytes / 1048576d:0} MB, " +
            $"{report.FilesHashed:N0} file(s) hashed"));
        if (report.Error is not null) Console.Error.WriteLine($"error: {report.Error}");
        return report.Error is null ? 0 : 1;
    }

    private static int Count(IReadOnlyList<string> arguments, string option, int fallback)
    {
        var text = Value(arguments, option);
        if (text is null) return fallback;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"{option} requires a whole number of 0 or more.");
    }

    private static double Share(IReadOnlyList<string> arguments, string option, double fallback)
    {
        var text = Value(arguments, option);
        if (text is null) return fallback;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1
            ? value
            : throw new ArgumentException($"{option} requires a number from 0 to 1.");
    }

    private static string? Value(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 2; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals(option, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{option} requires a value.");
            return arguments[index + 1];
        }
        return null;
    }

    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
