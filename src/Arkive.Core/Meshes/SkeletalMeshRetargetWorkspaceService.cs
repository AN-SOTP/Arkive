using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkive.Core.Meshes;

/// <summary>
/// Creates an artist-facing cross-skeleton authoring workspace. It deliberately
/// does not emit an installable candidate: the untouched target armature must be
/// used and every unmapped donor weight group must be resolved in Blender before
/// Arkive's normal preflight can accept the exported GLB.
/// </summary>
public sealed class SkeletalMeshRetargetWorkspaceService
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-retarget-workspace.json";
    public const string CompatibilityFileName = "arkive-skeleton-compatibility.json";
    public const string BlenderScriptFileName = "setup-arkive-retarget.py";
    public const string InstructionsFileName = "README-retarget.md";

    private readonly SkeletalMeshSkeletonCompatibilityService _compatibility = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<SkeletalMeshRetargetWorkspaceResult> CreateAsync(
        SkeletalMeshRetargetWorkspaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Donor.Manifest.ExportId.Equals(
                request.Target.Manifest.ExportId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose two different editable mesh exports for a retarget workspace.");
        if (!request.Donor.GlbExists || !File.Exists(request.Donor.GlbPath))
            throw new FileNotFoundException("The donor editable GLB is missing.", request.Donor.GlbPath);
        if (!request.Target.GlbExists || !File.Exists(request.Target.GlbPath))
            throw new FileNotFoundException("The target editable GLB is missing.", request.Target.GlbPath);

        var compatibility = await _compatibility.AnalyzeAsync(
            request.Donor, request.Target, cancellationToken).ConfigureAwait(false);
        if (!compatibility.BlenderAuthoringWorkspaceAllowed)
            throw new NotSupportedException(
                "SKR001: The selected rigs do not have enough proven correspondence for Arkive's bounded Blender workflow. " +
                compatibility.Summary);

        var workspaceId = Guid.NewGuid().ToString("N");
        var output = Path.GetFullPath(request.OutputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException(
                "SKR002: The retarget workspace destination is not empty. Choose a fresh directory.");
        Directory.CreateDirectory(output);
        var donorName = "DONOR-" + Safe(request.Donor.Manifest.ObjectName) + ".glb";
        var targetName = "TARGET-" + Safe(request.Target.Manifest.ObjectName) + ".glb";
        var candidateName = Safe(request.Target.Manifest.ObjectName) + ".arkive-retarget-candidate.glb";
        var donorPath = Path.Combine(output, donorName);
        var targetPath = Path.Combine(output, targetName);
        var reportPath = Path.Combine(output, CompatibilityFileName);
        var scriptPath = Path.Combine(output, BlenderScriptFileName);
        var instructionsPath = Path.Combine(output, InstructionsFileName);
        var manifestPath = Path.Combine(output, ManifestFileName);

        await CopyAtomicAsync(request.Donor.GlbPath, donorPath, cancellationToken).ConfigureAwait(false);
        await CopyAtomicAsync(request.Target.GlbPath, targetPath, cancellationToken).ConfigureAwait(false);
        await WriteJsonAtomicAsync(reportPath, compatibility, cancellationToken).ConfigureAwait(false);
        await WriteTextAtomicAsync(scriptPath,
            BuildBlenderScript(donorName, targetName, CompatibilityFileName, candidateName),
            cancellationToken).ConfigureAwait(false);
        await WriteTextAtomicAsync(instructionsPath,
            BuildInstructions(request, compatibility, candidateName), cancellationToken).ConfigureAwait(false);

        var requirements = new[]
        {
            "Keep TARGET_REFERENCE_ARMATURE unchanged; it is the native target skeleton contract.",
            "Fit donor geometry to the target bind pose before judging skin deformation.",
            "Resolve every ARKIVE_UNMAPPED__ vertex group by transferring or repainting weights.",
            "Retain only existing target material-slot names; texture/material substitution is composed separately.",
            "Export an embedded GLB with custom properties and use Add edited GLB on the target export.",
            "Arkive must pass exact-skeleton, animation-stress, native decode-back and staged-UPK validation.",
        };
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"{request.Donor.Manifest.ObjectName} to {request.Target.Manifest.ObjectName}"
            : request.DisplayName.Trim();
        var manifest = new SkeletalMeshRetargetWorkspaceManifest(
            CurrentSchemaVersion, workspaceId, DateTimeOffset.UtcNow, displayName,
            request.Donor.Manifest.ExportId, request.Target.Manifest.ExportId,
            request.Donor.Manifest.PackageAlias, request.Donor.Manifest.ObjectName,
            request.Target.Manifest.PackageAlias, request.Target.Manifest.ObjectName,
            request.Donor.ManifestPath, request.Target.ManifestPath,
            donorName, targetName, CompatibilityFileName, BlenderScriptFileName,
            InstructionsFileName, candidateName, false, true,
            compatibility.Compatibility != SkeletalMeshSkeletonCompatibilityKind.ExactTargetSkeleton,
            compatibility.Compatibility, compatibility.MappedSourceWeightPercent,
            compatibility.MissingSourceBoneCount, compatibility.MissingCoreBoneCount,
            requirements);
        await WriteJsonAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new SkeletalMeshRetargetWorkspaceResult(
            output, manifestPath, reportPath, scriptPath, instructionsPath, manifest, compatibility);
    }

    private static string BuildInstructions(
        SkeletalMeshRetargetWorkspaceRequest request,
        SkeletalMeshSkeletonCompatibilityReport report,
        string candidateName) => $"""
        # Arkive cross-skeleton retarget workspace

        Donor geometry: `{request.Donor.Manifest.PackageAlias}.{request.Donor.Manifest.ObjectName}`
        Native target: `{request.Target.Manifest.PackageAlias}.{request.Target.Manifest.ObjectName}`

        Classification: **{report.Compatibility}**
        Exact bone-name coverage: **{report.MappedSourceWeightPercent:N3}% of donor weight mass**
        Missing donor bones: **{report.MissingSourceBoneCount:N0}**
        Missing core bones: **{report.MissingCoreBoneCount:N0}**

        {report.Summary}

        ## Set up in Blender

        1. Open Blender's Scripting workspace and run `{BlenderScriptFileName}`.
        2. The script imports the untouched target armature/reference mesh and a donor reference.
        3. It creates `ARKIVE_RETARGET_WORK_*` mesh copies, renames only exact donor-to-target
           vertex groups, and prefixes unresolved groups with `ARKIVE_UNMAPPED__`.
        4. Fit the work meshes to the target bind pose. Resolve every unmapped group by weight
           transfer or manual weight painting. Do not edit, rename, scale, or apply transforms to
           `TARGET_REFERENCE_ARMATURE`.
        5. Map the work mesh material slots to the target export's existing material names.
        6. Test shoulders, spine, hips, hands, legs, hair and cloth-like appendages in Pose Mode.
        7. Export only `TARGET_REFERENCE_ARMATURE` and the finished `ARKIVE_RETARGET_WORK_*`
           meshes as glTF Binary (`.glb`) to `{candidateName}`. Enable custom properties.
        8. In Arkive, select the target clean export, click **Add edited GLB**, and choose the
           candidate. A proper result must report **Exact target skeleton** and pass animation safety.

        ## Non-negotiable safety rules

        - Do not collapse missing donor bones to the nearest target ancestor. That shortcut caused
          the runtime-broken Thaemine/Brelshaza experiment.
        - A correct static bind pose is not enough; Arkive's deterministic pose suite must pass.
        - This workspace never changes a UPK and never creates an installable candidate by itself.
        - Keep the compatibility JSON with the project; it is the audit trail for all mapping gaps.
        """;

    private static string BuildBlenderScript(
        string donorName,
        string targetName,
        string reportName,
        string candidateName) => $$"""
        # Arkive conservative cross-skeleton authoring setup.
        # Run inside Blender. No UPK is read or modified by this script.
        import bpy
        import json
        import os

        ROOT = os.path.dirname(os.path.abspath(__file__))
        DONOR = os.path.join(ROOT, {{PythonString(donorName)}})
        TARGET = os.path.join(ROOT, {{PythonString(targetName)}})
        REPORT = os.path.join(ROOT, {{PythonString(reportName)}})
        EXPECTED_OUTPUT = os.path.join(ROOT, {{PythonString(candidateName)}})

        def canonical(name):
            marker = name.lower().rfind("__arkive_")
            return (name[:marker] if marker >= 0 else name).strip().lower()

        def import_glb(path):
            before = set(bpy.data.objects)
            bpy.ops.import_scene.gltf(filepath=path)
            return [obj for obj in bpy.data.objects if obj not in before]

        def armature(objects):
            rigs = [obj for obj in objects if obj.type == 'ARMATURE']
            if len(rigs) != 1:
                raise RuntimeError(f"Expected one armature, found {len(rigs)}")
            return rigs[0]

        bpy.ops.object.select_all(action='SELECT')
        bpy.ops.object.delete(use_global=False)

        with open(REPORT, 'r', encoding='utf-8') as stream:
            report = json.load(stream)

        target_objects = import_glb(TARGET)
        target_rig = armature(target_objects)
        target_rig.name = "TARGET_REFERENCE_ARMATURE"
        target_rig.show_in_front = True
        target_meshes = [obj for obj in target_objects if obj.type == 'MESH']
        for obj in target_meshes:
            obj.name = "TARGET_REFERENCE_" + obj.name
            obj.display_type = 'WIRE'
            obj.hide_render = True

        donor_objects = import_glb(DONOR)
        donor_rig = armature(donor_objects)
        donor_rig.name = "DONOR_REFERENCE_ARMATURE"
        donor_rig.hide_render = True
        donor_meshes = [obj for obj in donor_objects if obj.type == 'MESH']
        for obj in donor_meshes:
            obj.name = "DONOR_REFERENCE_" + obj.name
            obj.hide_render = True

        target_names = {canonical(bone.name): bone.name for bone in target_rig.data.bones}
        mapping = {}
        unresolved = set()
        for item in report['boneMappings']:
            source = canonical(item['sourceBoneName'])
            target = item.get('targetBoneName')
            if target and canonical(target) in target_names:
                mapping[source] = target_names[canonical(target)]
            else:
                unresolved.add(source)

        work_meshes = []
        for source in donor_meshes:
            work = source.copy()
            work.data = source.data.copy()
            bpy.context.collection.objects.link(work)
            work.name = "ARKIVE_RETARGET_WORK_" + source.name.removeprefix("DONOR_REFERENCE_")
            work.hide_render = False
            for modifier in list(work.modifiers):
                if modifier.type == 'ARMATURE':
                    work.modifiers.remove(modifier)
            modifier = work.modifiers.new(name="Arkive target armature", type='ARMATURE')
            modifier.object = target_rig
            for group in work.vertex_groups:
                key = canonical(group.name)
                if key in mapping:
                    group.name = mapping[key]
                elif key in unresolved:
                    group.name = "ARKIVE_UNMAPPED__" + group.name
            work['arkiveTargetArmature'] = target_rig.name
            work['arkiveUnresolvedGroups'] = sum(
                1 for group in work.vertex_groups if group.name.startswith('ARKIVE_UNMAPPED__'))
            work_meshes.append(work)

        target_rig['arkiveRole'] = 'untouched-target-skeleton-contract'
        target_rig['arkiveExpectedOutput'] = EXPECTED_OUTPUT
        text = bpy.data.texts.get('ARKIVE_RETARGET_REPORT') or bpy.data.texts.new('ARKIVE_RETARGET_REPORT')
        text.clear()
        text.write(json.dumps(report, indent=2))
        bpy.context.view_layer.objects.active = work_meshes[0] if work_meshes else target_rig
        for obj in bpy.context.selected_objects:
            obj.select_set(False)
        for obj in work_meshes:
            obj.select_set(True)
        target_rig.select_set(True)

        print("Arkive retarget workspace prepared")
        print("Target armature:", target_rig.name)
        print("Work meshes:", len(work_meshes))
        print("Exact mapped donor weight groups:", len(mapping))
        print("Unresolved donor bone names:", len(unresolved))
        print("Expected output:", EXPECTED_OUTPUT)
        print("Resolve every ARKIVE_UNMAPPED__ group before exporting.")
        """;

    private static string PythonString(string value) => JsonSerializer.Serialize(value);

    private static async Task CopyAtomicAsync(string source, string destination, CancellationToken token)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteTextAtomicAsync(string path, string value, CancellationToken token)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, value, new UTF8Encoding(false), token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(result) ? "mesh" : result.Length <= 80 ? result : result[..80];
    }
}
