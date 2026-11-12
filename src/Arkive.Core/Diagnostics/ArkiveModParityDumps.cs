using System.Text.Json;
using System.Text.Json.Serialization;
using Arkive.Core.Mods;

namespace Arkive.Core.Diagnostics;

/// <summary>
/// Complete, sorted dumps of the Mods list status and of the Apply plan, for comparing one data folder before and after
/// a change to Arkive. They hold no times and no data-folder location (paths are relative to the data folder, game paths
/// start with &lt;game&gt;), so two dumps of the same state are byte-identical and two mirrors of one folder compare equal.
/// Building them only reads.
/// </summary>
public static class ArkiveModParityDumps
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Every mod and every asset as the Mods list shows them.</summary>
    /// <param name="apply">When given, game files changed outside Arkive are looked for, as the Mods list does.</param>
    public static async Task<string> ListAsync(
        ArkiveModApplyContext context,
        ArkiveModApplyService? apply,
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var library = await context.Library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var paths = new Relative(dataRoot, library.GamePackagesDirectory);
        var snapshot = await ArkiveModEditSnapshot.LoadAsync(context.Edits, cancellationToken).ConfigureAwait(false);
        var projects = await context.Packages.LoadAsync(cancellationToken).ConfigureAwait(false);
        var active = new Dictionary<string, GuidedTexturePackageRevision?>(StringComparer.Ordinal);
        foreach (var physical in projects.Select(package => Path.GetFileName(package.Manifest.SourcePackagePath))
                     .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ArkiveModApplyService.FindPackageProject(physical, library.GamePackagesDirectory, projects) is not { } project)
                continue;
            active[ArkiveModFileService.PhysicalPackageKey(physical)] =
                (await context.FindActiveDeploymentAsync(project, cancellationToken, reuseKnownHashes: true)
                    .ConfigureAwait(false))?.Revision;
        }
        var outside = apply is null
            ? []
            : await apply.FindOutsideChangesAsync(cancellationToken).ConfigureAwait(false);
        var statuses = await ArkiveModApplyService.DescribeAsync(
            context.Library, library, snapshot, active, cancellationToken,
            outside.Select(change => change.PhysicalPackageName).ToArray(),
            context.GameDataFor(library)?.Archive.Current(),
            library.Mods.Any(mod => mod.Manifest.Looks is not null) ? context.LooksFor(library)?.Archive.Current() : null)
            .ConfigureAwait(false);
        var dump = new
        {
            GamePackagesDirectory = paths.PathOf(library.GamePackagesDirectory),
            library.PackageProjectsMigrated,
            ModCount = statuses.Count,
            EnabledCount = statuses.Count(status => status.Mod.Enabled),
            InGameCount = statuses.Count(status => status.InGame),
            WorkingFileCount = snapshot.Edits.Count,
            BoundWorkingFileCount = snapshot.Edits.Count(edit => library.FindOwner(edit.EditId) is not null),
            InstalledPackages = active.Where(pair => pair.Value is not null).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray(),
            OutsideChanges = outside
                .Select(change => new
                {
                    change.PhysicalPackageName,
                    change.PackageAlias,
                    GamePath = paths.PathOf(change.GamePath),
                    change.GameSha256,
                    CleanSha256s = change.CleanSha256s.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    ChangedObjects = change.ChangedObjects.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                })
                .OrderBy(change => change.PhysicalPackageName, StringComparer.OrdinalIgnoreCase).ToArray(),
            Mods = statuses.OrderBy(status => status.Mod.ModId, StringComparer.OrdinalIgnoreCase).Select(status => new
            {
                status.Mod.ModId,
                Name = paths.Text(status.Mod.Manifest.Name),
                status.Mod.Manifest.Version,
                status.Mod.FileName,
                status.Mod.FileSha256,
                status.Mod.FileBytes,
                status.Mod.Enabled,
                status.Mod.Priority,
                status.Mod.Origin,
                status.Mod.AuthoredForPackageProjectId,
                status.FileMissing,
                State = paths.Text(status.State),
                status.InGame,
                status.StillInGame,
                status.OutsideChange,
                ConflictSummary = paths.Text(status.ConflictSummary),
                status.WinningCount,
                status.OverriddenCount,
                status.TiedCount,
                status.ModifiedLocally,
                status.GameData,
                status.Looks,
                Assets = status.Assets.OrderBy(asset => asset.Asset.AssetId, StringComparer.OrdinalIgnoreCase).Select(asset => new
                {
                    asset.Asset.AssetId,
                    asset.Asset.Kind,
                    DisplayName = paths.Text(asset.Asset.DisplayName),
                    asset.Asset.Target.PackageAlias,
                    asset.Asset.Target.PhysicalPackageName,
                    asset.Asset.Target.ClassName,
                    asset.Asset.Target.ObjectName,
                    asset.Asset.Target.ObjectPath,
                    asset.Asset.Target.SourcePackageSha256,
                    asset.Setup,
                    asset.Outcome,
                    Competitor = asset.Competitor is { } competitor
                        ? new { competitor.ModId, competitor.Priority, competitor.Enabled }
                        : null,
                    asset.ModifiedLocally,
                    asset.InGame,
                    Binding = asset.Binding is { } binding
                        ? new
                        {
                            binding.EditId,
                            binding.Kind,
                            SourcePackagePath = paths.PathOf(binding.SourcePackagePath),
                            binding.PackedContentSha256,
                        }
                        : null,
                    Edit = asset.Edit is { } edit
                        ? new
                        {
                            edit.Kind,
                            ManifestPath = paths.PathOf(edit.ManifestPath),
                            SourcePackagePath = paths.PathOf(edit.SourcePackagePath),
                            edit.ObjectName,
                            edit.Included,
                        }
                        : null,
                }).ToArray(),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(dump, JsonOptions) + "\n";
    }

    /// <summary>The Apply plan: every package, its action and reason, who contributes to it and what must be set up.</summary>
    public static async Task<string> PlanAsync(
        ArkiveModApplyService apply,
        ArkiveModLibrary library,
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(library);
        var manifest = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var paths = new Relative(dataRoot, manifest.GamePackagesDirectory);
        var plan = await apply.PlanAsync(cancellationToken).ConfigureAwait(false);
        var dump = new
        {
            plan.CanApply,
            plan.HasChanges,
            Summary = paths.Text(plan.Summary),
            Fingerprint = paths.Text(plan.Fingerprint),
            BlockingProblems = plan.BlockingProblems.Select(paths.Text).Order(StringComparer.Ordinal).ToArray(),
            Warnings = plan.Warnings.Select(paths.Text).Order(StringComparer.Ordinal).ToArray(),
            ModsNeedingClientUpdate = plan.ModsNeedingClientUpdate.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            BlockingTies = plan.Resolution.BlockingTies.Select(tie => $"{tie.PhysicalPackageName}:{tie.ObjectName}")
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Packages = plan.Packages.OrderBy(package => package.PhysicalPackageName, StringComparer.OrdinalIgnoreCase).Select(package => new
            {
                package.PhysicalPackageName,
                package.PackageAlias,
                SourcePackagePath = paths.PathOf(package.SourcePackagePath),
                package.PackageProjectId,
                package.Action,
                package.RestoreFirst,
                Describe = paths.Text(package.Describe),
                Reason = paths.Text(package.Reason),
                ActiveBundleManifestPath = paths.PathOf(package.ActiveBundleManifestPath),
                package.ContentFingerprint,
                Contributors = package.Contributors
                    .OrderBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.AssetName, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new
                    {
                        item.ModId,
                        ModName = paths.Text(item.ModName),
                        item.Priority,
                        AssetName = paths.Text(item.AssetName),
                        item.Kind,
                        item.ObjectName,
                        item.IsDraft,
                    }).ToArray(),
                Setups = package.Setups
                    .OrderBy(item => item.Asset.AssetId, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new
                    {
                        item.ModId,
                        ModName = paths.Text(item.ModName),
                        item.Asset.AssetId,
                        item.Asset.Kind,
                        item.Asset.Target.ObjectName,
                        ReplacesEditId = item.ReplacesBinding?.EditId,
                        Reason = paths.Text(item.Reason),
                    }).ToArray(),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(dump, JsonOptions) + "\n";
    }

    /// <summary>Makes paths and messages independent of where the data folder and the game are.</summary>
    private sealed class Relative
    {
        private readonly string _root;
        private readonly string? _game;

        public Relative(string dataRoot, string? gamePackagesDirectory)
        {
            _root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);
            // The game folder is the one holding EFGame, three levels above EFGame\ReleasePC\Packages.
            _game = gamePackagesDirectory is null
                ? null
                : Path.GetFullPath(Path.Combine(gamePackagesDirectory, "..", "..", "..")).TrimEnd(Path.DirectorySeparatorChar);
        }

        public string? PathOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            { return Text(path); }
            if (Under(full, _root)) return full.Length == _root.Length ? "." : full[(_root.Length + 1)..];
            if (_game is not null && Under(full, _game))
                return full.Length == _game.Length ? "<game>" : "<game>" + full[_game.Length..];
            return full;
        }

        public string Text(string text)
        {
            var result = text.Replace(_root + System.IO.Path.DirectorySeparatorChar, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(_root, ".", StringComparison.OrdinalIgnoreCase);
            return _game is null ? result : result.Replace(_game, "<game>", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Under(string full, string root) =>
            full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
