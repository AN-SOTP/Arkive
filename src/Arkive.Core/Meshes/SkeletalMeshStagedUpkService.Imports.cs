using System.Diagnostics;
using Arkive.Core.Packages;
using Arkive.Core.Validation;

namespace Arkive.Core.Meshes;

/// <summary>References to other packages' objects that one package should be able to name.</summary>
public sealed record PackageImportsStagedUpkRequest(
    string SourcePackagePath,
    string ExpectedSourcePackageSha256,
    string PhysicalPackageName,
    string PackageAlias,
    IReadOnlyList<PackageObjectReference> References,
    string OutputDirectory);

public sealed record PackageImportsStagedUpkManifest(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CreatedAt,
    string SourcePackagePath,
    string SourcePackageSha256,
    string OutputUpkRelativePath,
    string OutputPackageSha256,
    string PhysicalPackageName,
    string PackageAlias,
    int SourceNameCount,
    int SourceImportCount,
    int AddedNames,
    int AddedImports,
    long SourceHeaderEnd,
    long OutputHeaderEnd,
    IReadOnlyDictionary<string, int> Imports,
    IReadOnlyList<PackageSummaryPatch> SummaryPatches,
    IReadOnlyList<PackageRelocatedExport> Relocated,
    bool OriginalTablesRetained,
    bool ExportsRetained,
    bool AllUnplannedSourceBytesIdentical,
    bool AllEncodedBlocksDecodeBack,
    bool StructuralValidationPassed,
    IReadOnlyList<SkeletalMeshStagedUpkChunkEvidence> Chunks,
    IReadOnlyList<SkeletalMeshStagedUpkTablePatchEvidence> ChunkTablePatches,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    double DurationSeconds);

public sealed record PackageImportsStagedUpkResult(
    string OutputDirectory,
    string ManifestPath,
    string OutputUpkPath,
    PackageImportsStagedUpkManifest Manifest);

public sealed partial class SkeletalMeshStagedUpkService
{
    public const string ImportsManifestFileName = "arkive-package-imports-staged-upk.json";

    /// <summary>
    /// Adds imports for other packages' objects (and the names they need) to a staged copy of a package. Every original
    /// name and import keeps its index; the header grows in place and the few exports it now covers move, unchanged, to
    /// the end. The copy is checked against the source: original tables and every export's bytes identical, only the
    /// header and the planned summary fields changed, each reference found under its planned index, blocks decode back.
    /// Returns null when the package already imports everything asked for.
    /// </summary>
    public async Task<PackageImportsStagedUpkResult?> BuildImportsAsync(
        PackageImportsStagedUpkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        if (request.References.Count == 0) Refuse("SPI010", "No references were given to add.");
        var sourcePath = RequireFile(request.SourcePackagePath, "SPI011", "source package");
        var sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!sourceHashBefore.Equals(request.ExpectedSourcePackageSha256, StringComparison.OrdinalIgnoreCase))
            Refuse("SPI012", "The cumulative source UPK does not match the requested package stage.");
        var sourceSummary = _summaryReader.Read(sourcePath);
        if (!sourceSummary.IsWesternLostArkProfile)
            Refuse("SPI013", "Only the Western Lost Ark 868/16 package profile is supported.");
        var sourceCatalog = _catalogReader.Read(sourceSummary);
        var sourceLogical = await ReadLogicalAsync(sourceSummary, cancellationToken).ConfigureAwait(false);
        PackageImportGrowthPlan? plan;
        try { plan = PackageImportGrowth.Plan(sourceCatalog, sourceLogical, request.References); }
        catch (PackageImportGrowthRefusalException exception) { throw new SkeletalMeshStagedUpkRefusalException(exception.Code, exception.Message); }
        if (plan is null) return null;

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputName = SafePhysicalName(request.PhysicalPackageName);
        var outputPath = Path.Combine(outputDirectory, outputName);
        if (Path.GetFullPath(outputPath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            Refuse("SPI014", "The staging output cannot be the source UPK.");
        var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // Every chunk the grown header touches is rebuilt; summary fields live in the uncompressed prefix.
            var changed = plan.SummaryPatches.Select(patch => patch.Offset)
                .Concat(Steps(plan.HeaderStart, plan.OutputHeaderEnd)).ToArray();
            var build = sourceSummary.CompressedChunks.Count == 0
                ? await BuildUncompressedAsync(plan.Logical, temporaryPath, cancellationToken).ConfigureAwait(false)
                : await BuildCompressedAppendAsync(sourcePath, sourceSummary, plan.Logical, changed, temporaryPath, cancellationToken)
                    .ConfigureAwait(false);
            var outputSummary = _summaryReader.Read(temporaryPath);
            var validation = PackageValidator.Validate(outputSummary);
            if (validation.Any(issue => issue.Severity == ValidationSeverity.Error))
                Refuse("SPI015", "The staged UPK failed structural validation.");
            await VerifyPhysicalChunksAsync(sourcePath, temporaryPath, outputSummary, build.Chunks, cancellationToken).ConfigureAwait(false);
            var outputLogical = await ReadLogicalAsync(outputSummary, cancellationToken).ConfigureAwait(false);
            if (!outputLogical.AsSpan().SequenceEqual(build.ExpectedLogical))
                Refuse("SPI016", "The staged UPK did not decode to the expected logical image.");

            // Only the header, the planned summary fields and the chunk table may differ from the source.
            var metadata = plan.SummaryPatches.Select(patch => (patch.Offset, 4))
                .Concat(build.TablePatches.Where(item => item.Changed && item.PhysicalOffset < sourceLogical.LongLength)
                    .Select(item => (item.PhysicalOffset, sizeof(uint))))
                .Append((plan.HeaderStart, checked((int)(plan.OutputHeaderEnd - plan.HeaderStart)))).ToArray();
            if (!VerifyRetainedSourceBytes(sourceLogical, outputLogical, metadata))
                Refuse("SPI017", "A source logical byte outside the grown header changed.");

            var outputCatalog = _catalogReader.Read(outputSummary);
            var tablesRetained =
                outputCatalog.Names.Count == sourceCatalog.Names.Count + plan.AddedNames &&
                outputCatalog.Names.Take(sourceCatalog.Names.Count).SequenceEqual(sourceCatalog.Names) &&
                outputCatalog.Imports.Count == sourceCatalog.Imports.Count + plan.AddedImports &&
                outputCatalog.Imports.Take(sourceCatalog.Imports.Count).SequenceEqual(sourceCatalog.Imports);
            if (!tablesRetained) Refuse("SPI018", "The original names and imports did not keep their places.");
            var moved = plan.Relocated.ToDictionary(item => item.ExportIndex);
            var exportsRetained = outputCatalog.Exports.Count == sourceCatalog.Exports.Count;
            for (var i = 0; exportsRetained && i < sourceCatalog.Exports.Count; i++)
            {
                var source = sourceCatalog.Exports[i];
                var output = outputCatalog.Exports[i];
                var offset = moved.TryGetValue(source.TableIndex, out var relocated) ? relocated.OutputOffset : source.SerialOffset;
                exportsRetained = ExportsEquivalent(source with { SerialOffset = offset }, output) &&
                                  sourceLogical.AsSpan(source.SerialOffset, source.SerialSize)
                                      .SequenceEqual(outputLogical.AsSpan(output.SerialOffset, output.SerialSize));
            }
            if (!exportsRetained) Refuse("SPI019", "An export's table entry or bytes changed while the header grew.");
            foreach (var reference in request.References)
                if (reference.FindImport(outputCatalog) is not { } index || index != plan.Imports[reference.Path])
                    Refuse("SPI020", $"The staged package does not import {reference.Path} where Arkive planned it.");

            var outputHash = await HashFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            var sourceHashAfter = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                Refuse("SPI021", "The source package changed while Arkive built the staged copy.");
            File.Move(temporaryPath, outputPath, overwrite: true);
            watch.Stop();
            var manifest = new PackageImportsStagedUpkManifest(
                CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, sourcePath, sourceHashAfter, outputName,
                outputHash, request.PhysicalPackageName, request.PackageAlias, sourceCatalog.Names.Count, sourceCatalog.Imports.Count,
                plan.AddedNames, plan.AddedImports, plan.SourceHeaderEnd, plan.OutputHeaderEnd, plan.Imports, plan.SummaryPatches,
                plan.Relocated, OriginalTablesRetained: true, ExportsRetained: true, AllUnplannedSourceBytesIdentical: true,
                AllEncodedBlocksDecodeBack: build.Chunks.SelectMany(item => item.Blocks).All(item => item.DecodeBackPassed),
                StructuralValidationPassed: true, build.Chunks, build.TablePatches, validation, watch.Elapsed.TotalSeconds);
            var manifestPath = Path.Combine(outputDirectory, ImportsManifestFileName);
            await SaveJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new PackageImportsStagedUpkResult(outputDirectory, manifestPath, outputPath, manifest);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static IEnumerable<long> Steps(long start, long end)
    {
        for (var offset = start; offset < end; offset += 4096) yield return offset;
        yield return end - 1;
    }
}
