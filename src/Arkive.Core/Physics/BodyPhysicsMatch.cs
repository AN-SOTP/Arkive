using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Arkive.Core.Archives;

namespace Arkive.Core.Physics;

/// <summary>A character physics asset by package alias and object name (pc_wr_f_00, pc_wr_f_00_physics).</summary>
public sealed record BodyPhysicsTarget(string PackageAlias, string ObjectName);

/// <summary>
/// Which physics asset the characters wearing a piece use. The game names it in each class's look, one per base body
/// and sex (every Slayer and Female Warrior class uses pc_wr_f_00_physics), and a piece's class code tells whose
/// outfit it is, the same way <see cref="Proportions.BodyProfiles.ForObjectName"/> tells its body.
/// </summary>
public static partial class BodyPhysicsTargets
{
    /// <summary>The physics asset worn with <paramref name="objectName"/>, or null for a mesh no player class wears (NPCs).</summary>
    public static BodyPhysicsTarget? ForPiece(string objectName)
    {
        var match = PlayerMeshName().Match(objectName);
        if (!match.Success) return null;
        var code = match.Groups["code"].Value.ToLowerInvariant();
        var sex = match.Groups["sex"].Value.ToLowerInvariant();
        var body = (code[0], sex) switch
        {
            ('w', "f") => "pc_wr_f",
            ('w', _) => "pc_wr",
            ('f', "m") => "pc_ft_m",
            ('f', _) => "pc_ft",
            ('g', "f") => "pc_gn_f",
            ('g', _) => "pc_gn",
            ('d', _) when code is "dk" or "ddk" => "pc_dk",
            ('d', _) => "pc_dl",
            ('m', _) => "pc_mg",
            ('s', "m") => "pc_sp_m",
            ('s', _) => "pc_sp",
            _ => null,
        };
        return body is null ? null : new BodyPhysicsTarget($"{body}_00", $"{body}_00_physics");
    }

    [GeneratedRegex(@"^pc_(?<code>[a-z]{2,3})(?:_(?<sex>[mf]))?_(?<rest>.+)_sk$", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerMeshName();
}

/// <summary>
/// Matches the physics of every class that wears a set of reshaped pieces: one fitter per physics asset, fed each
/// piece before and after its reshape (from several threads at once), then one fit per asset.
/// </summary>
public sealed class BodyPhysicsMatch
{
    private readonly Dictionary<BodyPhysicsTarget, (PhysicsAssetReport Report, BodyPhysicsFitter Fitter)> _assets;

    private BodyPhysicsMatch(Dictionary<BodyPhysicsTarget, (PhysicsAssetReport, BodyPhysicsFitter)> assets, IReadOnlyList<BodyPhysicsTarget> missing)
    {
        _assets = assets;
        Missing = missing;
    }

    /// <summary>Physics assets the pieces are worn with that could not be found in the game files.</summary>
    public IReadOnlyList<BodyPhysicsTarget> Missing { get; }

    /// <summary>
    /// Reads the physics asset of every piece in <paramref name="pieceNames"/> from its clean package, which
    /// <paramref name="findPackage"/> finds (null when it is not in the game).
    /// </summary>
    public static async Task<BodyPhysicsMatch> PrepareAsync(
        IEnumerable<string> pieceNames,
        Func<BodyPhysicsTarget, CancellationToken, Task<string?>> findPackage,
        IChunkProtectionProvider protectionProvider,
        CancellationToken cancellationToken = default)
    {
        var assets = new Dictionary<BodyPhysicsTarget, (PhysicsAssetReport, BodyPhysicsFitter)>();
        var missing = new List<BodyPhysicsTarget>();
        foreach (var target in pieceNames.Select(BodyPhysicsTargets.ForPiece).OfType<BodyPhysicsTarget>().Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await findPackage(target, cancellationToken).ConfigureAwait(false) is not { } package)
            {
                missing.Add(target);
                continue;
            }
            var report = await Task.Run(async () =>
            {
                await using var stream = File.OpenRead(package);
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                return PhysicsAssets.Inspect(package, sha, target.ObjectName, null, protectionProvider, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            assets[target] = (report, new BodyPhysicsFitter(report));
        }
        return new BodyPhysicsMatch(assets, missing);
    }

    /// <summary>Adds a reshaped piece to the fit of the physics asset it is worn with.</summary>
    public void Add(string pieceObjectName, byte[] before, byte[] after)
    {
        if (BodyPhysicsTargets.ForPiece(pieceObjectName) is { } target && _assets.TryGetValue(target, out var asset))
            asset.Fitter.Add(before, after);
    }

    /// <summary>Every physics asset whose body shapes the pieces changed, with its fit.</summary>
    public IReadOnlyList<(BodyPhysicsTarget Target, PhysicsAssetReport Report, BodyPhysicsFit Fit)> Results() =>
        _assets.Select(pair => (pair.Key, pair.Value.Report, pair.Value.Fitter.Result()))
            .Where(item => !item.Item3.IsEmpty && PhysicsAssets.Patches(item.Report, item.Item3).Count > 0)
            .ToArray();
}
