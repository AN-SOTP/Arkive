namespace Arkive.SmokeTests;

internal static class ModWorkflowSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("blocks client-wide checks when protected package decoding or reconstruction is unsupported", TestClientUpdateDeepFormatProtection),
        new("accepts only semantic Texture2D offset changes during client updates", TestTextureUpdateSemanticComparer),
        new("rolls back every project manifest when a bulk client rebase fails", TestClientUpdateBulkRebaseRollback),
        new("resolves only verified texture-to-model preview links", TestTextureModelPreviewResolver),
        new("audits exact mod conflicts and serialized dependencies", TestModConflictDependencies),
        new("recommends safe unified workshop steps", TestUnifiedGuidedWorkflow),
    ];

    private static void TestClientUpdateDeepFormatProtection()
    {
        var root = Path.Combine(fixtureDirectory, "client-update-protection");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "protected-client-package.upk");
        var payload = Encoding.ASCII.GetBytes(
            "Arkive client-update deep format probe protected package fixture.");
        WriteCompressedPayloadPackage(path, payload, protect: true);
        var workspace = new GuidedTexturePackageWorkspace(Path.Combine(root, "projects"));
        var package = workspace.GetOrCreateAsync(
                "protected_client_package", Path.GetFileName(path), path)
            .GetAwaiter().GetResult();
        var input = new ClientUpdatePackageInput(package, [], []);
        var unsupported = new ClientUpdateAuditService(
                new ClientUpdateCompatibilityService(
                    protectionProvider: NoChunkProtectionProvider.Instance))
            .InspectAllAsync([input], Path.Combine(root, "empty-state"))
            .GetAwaiter().GetResult();
        Assert(unsupported.State == ClientUpdateAuditState.UnsupportedClientFormat &&
               !unsupported.EveryAffectedPackageFormatCompatible &&
               unsupported.Packages.Single().FormatProbe?.State ==
                   ClientPackageFormatState.ProtectionUnavailable,
            "client-wide audit did not block a protected package without read/write protection support");

        var wrongKey = Enumerable.Range(0, 32).Select(index => (byte)(255 - index)).ToArray();
        var wrongKeyAudit = new ClientUpdateAuditService(
                new ClientUpdateCompatibilityService(
                    protectionProvider: new AesEcbPrefixProtectionProvider(wrongKey)))
            .InspectAllAsync([input], Path.Combine(root, "empty-state"))
            .GetAwaiter().GetResult();
        Assert(wrongKeyAudit.State == ClientUpdateAuditState.UnsupportedClientFormat &&
               wrongKeyAudit.Packages.Single().FormatProbe?.State ==
                   ClientPackageFormatState.DecodeFailed,
            "client-wide audit did not detect a protection key that advertised support but failed full chunk decode");

        var supported = new ClientUpdateAuditService(
                new ClientUpdateCompatibilityService(
                    protectionProvider: new AesEcbPrefixProtectionProvider(testKey)))
            .InspectAllAsync([input], Path.Combine(root, "empty-state"))
            .GetAwaiter().GetResult();
        var probe = supported.Packages.Single().FormatProbe;
        Assert(supported.EveryAffectedPackageFormatCompatible &&
               supported.State == ClientUpdateAuditState.ReviewRequired &&
               probe is { CanRead: true, CanReconstruct: true, DecodedChunkCount: 1 } &&
               probe.DecodedChunkCount == probe.CompressedChunkCount,
            "client-wide audit did not decode every protected chunk with the correct provider");
        CryptographicOperations.ZeroMemory(wrongKey);
    }

    private static void TestTextureUpdateSemanticComparer()
    {
        const string packageHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string updatedPackageHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        const string exportHash = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        const string updatedExportHash = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
        const string mipHash = "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";

        TexturePropertyEvidence Property(
            long offset, string name, string type, int size,
            string preview, long? numeric, string raw) =>
            new(offset, 1_000 + offset, name, type, size, 0, null,
                offset + 8, 1_008 + offset, preview, numeric, raw);

        var properties = new TexturePropertyEvidence[]
        {
            Property(4, "Format", "ByteProperty", 8, "PF_DXT5", 7, "D200000000000000"),
            Property(24, "SizeX", "IntProperty", 4, "64", 64, "40000000"),
            Property(44, "SizeY", "IntProperty", 4, "64", 64, "40000000"),
            Property(64, "OriginalSizeX", "IntProperty", 4, "64", 64, "40000000"),
            Property(84, "OriginalSizeY", "IntProperty", 4, "64", 64, "40000000"),
            Property(104, "bUseCrunchCompression", "BoolProperty", 0, "True", 1, "01000000"),
            Property(124, "LODGroup", "ByteProperty", 8, "TEXTUREGROUP_Character", null,
                "F300000000000000"),
        };
        var priorMip = new TextureMipEvidence(
            0, 256, 1_256, 512, 4_096, 1_234, 4_000,
            64, 64, "Inline", 300, 1_300, mipHash, [], true, true);
        var priorChain = new TextureMipChainEvidence(
            1, 240, 1_240, 1, 100, TextureReconnaissanceConfidence.Confirmed,
            "PF_DXT5", ["synthetic verified chain"], [priorMip]);
        var profile = new TexturePackageProfile(
            PackageConstants.WesternFileVersion,
            PackageConstants.WesternLicenseeVersion,
            0, 0, 0, PackageConstants.WesternCompressionFlags,
            Guid.Empty, 0, 8_192);
        var previous = new TextureReconnaissanceReport(
            TextureReconnaissanceService.CurrentSchemaVersion,
            DateTimeOffset.UtcNow, "old.upk", "old.upk", "semantic_test",
            8_192, 1, packageHash, profile, "test", 0, 1,
            AssetKind.Texture, "Texture2D", "semantic_texture", 1_000, 2_000,
            exportHash, 4, 240, "PF_DXT5", properties, [priorChain], [], [], TimeSpan.Zero);
        var retainedMip = new NativeTextureMipExport(
            0, 64, 64, 1_300, 512, "Crunch CRN / DXT5",
            1_234, mipHash, "source-mips/mip-00.crn", 74,
            4_096, new string('F', 64), 128);
        var source = new NativeTextureExportManifest(
            NativeTextureExportService.CurrentManifestSchemaVersion,
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            "old.upk", "old.upk", "semantic_test", 8_192, 1,
            packageHash, "test", 1, 1, "Texture2D", "semantic_texture",
            1_000, 2_000, exportHash, 4, 240, "PF_DXT5 / BC3", "test",
            64, 64, 64, 64, 1, "test", "semantic_texture.dds", 4_224,
            new string('F', 64), [retainedMip], "old-recon.json", 0);

        var movedProperties = properties.Select(property => property with
        {
            RelativeOffset = property.RelativeOffset + 12,
            LogicalOffset = property.LogicalOffset + 5_000,
            ValueRelativeOffset = property.ValueRelativeOffset + 12,
            ValueLogicalOffset = property.ValueLogicalOffset + 5_000,
            RawValuePrefix = property.Name is "Format" ? "D300000000000000" : property.RawValuePrefix,
        }).ToArray();
        var movedMip = priorMip with
        {
            HeaderRelativeOffset = priorMip.HeaderRelativeOffset + 12,
            HeaderLogicalOffset = priorMip.HeaderLogicalOffset + 5_000,
            BulkDataOffset = priorMip.BulkDataOffset + 5_000,
            PayloadRelativeOffset = priorMip.PayloadRelativeOffset + 12,
            PayloadLogicalOffset = priorMip.PayloadLogicalOffset + 5_000,
        };
        var current = previous with
        {
            PackagePath = "updated.upk",
            PhysicalPackageName = "updated.upk",
            PackageSha256 = updatedPackageHash,
            ExportPayloadSha256 = updatedExportHash,
            SerialOffset = 6_000,
            PropertyStreamRelativeOffset = 16,
            NativeDataRelativeOffset = 252,
            Properties = movedProperties,
            MipCandidates = [priorChain with
            {
                ArrayRelativeOffset = priorChain.ArrayRelativeOffset + 12,
                ArrayLogicalOffset = priorChain.ArrayLogicalOffset + 5_000,
                Mips = [movedMip],
            }],
        };

        var offsetOnly = TextureUpdateSemanticComparer.Compare(source, previous, current);
        Assert(offsetOnly.IsEquivalent && offsetOnly.Code == "CTS000",
            "offset/name-reference-only Texture2D update was not accepted for safe rebase: " +
            offsetOnly.Code + ":" + offsetOnly.Message);

        var changedProperties = movedProperties.Select(property =>
            property.Name == "LODGroup"
                ? property with { ValuePreview = "TEXTUREGROUP_World" }
                : property).ToArray();
        var propertyChange = TextureUpdateSemanticComparer.Compare(
            source, previous, current with { Properties = changedProperties });
        Assert(!propertyChange.IsEquivalent && propertyChange.Code == "CTS008",
            "semantic Texture2D property change was not refused");

        var changedMip = movedMip with { PayloadSha256 = new string('1', 64) };
        var mipChange = TextureUpdateSemanticComparer.Compare(
            source, previous, current with
            {
                MipCandidates = [current.MipCandidates[0] with { Mips = [changedMip] }],
            });
        Assert(!mipChange.IsEquivalent && mipChange.Code == "CTS012",
            "changed Texture2D mip payload was not refused");
    }

    private static void TestClientUpdateBulkRebaseRollback()
    {
        var root = Path.Combine(fixtureDirectory, "client-update-bulk-rollback");
        Directory.CreateDirectory(root);
        var packageWorkspace = new GuidedTexturePackageWorkspace(Path.Combine(root, "projects"));
        var packageEntries = new List<GuidedTexturePackageEntry>();
        for (var index = 0; index < 2; index++)
        {
            var path = Path.Combine(root, $"package-{index + 1}.upk");
            WritePackage(path, []);
            packageEntries.Add(packageWorkspace.GetOrCreateAsync(
                    $"package_{index + 1}", Path.GetFileName(path), path)
                .GetAwaiter().GetResult());
        }
        var expected = new string('A', 64);
        var current = new string('B', 64);
        var reports = packageEntries.Select(package => new ClientPackageCompatibilityReport(
            ClientUpdateCompatibilityService.CurrentSchemaVersion, DateTimeOffset.UtcNow,
            package.Manifest.PackageProjectId, package.Manifest.PackageAlias,
            package.Manifest.PhysicalPackageName, package.Manifest.SourcePackagePath,
            expected, current, null, ClientPackageCompatibilityState.SafeAutomaticRebase,
            CanRebuildWithoutRebase: false, CanAutomaticallyRebase: true, [],
            "Synthetic byte-identical asset evidence.", 0)).ToArray();
        var audit = new ClientUpdateAuditReport(
            ClientUpdateAuditService.CurrentSchemaVersion, Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow, "Synthetic", ClientUpdateAuditState.RebaseAvailable,
            RequiresRestoreBeforeUpdateCheck: false,
            EveryAffectedPackageFormatCompatible: true, CanBulkRebase: true,
            UniquePackageCount: 2, reports, [], [], "Two safe synthetic packages.", 0);
        var snapshots = packageEntries.ToDictionary(
            package => package.ManifestPath,
            package => File.ReadAllBytes(package.ManifestPath),
            StringComparer.OrdinalIgnoreCase);
        var calls = 0;
        var syntheticRebase = new SyntheticClientUpdateRebaseService((package, _, _, _, _) =>
        {
            calls++;
            if (calls == 2)
            {
                File.AppendAllText(package.ManifestPath, "synthetic partial mutation");
                throw new IOException("Synthetic second-package rebase failure.");
            }
            return Task.FromResult(new ClientUpdateRebaseResult(
                package.Manifest.PackageProjectId, package.Manifest.PackageAlias,
                expected, current, [], [], EveryAssetRebased: true,
                GameFilesModified: false, TimeSpan.Zero));
        });
        var meshWorkspace = new SkeletalMeshEditWorkspaceStore(Path.Combine(root, "meshes"));
        var service = new ClientUpdateBulkRebaseService(
            syntheticRebase, packageWorkspace, meshWorkspace);
        try
        {
            _ = service.RebaseSafePackagesAsync(
                    audit, packageEntries.Select(package =>
                        new ClientUpdatePackageInput(package, [], [])).ToArray(),
                    Path.Combine(root, "output"))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("Synthetic bulk rebase failure was not surfaced.");
        }
        catch (IOException exception) when (exception.Message.Contains(
                   "Synthetic second-package", StringComparison.Ordinal))
        {
        }
        Assert(calls == 2 && packageEntries.All(package =>
                   File.ReadAllBytes(package.ManifestPath).SequenceEqual(snapshots[package.ManifestPath])),
            "bulk client rebase did not restore every package manifest after a later package failed");
    }

    private static void TestTextureModelPreviewResolver()
    {
        GameAssetAssociation[] associations =
        [
            new(
                "Naturewear (Female Warrior)", "item.naturewear", "Item", 237,
                "Model", "PC_WR_F_BASEBODY.pc_wr_f_basebody_upper_d_loc_int",
                "PC_WR_F_BASEBODY", "pc_wr_f_basebody_upper_d_loc_int")
            {
                Confidence = GameAssetAssociationConfidence.Verified,
                Relation = GameAssetAssociationRelation.VerifiedSerializedDependency,
                SourceAssetReference = "PC_WR_F_AV_237B.PC_WR_F_AV_237B_UPPER",
                Archetype = "Female Warrior",
            },
            new(
                "Naming guess", "guess", "Item", 238, "Model",
                "PC_WR_F_BASEBODY.pc_wr_f_basebody_upper_d_loc_int",
                "PC_WR_F_BASEBODY", "pc_wr_f_basebody_upper_d_loc_int")
            {
                Confidence = GameAssetAssociationConfidence.Inferred,
                Relation = GameAssetAssociationRelation.NamingFamilyInference,
                SourceAssetReference = "PC_WR_F_AV_FAKE.PC_WR_F_AV_FAKE_UPPER",
            },
        ];

        var resolved = TextureModelPreviewResolver.Resolve(associations);
        Assert(resolved.Count == 1, "Model preview resolver accepted unverified naming evidence.");
        Assert(resolved[0].PackageName == "PC_WR_F_AV_237B" &&
               resolved[0].ObjectName == "PC_WR_F_AV_237B_UPPER" &&
               resolved[0].Archetype == "Female Warrior",
            "Model preview resolver did not preserve the verified source model reference.");
    }

    private static void TestModConflictDependencies()
    {
        var root = Path.Combine(fixtureDirectory, "mod-graph");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "CLIENT.upk");
        var payload = Path.Combine(root, "payload.upk");
        var bundle = Path.Combine(root, "bundle.json");
        var materialPath = Path.Combine(root, "material-edit.json");
        File.WriteAllBytes(source, [1]);
        File.WriteAllBytes(payload, [2]);
        File.WriteAllText(bundle, "{}");
        var materialDocument = new MaterialInstanceEditDocument(
            MaterialInstanceEditService.CurrentEditSchemaVersion,
            "material-edit", DateTimeOffset.UtcNow, "Body material", "recon.json",
            source, new string('A', 64), "PC_TEST", 2, "MaterialInstanceConstant",
            "mat_body", 100, 40, new string('B', 64), [], [],
            [new MaterialTextureReference("Diffuse", 3, "Texture2D'PC_TEST.body_d'")]);
        File.WriteAllText(materialPath, JsonSerializer.Serialize(materialDocument));
        var texture = new GuidedTexturePackageAssetRevision(
            "texture-edit", "body_d", 1, 1, "PF_DXT5", new string('C', 64),
            "texture-build.json", 10, 20);
        var material = new GuidedMaterialPackageAssetRevision(
            "material-edit", "Body material", "mat_body", 2, materialPath,
            "candidate.json", new string('D', 64), "staged.json", 30, 40);
        var revision = new GuidedTexturePackageRevision(
            "revision", DateTimeOffset.UtcNow, new string('E', 64), [texture],
            payload, "final.json", bundle, "ready.json", payload, 1,
            new string('F', 64), 0.1)
        {
            MaterialAssets = [material],
        };
        var firstManifest = new GuidedTexturePackageManifest(
            GuidedTexturePackageWorkspace.CurrentSchemaVersion,
            "package-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "PC_TEST",
            "CLIENT.upk", source, revision.RevisionId, [revision]);
        var secondManifest = new GuidedTexturePackageManifest(
            GuidedTexturePackageWorkspace.CurrentSchemaVersion,
            "package-b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "PC_DUPLICATE",
            "CLIENT.upk", source, null, []);
        var report = new ModConflictDependencyService().AnalyzeAsync(
            [
                new GuidedTexturePackageEntry(Path.Combine(root, "a.json"), root, firstManifest),
                new GuidedTexturePackageEntry(Path.Combine(root, "b.json"), root, secondManifest),
            ]).GetAwaiter().GetResult();
        Assert(report.Conflicts.Any(item => item.Code == "MCV001" &&
                                            item.Severity == ModGraphSeverity.Blocking),
            "mod graph did not block duplicate physical package targets");
        Assert(report.Dependencies.Any(item => item.SourceObject == "mat_body" &&
                                               item.Relation == "Texture parameter: Diffuse" &&
                                               item.Resolution == "Modified in this package"),
            "mod graph did not resolve a serialized material texture reference to the composed texture");
        Assert(report.Dependencies.Count(item => item.Relation == "Composes replacement") == 2,
            "mod graph did not inventory every composed asset");
    }

    private static void TestUnifiedGuidedWorkflow()
    {
        var service = new UnifiedGuidedWorkflowService();
        var find = service.Recommend(new UnifiedGuidedWorkflowSnapshot(
            false, false, 0, false, false, false, false));
        var edit = service.Recommend(new UnifiedGuidedWorkflowSnapshot(
            true, false, 0, false, false, false, false));
        var build = service.Recommend(new UnifiedGuidedWorkflowSnapshot(
            true, true, 2, true, true, false, false));
        var install = service.Recommend(new UnifiedGuidedWorkflowSnapshot(
            true, true, 2, true, false, true, false));
        var restore = service.Recommend(new UnifiedGuidedWorkflowSnapshot(
            true, true, 2, true, false, false, true));
        Assert(find.Stage == UnifiedGuidedWorkflowStage.Find &&
               edit.Stage == UnifiedGuidedWorkflowStage.Edit &&
               build.Stage == UnifiedGuidedWorkflowStage.Build &&
               install.Stage == UnifiedGuidedWorkflowStage.Deploy &&
               restore.Stage == UnifiedGuidedWorkflowStage.Deploy &&
               restore.PrimaryAction == "Restore original",
            "unified workshop state machine did not recommend the safe find/edit/build/deploy sequence");
    }

}
