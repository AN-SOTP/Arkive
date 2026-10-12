using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Packages;

namespace Arkive.Core.StaticMeshes;

public sealed class StaticMeshEditPreflightService
{
    public const int CurrentSchemaVersion = 1;
    private readonly AssetCatalogReader _catalogReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public StaticMeshEditPreflightService(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
        _catalogReader = new AssetCatalogReader(summaryReader, _protectionProvider);
    }

    public async Task<StaticMeshEditPreflightReport> InspectAsync(
        string sourceEditableManifestPath,
        string editedGlbPath,
        CancellationToken cancellationToken = default)
    {
        var source = await StaticMeshEditableExportService.LoadManifestAsync(
            sourceEditableManifestPath, cancellationToken).ConfigureAwait(false);
        var recon = await StaticMeshReconnaissanceWorkspaceStore.LoadReportAsync(
            source.ReconnaissanceJsonPath, cancellationToken).ConfigureAwait(false);
        if (recon.Readiness != StaticMeshReadiness.Editable || recon.NativeLayout is null)
            throw new StaticMeshRefusalException("STP001", "The source StaticMesh reconnaissance is not editable.");
        var document = StaticMeshGlbSemanticReader.Read(editedGlbPath, source);
        var issues = new List<string>();
        if (!document.MeshNodeTransformsIdentity)
            issues.Add("Error STP100: Apply object transforms before export; mesh-node transforms must be identity.");
        var sourceBytes = ReadSourceBytes(recon, cancellationToken);
        var decoded = new StaticMeshSemanticDecoder().Decode(sourceBytes, recon.NativeLayout)
            .ToDictionary(lod => lod.Evidence.LodIndex);
        var materialSlotsMatch = true;
        var everyVertexMapped = true;
        var geometryFinite = true;
        var boundsReasonable = true;
        var topologyChanged = false;
        foreach (var sourceLod in recon.NativeLayout.Lods)
        {
            if (!document.Lods.TryGetValue(sourceLod.LodIndex, out var candidate))
            {
                issues.Add($"Error STP101: Candidate is missing LOD {sourceLod.LodIndex}.");
                everyVertexMapped = false;
                continue;
            }
            var sourceDecoded = decoded[sourceLod.LodIndex];
            if (candidate.Vertices.Count != sourceLod.VertexCount || !candidate.StableIdentityPresent)
            {
                topologyChanged = true;
                everyVertexMapped = false;
            }
            if (candidate.Sections.Count != sourceLod.Sections.Count)
            {
                materialSlotsMatch = false;
                issues.Add($"Error STP102: LOD {sourceLod.LodIndex} has {candidate.Sections.Count} material sections; {sourceLod.Sections.Count} are required.");
                continue;
            }
            var sourceOracle = recon.Oracle?.Lods.SingleOrDefault(lod => lod.LodIndex == sourceLod.LodIndex);
            var combinedCandidateIndices = new List<uint>();
            foreach (var sourceSection in sourceLod.Sections)
            {
                var section = candidate.Sections.SingleOrDefault(item => item.SectionIndex == sourceSection.SectionIndex);
                if (section is null)
                {
                    materialSlotsMatch = false;
                    issues.Add($"Error STP103: LOD {sourceLod.LodIndex} is missing section {sourceSection.SectionIndex}.");
                    continue;
                }
                var expectedMaterial = sourceOracle?.Sections
                    .SingleOrDefault(item => item.SectionIndex == sourceSection.SectionIndex)?.Material;
                if (!string.IsNullOrWhiteSpace(expectedMaterial) &&
                    !NormalizeMaterial(section.MaterialName).Equals(
                        NormalizeMaterial(expectedMaterial), StringComparison.OrdinalIgnoreCase))
                {
                    materialSlotsMatch = false;
                    issues.Add($"Error STP104: LOD {sourceLod.LodIndex} section {sourceSection.SectionIndex} material '{section.MaterialName}' does not match '{expectedMaterial}'.");
                }
                combinedCandidateIndices.AddRange(section.Indices);
            }
            if (combinedCandidateIndices.Count != sourceDecoded.Indices.Length ||
                !combinedCandidateIndices.Select(value => (uint)value).SequenceEqual(
                    sourceDecoded.Indices.Select(value => (uint)value)))
                topologyChanged = true;
            if (candidate.Vertices.Count > ushort.MaxValue)
            {
                issues.Add($"Error STP105: LOD {sourceLod.LodIndex} has {candidate.Vertices.Count:N0} vertices; UE3 StaticMesh3 supports at most 65,535.");
                boundsReasonable = false;
            }
            if (sourceLod.Colours is not null && candidate.Vertices.Any(vertex => vertex.Colour is null))
                issues.Add($"Error STP106: LOD {sourceLod.LodIndex} dropped required vertex colours.");
            var sourceExtent = sourceDecoded.Maximum - sourceDecoded.Minimum;
            var candidateExtent = candidate.Maximum - candidate.Minimum;
            var sourceDiagonal = Math.Max(0.001f, sourceExtent.Length());
            var candidateDiagonal = candidateExtent.Length();
            if (!float.IsFinite(candidateDiagonal))
            {
                geometryFinite = false;
                issues.Add($"Error STP107: LOD {sourceLod.LodIndex} bounds are non-finite.");
            }
            else if (candidateDiagonal > Math.Max(100f, sourceDiagonal * 20f))
            {
                boundsReasonable = false;
                issues.Add($"Error STP108: LOD {sourceLod.LodIndex} bounds expanded from {sourceDiagonal:N3} m to {candidateDiagonal:N3} m.");
            }
        }
        foreach (var extra in document.Lods.Keys.Except(recon.NativeLayout.Lods.Select(lod => lod.LodIndex)))
            issues.Add($"Error STP109: Candidate contains unexpected LOD {extra}.");

        var hasErrors = issues.Any(issue => issue.StartsWith("Error", StringComparison.OrdinalIgnoreCase));
        if (topologyChanged)
            issues.Add("Warning STP200: Render topology changed. Arkive will preserve the source collision tree; collision will not follow newly added or removed surfaces.");
        else
            issues.Add("Information STP201: Stable vertex identity and render topology are retained.");
        var readiness = hasErrors
            ? StaticMeshEditReadiness.Refused
            : topologyChanged
                ? StaticMeshEditReadiness.ReadyForTopologyCompilation
                : StaticMeshEditReadiness.ReadyForSameTopologyCompilation;
        return new StaticMeshEditPreflightReport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            readiness,
            topologyChanged,
            materialSlotsMatch,
            everyVertexMapped,
            geometryFinite,
            boundsReasonable,
            true,
            issues);
    }

    private byte[] ReadSourceBytes(StaticMeshReconnaissanceReport report, CancellationToken cancellationToken)
    {
        var catalog = _catalogReader.Read(report.PackagePath);
        using var logical = new LogicalPackageStream(catalog.Summary, _protectionProvider);
        var bytes = new byte[checked((int)report.SerialSize)];
        logical.Position = report.SerialOffset;
        logical.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StaticMeshSemanticDecoder.Sha(bytes).Equals(report.ExportPayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new StaticMeshRefusalException("STP002", "The source StaticMesh export changed.");
        return bytes;
    }

    private static string NormalizeMaterial(string value)
    {
        var apostrophe = value.LastIndexOf('\'');
        if (apostrophe >= 0 && apostrophe < value.Length - 1) value = value[(apostrophe + 1)..];
        value = value.Trim('\'', '"');
        var dot = value.LastIndexOf('.');
        if (dot >= 0 && dot < value.Length - 1) value = value[(dot + 1)..];
        return value.Trim();
    }
}
