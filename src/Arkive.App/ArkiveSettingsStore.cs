using System.IO;
using System.Text.Json;

namespace Arkive.App;

internal sealed record ArkiveSettings
{
    public const int CurrentVersion = 8;

    public int Version { get; init; } = CurrentVersion;

    public string? LibraryRootPath { get; init; }

    public string? LibraryScratchPath { get; init; }

    public string? GameMetadataPath { get; init; }

    public int LibraryWorkers { get; init; } = 4;

    public string LibrarySearchText { get; init; } = string.Empty;

    public string LibrarySearchScope { get; init; } = "AllFields";

    public string LibraryCategory { get; init; } = string.Empty;

    public string LibraryArchetype { get; init; } = string.Empty;

    public int SelectedTabIndex { get; init; } = 2;

    public double ResultsPaneRatio { get; init; } = 0.8;

    public double AuthoringSearchPaneRatio { get; init; } = 0.65;

    public double ModDetailsPaneRatio { get; init; } = 0.5;

    public bool LibrarySetupExpanded { get; init; }

    public bool AuthoringDetailsExpanded { get; init; }

    public bool AuthoringSearchExpanded { get; init; }

    public double WindowWidth { get; init; } = 1320;

    public double WindowHeight { get; init; } = 860;

    public double? WindowLeft { get; init; }

    public double? WindowTop { get; init; }

    public bool WindowMaximized { get; init; }

    public string Theme { get; init; } = "System";

    public string BannerArtwork { get; init; } = BannerArtworkManager.RandomPreference;

    public bool CompactDensity { get; init; }

    public bool ShowValidationPane { get; init; }

    public bool CheckForUpdatesOnStartup { get; init; } = true;

    public string? BlenderPath { get; init; }
}

internal sealed class ArkiveSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public ArkiveSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
    }

    public string FilePath { get; }

    public ArkiveSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new ArkiveSettings();
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            var settings = JsonSerializer.Deserialize<ArkiveSettings>(stream, JsonOptions);
            if (settings?.Version == ArkiveSettings.CurrentVersion)
            {
                return settings;
            }

            if (settings?.Version is 5 or 6)
            {
                return settings with { Version = ArkiveSettings.CurrentVersion };
            }

            if (settings?.Version is 3 or 4)
            {
                return settings with
                {
                    Version = ArkiveSettings.CurrentVersion,
                    // Mod workshop was inserted after Library.
                    SelectedTabIndex = settings.SelectedTabIndex >= 2
                        ? settings.SelectedTabIndex + 1
                        : settings.SelectedTabIndex,
                };
            }

            if (settings?.Version == 2)
            {
                return settings with
                {
                    Version = ArkiveSettings.CurrentVersion,
                    // Characters & NPCs was inserted after Library.
                    SelectedTabIndex = settings.SelectedTabIndex >= 2
                        ? settings.SelectedTabIndex + 2
                        : settings.SelectedTabIndex,
                };
            }

            if (settings?.Version == 1)
            {
                var version2Tab = settings.SelectedTabIndex >= 5
                    ? settings.SelectedTabIndex + 1
                    : settings.SelectedTabIndex;
                return settings with
                {
                    Version = ArkiveSettings.CurrentVersion,
                    // Apply both the Texture recon and Characters & NPCs insertions.
                    SelectedTabIndex = version2Tab >= 2 ? version2Tab + 2 : version2Tab,
                };
            }

            return new ArkiveSettings();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ArkiveSettings();
        }
    }

    public void Save(ArkiveSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = FilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
