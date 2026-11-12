using System.IO;
using System.Windows.Threading;
using Arkive.Core.Meshes;
using Arkive.Core.Mods;

namespace Arkive.App;

public partial class MainWindow
{
    // Compaction waits until the user has not clicked or typed for this long, and spaces records out, so it never
    // competes with something the user started.
    private static readonly TimeSpan MeshRecordCompactionQuietTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MeshRecordCompactionRecordGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MeshRecordCompactionPollTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MeshRecordCompactionCloseWait = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _meshRecordCompactionCancellation = new();
    private Task<SkeletalMeshEditCompactionSummary>? _meshRecordCompaction;
    private DateTime _lastUserInputUtc = DateTime.UtcNow;
    private int _meshRecordCompactionLogged;

    /// <summary>
    /// Once startup has finished, moves the full skeleton report out of each older edited-mesh record into the
    /// compressed file beside it (see <see cref="SkeletalMeshEditWorkspaceStore.CompactAsync"/>), one record at a
    /// time, so later launches read a tenth of the bytes. Failures only reach the log and leave records as they were.
    /// </summary>
    private async void StartMeshRecordCompaction()
    {
        if (_meshRecordCompaction is not null) return;
        PreviewMouseDown += (_, _) => _lastUserInputUtc = DateTime.UtcNow;
        PreviewKeyDown += (_, _) => _lastUserInputUtc = DateTime.UtcNow;
        var token = _meshRecordCompactionCancellation.Token;
        _meshRecordCompaction = Task.Run(() => _skeletalMeshEditWorkspaceStore.CompactAllAsync(
            dryRun: false, WaitForMeshRecordCompactionTurnAsync, token), token);
        try
        {
            LogMeshRecordCompaction(await _meshRecordCompaction);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
        {
            Log($"Edited-mesh record compaction stopped: {exception.Message}");
        }
    }

    // Runs on the thread pool before each record: waits while an operation runs or the user is active.
    private async Task WaitForMeshRecordCompactionTurnAsync(CancellationToken token)
    {
        await Task.Delay(MeshRecordCompactionRecordGap, token);
        while (await Dispatcher.InvokeAsync(() => MeshRecordCompactionShouldWait, DispatcherPriority.Background, token))
            await Task.Delay(MeshRecordCompactionPollTime, token);
    }

    private bool MeshRecordCompactionShouldWait =>
        !ModLibraryIdle || _skeletalMeshEditableWorkspaceBusy || _skeletalMeshDeploymentBusy || _proportionBusy ||
        _workshopBusy || _outfitSwapBusy || _staticMeshWorkspaceBusy ||
        DateTime.UtcNow - _lastUserInputUtc < MeshRecordCompactionQuietTime;

    /// <summary>Called as the window closes: stops between steps and logs what was done.</summary>
    private void StopMeshRecordCompaction()
    {
        _meshRecordCompactionCancellation.Cancel();
        if (_meshRecordCompaction is not { } compaction) return;
        try
        {
            if (compaction.Wait(MeshRecordCompactionCloseWait)) LogMeshRecordCompaction(compaction.Result);
        }
        catch (AggregateException)
        {
        }
    }

    private void LogMeshRecordCompaction(SkeletalMeshEditCompactionSummary summary)
    {
        if (Interlocked.Exchange(ref _meshRecordCompactionLogged, 1) != 0) return;
        Log($"Edited-mesh record compaction {(summary.Stopped ? "stopped" : "finished")}: records={summary.Records}, " +
            $"compacted={summary.Compacted}, alreadyCompact={summary.AlreadySlim}, recentlyChanged={summary.RecentlyChanged}, " +
            $"failed={summary.Failed}, bytesSaved={summary.BytesSaved} ({FormatByteSize(Math.Max(0, summary.BytesSaved))}).");
        foreach (var problem in summary.Problems.Take(10))
            Log($"Edited-mesh record not compacted: {problem.ManifestPath}: {problem.Problem}");
    }
}
