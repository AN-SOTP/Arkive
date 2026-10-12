namespace Arkive.Core.Validation;

public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message);
