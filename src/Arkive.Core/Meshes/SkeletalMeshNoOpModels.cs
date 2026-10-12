using Arkive.Core.Inspection;

namespace Arkive.Core.Meshes;

public sealed record SkeletalMeshNoOpRequest(
    SkeletalMeshReconnaissanceReport Reconnaissance,
    string ReconnaissanceJsonPath,
    string OutputDirectory);

public sealed record SkeletalMeshNoOpManifest(
    int SchemaVersion,
    string ProofId,
    DateTimeOffset CreatedAt,
    string SourcePackagePath,
    string PhysicalPackageName,
    string PackageAlias,
    long SourcePackageLength,
    long SourcePackageLastWriteUtcTicks,
    string SourcePackageSha256,
    string ProtectionProviderName,
    int SourceExportIndex,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SerialSize,
    long PropertyAndPrefixBytes,
    long NativeDataBytes,
    string NativeParserProfile,
    int VerifiedNativeRangeCount,
    string SourceExportSha256,
    string ReconstructedExportSha256,
    string ReconstructedExportRelativePath,
    bool ByteIdentical,
    long FirstDifferenceOffset,
    string ReconnaissanceJsonPath,
    double DurationSeconds);

public sealed record SkeletalMeshNoOpResult(
    string OutputDirectory,
    string ExportPath,
    string ManifestPath,
    SkeletalMeshNoOpManifest Manifest,
    TimeSpan Duration);

public sealed class SkeletalMeshNoOpRefusalException : NotSupportedException
{
    public SkeletalMeshNoOpRefusalException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}
