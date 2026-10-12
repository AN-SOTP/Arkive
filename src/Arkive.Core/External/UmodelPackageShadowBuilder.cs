namespace Arkive.Core.External;

public sealed record UmodelPackageShadowResult(
    string ShadowDirectory,
    string ModelPackagePath,
    string ReplacementPackagePath,
    IReadOnlyList<string> StagedPackagePaths,
    long CopiedBytes);

/// <summary>
/// Builds a disposable package directory containing only the dependency closure
/// UModel observed for one model. The replacement is written under the clean
/// package's physical hashed filename, preventing UModel from resolving a second,
/// clean copy of that package.
/// </summary>
public static class UmodelPackageShadowBuilder
{
    public static async Task<UmodelPackageShadowResult> BuildAsync(
        string sourcePackageRoot,
        string modelPackagePath,
        IReadOnlyList<string> physicalPackageNames,
        string replacementPackagePath,
        string replacementPhysicalName,
        string shadowDirectory,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = Path.GetFullPath(sourcePackageRoot);
        var modelPath = Path.GetFullPath(modelPackagePath);
        var replacementPath = Path.GetFullPath(replacementPackagePath);
        var shadowRoot = Path.GetFullPath(shadowDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"The source package root was not found: {sourceRoot}");
        }
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("The preview model package was not found.", modelPath);
        }
        if (!File.Exists(replacementPath))
        {
            throw new FileNotFoundException("The staged replacement package was not found.", replacementPath);
        }

        var replacementFileName = NormalizePackageFileName(replacementPhysicalName);
        var dependencyNames = physicalPackageNames
            .Append(Path.GetFileName(modelPath))
            .Select(NormalizePackageFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!dependencyNames.Contains(replacementFileName, StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"The selected model did not load {replacementFileName}. " +
                "Arkive will not claim that this texture can be previewed on that model.");
        }

        Directory.CreateDirectory(shadowRoot);
        if (Directory.EnumerateFileSystemEntries(shadowRoot).Any())
        {
            throw new IOException("The UModel package-shadow directory must be empty.");
        }

        var staged = new List<string>(dependencyNames.Length);
        long copiedBytes = 0;
        foreach (var fileName in dependencyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = fileName.Equals(replacementFileName, StringComparison.OrdinalIgnoreCase)
                ? replacementPath
                : Path.Combine(sourceRoot, fileName);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    $"A package reported by UModel's dependency probe was not found: {fileName}", source);
            }

            var destination = Path.Combine(shadowRoot, fileName);
            await CopyFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
            copiedBytes += new FileInfo(destination).Length;
            staged.Add(destination);
        }

        var shadowModelPath = Path.Combine(shadowRoot, Path.GetFileName(modelPath));
        return new UmodelPackageShadowResult(
            shadowRoot,
            shadowModelPath,
            Path.Combine(shadowRoot, replacementFileName),
            staged,
            copiedBytes);
    }

    private static string NormalizePackageFileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var fileName = Path.GetFileName(value.Trim());
        if (!fileName.EndsWith(".upk", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".upk";
        }
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("A dependency package name is invalid.", nameof(value));
        }
        return fileName;
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
