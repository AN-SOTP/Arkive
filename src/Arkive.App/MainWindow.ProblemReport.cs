using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Arkive.Core.External;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Updates;
using Arkive.Core.Validation;

namespace Arkive.App;

/// <summary>
/// Report a problem: packs the recent session logs, the mod list with each mod's state, and the checks of the meshes
/// in the open mod into one zip, copies it to the clipboard and shows it in Explorer, so a user can send it in one step.
/// </summary>
public partial class MainWindow
{
    private async void ReportProblemButton_Click(object sender, RoutedEventArgs e) => await ReportProblemAsync();

    internal async Task ReportProblemAsync()
    {
        try
        {
            Log("Creating a problem report.");
            var path = await ArkiveProblemReport.CreateAsync(
                Path.Combine(ArkiveRuntimePaths.ResolveDataRoot(), "reports"), await ProblemReportContentAsync());
            var copied = false;
            try
            {
                Clipboard.SetFileDropList(new StringCollection { path });
                copied = true;
            }
            catch (System.Runtime.InteropServices.ExternalException exception)
            {
                Log($"The problem report could not be copied to the clipboard: {exception.Message}");
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            Log($"Problem report created: {path}, copied={copied}.");
            MessageBox.Show(this,
                (copied ? "The problem report is copied: paste it (Ctrl+V) into Discord, an email or a GitHub issue. " : "") +
                $"It is also saved as {Path.GetFileName(path)}, shown in Explorer.\n\n" +
                "It holds Arkive's recent logs, your mod list and the checks of the meshes you're editing, with file paths " +
                "on this PC. It holds no game files and no settings.",
                "Problem report", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"The problem report could not be created: {exception}");
            MessageBox.Show(this, $"The problem report could not be created: {exception.Message}", "Problem report",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<ArkiveProblemReportContent> ProblemReportContentAsync()
    {
        var summary = new StringBuilder()
            .AppendLine($"Arkive {ArkiveReleasePolicy.CurrentVersion.ToString(3)}")
            .AppendLine($"Created: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"Windows: {Environment.OSVersion.VersionString}, .NET {Environment.Version}")
            .AppendLine($"Data folder: {ArkiveRuntimePaths.ResolveDataRoot()}")
            .AppendLine($"Game Packages folder: {_libraryRootPath ?? "not chosen"}")
            .AppendLine($"Game running: {(_modDeploymentService.FindBlockingProcesses() is { Count: > 0 } running ? string.Join(", ", running) : "no")}")
            .AppendLine($"Open tab: {(MainTabControl.SelectedItem as System.Windows.Controls.TabItem)?.Header}")
            .AppendLine($"Mod being edited: {_authoringDraft?.Metadata.Name ?? "none"}")
            .AppendLine($"Unexpected errors this session: {UnexpectedErrors.Gate.Count}" +
                        (UnexpectedErrors.Gate.LastReason is { } last ? $" (last: {last})" : string.Empty))
            .AppendLine()
            .AppendLine("Last messages shown:")
            .AppendLine($"  Status: {StatusText.Text}")
            .AppendLine($"  Edit mods: {AuthoringStatusText.Text}")
            .AppendLine($"  Mods: {ModLibraryStatusText.Text}")
            .AppendLine($"  Simple upscale: {ProportionStatusText.Text}");

        var mods = new StringBuilder();
        foreach (var row in _modLibraryRows)
            mods.AppendLine($"{(row.Enabled ? "[on] " : "[off]")} {row.DisplayName} {row.Version} · {row.State} · {row.AssetSummary} · {row.FileName}" +
                             (row.Group?.Pack is { } pack ? $" · pack {pack.Name}" : string.Empty));

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var logDirectory = Path.GetDirectoryName(_sessionLog.FilePath)!;
        foreach (var log in Directory.EnumerateFiles(logDirectory, "arkive-*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
            files[$"logs/{Path.GetFileName(log)}"] = log;

        var checks = new StringBuilder();
        if (_authoringDraft is not null)
        {
            var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits);
            foreach (var edit in _authoringDraft.EditIds.Select(snapshot.Find).OfType<ArkiveModLocalEdit>())
            {
                if (ArkiveModEditCheck.Read(edit) is not { } check) continue;
                var stale = await ArkiveModEditCheck.IsStaleAsync(edit);
                checks.AppendLine($"{edit.DisplayName}: {(check.Passed ? "passes" : "FAILS")}" +
                                  (stale ? " (edited file changed since this check)" : ""));
                foreach (var problem in check.Problems) checks.AppendLine($"  {problem}");
                var manifest = edit.Entry switch
                {
                    SkeletalMeshEditWorkspaceEntry mesh => mesh.ManifestPath,
                    StaticMeshEditEntry mesh => mesh.ManifestPath,
                    _ => null,
                };
                if (manifest is not null) files[$"mesh-checks/{edit.EditId}-{Path.GetFileName(manifest)}"] = manifest;
            }
        }

        return new ArkiveProblemReportContent(summary.ToString(),
            new Dictionary<string, string>
            {
                ["mods.txt"] = mods.Length == 0 ? "No mods are listed (open Mods once to load them)." : mods.ToString(),
                ["mesh-checks.txt"] = checks.Length == 0 ? "No meshes in the mod being edited." : checks.ToString(),
            },
            files);
    }
}
