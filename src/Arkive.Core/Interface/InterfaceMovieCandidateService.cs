using System.Text.Json;

namespace Arkive.Core.Interface;

/// <summary>
/// Proof that a candidate export is the game's interface movie export with only its movie replaced by the edited
/// one, read back from the candidate's own bytes.
/// </summary>
public sealed record InterfaceMovieCandidateManifest(
    int SchemaVersion,
    string CandidateId,
    DateTimeOffset CreatedAt,
    string SourcePackageSha256,
    int NativeExportIndex,
    string ClassName,
    string ObjectName,
    long SerialOffset,
    long SourceSerialSize,
    string SourceExportSha256,
    string EditedMovieSha256,
    string CandidateExportSha256,
    long CandidateExportBytes,
    bool MovieReadBack);

public sealed record InterfaceMovieCandidateResult(
    string CandidatePath,
    string ManifestPath,
    InterfaceMovieCandidateManifest Manifest);

public static class InterfaceMovieCandidateService
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "arkive-interface-movie-candidate.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Builds the export the package will hold: the game's export bytes around the edited movie.</summary>
    public static async Task<InterfaceMovieCandidateResult> BuildAsync(
        byte[] sourceExport,
        string sourcePackageSha256,
        int nativeExportIndex,
        string className,
        string objectName,
        long serialOffset,
        byte[] editedMovie,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        GfxMovieReader.Read(editedMovie);
        var candidate = GfxMovieFile.Replace(sourceExport, editedMovie);
        var editedHash = InterfaceMovieEditWorkspaceStore.Hash(editedMovie);
        if (!HoldsMovie(candidate, editedHash))
            throw new InvalidDataException($"{objectName}'s rebuilt export did not read back as the edited movie.");
        Directory.CreateDirectory(outputDirectory);
        var candidatePath = Path.Combine(outputDirectory, "candidate-export.bin");
        await File.WriteAllBytesAsync(candidatePath, candidate, cancellationToken).ConfigureAwait(false);
        var manifest = new InterfaceMovieCandidateManifest(
            CurrentSchemaVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, sourcePackageSha256.ToUpperInvariant(),
            nativeExportIndex, className, objectName, serialOffset, sourceExport.LongLength,
            InterfaceMovieEditWorkspaceStore.Hash(sourceExport), editedHash, InterfaceMovieEditWorkspaceStore.Hash(candidate),
            candidate.LongLength, MovieReadBack: true);
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        return new InterfaceMovieCandidateResult(candidatePath, manifestPath, manifest);
    }

    public static async Task<InterfaceMovieCandidateManifest> LoadManifestAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<InterfaceMovieCandidateManifest>(stream, JsonOptions, cancellationToken)
                           .ConfigureAwait(false) ??
                       throw new InvalidDataException("The interface movie candidate manifest is empty.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Interface movie candidate schema {manifest.SchemaVersion} is unsupported.");
        return manifest;
    }

    /// <summary>True when an export holds exactly this movie and Arkive can read the movie's tags.</summary>
    public static bool HoldsMovie(byte[] export, string movieSha256)
    {
        try
        {
            var movie = GfxMovieFile.Extract(export);
            if (!InterfaceMovieEditWorkspaceStore.Hash(movie).Equals(movieSha256, StringComparison.OrdinalIgnoreCase)) return false;
            GfxMovieReader.Read(movie);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
