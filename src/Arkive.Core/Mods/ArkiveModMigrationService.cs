namespace Arkive.Core.Mods;

public sealed record ArkiveModMigratedPackage(
    string PackageProjectId,
    string PackageName,
    string ModId,
    string ModFilePath,
    bool Enabled,
    int AssetCount,
    IReadOnlyList<string> Warnings);

public sealed record ArkiveModMigrationSkip(string PackageProjectId, string PackageName, string Reason);

public sealed record ArkiveModMigrationResult(
    IReadOnlyList<ArkiveModMigratedPackage> Migrated,
    IReadOnlyList<ArkiveModMigrationSkip> Skipped,
    IReadOnlyList<ArkiveModSelectionChange> SelectionChanges);

public sealed class ArkiveModMigrationService
{
    private readonly ArkiveModApplyContext _context;
    private readonly ArkiveModAuthoringService _authoring;

    public ArkiveModMigrationService(ArkiveModApplyContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _authoring = new ArkiveModAuthoringService(context.Library, context.Edits);
    }

    public async Task<ArkiveModMigrationResult> MigratePackageProjectsAsync(
        string? author = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var migrated = new List<ArkiveModMigratedPackage>();
        var skipped = new List<ArkiveModMigrationSkip>();
        var packages = (await _context.Packages.LoadAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(package => package.Manifest.PackageAlias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await _context.Library.InferGamePackagesDirectoryAsync(
            packages.Select(package => package.Manifest.SourcePackagePath), cancellationToken).ConfigureAwait(false);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(_context.Edits, cancellationToken).ConfigureAwait(false);
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = ArkiveModAuthoringService.PackageDisplayName(package);
            var library = await _context.Library.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (library.AuthoredFor(package.Manifest.PackageProjectId) is not null)
            {
                skipped.Add(new ArkiveModMigrationSkip(package.Manifest.PackageProjectId, name,
                    "Already has an .arkive mod."));
                continue;
            }
            var checkedEdits = snapshot.DraftsFor(package.Manifest.SourcePackagePath, library);
            if (checkedEdits.Count == 0)
            {
                skipped.Add(new ArkiveModMigrationSkip(package.Manifest.PackageProjectId, name,
                    "No checked, unassigned asset projects."));
                continue;
            }
            progress?.Report($"Saving {name} as an .arkive mod ({checkedEdits.Count:N0} asset(s))...");
            var warnings = new List<string>();
            var captured = new List<ArkiveModCapturedAsset>();
            foreach (var edit in checkedEdits.GroupBy(edit => edit.TargetKey).Select(group => group.First()))
            {
                try
                {
                    captured.Add(await ArkiveModAuthoringService.CaptureAsync(edit, cancellationToken: cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
                {
                    warnings.Add($"{edit.DisplayName} was left as a local draft: {exception.Message}");
                }
            }
            if (captured.Count == 0)
            {
                skipped.Add(new ArkiveModMigrationSkip(package.Manifest.PackageProjectId, name,
                    string.Join(" ", warnings)));
                continue;
            }
            ArkiveModLibraryEntry mod;
            try
            {
                var installed = await _context.FindActiveDeploymentAsync(package, cancellationToken)
                    .ConfigureAwait(false) is not null;
                mod = await _authoring.CreateModAsync(
                        new ArkiveModMetadata(name, "1.0.0", author, package.Manifest.Notes),
                        captured, ArkiveModOrigin.Migrated, enabled: installed,
                        package.Manifest.PackageProjectId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (ArkiveModApplyService.IsOperational(exception))
            {
                skipped.Add(new ArkiveModMigrationSkip(package.Manifest.PackageProjectId, name, exception.Message));
                continue;
            }
            migrated.Add(new ArkiveModMigratedPackage(
                package.Manifest.PackageProjectId, name, mod.ModId, _context.Library.GetModFilePath(mod),
                mod.Enabled, captured.Count, warnings));
        }

        await _context.Library.UpdateAsync(current => current with { PackageProjectsMigrated = true }, cancellationToken)
            .ConfigureAwait(false);
        var changes = await ArkiveModSelectionService.SynchronizeAsync(_context.Library, _context.Edits, cancellationToken)
            .ConfigureAwait(false);
        return new ArkiveModMigrationResult(migrated, skipped, changes);
    }
}
