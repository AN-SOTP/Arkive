using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Arkive.Core.Mods;

namespace Arkive.App;

public partial class MainWindow
{
    private CancellationTokenSource? _authoringExtractionCancellation;
    private readonly DispatcherTimer _authoringProgressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _authoringExtractionWatch = new();
    private string _authoringExtractionStage = string.Empty;
    private int _authoringExtractionCompleted;
    private int _authoringExtractionTotal;

    private sealed record AuthoringAddSession(CancellationToken CancellationToken,
        Dictionary<string, ArkiveModLocalEdit> Existing,
        Dictionary<string, string> CleanPackages);

    private static string AuthoringAssetKey(string packagePath, string objectName) =>
        Path.GetFileName(packagePath) + "\0" + objectName;

    private async Task<AuthoringAddSession> CreateAuthoringAddSessionAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_arkiveModEdits, cancellationToken);
        var existing = new Dictionary<string, ArkiveModLocalEdit>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _authoringDraft!.EditIds.Concat(_authoringDraft.ReferenceEditIds))
            if (snapshot.Find(id) is { } edit)
                existing.TryAdd(AuthoringAssetKey(edit.SourcePackagePath, edit.ObjectPath ?? edit.ObjectName), edit);
        return new(cancellationToken, existing, new(StringComparer.OrdinalIgnoreCase));
    }

    private async Task RunAuthoringExtractionAsync(Func<CancellationToken, Task> operation)
    {
        await RunAuthoringOperationAsync(async () =>
        {
            await PersistAuthoringDraftAsync();
            using var cancellation = new CancellationTokenSource();
            _authoringExtractionCancellation = cancellation;
            _authoringExtractionCompleted = 0;
            _authoringExtractionTotal = 0;
            _authoringExtractionWatch.Restart();
            _authoringProgressTimer.Tick += AuthoringProgressTimer_Tick;
            _authoringProgressTimer.Start();
            AuthoringOperationPanel.Visibility = Visibility.Visible;
            AuthoringCancelButton.IsEnabled = true;
            ReportAuthoringExtraction("Preparing selected asset");
            UpdateAuthoringActions();
            UpdateWorkshopActionState();
            try { await operation(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                AuthoringStatusText.Text = "Cancelled. Assets already added to the draft have been retained. No game files changed.";
                Log($"Authoring extraction cancelled after {_authoringExtractionWatch.Elapsed.TotalSeconds:N1}s.");
            }
            finally
            {
                Log($"Authoring extraction finished: elapsed={_authoringExtractionWatch.Elapsed.TotalSeconds:N1}s, " +
                    $"stage={_authoringExtractionStage}, completed={_authoringExtractionCompleted}/{_authoringExtractionTotal}.");
                _authoringProgressTimer.Stop();
                _authoringProgressTimer.Tick -= AuthoringProgressTimer_Tick;
                _authoringExtractionWatch.Stop();
                _authoringExtractionCancellation = null;
                AuthoringOperationPanel.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void ReportAuthoringExtraction(string stage, int? completed = null, int? total = null)
    {
        _authoringExtractionStage = stage;
        if (completed is { } count) _authoringExtractionCompleted = count;
        if (total is { } maximum) _authoringExtractionTotal = maximum;
        AuthoringOperationProgress.IsIndeterminate = _authoringExtractionTotal == 0;
        AuthoringOperationProgress.Maximum = Math.Max(1, _authoringExtractionTotal);
        AuthoringOperationProgress.Value = _authoringExtractionCompleted;
        UpdateAuthoringExtractionText();
        Log($"Authoring extraction: {stage}; {_authoringExtractionCompleted}/{_authoringExtractionTotal}.");
    }

    private void UpdateAuthoringExtractionText()
    {
        var count = _authoringExtractionTotal == 0 ? string.Empty :
            $" · {_authoringExtractionCompleted}/{_authoringExtractionTotal} assets";
        AuthoringOperationText.Text = $"{_authoringExtractionStage}{count} · {_authoringExtractionWatch.Elapsed:mm\\:ss}";
    }

    private void AuthoringProgressTimer_Tick(object? sender, EventArgs e) => UpdateAuthoringExtractionText();

    private void AuthoringCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _authoringExtractionCancellation?.Cancel();
        AuthoringCancelButton.IsEnabled = false;
        ReportAuthoringExtraction("Cancelling — finishing the current safe checkpoint");
    }
}
