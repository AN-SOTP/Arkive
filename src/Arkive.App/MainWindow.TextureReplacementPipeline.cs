using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arkive.Core.Archives;
using Arkive.Core.Assets;
using Arkive.Core.Baselines;
using Arkive.Core.Discovery;
using Arkive.Core.External;
using Arkive.Core.Exports;
using Arkive.Core.Inspection;
using Arkive.Core.Meshes;
using Arkive.Core.Materials;
using Arkive.Core.Mods;
using Arkive.Core.Packages;
using Arkive.Core.StaticMeshes;
using Arkive.Core.Textures;
using Arkive.Core.Validation;
using Microsoft.Win32;

namespace Arkive.App;

public partial class MainWindow : Window
{
    private void OpenEditableTextureButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportWorkspaceGrid.SelectedItem is ExportWorkspaceRow row)
        {
            OpenTextureCandidateFile(
                FindEditableTexturePath(row),
                "editable texture image");
        }
    }

    private async void CreateTextureImportCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureImportCandidateInProgress ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow sourceRow ||
            string.IsNullOrWhiteSpace(sourceRow.Entry.Manifest.NativeDetailsManifestPath))
        {
            return;
        }

        var nativeManifestPath = Path.GetFullPath(
            sourceRow.Entry.Manifest.NativeDetailsManifestPath);
        if (!File.Exists(nativeManifestPath))
        {
            StatusText.Text = "The selected export's native texture manifest is missing.";
            Log(
                $"Texture import candidate refused before file selection: " +
                $"nativeManifest={nativeManifestPath}, reason=manifest missing.");
            UpdateExportWorkspaceActionState();
            return;
        }

        var preferredEditCopy = FindEditableTexturePath(sourceRow);
        var dialog = new OpenFileDialog
        {
            Title = "Select the edited texture image",
            Filter = "Editable texture image (*.png;*.dds)|*.png;*.dds|PNG image (*.png)|*.png|DirectDraw Surface (*.dds)|*.dds|All files (*.*)|*.*",
            InitialDirectory = sourceRow.Entry.OutputDirectory,
            CheckFileExists = true,
            Multiselect = false,
            FileName = preferredEditCopy is null ? string.Empty : Path.GetFileName(preferredEditCopy),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var alphaPolicy = TextureImportAlphaPolicy.PreserveOriginal;
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            sourceRow.Entry.Manifest.PackageAlias,
            sourceRow.Entry.Manifest.ObjectName + "-import-candidate");
        _textureImportCandidateInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text =
            $"Normalizing edited image for {sourceRow.Entry.Manifest.ObjectName}...";
        Log(
            $"Texture import candidate started: parentExportId={sourceRow.Entry.Manifest.ExportId}, " +
            $"package={sourceRow.Entry.Manifest.SourcePackagePath}, " +
            $"object={sourceRow.Entry.Manifest.ObjectName}, nativeManifest={nativeManifestPath}, " +
            $"editedImage={dialog.FileName}, alphaPolicy={alphaPolicy}, output={outputDirectory}. " +
            "Operation is staging-only; no package or game file will be written.");
        try
        {
            var result = await _textureImportCandidateService.CreateAsync(
                new TextureImportCandidateRequest(
                    nativeManifestPath,
                    dialog.FileName,
                    outputDirectory,
                    alphaPolicy));
            var workspaceEntry = await _exportWorkspaceStore.CommitTextureImportCandidateAsync(
                result,
                sourceRow.Entry);
            Log(
                $"Texture import candidate completed in {result.Duration.TotalSeconds:N3}s: " +
                $"candidateId={result.Manifest.CandidateId}, parentExportId={result.Manifest.SourceExportId}, " +
                $"candidate={result.CandidateDdsPath}, candidateBytes={result.Manifest.CandidateDdsBytes}, " +
                $"candidateSha={result.Manifest.CandidateDdsSha256}, format={result.Manifest.PixelFormat}, " +
                $"dimensions={result.Manifest.Width}x{result.Manifest.Height}, mips={result.Manifest.MipCount}, " +
                $"alphaPolicy={result.Manifest.AlphaPolicy}, totalBlocks={result.Manifest.TotalBlocks}, " +
                $"editedBlocks={result.Manifest.EditedBlocksDifferentFromOriginal}, " +
                $"editedColourBlocks={result.Manifest.EditedColourBlocksDifferentFromOriginal}, " +
                $"editedAlphaBlocks={result.Manifest.EditedAlphaBlocksDifferentFromOriginal}, " +
                $"editedPixels={result.Manifest.EditedPixels}, inputFormat={result.Manifest.EditedInputFormat}, " +
                $"inputMips={result.Manifest.EditedInputMipCount}, generatedMips={result.Manifest.GeneratedMipCount}, " +
                $"touchedBlocks={result.Manifest.TouchedBlocks}, " +
                $"unchangedBlocksCopied={result.Manifest.UnchangedBlocksCopied}, " +
                $"semantic={result.Manifest.TextureSemantic}, " +
                $"candidateBlocks={result.Manifest.CandidateBlocksDifferentFromOriginal}, " +
                $"inspectionErrors={result.CandidateInspection.ErrorCount}, " +
                $"inspectionWarnings={result.CandidateInspection.WarningCount}, " +
                $"candidateManifest={result.ManifestPath}, workspaceManifest={workspaceEntry.ManifestPath}.");
            foreach (var check in result.Manifest.PreflightChecks)
            {
                Log(
                    $"Texture import preflight: status={check.Status}, code={check.Code}, " +
                    $"message={check.Message}");
            }

            LogTextureImportInspection("original", result.OriginalInspection);
            LogTextureImportInspection("edited", result.EditedInspection);
            LogTextureImportInspection("candidate", result.CandidateInspection);
            await RefreshExportWorkspaceAsync("texture import candidate completed");
            if (ExportWorkspaceGrid.ItemsSource is IEnumerable<ExportWorkspaceRow> rows)
            {
                var createdRow = rows.FirstOrDefault(row =>
                    row.Entry.Manifest.ExportId.Equals(
                        result.Manifest.CandidateId,
                        StringComparison.OrdinalIgnoreCase));
                if (createdRow is not null)
                {
                    ExportWorkspaceGrid.SelectedItem = createdRow;
                    ExportWorkspaceGrid.ScrollIntoView(createdRow);
                }
            }

            ExportsTab.IsSelected = true;
            StatusText.Text =
                $"Import candidate ready: {result.Manifest.EditedPixels:N0} edited pixel(s); " +
                $"Arkive generated {result.Manifest.GeneratedMipCount:N0} mip(s), restored " +
                $"{result.Manifest.PixelFormat}, and preserved game alpha automatically. No package was modified.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException)
        {
            StatusText.Text = "The edited texture failed import preflight.";
            Log(
                $"Texture import candidate failed: parentExportId={sourceRow.Entry.Manifest.ExportId}, " +
                $"nativeManifest={nativeManifestPath}, editedImage={dialog.FileName}, " +
                $"alphaPolicy={alphaPolicy}, output={outputDirectory}, error={exception}");
            try
            {
                if (Directory.Exists(outputDirectory) &&
                    !File.Exists(Path.Combine(outputDirectory, ExportWorkspaceStore.ManifestFileName)))
                {
                    Directory.Delete(outputDirectory, recursive: true);
                    Log($"Incomplete texture import candidate directory removed: {outputDirectory}");
                }
            }
            catch (Exception cleanupException) when (
                cleanupException is IOException or UnauthorizedAccessException)
            {
                Log(
                    $"Incomplete texture import candidate cleanup failed: directory={outputDirectory}, " +
                    $"error={cleanupException}");
            }

            MessageBox.Show(
                this,
                exception.Message,
                "Arkive texture import preflight",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _textureImportCandidateInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async void VerifyTextureLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress || _textureLayoutConfirmationService is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow sourceRow ||
            string.IsNullOrWhiteSpace(sourceRow.Entry.Manifest.NativeDetailsManifestPath))
        {
            return;
        }

        var nativeManifestPath = Path.GetFullPath(
            sourceRow.Entry.Manifest.NativeDetailsManifestPath);
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            sourceRow.Entry.Manifest.PackageAlias,
            sourceRow.Entry.Manifest.ObjectName + "-layout-confirmation");
        _textureReplacementInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text = $"Verifying replacement layout for {sourceRow.Entry.Manifest.ObjectName}...";
        Log(
            $"Texture layout confirmation started: parentExportId={sourceRow.Entry.Manifest.ExportId}, " +
            $"package={sourceRow.Entry.Manifest.SourcePackagePath}, object={sourceRow.Entry.Manifest.ObjectName}, " +
            $"nativeManifest={nativeManifestPath}, output={outputDirectory}. " +
            "Source is opened read-only; no package or game file will be written.");
        try
        {
            var result = await _textureLayoutConfirmationService.ConfirmAsync(
                new TextureLayoutConfirmationRequest(nativeManifestPath, outputDirectory));
            var entry = await _exportWorkspaceStore.CommitTextureLayoutConfirmationAsync(
                result, sourceRow.Entry);
            Log(
                $"Texture layout confirmation completed in {result.Duration.TotalSeconds:N3}s: " +
                $"confirmationId={result.Manifest.ConfirmationId}, parentExportId={result.Manifest.SourceExportId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"format={result.Manifest.PixelFormat}, dimensions={result.Manifest.Width}x{result.Manifest.Height}, " +
                $"mips={result.Manifest.MipCount}, array=+0x{result.Manifest.MipArrayRelativeOffset:X}.." +
                $"+0x{result.Manifest.MipArrayEndRelativeOffset:X}, " +
                $"opaquePrefixBytes={result.Manifest.OpaqueNativePrefixBytes}, " +
                $"opaqueSuffixBytes={result.Manifest.OpaqueNativeSuffixBytes}, " +
                $"payloadLayoutConfirmed={result.Manifest.MipPayloadLayoutConfirmed}, " +
                $"packageRewriteConfirmed={result.Manifest.PackageRewriteConfirmed}, " +
                $"report={result.ManifestPath}, workspaceManifest={entry.ManifestPath}.");
            foreach (var check in result.Manifest.Checks)
            {
                Log(
                    $"Texture layout confirmation check: status={check.Status}, " +
                    $"code={check.Code}, message={check.Message}");
            }
            foreach (var mip in result.Manifest.Mips)
            {
                Log(
                    $"Texture layout confirmation mip: mip={mip.MipIndex}, dimensions={mip.Width}x{mip.Height}, " +
                    $"header=+0x{mip.HeaderRelativeOffset:X}, payload=+0x{mip.PayloadRelativeOffset:X}, " +
                    $"logical=0x{mip.PayloadLogicalOffset:X}, flags=0x{mip.BulkDataFlags:X8}, " +
                    $"elements={mip.ElementCount}, storedBytes={mip.StoredBytes}, codec={mip.StorageCodec}, " +
                    $"storedSha={mip.StoredSha256}, decodedBytes={mip.DecodedGpuBytes}, " +
                    $"decodedSha={mip.DecodedGpuSha256}, descriptor={mip.DescriptorMatches}, " +
                    $"codecMetadata={mip.CodecMetadataMatches}, ddsMatch={mip.DecodeMatchesExportedDds}.");
            }
            await RefreshAndSelectExportAsync(
                "texture layout confirmation completed", result.Manifest.ConfirmationId);
            ExportsTab.IsSelected = true;
            StatusText.Text =
                $"Replacement-safe mip layout confirmed for {result.Manifest.ObjectName}; " +
                "package rewriting remains deliberately unconfirmed.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException)
        {
            CleanupIncompleteExportDirectory(outputDirectory, "texture layout confirmation");
            StatusText.Text = "Texture replacement layout could not be confirmed.";
            Log(
                $"Texture layout confirmation failed: parentExportId={sourceRow.Entry.Manifest.ExportId}, " +
                $"nativeManifest={nativeManifestPath}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive replacement layout verification",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async void BuildTexturePayloadsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress || _textureReplacementPayloadService is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow candidateRow ||
            string.IsNullOrWhiteSpace(candidateRow.Entry.Manifest.TextureImportCandidateManifestPath))
        {
            return;
        }

        var candidateManifestPath = Path.GetFullPath(
            candidateRow.Entry.Manifest.TextureImportCandidateManifestPath);
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            candidateRow.Entry.Manifest.PackageAlias,
            candidateRow.Entry.Manifest.ObjectName + "-native-payloads");
        _textureReplacementInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text = $"Building verified native mip payloads for {candidateRow.Entry.Manifest.ObjectName}...";
        Log(
            $"Texture replacement payload build started: candidateExportId={candidateRow.Entry.Manifest.ExportId}, " +
            $"package={candidateRow.Entry.Manifest.SourcePackagePath}, " +
            $"object={candidateRow.Entry.Manifest.ObjectName}, candidateManifest={candidateManifestPath}, " +
            $"encoder={_textureReplacementPayloadService.EncoderName}, output={outputDirectory}. " +
            "A fresh layout confirmation is mandatory; output is staging-only and no package will be written.");
        try
        {
            var result = await _textureReplacementPayloadService.BuildAsync(
                new TextureReplacementPayloadRequest(candidateManifestPath, outputDirectory));
            var entry = await _exportWorkspaceStore.CommitTextureReplacementPayloadsAsync(
                result, candidateRow.Entry);
            Log(
                $"Texture replacement payload build completed in {result.Duration.TotalSeconds:N3}s: " +
                $"payloadSetId={result.Manifest.PayloadSetId}, candidateId={result.Manifest.CandidateId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"format={result.Manifest.PixelFormat}, dimensions={result.Manifest.Width}x{result.Manifest.Height}, " +
                $"mips={result.Manifest.MipCount}, copied={result.Manifest.SourcePayloadsCopied}, " +
                $"crnEncoded={result.Manifest.CrunchPayloadsEncoded}, lz4Encoded={result.Manifest.Lz4PayloadsEncoded}, " +
                $"sourceStoredBytes={result.Manifest.SourceStoredBytes}, " +
                $"replacementStoredBytes={result.Manifest.ReplacementStoredBytes}, " +
                $"allDecode={result.Manifest.AllStoragePayloadsDecode}, " +
                $"allQuality={result.Manifest.AllQualityGatesPassed}, " +
                $"writerReady={result.Manifest.PayloadSetReadyForWriter}, " +
                $"packageModified={result.Manifest.PackageWasModified}, report={result.ManifestPath}, " +
                $"layoutReport={result.Manifest.LayoutConfirmationManifestPath}, " +
                $"workspaceManifest={entry.ManifestPath}.");
            foreach (var check in result.Manifest.Checks)
            {
                Log(
                    $"Texture replacement payload check: status={check.Status}, " +
                    $"code={check.Code}, message={check.Message}");
            }
            foreach (var mip in result.Manifest.Mips)
            {
                Log(
                    $"Texture replacement payload mip: mip={mip.MipIndex}, dimensions={mip.Width}x{mip.Height}, " +
                    $"flags=0x{mip.BulkDataFlags:X8}, sourceCodec={mip.SourceStorageCodec}, " +
                    $"action={mip.EncodingAction}, sourceBytes={mip.SourceStoredBytes}, " +
                    $"replacementBytes={mip.ReplacementStoredBytes}, sourceSha={mip.SourceStoredSha256}, " +
                    $"replacementSha={mip.ReplacementStoredSha256}, candidateGpuSha={mip.CandidateGpuSha256}, " +
                    $"decodedGpuSha={mip.DecodeBackGpuSha256}, exactGpu={mip.Quality.ExactGpuBlocks}, " +
                    $"mae={mip.Quality.MeanAbsoluteChannelError:N4}, " +
                    $"rmse={mip.Quality.RootMeanSquareChannelError:N4}, " +
                    $"psnr={(mip.Quality.ExactGpuBlocks ? "exact" : FormatQualityNumber(mip.Quality.PsnrDb))}, " +
                    $"maxError={mip.Quality.MaximumChannelError}, " +
                    $"pixelsAbove16={mip.Quality.PixelsAboveError16}, " +
                    $"meanNormalAngle={mip.Quality.MeanNormalAngularErrorDegrees?.ToString("N3") ?? "n/a"}, " +
                    $"maxNormalAngle={mip.Quality.MaximumNormalAngularErrorDegrees?.ToString("N3") ?? "n/a"}, " +
                    $"quality={mip.Quality.QualityGatePassed}, codecMetadata={mip.CodecMetadataVerified}, " +
                    $"decodeBack={mip.DecodeBackVerified}, output={mip.ReplacementRelativePath}, " +
                    $"encoder={mip.Encoder}, command={mip.EncoderCommand}.");
            }
            await RefreshAndSelectExportAsync(
                "texture replacement payload build completed", result.Manifest.PayloadSetId);
            ExportsTab.IsSelected = true;
            StatusText.Text = result.Manifest.PayloadSetReadyForWriter
                ? $"Verified native mip payloads are ready for a future writer; " +
                  $"{result.Manifest.CrunchPayloadsEncoded:N0} CRN and " +
                  $"{result.Manifest.Lz4PayloadsEncoded:N0} UE3/LZ4 level(s) encoded. No package was modified."
                : "Native mip payloads were created, but at least one decoded quality gate needs review.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                TimeoutException or ArgumentException)
        {
            CleanupIncompleteExportDirectory(outputDirectory, "texture replacement payload build");
            StatusText.Text = "Native texture payload generation failed verification.";
            Log(
                $"Texture replacement payload build failed: candidateExportId={candidateRow.Entry.Manifest.ExportId}, " +
                $"candidateManifest={candidateManifestPath}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive native texture payloads",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async void AssembleTextureExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress || _textureExportAssemblyService is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow payloadRow ||
            string.IsNullOrWhiteSpace(payloadRow.Entry.Manifest.TextureReplacementPayloadManifestPath) ||
            payloadRow.Entry.Manifest.TextureExportAssemblyManifestPath is not null)
        {
            return;
        }
        var payloadManifestPath = Path.GetFullPath(
            payloadRow.Entry.Manifest.TextureReplacementPayloadManifestPath);
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            payloadRow.Entry.Manifest.PackageAlias,
            payloadRow.Entry.Manifest.ObjectName + "-export-assembly");
        _textureReplacementInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text = $"Assembling verified Texture2D export for {payloadRow.Entry.Manifest.ObjectName}...";
        Log(
            $"Texture export assembly started: payloadExportId={payloadRow.Entry.Manifest.ExportId}, " +
            $"package={payloadRow.Entry.Manifest.SourcePackagePath}, object={payloadRow.Entry.Manifest.ObjectName}, " +
            $"payloadManifest={payloadManifestPath}, output={outputDirectory}. " +
            "Arkive will prove a byte-identical no-op reconstruction before emitting the replacement blob; " +
            "no package or game file will be written.");
        try
        {
            var result = await _textureExportAssemblyService.AssembleAsync(
                new TextureExportAssemblyRequest(payloadManifestPath, outputDirectory));
            var entry = await _exportWorkspaceStore.CommitTextureExportAssemblyAsync(
                result, payloadRow.Entry);
            Log(
                $"Texture export assembly completed in {result.Duration.TotalSeconds:N3}s: " +
                $"assemblyId={result.Manifest.AssemblyId}, payloadSetId={result.Manifest.PayloadSetId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"format={result.Manifest.PixelFormat}, dimensions={result.Manifest.Width}x{result.Manifest.Height}, " +
                $"mips={result.Manifest.MipCount}, serialOffset=0x{result.Manifest.SerialOffset:X}, " +
                $"sourceBytes={result.Manifest.SourceSerialSize}, sourceSha={result.Manifest.SourceExportSha256}, " +
                $"noopBytes={result.Manifest.NoOpExportBytes}, noopSha={result.Manifest.NoOpExportSha256}, " +
                $"noopIdentical={result.Manifest.NoOpExportByteIdentical}, " +
                $"replacementBytes={result.Manifest.ReplacementExportBytes}, " +
                $"replacementSha={result.Manifest.ReplacementExportSha256}, " +
                $"sizeDelta={result.Manifest.ReplacementSizeDelta:+#;-#;0}, " +
                $"prefixBytes={result.Manifest.PreservedPrefixBytes}, suffixBytes={result.Manifest.PreservedSuffixBytes}, " +
                $"nonMipPreserved={result.Manifest.AllNonMipBytesPreserved}, " +
                $"descriptors={result.Manifest.AllDescriptorsReplayed}, decode={result.Manifest.AllStoragePayloadsDecode}, " +
                $"quality={result.Manifest.AllQualityGatesRetained}, " +
                $"planReady={result.Manifest.ReplacementBlobReadyForRewritePlan}, " +
                $"packageModified={result.Manifest.PackageWasModified}, report={result.ManifestPath}, " +
                $"workspaceManifest={entry.ManifestPath}.");
            foreach (var check in result.Manifest.Checks)
            {
                Log($"Texture export assembly check: status={check.Status}, code={check.Code}, message={check.Message}");
            }
            foreach (var mip in result.Manifest.Mips)
            {
                Log(
                    $"Texture export assembly mip: mip={mip.MipIndex}, dimensions={mip.Width}x{mip.Height}, " +
                    $"flags=0x{mip.BulkDataFlags:X8}, codec={mip.StorageCodec}, " +
                    $"header=+0x{mip.HeaderRelativeOffset:X}, payload=+0x{mip.PayloadRelativeOffset:X}, " +
                    $"logical=0x{mip.PayloadLogicalOffset:X}, elements={mip.SourceElementCount}->{mip.ReplacementElementCount}, " +
                    $"storedBytes={mip.SourceStoredBytes}->{mip.ReplacementStoredBytes}, " +
                    $"storedSha={mip.ReplacementStoredSha256}, decodedSha={mip.DecodedGpuSha256}, " +
                    $"descriptor={mip.DescriptorReplayed}, codecMetadata={mip.CodecMetadataVerified}, " +
                    $"decodeBack={mip.DecodeBackVerified}, quality={mip.QualityGateRetained}.");
            }
            await RefreshAndSelectExportAsync("texture export assembly completed", result.Manifest.AssemblyId);
            ExportsTab.IsSelected = true;
            StatusText.Text =
                $"Verified Texture2D export blob ready; no-op is byte-identical and replacement size changed " +
                $"by {result.Manifest.ReplacementSizeDelta:+#,0;-#,0;0} bytes. No package was modified.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            CleanupIncompleteExportDirectory(outputDirectory, "texture export assembly");
            StatusText.Text = "Texture2D export assembly failed verification.";
            Log(
                $"Texture export assembly failed: payloadExportId={payloadRow.Entry.Manifest.ExportId}, " +
                $"payloadManifest={payloadManifestPath}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive Texture2D export assembly",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async void PlanPackageRewriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress || _packageRewritePlanService is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow assemblyRow ||
            string.IsNullOrWhiteSpace(assemblyRow.Entry.Manifest.TextureExportAssemblyManifestPath) ||
            assemblyRow.Entry.Manifest.PackageRewritePlanManifestPath is not null)
        {
            return;
        }
        var assemblyManifestPath = Path.GetFullPath(
            assemblyRow.Entry.Manifest.TextureExportAssemblyManifestPath);
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            assemblyRow.Entry.Manifest.PackageAlias,
            assemblyRow.Entry.Manifest.ObjectName + "-rewrite-plan");
        _textureReplacementInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text = $"Planning package rewrite for {assemblyRow.Entry.Manifest.ObjectName}...";
        Log(
            $"Package rewrite dry run started: assemblyExportId={assemblyRow.Entry.Manifest.ExportId}, " +
            $"package={assemblyRow.Entry.Manifest.SourcePackagePath}, object={assemblyRow.Entry.Manifest.ObjectName}, " +
            $"assemblyManifest={assemblyManifestPath}, output={outputDirectory}. " +
            "This operation reads package/export tables and calculates mutations only; no logical image, UPK, " +
            "source package, or game file will be written.");
        try
        {
            var result = await _packageRewritePlanService.PlanAsync(
                new PackageRewritePlanRequest(assemblyManifestPath, outputDirectory));
            var entry = await _exportWorkspaceStore.CommitPackageRewritePlanAsync(
                result, assemblyRow.Entry);
            Log(
                $"Package rewrite dry run completed in {result.Duration.TotalSeconds:N3}s: " +
                $"planId={result.Manifest.PlanId}, assemblyId={result.Manifest.AssemblyId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"strategy={result.Manifest.SelectedStrategy}, " +
                $"sourceLogicalBytes={result.Manifest.SourceLogicalPackageBytes}, " +
                $"plannedLogicalBytes={result.Manifest.PlannedLogicalPackageBytes}, " +
                $"growth={result.Manifest.PlannedLogicalGrowthBytes}, padding={result.Manifest.AppendPaddingBytes}, " +
                $"oldSerial=0x{result.Manifest.SourceSerialOffset:X}+0x{result.Manifest.SourceSerialSize:X}, " +
                $"plannedSerial=0x{result.Manifest.PlannedSerialOffset:X}+0x{result.Manifest.PlannedSerialSize:X}, " +
                $"inlineDelta={result.Manifest.InlineSpliceSizeDelta:+#;-#;0}, " +
                $"inlineRelocations={result.Manifest.InlineSpliceRelocatedExportCount}, " +
                $"existingExportsRelocated={result.Manifest.ExistingExportsRelocated}, " +
                $"existingPayloadsModified={result.Manifest.ExistingExportPayloadsModified}, " +
                $"tablePatches={result.Manifest.SelectedTablePatches.Count}, " +
                $"bulkRebases={result.Manifest.BulkOffsetRebases.Count}, " +
                $"logicalPlanReady={result.Manifest.PlanReadyForLogicalImageWriter}, " +
                $"physicalPlanReady={result.Manifest.PhysicalChunkPlanReady}, " +
                $"packageWriterReady={result.Manifest.PackageFileWriterReady}, " +
                $"packageModified={result.Manifest.PackageWasModified}, report={result.ManifestPath}, " +
                $"workspaceManifest={entry.ManifestPath}.");
            foreach (var check in result.Manifest.Checks)
            {
                Log($"Package rewrite plan check: status={check.Status}, code={check.Code}, message={check.Message}");
            }
            foreach (var patch in result.Manifest.SelectedTablePatches)
            {
                Log(
                    $"Package rewrite table patch: export={patch.ExportIndex}, field={patch.Field}, " +
                    $"logical=0x{patch.FieldLogicalOffset:X}, original={patch.OriginalValue}, " +
                    $"planned={patch.PlannedValue}, storage={patch.LogicalStorage}, reason={patch.Reason}");
            }
            foreach (var rebase in result.Manifest.BulkOffsetRebases)
            {
                Log(
                    $"Package rewrite bulk rebase: mip={rebase.MipIndex}, fieldRelative=+0x{rebase.FieldRelativeOffset:X}, " +
                    $"assembly=0x{rebase.AssemblyValue:X}, planned=0x{rebase.PlannedValue:X}, " +
                    $"payloadRelative=+0x{rebase.PayloadRelativeOffset:X}.");
            }
            foreach (var relocation in result.Manifest.ExportRelocations.Where(item => item.IsTarget || item.WouldRelocateInline))
            {
                Log(
                    $"Package rewrite strategy comparison: export={relocation.ExportIndex}, " +
                    $"object={relocation.ObjectName}, class={relocation.ClassName}, size={relocation.SerialSize}, " +
                    $"original=0x{relocation.OriginalSerialOffset:X}, " +
                    $"inline=0x{relocation.InlineSpliceSerialOffset:X}, " +
                    $"selected=0x{relocation.SelectedSerialOffset:X}, " +
                    $"wouldRelocateInline={relocation.WouldRelocateInline}, target={relocation.IsTarget}.");
            }
            foreach (var impact in result.Manifest.ChunkImpacts)
            {
                Log(
                    $"Package rewrite chunk impact: chunk={impact.ChunkIndex?.ToString() ?? "append/raw"}, " +
                    $"logical=0x{impact.LogicalOffset:X}+0x{impact.LogicalBytes:X}, " +
                    $"impact={impact.Impact}, required={impact.RequiredAction}");
            }
            await RefreshAndSelectExportAsync("package rewrite dry run completed", result.Manifest.PlanId);
            ExportsTab.IsSelected = true;
            StatusText.Text =
                "Package rewrite dry run is complete. The append-and-retarget logical plan is ready; " +
                "physical chunk writing remains disabled and no package was modified.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            CleanupIncompleteExportDirectory(outputDirectory, "package rewrite dry run");
            StatusText.Text = "Package rewrite planning failed verification.";
            Log(
                $"Package rewrite dry run failed: assemblyExportId={assemblyRow.Entry.Manifest.ExportId}, " +
                $"assemblyManifest={assemblyManifestPath}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive package rewrite dry run",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async void BuildLogicalPackageImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress || _logicalPackageImageService is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow planRow ||
            string.IsNullOrWhiteSpace(planRow.Entry.Manifest.PackageRewritePlanManifestPath) ||
            planRow.Entry.Manifest.LogicalPackageImageManifestPath is not null)
        {
            return;
        }
        var planManifestPath = Path.GetFullPath(
            planRow.Entry.Manifest.PackageRewritePlanManifestPath);
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            planRow.Entry.Manifest.PackageAlias,
            planRow.Entry.Manifest.ObjectName + "-logical-image");
        _textureReplacementInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text = $"Building verified logical package image for {planRow.Entry.Manifest.ObjectName}...";
        Log(
            $"Logical package image build started: planExportId={planRow.Entry.Manifest.ExportId}, " +
            $"package={planRow.Entry.Manifest.SourcePackagePath}, object={planRow.Entry.Manifest.ObjectName}, " +
            $"planManifest={planManifestPath}, output={outputDirectory}. " +
            "Arkive will copy the decoded logical package to staging, replay only the audited table/bulk " +
            "patches, append the replacement export, and compare every retained source byte. The source UPK " +
            "will remain read-only and no physical package will be emitted.");
        try
        {
            var result = await _logicalPackageImageService.BuildAsync(
                new LogicalPackageImageRequest(planManifestPath, outputDirectory));
            var entry = await _exportWorkspaceStore.CommitLogicalPackageImageAsync(
                result, planRow.Entry);
            Log(
                $"Logical package image build completed in {result.Duration.TotalSeconds:N3}s: " +
                $"imageId={result.Manifest.ImageId}, planId={result.Manifest.PlanId}, " +
                $"assemblyId={result.Manifest.AssemblyId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"sourceLogicalBytes={result.Manifest.SourceLogicalPackageBytes}, " +
                $"imageBytes={result.Manifest.LogicalImageBytes}, imageSha={result.Manifest.LogicalImageSha256}, " +
                $"sourceLogicalSha={result.Manifest.SourceLogicalPackageSha256}, " +
                $"auditedPatchBytes={result.Manifest.AuditedPatchBytes}, " +
                $"unplannedBytesCompared={result.Manifest.UnplannedSourceBytesCompared}, " +
                $"unplannedExact={result.Manifest.AllUnplannedSourceBytesIdentical}, " +
                $"tablePatches={result.Manifest.TablePatches.Count}, " +
                $"bulkRebases={result.Manifest.BulkOffsetRebases.Count}, " +
                $"originalExportRetained={result.Manifest.OriginalExportBytesRetained}, " +
                $"appendedVerified={result.Manifest.AppendedReplacementVerified}, " +
                $"logicalImageReady={result.Manifest.LogicalImageReadyForPhysicalWriter}, " +
                $"physicalWriterReady={result.Manifest.PhysicalChunkWriterReady}, " +
                $"packageWriterReady={result.Manifest.PackageFileWriterReady}, " +
                $"packageModified={result.Manifest.PackageWasModified}, image={result.LogicalImagePath}, " +
                $"report={result.ManifestPath}, workspaceManifest={entry.ManifestPath}.");
            foreach (var check in result.Manifest.Checks)
            {
                Log($"Logical package image check: status={check.Status}, code={check.Code}, message={check.Message}");
            }
            foreach (var patch in result.Manifest.TablePatches)
            {
                Log(
                    $"Logical package image table patch: export={patch.ExportIndex}, field={patch.Field}, " +
                    $"logical=0x{patch.FieldLogicalOffset:X}, original={patch.ExpectedOriginalValue}, " +
                    $"planned={patch.PlannedValue}, verified={patch.VerifiedImageValue}.");
            }
            foreach (var rebase in result.Manifest.BulkOffsetRebases)
            {
                Log(
                    $"Logical package image bulk rebase: mip={rebase.MipIndex}, " +
                    $"fieldRelative=+0x{rebase.FieldRelativeOffset:X}, " +
                    $"fieldLogical=0x{rebase.FieldLogicalOffset:X}, assembly=0x{rebase.AssemblyValue:X}, " +
                    $"planned=0x{rebase.PlannedValue:X}, verified=0x{rebase.VerifiedImageValue:X}, " +
                    $"payloadRelative=+0x{rebase.PayloadRelativeOffset:X}, " +
                    $"payloadLogical=0x{rebase.VerifiedPayloadLogicalOffset:X}.");
            }
            foreach (var range in result.Manifest.Ranges)
            {
                Log(
                    $"Logical package image range: label={range.Label}, " +
                    $"logical=0x{range.LogicalOffset:X}+0x{range.Bytes:X}, " +
                    $"sourceSha={range.SourceSha256 ?? "n/a"}, imageSha={range.ImageSha256}, " +
                    $"verified={range.Verified}, meaning={range.Meaning}");
            }
            await RefreshAndSelectExportAsync(
                "logical package image build completed", result.Manifest.ImageId);
            ExportsTab.IsSelected = true;
            StatusText.Text =
                "Verified logical staging image created. Every unplanned source byte is identical; " +
                "physical chunk writing remains disabled and no package was modified.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            CleanupIncompleteExportDirectory(outputDirectory, "logical package image build");
            StatusText.Text = "Logical package image construction failed verification.";
            Log(
                $"Logical package image build failed: planExportId={planRow.Entry.Manifest.ExportId}, " +
                $"planManifest={planManifestPath}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive logical package image",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async void PlanPhysicalPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress || _physicalPackagePlanService is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow imageRow ||
            string.IsNullOrWhiteSpace(imageRow.Entry.Manifest.LogicalPackageImageManifestPath) ||
            imageRow.Entry.Manifest.PhysicalPackagePlanManifestPath is not null)
        {
            return;
        }
        var imageManifestPath = Path.GetFullPath(
            imageRow.Entry.Manifest.LogicalPackageImageManifestPath);
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            imageRow.Entry.Manifest.PackageAlias,
            imageRow.Entry.Manifest.ObjectName + "-physical-plan");
        _textureReplacementInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text = $"Planning physical chunks for {imageRow.Entry.Manifest.ObjectName}...";
        Log(
            $"Physical package planning started: logicalImageExportId={imageRow.Entry.Manifest.ExportId}, " +
            $"package={imageRow.Entry.Manifest.SourcePackagePath}, object={imageRow.Entry.Manifest.ObjectName}, " +
            $"logicalImageManifest={imageManifestPath}, output={outputDirectory}. " +
            "Arkive will parse and hash every physical prefix, chunk envelope, block descriptor, protection " +
            "prefix, raw range, copy region, and future replacement impact. Planning is read-only.");
        try
        {
            var result = await _physicalPackagePlanService.PlanAsync(
                new PhysicalPackagePlanRequest(imageManifestPath, outputDirectory));
            var entry = await _exportWorkspaceStore.CommitPhysicalPackagePlanAsync(
                result, imageRow.Entry);
            Log(
                $"Physical package planning completed in {result.Duration.TotalSeconds:N3}s: " +
                $"physicalPlanId={result.Manifest.PhysicalPlanId}, logicalImageId={result.Manifest.LogicalImageId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"physicalBytes={result.Manifest.SourcePhysicalPackageBytes}, " +
                $"logicalBytes={result.Manifest.SourceLogicalPackageBytes}, " +
                $"physicalSha={result.Manifest.SourcePackageSha256}, " +
                $"logicalSha={result.Manifest.SourceLogicalPackageSha256}, " +
                $"chunks={result.Manifest.CompressedChunkCount}, protected={result.Manifest.ProtectedChunkCount}, " +
                $"raw={result.Manifest.RawChunkCount}, blocks={result.Manifest.TotalBlockCount}, " +
                $"prefixBytes={result.Manifest.PhysicalPrefixBytes}, copyRegions={result.Manifest.CopyRegions.Count}, " +
                $"replacementAffectedChunks={result.Manifest.ReplacementAffectedChunkCount}, " +
                $"envelopes={result.Manifest.AllChunkEnvelopesParsed}, " +
                $"protection={result.Manifest.AllProtectedBlocksReadable}, " +
                $"copyCoverage={result.Manifest.CopyRegionsCoverPhysicalFile}, " +
                $"noopReady={result.Manifest.PlanReadyForNoOpReconstruction}, " +
                $"replacementLayout={result.Manifest.ReplacementChunkLayoutUnderstood}, " +
                $"replacementWriter={result.Manifest.ReplacementPhysicalWriterReady}, " +
                $"packageModified={result.Manifest.PackageWasModified}, report={result.ManifestPath}, " +
                $"workspaceManifest={entry.ManifestPath}.");
            foreach (var check in result.Manifest.Checks)
            {
                Log($"Physical package plan check: status={check.Status}, code={check.Code}, message={check.Message}");
            }
            foreach (var chunk in result.Manifest.Chunks)
            {
                Log(
                    $"Physical package plan chunk: chunk={chunk.ChunkIndex}, " +
                    $"record=0x{chunk.RecordPhysicalOffset:X}, " +
                    $"logical=0x{chunk.LogicalOffset:X}+0x{chunk.LogicalBytes:X}, " +
                    $"physical=0x{chunk.PhysicalOffset:X}+0x{chunk.PhysicalBytes:X}, " +
                    $"flags=0x{chunk.LostArkFlags:X8}, storage={chunk.StorageKind}, " +
                    $"blockSize={chunk.BlockSize?.ToString() ?? "raw"}, blocks={chunk.BlockCount}, " +
                    $"physicalSha={chunk.PhysicalSha256}, logicalSha={chunk.LogicalSha256}, " +
                    $"envelope={chunk.EnvelopeParsed}, protection={chunk.ProtectionReadable}, " +
                    $"tablePatch={chunk.ContainsReplacementTablePatch}, append={chunk.ReceivesLogicalAppend}, " +
                    $"plannedLogicalBytes={chunk.PlannedReplacementLogicalBytes}, action={chunk.ReplacementAction}");
                foreach (var block in chunk.Blocks)
                {
                    Log(
                        $"Physical package plan block: chunk={chunk.ChunkIndex}, block={block.BlockIndex}, " +
                        $"descriptor=0x{block.DescriptorPhysicalOffset:X}, payload=0x{block.PayloadPhysicalOffset:X}, " +
                        $"storedBytes={block.StoredBytes}, rawBytes={block.UncompressedBytes}, " +
                        $"protectedPrefix={block.ProtectedPrefixBytes}, storedSha={block.StoredSha256}, " +
                        $"plainSha={block.PlainCompressedSha256}, protectionReplayed={block.ProtectionReplayed}.");
                }
            }
            foreach (var region in result.Manifest.CopyRegions)
            {
                Log(
                    $"Physical package plan region: index={region.RegionIndex}, kind={region.Kind}, " +
                    $"physical=0x{region.PhysicalOffset:X}+0x{region.Bytes:X}, " +
                    $"chunk={region.ChunkIndex?.ToString() ?? "none"}, sha={region.Sha256}, meaning={region.Meaning}");
            }
            await RefreshAndSelectExportAsync(
                "physical package planning completed", result.Manifest.PhysicalPlanId);
            ExportsTab.IsSelected = true;
            StatusText.Text =
                "Physical chunk plan verified. Every source byte is covered and the no-op reconstructor is ready; " +
                "no package was modified.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            CleanupIncompleteExportDirectory(outputDirectory, "physical package planning");
            StatusText.Text = "Physical package planning failed verification.";
            Log(
                $"Physical package planning failed: logicalImageExportId={imageRow.Entry.Manifest.ExportId}, " +
                $"logicalImageManifest={imageManifestPath}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive physical package plan",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async void ReconstructNoOpUpkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress || _noOpUpkReconstructionService is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow physicalRow ||
            string.IsNullOrWhiteSpace(physicalRow.Entry.Manifest.PhysicalPackagePlanManifestPath) ||
            physicalRow.Entry.Manifest.NoOpUpkReconstructionManifestPath is not null)
        {
            return;
        }
        var physicalPlanPath = Path.GetFullPath(
            physicalRow.Entry.Manifest.PhysicalPackagePlanManifestPath);
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            physicalRow.Entry.Manifest.PackageAlias,
            physicalRow.Entry.Manifest.ObjectName + "-noop-upk");
        _textureReplacementInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text = $"Reconstructing source-identical no-op UPK for {physicalRow.Entry.Manifest.ObjectName}...";
        Log(
            $"No-op UPK reconstruction started: physicalPlanExportId={physicalRow.Entry.Manifest.ExportId}, " +
            $"package={physicalRow.Entry.Manifest.SourcePackagePath}, object={physicalRow.Entry.Manifest.ObjectName}, " +
            $"physicalPlanManifest={physicalPlanPath}, output={outputDirectory}. " +
            "Arkive will reassemble every audited source region into a separate staging UPK and require exact " +
            "physical, logical, summary, chunk-table, catalogue, and structural equivalence. It contains no " +
            "replacement and the source/game package remains read-only.");
        try
        {
            var result = await _noOpUpkReconstructionService.ReconstructAsync(
                new NoOpUpkReconstructionRequest(physicalPlanPath, outputDirectory));
            var entry = await _exportWorkspaceStore.CommitNoOpUpkReconstructionAsync(
                result, physicalRow.Entry);
            Log(
                $"No-op UPK reconstruction completed in {result.Duration.TotalSeconds:N3}s: " +
                $"reconstructionId={result.Manifest.ReconstructionId}, " +
                $"physicalPlanId={result.Manifest.PhysicalPlanId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"physicalBytes={result.Manifest.OutputPhysicalPackageBytes}, " +
                $"outputSha={result.Manifest.OutputPackageSha256}, " +
                $"logicalBytes={result.Manifest.LogicalPackageBytes}, " +
                $"logicalSha={result.Manifest.OutputLogicalPackageSha256}, " +
                $"catalogueSha={result.Manifest.OutputCatalogueSha256}, chunks={result.Manifest.ChunkCount}, " +
                $"copyRegions={result.Manifest.CopyRegionCount}, " +
                $"regionsExact={result.Manifest.AllCopyRegionsByteIdentical}, " +
                $"physicalExact={result.Manifest.PhysicalFileByteIdentical}, " +
                $"logicalExact={result.Manifest.LogicalImageByteIdentical}, " +
                $"summary={result.Manifest.PackageSummaryEquivalent}, " +
                $"chunkTable={result.Manifest.ChunkTableEquivalent}, " +
                $"catalogue={result.Manifest.AssetCatalogueEquivalent}, " +
                $"structural={result.Manifest.StructuralValidationPassed}, " +
                $"proof={result.Manifest.NoOpReconstructionProofPassed}, " +
                $"containsReplacement={result.Manifest.OutputContainsReplacement}, " +
                $"replacementWriter={result.Manifest.ReplacementPhysicalWriterReady}, " +
                $"packageModified={result.Manifest.PackageWasModified}, outputUpk={result.OutputUpkPath}, " +
                $"report={result.ManifestPath}, workspaceManifest={entry.ManifestPath}.");
            foreach (var check in result.Manifest.Checks)
            {
                Log($"No-op UPK reconstruction check: status={check.Status}, code={check.Code}, message={check.Message}");
            }
            foreach (var region in result.Manifest.CopyRegions)
            {
                Log(
                    $"No-op UPK reconstruction region: index={region.RegionIndex}, kind={region.Kind}, " +
                    $"physical=0x{region.PhysicalOffset:X}+0x{region.Bytes:X}, " +
                    $"chunk={region.ChunkIndex?.ToString() ?? "none"}, plannedSha={region.PlannedSha256}, " +
                    $"writtenSha={region.WrittenSha256}, byteIdentical={region.ByteIdentical}.");
            }
            foreach (var issue in result.Manifest.ValidationIssues)
            {
                Log(
                    $"No-op UPK structural validation: severity={issue.Severity}, " +
                    $"code={issue.Code}, message={issue.Message}");
            }
            await RefreshAndSelectExportAsync(
                "no-op UPK reconstruction completed", result.Manifest.ReconstructionId);
            ExportsTab.IsSelected = true;
            StatusText.Text =
                "Source-identical no-op UPK proof passed. The output is staging-only and contains no replacement; " +
                "select this session to build the replacement-bearing staged mod UPK.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            CleanupIncompleteExportDirectory(outputDirectory, "no-op UPK reconstruction");
            StatusText.Text = "No-op UPK reconstruction failed verification.";
            Log(
                $"No-op UPK reconstruction failed: physicalPlanExportId={physicalRow.Entry.Manifest.ExportId}, " +
                $"physicalPlanManifest={physicalPlanPath}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive no-op UPK reconstruction",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async void BuildReplacementUpkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress || _replacementUpkBuildService is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow noOpRow ||
            string.IsNullOrWhiteSpace(noOpRow.Entry.Manifest.NoOpUpkReconstructionManifestPath) ||
            noOpRow.Entry.Manifest.ReplacementUpkBuildManifestPath is not null)
        {
            return;
        }
        var noOpManifestPath = Path.GetFullPath(
            noOpRow.Entry.Manifest.NoOpUpkReconstructionManifestPath);
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            noOpRow.Entry.Manifest.PackageAlias,
            noOpRow.Entry.Manifest.ObjectName + "-staged-mod-upk");
        _textureReplacementInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text = $"Building validated staged mod UPK for {noOpRow.Entry.Manifest.ObjectName}...";
        Log(
            $"Replacement staging-UPK build started: noOpExportId={noOpRow.Entry.Manifest.ExportId}, " +
            $"package={noOpRow.Entry.Manifest.SourcePackagePath}, object={noOpRow.Entry.Manifest.ObjectName}, " +
            $"noOpManifest={noOpManifestPath}, output={outputDirectory}. Arkive will require the no-op proof, " +
            "rebuild only affected chunks, protect changed blocks, recalculate the physical chunk table, " +
            "decode the result, verify the target and every unrelated export, and write only to staging. " +
            "This stage cannot write the game package; deployment requires a later verified mod-bundle action and confirmation.");
        try
        {
            var result = await _replacementUpkBuildService.BuildAsync(
                new ReplacementUpkBuildRequest(noOpManifestPath, outputDirectory));
            var entry = await _exportWorkspaceStore.CommitReplacementUpkBuildAsync(
                result, noOpRow.Entry);
            Log(
                $"Replacement staging-UPK build completed in {result.Duration.TotalSeconds:N3}s: " +
                $"buildId={result.Manifest.BuildId}, noOpId={result.Manifest.NoOpReconstructionId}, " +
                $"physicalPlanId={result.Manifest.PhysicalPlanId}, " +
                $"object={result.Manifest.PackageAlias}.{result.Manifest.ObjectName}, " +
                $"physicalBytes={result.Manifest.OutputPhysicalPackageBytes}, outputSha={result.Manifest.OutputPackageSha256}, " +
                $"logicalBytes={result.Manifest.OutputLogicalPackageBytes}, logicalSha={result.Manifest.OutputLogicalPackageSha256}, " +
                $"chunks={result.Manifest.ChunkCount}, rebuilt={result.Manifest.RebuiltChunkCount}, " +
                $"retained={result.Manifest.ByteIdenticalChunkCount}, protectedRebuilt={result.Manifest.ProtectedRebuiltChunkCount}, " +
                $"blocks={result.Manifest.EncodedBlockCount}, blockDecode={result.Manifest.AllEncodedBlocksDecodeBack}, " +
                $"chunkTable={result.Manifest.ChunkTableRecalculated}, logicalExact={result.Manifest.ExpectedLogicalImageMatched}, " +
                $"targetRetargeted={result.Manifest.TargetExportRetargeted}, originalRetained={result.Manifest.OriginalExportBytesRetained}, " +
                $"replacementVerified={result.Manifest.ReplacementExportBytesVerified}, " +
                $"unrelatedExports={result.Manifest.UnrelatedExportsEquivalent}, catalogueShape={result.Manifest.AssetCatalogueShapeRetained}, " +
                $"structural={result.Manifest.StructuralValidationPassed}, targetNativeDecode={result.Manifest.TargetTextureNativeDecodePassed}, " +
                $"proof={result.Manifest.ReplacementPackageBuildPassed}, " +
                $"containsReplacement={result.Manifest.OutputContainsReplacement}, deployment={result.Manifest.DeploymentReady}, " +
                $"sourceModified={result.Manifest.PackageWasModified}, outputUpk={result.OutputUpkPath}, " +
                $"report={result.ManifestPath}, workspaceManifest={entry.ManifestPath}.");
            foreach (var check in result.Manifest.Checks)
            {
                Log($"Replacement staging-UPK check: status={check.Status}, code={check.Code}, message={check.Message}");
            }
            foreach (var chunk in result.Manifest.Chunks)
            {
                Log(
                    $"Replacement staging-UPK chunk: index={chunk.ChunkIndex}, rebuilt={chunk.Rebuilt}, " +
                    $"sourcePhysical=0x{chunk.SourcePhysicalOffset:X}+0x{chunk.SourcePhysicalBytes:X}, " +
                    $"outputPhysical=0x{chunk.OutputPhysicalOffset:X}+0x{chunk.OutputPhysicalBytes:X}, " +
                    $"outputLogical=0x{chunk.OutputLogicalOffset:X}+0x{chunk.OutputLogicalBytes:X}, " +
                    $"flags=0x{chunk.LostArkFlags:X}, blocks={chunk.Blocks.Count}, " +
                    $"sourceExact={chunk.SourcePhysicalBytesRetained}, physicalSha={chunk.OutputPhysicalSha256}, " +
                    $"logicalSha={chunk.OutputLogicalSha256}, action={chunk.Action}");
                foreach (var block in chunk.Blocks)
                {
                    Log(
                        $"Replacement staging-UPK block: chunk={chunk.ChunkIndex}, block={block.BlockIndex}, " +
                        $"logicalBytes={block.LogicalBytes}, storedBytes={block.PlainStoredBytes}, " +
                        $"storedRaw={block.StoredRaw}, protectedPrefix={block.ProtectedPrefixBytes}, " +
                        $"plainSha={block.PlainStoredSha256}, physicalSha={block.PhysicalStoredSha256}, " +
                        $"decodeBack={block.DecodeBackVerified}");
                }
            }
            foreach (var patch in result.Manifest.ChunkTablePatches.Where(item => item.Changed))
            {
                Log(
                    $"Replacement staging-UPK chunk-table patch: chunk={patch.ChunkIndex}, field={patch.Field}, " +
                    $"physical=0x{patch.PhysicalOffset:X}, source=0x{patch.SourceValue:X}, output=0x{patch.OutputValue:X}");
            }
            foreach (var issue in result.Manifest.ValidationIssues)
            {
                Log(
                    $"Replacement staging-UPK structural validation: severity={issue.Severity}, " +
                    $"code={issue.Code}, message={issue.Message}");
            }
            await RefreshAndSelectExportAsync(
                "replacement staging-UPK build completed", result.Manifest.BuildId);
            ExportsTab.IsSelected = true;
            StatusText.Text =
                "Replacement-bearing UPK passed staging validation. The installed game package was not changed; " +
                "deployment remains disabled.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            CleanupIncompleteExportDirectory(outputDirectory, "replacement staging-UPK build");
            StatusText.Text = "Replacement staging-UPK build failed verification.";
            Log(
                $"Replacement staging-UPK build failed: noOpExportId={noOpRow.Entry.Manifest.ExportId}, " +
                $"noOpManifest={noOpManifestPath}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this, exception.Message, "Arkive staged mod UPK",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async void PrepareModBundleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textureReplacementInProgress || _modBundleService is null ||
            ExportWorkspaceGrid.SelectedItem is not ExportWorkspaceRow replacementRow ||
            string.IsNullOrWhiteSpace(replacementRow.Entry.Manifest.ReplacementUpkBuildManifestPath) ||
            replacementRow.Entry.Manifest.ModBundleManifestPath is not null)
        {
            return;
        }

        var replacementManifestPath = Path.GetFullPath(
            replacementRow.Entry.Manifest.ReplacementUpkBuildManifestPath);
        var outputDirectory = _exportWorkspaceStore.CreateSessionDirectory(
            replacementRow.Entry.Manifest.PackageAlias,
            replacementRow.Entry.Manifest.ObjectName + "-mod-bundle");
        var backupRoot = Path.Combine(_projectRoot, "backups", "packages");
        var friendlyName = replacementRow.Entry.Manifest.InGameNames.FirstOrDefault();
        var displayName = string.IsNullOrWhiteSpace(friendlyName)
            ? $"{replacementRow.Entry.Manifest.ObjectName} texture mod"
            : $"{friendlyName} texture mod";

        _textureReplacementInProgress = true;
        UpdateExportWorkspaceActionState();
        StatusText.Text = $"Preparing reversible mod bundle for {replacementRow.Entry.Manifest.ObjectName}...";
        Log(
            $"Mod-bundle rehearsal started: replacementExportId={replacementRow.Entry.Manifest.ExportId}, " +
            $"replacementManifest={replacementManifestPath}, source={replacementRow.Entry.Manifest.SourcePackagePath}, " +
            $"object={replacementRow.Entry.Manifest.ObjectName}, displayName={displayName}, " +
            $"output={outputDirectory}, proposedBackupRoot={backupRoot}. " +
            "Arkive will copy the staged UPK under the original physical filename, recheck source and payload hashes, " +
            "compare package identity, require UModel catalogue plus native export, calculate backup capacity, and " +
            "write only to Arkive staging. No live game file or backup will be created or changed.");
        try
        {
            var result = await _modBundleService.BuildAsync(
                new ArkiveModBundleRequest(
                    replacementManifestPath,
                    outputDirectory,
                    backupRoot,
                    displayName));
            var entry = await _exportWorkspaceStore.CommitModBundleAsync(
                result,
                replacementRow.Entry);
            Log(
                $"Mod-bundle rehearsal completed in {result.Duration.TotalSeconds:N3}s: " +
                $"modId={result.Bundle.ModId}, auditId={result.Readiness.AuditId}, " +
                $"object={result.Bundle.Assets[0].PackageAlias}.{result.Bundle.Assets[0].ObjectName}, " +
                $"sourceSha={result.Bundle.RequiredSourcePackageSha256}, payloadSha={result.Bundle.PayloadSha256}, " +
                $"payloadBytes={result.Bundle.PayloadBytes}, relativeTarget={result.Bundle.SourcePackageRelativePath}, " +
                $"sourceMatch={result.Readiness.SourceIdentityMatches}, payloadMatch={result.Readiness.PayloadIdentityMatchesManifest}, " +
                $"physicalName={result.Readiness.PayloadUsesOriginalPhysicalName}, summary={result.Readiness.PackageSummaryIdentityRetained}, " +
                $"structural={result.Readiness.StructuralValidationPassed}, umodelList={result.Readiness.ExternalCataloguePassed}, " +
                $"umodelNative={result.Readiness.ExternalNativeLoadPassed}, targetFound={result.Readiness.ExpectedObjectFound}, " +
                $"backupBytes={result.Readiness.RequiredBackupBytes}, availableBytes={result.Readiness.AvailableBackupDriveBytes}, " +
                $"ready={result.Readiness.ReadyForControlledInstall}, installation={result.Readiness.InstallationPerformed}, " +
                $"gameFilesModified={result.Readiness.GameFilesModified}, bundle={result.BundleManifestPath}, " +
                $"readiness={result.ReadinessManifestPath}, payload={result.PayloadPath}, workspaceManifest={entry.ManifestPath}.");
            foreach (var check in result.Readiness.Checks)
            {
                Log(
                    $"Mod-bundle readiness check: status={check.Status}, " +
                    $"code={check.Code}, message={check.Message}");
            }
            foreach (var diagnostic in result.Readiness.ExternalDiagnostics)
            {
                Log($"Mod-bundle external validation: {diagnostic}");
            }
            await RefreshAndSelectExportAsync(
                "mod-bundle deployment rehearsal completed",
                result.Bundle.ModId);
            ExportsTab.IsSelected = true;
            StatusText.Text =
                "Mod bundle and reversible deployment rehearsal passed. No game file changed yet; " +
                "select this bundle and use Install mod when ready.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.ComponentModel.Win32Exception)
        {
            CleanupIncompleteExportDirectory(outputDirectory, "mod-bundle rehearsal");
            StatusText.Text = "Mod-bundle deployment rehearsal failed validation.";
            Log(
                $"Mod-bundle rehearsal failed: replacementExportId={replacementRow.Entry.Manifest.ExportId}, " +
                $"replacementManifest={replacementManifestPath}, output={outputDirectory}, error={exception}");
            MessageBox.Show(
                this,
                exception.Message,
                "Arkive mod-bundle rehearsal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _textureReplacementInProgress = false;
            UpdateExportWorkspaceActionState();
        }
    }

    private async Task RefreshAndSelectExportAsync(string trigger, string exportId)
    {
        await RefreshExportWorkspaceAsync(trigger);
        if (ExportWorkspaceGrid.ItemsSource is not IEnumerable<ExportWorkspaceRow> rows) return;
        var created = rows.FirstOrDefault(row =>
            row.Entry.Manifest.ExportId.Equals(exportId, StringComparison.OrdinalIgnoreCase));
        if (created is null) return;
        ExportWorkspaceGrid.SelectedItem = created;
        ExportWorkspaceGrid.ScrollIntoView(created);
    }

    private void CleanupIncompleteExportDirectory(string outputDirectory, string operation)
    {
        try
        {
            if (Directory.Exists(outputDirectory) &&
                !File.Exists(Path.Combine(outputDirectory, ExportWorkspaceStore.ManifestFileName)))
            {
                Directory.Delete(outputDirectory, recursive: true);
                Log($"Incomplete {operation} directory removed: {outputDirectory}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Incomplete {operation} cleanup failed: directory={outputDirectory}, error={exception}");
        }
    }

    private void LogTextureImportInspection(string role, ArtifactInspectionResult inspection)
    {
        Log(
            $"Texture import image inspection: role={role}, path={inspection.FilePath}, " +
            $"summary={inspection.Summary}, errors={inspection.ErrorCount}, " +
            $"warnings={inspection.WarningCount}.");
        foreach (var issue in inspection.Issues)
        {
            Log(
                $"Texture import image validation: role={role}, severity={issue.Severity}, " +
                $"code={issue.Code}, message={issue.Message}");
        }
    }

}
