using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Arkive.Core.Audio;
using Microsoft.Win32;
using NAudio.Wave;

namespace Arkive.App;

/// <summary>One clip of a sound in the sound window.</summary>
public sealed record SoundClipRow(int Number, SoundClip Clip, string Length, string Format, string Status);

/// <summary>
/// Plays a sound's clips (play, pause, stop, seek, volume) and replaces or restores single clips.
/// </summary>
public partial class SoundWindow : Window
{
    private readonly SoundCatalog _catalog;
    private readonly SoundEvent _sound;
    private readonly SoundReplacementService _replacements;
    private readonly SoundPreviewService _preview;
    private readonly Action<string> _log;
    private readonly DispatcherTimer _timer;
    private WaveOutEvent? _output;
    private WaveFileReader? _reader;
    private uint? _loadedClip;
    private bool _dragging;
    private bool _updatingPosition;
    private bool _busy;

    public SoundWindow(
        SoundCatalog catalog,
        SoundEvent sound,
        SoundReplacementService replacements,
        SoundPreviewService preview,
        Action<string> log)
    {
        InitializeComponent();
        _catalog = catalog;
        _sound = sound;
        _replacements = replacements;
        _preview = preview;
        _log = log;
        Title = $"{sound.Name} – Arkive";
        SoundNameText.Text = sound.Name;
        SoundDetailText.Text = $"{sound.Set} · {sound.SourceIds.Count:N0} clip{(sound.SourceIds.Count == 1 ? string.Empty : "s")}" +
                               (sound.SourceIds.Count > 1 ? " (the game picks between them, or plays them by situation)" : string.Empty);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => UpdatePosition(), Dispatcher);
        Loaded += async (_, _) => await LoadClipsAsync();
        Closed += (_, _) => StopPlayback(release: true);
        PreviewKeyDown += SoundWindow_PreviewKeyDown;
    }

    private SoundClipRow? SelectedRow => ClipsGrid.SelectedItem as SoundClipRow;

    private async Task LoadClipsAsync(uint? select = null)
    {
        var state = await Task.Run(_replacements.LoadState);
        var rows = await Task.Run(() => _sound.SourceIds
            .Select(_catalog.Clip)
            .OfType<SoundClip>()
            .Select((clip, index) => BuildRow(index + 1, clip, state))
            .ToArray());
        ClipsGrid.ItemsSource = rows;
        ClipsGrid.SelectedItem = rows.FirstOrDefault(row => row.Clip.SourceId == select) ?? rows.FirstOrDefault();
        UpdateButtons();
    }

    private SoundClipRow BuildRow(int number, SoundClip clip, SoundReplacementState state)
    {
        WemFormat? format = null;
        try
        {
            format = SoundCatalog.ReadFormat(clip);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }
        var length = format?.Duration ?? (clip.MusicDurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);
        var record = state.Replacements.FirstOrDefault(item => item.SourceId == clip.SourceId);
        var status = record is null
            ? "Game original"
            : _replacements.GetStatus(clip.SourceId) switch
            {
                SoundReplacementStatus.Replaced => $"Replaced with {record.ReplacementFileName}",
                _ => $"A game update put the original back (was {record.ReplacementFileName}); replace again",
            };
        var languages = clip.Files.Select(file => file.Language).Where(language => language != "sfx").Distinct().ToArray();
        var formatText = format is null
            ? "unknown"
            : $"{format.Codec}, {(format.Channels == 1 ? "mono" : format.Channels == 2 ? "stereo" : $"{format.Channels} channels")}, {format.SampleRate / 1000.0:0.#} kHz";
        if (clip.IsMusic) formatText += ", music";
        if (languages.Length > 0) formatText += $", voice ({string.Join(", ", languages)})";
        return new SoundClipRow(number, clip, length is { } value ? MainWindow.FormatLength(value) : "unknown", formatText, status);
    }

    private void UpdateButtons()
    {
        var row = SelectedRow;
        PlayButton.IsEnabled = row is not null && !_busy;
        StopButton.IsEnabled = _output is not null;
        ReplaceButton.IsEnabled = row is not null && !_busy;
        ExportButton.IsEnabled = row is not null && !_busy;
        RestoreButton.IsEnabled = row is not null && !_busy && _replacements.Find(row.Clip.SourceId) is not null;
        var music = row?.Clip.IsMusic == true;
        var fittable = row?.Clip.MusicFit is not null;
        FitBankCheckBox.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        FitBankCheckBox.IsEnabled = fittable && !_busy;
        NoteText.Text = !music
            ? "Sound effects and voices can be any length. Your file is converted to the clip's channels and sample rate."
            : fittable && FitBankCheckBox.IsChecked == true
                ? "Your file plays at its own length and loops from its end, with no cut and no silence." +
                  (row!.Clip.MusicFit!.Intros.Count > 0 ? " The intro this music plays first is pointed at your file too, so it plays through and loops as one song." : string.Empty)
                : (fittable ? string.Empty : "This music shares its timing with other audio, so it cannot be fitted. ") +
                  "Music keeps the game's length so it still loops and changes on cue: a longer file is cut (with a short fade) and a shorter one ends in silence.";
    }

    private void FitBankCheckBox_Click(object sender, RoutedEventArgs e) => UpdateButtons();

    private void ClipsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedRow?.Clip.SourceId != _loadedClip) StopPlayback(release: true);
        UpdateButtons();
    }

    private async void ClipsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && SelectedRow is not null) await PlayAsync();
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e) => await PlayAsync();

    private async void SoundWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.OriginalSource is TextBox) return;
        e.Handled = true;
        await PlayAsync();
    }

    private async Task PlayAsync()
    {
        if (SelectedRow is not { } row || _busy) return;
        if (_output is not null && _loadedClip == row.Clip.SourceId)
        {
            if (_output.PlaybackState == PlaybackState.Playing)
            {
                _output.Pause();
                PlayButton.Content = "Play";
            }
            else
            {
                if (_reader is not null && _reader.Position >= _reader.Length) _reader.Position = 0;
                _output.Play();
                PlayButton.Content = "Pause";
            }
            return;
        }

        StopPlayback(release: true);
        SetBusy(true, "Preparing the clip…");
        try
        {
            var wav = await _preview.GetWavAsync(row.Clip);
            _reader = new WaveFileReader(wav);
            _output = new WaveOutEvent { Volume = (float)VolumeSlider.Value };
            _output.Init(_reader);
            _output.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                PlayButton.Content = "Play";
                UpdatePosition();
            });
            _loadedClip = row.Clip.SourceId;
            PositionSlider.Maximum = Math.Max(0.01, _reader.TotalTime.TotalSeconds);
            _output.Play();
            PlayButton.Content = "Pause";
            _timer.Start();
            StatusText.Text = string.Empty;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or NAudio.MmException)
        {
            StopPlayback(release: true);
            StatusText.Text = $"This clip could not be played: {exception.Message}";
            _log($"Sound preview failed: sound={_sound.Name}, source={row.Clip.SourceId}, error={exception}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_output is null || _reader is null) return;
        _output.Stop();
        _reader.Position = 0;
        PlayButton.Content = "Play";
        UpdatePosition();
    }

    private void StopPlayback(bool release)
    {
        _timer.Stop();
        if (_output is not null)
        {
            _output.Stop();
            if (release)
            {
                _output.Dispose();
                _output = null;
            }
        }
        if (release)
        {
            _reader?.Dispose();
            _reader = null;
            _loadedClip = null;
            PlayButton.Content = "Play";
            _updatingPosition = true;
            PositionSlider.Value = 0;
            _updatingPosition = false;
            PositionText.Text = "0:00 / 0:00";
        }
        StopButton.IsEnabled = _output is not null;
    }

    private void UpdatePosition()
    {
        if (_reader is null) return;
        if (!_dragging)
        {
            _updatingPosition = true;
            PositionSlider.Value = Math.Min(PositionSlider.Maximum, _reader.CurrentTime.TotalSeconds);
            _updatingPosition = false;
        }
        PositionText.Text = $"{Clock(_reader.CurrentTime)} / {Clock(_reader.TotalTime)}";
        StopButton.IsEnabled = _output is not null;
    }

    private static string Clock(TimeSpan time) => $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private void PositionSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _dragging = true;

    private void PositionSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Seek(PositionSlider.Value);
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPosition || _reader is null) return;
        if (!_dragging) Seek(e.NewValue);
        else PositionText.Text = $"{Clock(TimeSpan.FromSeconds(e.NewValue))} / {Clock(_reader.TotalTime)}";
    }

    private void Seek(double seconds)
    {
        if (_reader is null) return;
        _reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _reader.TotalTime.TotalSeconds));
        UpdatePosition();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_output is not null) _output.Volume = (float)e.NewValue;
    }

    private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || _busy) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Replace clip {row.Number} of {_sound.Name}",
            Filter = "Audio files|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aac;*.wma|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        var fileName = Path.GetFileName(dialog.FileName);

        StopPlayback(release: true);
        SetBusy(true, $"Converting {fileName}…");
        try
        {
            var clip = row.Clip;
            var original = OriginalFormat(clip);
            var channels = original?.Channels is > 0 and <= 8 ? original.Channels : 2;
            var sampleRate = original?.SampleRate is > 0 ? original.SampleRate : 48000;
            var samples = await Task.Run(() => AudioInputConverter.Decode(dialog.FileName, sampleRate, channels));
            if (samples.Length == 0) throw new InvalidDataException($"{fileName} has no audio.");
            var inputLength = TimeSpan.FromSeconds((double)samples.Length / channels / sampleRate);
            var fit = clip.IsMusic && clip.MusicFit is not null && FitBankCheckBox.IsChecked == true;
            // A clip fitted to an earlier replacement has that length now; the game's own is in its record.
            var musicLength = _replacements.Find(clip.SourceId)?.OriginalMusicLengthMs is { } fittedFrom
                ? TimeSpan.FromMilliseconds(fittedFrom)
                : original?.Duration;
            if (clip.IsMusic && !fit && musicLength is { } keep)
                samples = WemAudio.FitLength(samples, channels, (int)Math.Round(keep.TotalSeconds * sampleRate), sampleRate);
            var wem = WemAudio.WritePcmWem(samples, channels, sampleRate);

            var lengthNote = fit
                ? $"\n\nThe game's music timing will be changed to play all {MainWindow.FormatLength(inputLength)} of your file and loop it from its end."
                : clip.IsMusic && musicLength is { } length && Math.Abs((length - inputLength).TotalSeconds) > 0.5
                    ? $"\n\nYour file is {MainWindow.FormatLength(inputLength)} and this music is {MainWindow.FormatLength(length)}, so it will be {(inputLength > length ? "cut to fit" : "followed by silence")}."
                    : string.Empty;
            var answer = MessageBox.Show(this,
                $"Play {fileName} in place of clip {row.Number} of {_sound.Name}?{lengthNote}\n\n" +
                "This changes the game's audio files. Lost Ark must be closed. You can restore the original at any time.",
                "Replace sound", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText.Text = "Nothing was changed.";
                return;
            }

            SetBusy(true, "Writing the new audio into the game files…");
            var record = await _replacements.ReplaceAsync(clip, _sound.Name, fileName, wem, fit);
            StatusText.Text = $"Clip {row.Number} now plays {fileName}" + (fit ? " at its own length" : string.Empty) +
                              ". Press Play to hear it as the game will.";
            _log($"Sound replaced: sound={_sound.Name}, source={clip.SourceId}, file={dialog.FileName}, " +
                 $"packages={record.Packages.Count}, bankFields={record.Fields.Count}, looseFiles={record.LooseFiles.Count}, " +
                 $"timing={record.BankBytes.Count}, fitted={fit}, bytes={wem.Length}.");
            await LoadClipsAsync(clip.SourceId);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                                              or InvalidOperationException or FormatException or ArgumentException
                                              or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = $"The clip was not replaced: {exception.Message}";
            _log($"Sound replace failed: sound={_sound.Name}, source={row.Clip.SourceId}, file={dialog.FileName}, error={exception}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>The clip's format before any replacement (a replacement keeps channels, rate and, for music, length).</summary>
    private static WemFormat? OriginalFormat(SoundClip clip)
    {
        try
        {
            return SoundCatalog.ReadFormat(clip);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || _busy) return;
        StopPlayback(release: true);
        SetBusy(true, "Putting the original audio back…");
        try
        {
            await _replacements.RestoreAsync(row.Clip.SourceId);
            StatusText.Text = $"Clip {row.Number} plays the game's original audio again.";
            _log($"Sound restored: sound={_sound.Name}, source={row.Clip.SourceId}.");
            await LoadClipsAsync(row.Clip.SourceId);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"The original was not restored: {exception.Message}";
            _log($"Sound restore failed: sound={_sound.Name}, source={row.Clip.SourceId}, error={exception}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || _busy) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save clip as WAV",
            Filter = "WAV audio|*.wav",
            FileName = _sound.SourceIds.Count == 1 ? $"{_sound.Name}.wav" : $"{_sound.Name}_{row.Number}.wav",
        };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true, "Saving…");
        try
        {
            var wav = await _preview.GetWavAsync(row.Clip);
            File.Copy(wav, dialog.FileName, overwrite: true);
            StatusText.Text = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText.Text = $"The clip was not saved: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        if (message is not null) StatusText.Text = message;
        Cursor = busy ? Cursors.Wait : null;
        UpdateButtons();
    }
}
