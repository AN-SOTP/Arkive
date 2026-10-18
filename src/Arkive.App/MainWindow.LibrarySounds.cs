using Arkive.Core.Assets;
using Arkive.Core.Audio;
using Arkive.Core.Discovery;

namespace Arkive.App;

public partial class MainWindow
{
    // Sounds live in the audio folder beside Packages, so the Asset Library lists them from the Sounds tab's catalog.
    private const string LibrarySoundCategory = "Sound";
    private const string LibrarySoundClass = "Sound";
    private IReadOnlyList<SoundEvent> _librarySoundEvents = [];

    private static bool IsLibrarySoundRow(LibraryAssetRow row) => row.ClassName == LibrarySoundClass;

    private async Task<PackageIndexSearchResult> ApplyLibrarySoundFilterAsync(string query)
    {
        var empty = new PackageIndexSearchResult([], 0, 0, 0);
        if (query.Length == 0)
        {
            LibraryGrid.ItemsSource = Array.Empty<LibraryAssetRow>();
            LibraryCountText.Text = "Enter part of a sound's name, for example kazeros battle or bgm login";
            return empty;
        }
        if (_soundBusy)
        {
            LibraryCountText.Text = "The game's sounds are still being read; search again in a moment";
            return empty;
        }
        _soundBusy = true;
        try
        {
            if (_soundCatalog is null) LibraryCountText.Text = "Reading the game's sounds; the first search takes a little while...";
            var catalog = await EnsureSoundCatalogAsync();
            if (catalog is null)
            {
                LibraryGrid.ItemsSource = Array.Empty<LibraryAssetRow>();
                LibraryCountText.Text = SoundStatusText.Text;
                return empty;
            }
            var replacements = SoundReplacements;
            var sounds = await Task.Run(() => FindSoundRows(catalog, replacements, query, "all"));
            _librarySoundEvents = sounds.Select(sound => sound.Event).ToArray();
            var rows = sounds.Select((sound, index) => new LibraryAssetRow(
                catalog.AudioRoot, sound.Set, string.Empty, 0, index, AssetKind.Object, LibrarySoundClass, sound.Name, 0, 0, [],
                MatchReason: string.Join(" · ", new[] { sound.Kind, sound.Length, $"{sound.ClipCount:N0} clip(s)", sound.Status }
                    .Where(part => !string.IsNullOrEmpty(part))))).ToArray();
            LibraryGrid.ItemsSource = rows;
            LibraryFacetText.Text = "Sounds come from the game's audio folder. Preview (or double-click) one to listen and replace its clips.";
            LibraryCountText.Text = rows.Length == 0
                ? "No sound matched. Names use the developers' terms (bgm for music, vox for voices)."
                : $"{rows.Length:N0} sound(s){(rows.Length >= SoundResultLimit ? $" (the first {SoundResultLimit:N0}; add words to narrow the search)" : string.Empty)}";
            Log($"Library sound search: query={query}, results={rows.Length}.");
            UpdateLibraryActionState();
            return new PackageIndexSearchResult([], rows.Length, 0, 0);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            LibraryCountText.Text = $"Arkive could not read the game's sounds: {exception.Message}";
            Log($"Library sound search failed: query={query}, error={exception}");
            return empty;
        }
        finally
        {
            _soundBusy = false;
        }
    }

    private void UpdateLibrarySoundActionState()
    {
        LibraryPreviewButton.IsEnabled = true;
        LibraryInspectButton.IsEnabled = false;
        LibraryBrowsePackageButton.IsEnabled = false;
        LibraryExportButton.IsEnabled = false;
        LibraryOpenPackageFolderButton.IsEnabled = false;
        LibraryCopyIdentityButton.IsEnabled = true;
        LibraryExploreRelationshipsButton.IsEnabled = false;
        LibraryBaselineButton.IsEnabled = false;
        LibraryTextureReconButton.IsEnabled = false;
        LibrarySkeletalMeshReconButton.IsEnabled = false;
    }

    private void OpenLibrarySound(LibraryAssetRow row)
    {
        if (row.ExportIndex < 0 || row.ExportIndex >= _librarySoundEvents.Count) return;
        OpenSoundWindow(new SoundRow(_librarySoundEvents[row.ExportIndex], string.Empty, string.Empty, string.Empty));
    }
}
