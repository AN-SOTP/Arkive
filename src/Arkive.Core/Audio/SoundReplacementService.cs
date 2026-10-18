using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Arkive.Core.Mods;

namespace Arkive.Core.Audio;

public enum SoundReplacementStatus
{
    /// <summary>The game plays its own audio.</summary>
    Original,
    /// <summary>Arkive's replacement is in the game files.</summary>
    Replaced,
    /// <summary>A game update (or a file check) put the original back; the replacement can be applied again.</summary>
    Undone,
}

/// <summary>A package table entry Arkive repointed at appended audio.</summary>
public sealed record SoundPackagePatch(
    string PackagePath, long EntryOffset, string OriginalEntry, string ReplacementEntry, long DataOffset, long DataLength);

/// <summary>A bank field (codec or stored size) Arkive rewrote.</summary>
public sealed record SoundFieldPatch(string PackagePath, long Offset, uint Original, uint Replacement);

/// <summary>A loose music file Arkive overwrote, with the copy of the original it keeps.</summary>
public sealed record SoundLoosePatch(string Path, string BackupPath, string OriginalSha256, string ReplacementSha256);

public sealed record SoundReplacementRecord(
    uint SourceId,
    string SoundName,
    string ReplacementFileName,
    DateTimeOffset ReplacedAt,
    string StoredWemPath,
    IReadOnlyList<SoundPackagePatch> Packages,
    IReadOnlyList<SoundFieldPatch> Fields,
    IReadOnlyList<SoundLoosePatch> LooseFiles);

/// <summary>A package Arkive appended audio to: its length before Arkive touched it and after Arkive's last write.</summary>
public sealed record SoundPackageBaseline(string PackagePath, long OriginalContentLength, long ArkiveContentLength);

public sealed record SoundReplacementState(
    IReadOnlyList<SoundReplacementRecord> Replacements, IReadOnlyList<SoundPackageBaseline> Packages);

/// <summary>
/// Replaces single sound clips in the game's audio files and puts the originals back.
///
/// A clip inside a .pck package is replaced by appending the new audio to the end of the package and pointing that
/// one table entry at it; every bank that describes the clip gets the new codec and size. Nothing else in the
/// package moves, so restoring is writing the saved entry and bank values back and, once no replacement uses the
/// package, cutting it back to its original length. A loose music file is replaced whole, with a copy of the
/// original kept under backups\sounds. Replacements are uncompressed (PCM), which Wwise always decodes.
/// </summary>
public sealed class SoundReplacementService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _statePath;
    private readonly string _storeDirectory;
    private readonly string _backupDirectory;
    private readonly IModDeploymentProcessGuard _processGuard;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SoundReplacementService(string dataRoot, IModDeploymentProcessGuard processGuard)
    {
        _statePath = Path.Combine(dataRoot, "sounds", "replacements.json");
        _storeDirectory = Path.Combine(dataRoot, "sounds", "replacements");
        _backupDirectory = Path.Combine(dataRoot, "backups", "sounds");
        _processGuard = processGuard;
    }

    public SoundReplacementState LoadState()
    {
        if (!File.Exists(_statePath)) return new SoundReplacementState([], []);
        using var stream = File.OpenRead(_statePath);
        return JsonSerializer.Deserialize<SoundReplacementState>(stream, JsonOptions) ?? new SoundReplacementState([], []);
    }

    public SoundReplacementRecord? Find(uint sourceId) =>
        LoadState().Replacements.FirstOrDefault(record => record.SourceId == sourceId);

    /// <summary>Whether the clip plays Arkive's replacement, its original, or an original a game update restored.</summary>
    public SoundReplacementStatus GetStatus(uint sourceId)
    {
        var record = Find(sourceId);
        if (record is null) return SoundReplacementStatus.Original;
        try
        {
            foreach (var patch in record.Packages)
            {
                using var file = WwiseAudioFile.Open(patch.PackagePath);
                if (!EntryIs(file, patch, patch.ReplacementEntry)) return SoundReplacementStatus.Undone;
            }
            foreach (var patch in record.LooseFiles)
                if (!File.Exists(patch.Path) || !Sha256(patch.Path).Equals(patch.ReplacementSha256, StringComparison.OrdinalIgnoreCase))
                    return SoundReplacementStatus.Undone;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return SoundReplacementStatus.Undone;
        }
        return SoundReplacementStatus.Replaced;
    }

    /// <summary>
    /// Puts <paramref name="wem"/> (a .wem file, normally from <see cref="WemAudio.WritePcmWem"/>) in place of the clip
    /// everywhere the game stores it. A clip that is already replaced is restored first.
    /// </summary>
    public async Task<SoundReplacementRecord> ReplaceAsync(
        SoundClip clip, string soundName, string replacementFileName, byte[] wem, CancellationToken cancellationToken = default)
    {
        var format = WemAudio.ReadFormat(wem) ?? throw new InvalidDataException("The replacement is not a .wem file.");
        if (!format.IsPcm) throw new InvalidDataException("Arkive replaces sounds with uncompressed (PCM) audio only.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureGameClosed();
            if (Find(clip.SourceId) is not null) RestoreLocked(clip.SourceId);
            clip = ReadLive(clip);

            Directory.CreateDirectory(_storeDirectory);
            var storedWem = Path.Combine(_storeDirectory, $"{clip.SourceId}.wem");
            await File.WriteAllBytesAsync(storedWem, wem, cancellationToken).ConfigureAwait(false);

            var state = LoadState();
            var baselines = state.Packages.ToDictionary(item => item.PackagePath, StringComparer.OrdinalIgnoreCase);
            var packages = new List<SoundPackagePatch>();
            var fields = new List<SoundFieldPatch>();
            var looseFiles = new List<SoundLoosePatch>();
            var record = new SoundReplacementRecord(
                clip.SourceId, soundName, replacementFileName, DateTimeOffset.Now, storedWem, packages, fields, looseFiles);

            foreach (var file in clip.Files.Where(file => !file.IsLoose))
            {
                using var package = WwiseAudioFile.Open(file.Path, writable: true);
                var entry = file.Entry!;
                if (!baselines.ContainsKey(file.Path))
                    baselines[file.Path] = new SoundPackageBaseline(file.Path, package.ContentLength, package.ContentLength);
                var block = Math.Max(1u, entry.BlockSize);
                var offset = (package.ContentLength + block - 1) / block * block;
                if (offset / block > uint.MaxValue)
                    throw new InvalidDataException($"{Path.GetFileName(file.Path)} is too large to append audio to.");
                var replacementEntry = entry.EncodeEntry((uint)wem.Length, (uint)(offset / block));
                packages.Add(new SoundPackagePatch(
                    file.Path, entry.EntryOffset, Convert.ToHexString(entry.EncodeEntry(entry.Size, entry.StartBlock)),
                    Convert.ToHexString(replacementEntry), offset, wem.Length));
            }
            foreach (var field in clip.BankFields)
            {
                fields.Add(new SoundFieldPatch(field.PackagePath, field.PluginIdOffset, field.PluginId, WwiseBank.PcmPluginId));
                if (field.StreamType == 0 || field.InMemorySize > wem.Length)
                    fields.Add(new SoundFieldPatch(field.PackagePath, field.InMemorySizeOffset, field.InMemorySize, (uint)wem.Length));
            }
            var scrambled = WwiseAudioFile.Scramble(wem);
            foreach (var file in clip.Files.Where(file => file.IsLoose))
            {
                var backup = Path.Combine(_backupDirectory, clip.SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(file.Path));
                looseFiles.Add(new SoundLoosePatch(file.Path, backup, Sha256(file.Path), Convert.ToHexString(SHA256.HashData(scrambled))));
            }

            // Save the originals before touching the game, so an interrupted replace can always be restored.
            SaveState(state with
            {
                Replacements = [.. state.Replacements, record],
                Packages = [.. baselines.Values],
            });

            foreach (var patch in packages)
            {
                using var package = WwiseAudioFile.Open(patch.PackagePath, writable: true);
                var padding = patch.DataOffset - package.ContentLength;
                if (padding > 0) package.Write(package.ContentLength, new byte[padding]);
                package.Write(patch.DataOffset, wem);
                package.Write(patch.EntryOffset, Convert.FromHexString(patch.ReplacementEntry));
                package.Flush();
                baselines[patch.PackagePath] = baselines[patch.PackagePath] with { ArkiveContentLength = package.ContentLength };
            }
            foreach (var group in fields.GroupBy(field => field.PackagePath, StringComparer.OrdinalIgnoreCase))
            {
                using var package = WwiseAudioFile.Open(group.Key, writable: true);
                foreach (var field in group) WriteUInt32(package, field.Offset, field.Replacement);
                package.Flush();
            }
            foreach (var patch in looseFiles)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(patch.BackupPath)!);
                if (!File.Exists(patch.BackupPath)) File.Copy(patch.Path, patch.BackupPath);
                WriteAtomically(patch.Path, scrambled);
            }

            state = LoadState();
            SaveState(state with { Packages = [.. baselines.Values] });
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Puts the game's original audio back for one clip.</summary>
    public async Task RestoreAsync(uint sourceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureGameClosed();
            RestoreLocked(sourceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RestoreLocked(uint sourceId)
    {
        var state = LoadState();
        var record = state.Replacements.FirstOrDefault(item => item.SourceId == sourceId);
        if (record is null) return;

        foreach (var patch in record.Packages.Where(patch => File.Exists(patch.PackagePath)))
        {
            using var package = WwiseAudioFile.Open(patch.PackagePath, writable: true);
            // Only undo what is still Arkive's: after a game update the entry is the game's own again.
            if (EntryIs(package, patch, patch.ReplacementEntry))
            {
                package.Write(patch.EntryOffset, Convert.FromHexString(patch.OriginalEntry));
                package.Flush();
            }
        }
        foreach (var group in record.Fields.Where(field => File.Exists(field.PackagePath))
                     .GroupBy(field => field.PackagePath, StringComparer.OrdinalIgnoreCase))
        {
            using var package = WwiseAudioFile.Open(group.Key, writable: true);
            foreach (var field in group.Reverse())
                if (field.Offset + 4 <= package.ContentLength && ReadUInt32(package, field.Offset) == field.Replacement)
                    WriteUInt32(package, field.Offset, field.Original);
            package.Flush();
        }
        foreach (var patch in record.LooseFiles)
        {
            if (File.Exists(patch.Path) && File.Exists(patch.BackupPath) &&
                Sha256(patch.Path).Equals(patch.ReplacementSha256, StringComparison.OrdinalIgnoreCase) &&
                Sha256(patch.BackupPath).Equals(patch.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                WriteAtomically(patch.Path, File.ReadAllBytes(patch.BackupPath));
            }
        }

        var remaining = state.Replacements.Where(item => item.SourceId != sourceId).ToArray();
        var stillUsed = remaining.SelectMany(item => item.Packages).Select(patch => patch.PackagePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baselines = new List<SoundPackageBaseline>();
        foreach (var baseline in state.Packages)
        {
            if (stillUsed.Contains(baseline.PackagePath))
            {
                baselines.Add(baseline);
                continue;
            }
            if (!File.Exists(baseline.PackagePath)) continue;
            using var package = WwiseAudioFile.Open(baseline.PackagePath, writable: true);
            // Cut the appended audio off only when the package is exactly as Arkive left it.
            if (package.ContentLength == baseline.ArkiveContentLength &&
                baseline.OriginalContentLength < baseline.ArkiveContentLength)
            {
                package.Truncate(baseline.OriginalContentLength);
                package.Flush();
            }
        }
        SaveState(new SoundReplacementState(remaining, baselines));
        foreach (var patch in record.LooseFiles.Where(patch => File.Exists(patch.BackupPath)))
            TryDelete(patch.BackupPath);
        TryDelete(record.StoredWemPath);
    }

    /// <summary>
    /// The clip with its table entries and bank values as the files hold them now, which become the "original" a
    /// restore writes back (the catalog may have been read while an earlier replacement was in place).
    /// </summary>
    private static SoundClip ReadLive(SoundClip clip)
    {
        var files = new List<SoundClipFile>();
        foreach (var file in clip.Files)
        {
            if (!File.Exists(file.Path))
                throw new InvalidDataException($"{Path.GetFileName(file.Path)} is missing from the game's audio folder.");
            if (file.Entry is null)
            {
                files.Add(file);
                continue;
            }
            using var package = WwiseAudioFile.Open(file.Path);
            var current = SoundCatalog.ReadCurrentEntry(package, file.Entry);
            if (current.DataOffset + current.Size > package.ContentLength)
                throw new InvalidDataException("The game's audio files changed since Arkive read them. Search again, then replace.");
            files.Add(file with { Entry = current });
        }
        var fields = new List<SoundBankField>();
        foreach (var field in clip.BankFields)
        {
            using var package = WwiseAudioFile.Open(field.PackagePath);
            fields.Add(field with
            {
                PluginId = ReadUInt32(package, field.PluginIdOffset),
                InMemorySize = ReadUInt32(package, field.InMemorySizeOffset),
            });
        }
        return clip with { Files = files, BankFields = fields };
    }

    private void EnsureGameClosed()
    {
        var blocking = _processGuard.FindBlockingProcesses();
        if (blocking.Count > 0)
            throw new InvalidOperationException($"Close Lost Ark first ({string.Join(", ", blocking)} is running).");
    }

    private static bool EntryIs(WwiseAudioFile package, SoundPackagePatch patch, string expectedHex)
    {
        var expected = Convert.FromHexString(expectedHex);
        return patch.EntryOffset + expected.Length <= package.ContentLength &&
               package.Read(patch.EntryOffset, expected.Length).AsSpan().SequenceEqual(expected);
    }

    private static uint ReadUInt32(WwiseAudioFile package, long offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(package.Read(offset, 4));

    private static void WriteUInt32(WwiseAudioFile package, long offset, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        package.Write(offset, bytes);
    }

    private void SaveState(SoundReplacementState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        WriteAtomically(_statePath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + ".arkive-tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
