namespace Arkive.Core.Inspection;

public enum ArtifactInspectionSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ArtifactInspectionProperty(
    string Group,
    string Name,
    string Value);

public sealed record ArtifactInspectionIssue(
    ArtifactInspectionSeverity Severity,
    string Code,
    string Message);

public sealed record ArtifactInspectionDependency(
    string Kind,
    string Reference,
    string? ResolvedPath,
    bool IsEmbedded,
    bool Exists,
    long? DeclaredBytes,
    long? ActualBytes);

public sealed record ArtifactInspectionResult(
    string FilePath,
    string Format,
    string Summary,
    IReadOnlyList<ArtifactInspectionProperty> Properties,
    IReadOnlyList<ArtifactInspectionDependency> Dependencies,
    IReadOnlyList<ArtifactInspectionIssue> Issues)
{
    public int ErrorCount => Issues.Count(issue => issue.Severity == ArtifactInspectionSeverity.Error);

    public int WarningCount => Issues.Count(issue => issue.Severity == ArtifactInspectionSeverity.Warning);
}
