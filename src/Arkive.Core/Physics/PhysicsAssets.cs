using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Particles;

namespace Arkive.Core.Physics;

public enum PhysicsShapeKind
{
    Sphere,
    Box,
    Capsule,
}

/// <summary>
/// One collision shape of one body of a physics asset, in its bone's space (centimetres, the game's axes).
/// </summary>
/// <param name="Body">The body's place in the asset's body list, which its collision table counts by.</param>
/// <param name="Fixed">A body moved by the animation (the character's own body), not simulated.</param>
/// <param name="Element">The shape's place among its body's shapes of the same kind.</param>
/// <param name="Transform">Shape to bone: rows are the shape's X, Y and Z axes, then its centre. A capsule's length runs along Z.</param>
/// <param name="TransformOffset">Where the transform's 16 floats start in the export.</param>
/// <param name="RadiusOffset">Where the radius float sits in the export (-1 for a box).</param>
public sealed record PhysicsShape(
    int Body,
    string Bone,
    bool Fixed,
    PhysicsShapeKind Kind,
    int Element,
    AssetCatalogEntry Export,
    string ExportSha256,
    Matrix4x4 Transform,
    float Radius,
    float Length,
    int TransformOffset,
    int RadiusOffset);

/// <summary>A character's physics asset: every shape of every body, in body order.</summary>
public sealed record PhysicsAssetReport(
    string PackagePath,
    string PackageSha256,
    AssetCatalogEntry Asset,
    string AssetExportSha256,
    IReadOnlyList<PhysicsShape> Shapes);

/// <summary>
/// Reads and rewrites a character's physics asset (pc_wr_f_00_physics). The class's look names one for every outfit:
/// hair, skirt and cape bones are simulated bodies, and they collide with fixed capsules on the body's own bones
/// (pelvis, spine, chest, thighs…). Each body is an RB_BodySetup export: a property stream (bone name, flags, the
/// shapes as KAggGeom arrays) followed by an empty cache, so the game builds the shapes from these values when it
/// loads them and a new radius or centre is a same-size write. The client stores each row of a shape's matrix with
/// its fourth value first.
/// </summary>
public static class PhysicsAssets
{
    private const int MatrixFloats = 16;

    public static PhysicsAssetReport Inspect(
        string packagePath, string packageSha256, string assetObjectName, string? assetObjectPath,
        IChunkProtectionProvider protectionProvider, CancellationToken cancellationToken = default)
    {
        var catalog = new AssetCatalogReader(protectionProvider: protectionProvider).Read(packagePath);
        var asset = FindAsset(catalog, packagePath, assetObjectName, assetObjectPath);
        using var logical = new LogicalPackageStream(catalog.Summary, protectionProvider);
        var assetBytes = ParticleEffects.Read(logical, asset);
        var assetProperties = ParticlePropertyStream.Read(assetBytes, catalog.Names)
                              ?? throw new InvalidDataException($"{asset.ObjectName} does not read as a physics asset.");
        var bodies = assetProperties.FirstOrDefault(property => property.Name.Equals("bodysetup", StringComparison.OrdinalIgnoreCase)) is { } list
            ? ParticlePropertyStream.ReadObjectArray(assetBytes, list)
            : null;
        if (bodies is null) throw new InvalidDataException($"{asset.ObjectName} lists no bodies.");
        var exports = catalog.Assets.ToDictionary(entry => entry.ExportIndex);
        var shapes = new List<PhysicsShape>();
        for (var body = 0; body < bodies.Count; body++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!exports.TryGetValue(bodies[body], out var export) ||
                !export.ClassName.Equals("rb_bodysetup", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Body {body} of {asset.ObjectName} is not a body setup in this package.");
            var bytes = ParticleEffects.Read(logical, export);
            var properties = ParticlePropertyStream.ReadLeading(bytes, catalog.Names, 4, out _)
                             ?? throw new InvalidDataException($"{export.ObjectName} does not read as a body setup.");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var bone = properties.FirstOrDefault(property => property.Name.Equals("bonename", StringComparison.OrdinalIgnoreCase)) is { Size: 8 } name
                ? NameAt(bytes, name.ValueOffset, catalog.Names)
                : throw new InvalidDataException($"{export.ObjectName} names no bone.");
            var isFixed = properties.Any(property => property.Name.Equals("bfixed", StringComparison.OrdinalIgnoreCase) &&
                                                     property.Type == "boolproperty" && bytes[property.ValueOffset] != 0);
            if (properties.FirstOrDefault(property => property.Name.Equals("agggeom", StringComparison.OrdinalIgnoreCase)) is not { } geometry)
                continue;
            foreach (var array in geometry.Children)
            {
                var kind = array.Name.ToLowerInvariant() switch
                {
                    "sphereelems" => PhysicsShapeKind.Sphere,
                    "boxelems" => PhysicsShapeKind.Box,
                    "sphylelems" => PhysicsShapeKind.Capsule,
                    _ => (PhysicsShapeKind?)null,
                };
                if (kind is null) continue;
                var elements = ParticlePropertyStream.ReadStructArray(bytes, array, catalog.Names)
                               ?? throw new InvalidDataException($"{export.ObjectName}'s {array.Name} do not read as shapes.");
                for (var element = 0; element < elements.Count; element++)
                {
                    var values = elements[element];
                    var tm = values.FirstOrDefault(value => value.Name.Equals("tm", StringComparison.OrdinalIgnoreCase));
                    if (tm is null || tm.Size != MatrixFloats * 4)
                        throw new InvalidDataException($"{export.ObjectName}'s shape {element} has no transform.");
                    var radius = values.FirstOrDefault(value => value.Name.Equals("radius", StringComparison.OrdinalIgnoreCase));
                    var length = values.FirstOrDefault(value => value.Name.Equals("length", StringComparison.OrdinalIgnoreCase));
                    shapes.Add(new PhysicsShape(body, bone, isFixed, kind.Value, element, export, hash, ReadTransform(bytes, tm.ValueOffset),
                        radius is { Type: "floatproperty" } ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(radius.ValueOffset)) : 0,
                        length is { Type: "floatproperty" } ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(length.ValueOffset)) : 0,
                        tm.ValueOffset, radius is { Type: "floatproperty" } ? radius.ValueOffset : -1));
                }
            }
        }
        return new PhysicsAssetReport(Path.GetFullPath(packagePath), packageSha256, asset,
            Convert.ToHexString(SHA256.HashData(assetBytes)), shapes);
    }

    internal static AssetCatalogEntry FindAsset(AssetCatalog catalog, string packagePath, string objectName, string? objectPath)
    {
        var assets = catalog.Assets.Where(asset => asset.ClassName.Equals("physicsasset", StringComparison.OrdinalIgnoreCase) &&
                                                    asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (objectPath is not null)
            assets = assets.Where(asset => asset.ObjectPath.Equals(objectPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        return assets.Length == 1
            ? assets[0]
            : throw new InvalidDataException($"{objectName} is {(assets.Length == 0 ? "not" : "more than once")} a physics asset in {Path.GetFileName(packagePath)}.");
    }

    /// <summary>
    /// The patches that write <paramref name="fit"/>'s changes into the shapes of <paramref name="report"/> it names:
    /// a capsule or sphere's radius, and its centre (the transform's last row).
    /// </summary>
    public static IReadOnlyList<ParticleEffectPatch> Patches(PhysicsAssetReport report, BodyPhysicsFit fit)
    {
        var patches = new List<ParticleEffectPatch>();
        foreach (var change in fit.Shapes)
        {
            var shape = report.Shapes.FirstOrDefault(candidate => candidate.Kind == change.Kind && candidate.Element == change.Element &&
                                                                  candidate.Bone.Equals(change.Bone, StringComparison.OrdinalIgnoreCase));
            if (shape is null) continue;
            if (shape.RadiusOffset >= 0 && Math.Abs(change.RadiusChange) > 1e-4f)
                patches.Add(Patch(shape, "radius", shape.RadiusOffset, [shape.Radius], [Math.Max(shape.Radius + change.RadiusChange, shape.Radius * 0.5f)]));
            if (change.CentreShift.LengthSquared() > 1e-8f)
            {
                var centre = shape.Transform.Translation;
                var moved = centre + change.CentreShift;
                // Each stored row is (w, x, y, z): the centre's x, y and z are floats 13 to 15.
                patches.Add(Patch(shape, "tm", shape.TransformOffset + 13 * 4, [centre.X, centre.Y, centre.Z], [moved.X, moved.Y, moved.Z]));
            }
        }
        return patches;
    }

    private static ParticleEffectPatch Patch(PhysicsShape shape, string property, int offset, float[] original, float[] value) => new(
        shape.Export.ObjectPath, shape.Export.ClassName, shape.Export.ExportIndex, shape.Export.SerialOffset, shape.Export.SerialSize,
        shape.ExportSha256, property, offset, original, value);

    private static Matrix4x4 ReadTransform(byte[] bytes, int offset)
    {
        float F(int index) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + index * 4));
        return new Matrix4x4(
            F(1), F(2), F(3), F(0),
            F(5), F(6), F(7), F(4),
            F(9), F(10), F(11), F(8),
            F(13), F(14), F(15), F(12));
    }

    private static string NameAt(byte[] bytes, int offset, IReadOnlyList<UnrealNameEntry> names) =>
        new UnrealNameReference(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4))).Resolve(names);
}
