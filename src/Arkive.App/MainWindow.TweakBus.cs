using System.IO;
using Arkive.Core.Tweaks;

namespace Arkive.App;

/// <summary>
/// The Arkive half of the Data Bus: while Arkive runs it watches the bus files that tweak code reads to spell a message
/// (see <see cref="TweakBus"/>) and hands each message to the handler registered for its channel. A tweak that sends
/// something registers its channel in <see cref="TweakBusHandlers"/>.
/// </summary>
public partial class MainWindow
{
    private TweakBus? _tweakBus;

    /// <summary>Handlers by channel; each runs on a background thread with the message's text.</summary>
    private Dictionary<string, Action<string>> TweakBusHandlers => new(StringComparer.Ordinal)
    {
        [TweakModService.SettingsChannel] = SaveInGameTweakChange,
    };

    private void StartTweakBus()
    {
        try
        {
            var handlers = TweakBusHandlers;
            _tweakBus = new TweakBus(Tweaks.BusFolder);
            _tweakBus.Received += (channel, text) =>
            {
                if (handlers.TryGetValue(channel, out var handler))
                    handler(text);
                else
                    Dispatcher.InvokeAsync(() => Log($"Tweaks: a message on unknown channel '{channel}' was ignored."));
            };
            _tweakBus.Failed += problem => Dispatcher.InvokeAsync(() => Log($"Tweaks bus: {problem}"));
            _tweakBus.Start();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Tweaks: Arkive could not set up its bus files, so changes made in game won't be saved: {exception.Message}");
        }
    }

    /// <summary>A change from the escape menu panel: save it, and show it in the Tweaks tab.</summary>
    private void SaveInGameTweakChange(string text)
    {
        string? change;
        try
        {
            change = Tweaks.ApplyInGameChange(text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Dispatcher.InvokeAsync(() => Log($"Tweaks: could not save a change made in game: {exception.Message}"));
            return;
        }
        Dispatcher.InvokeAsync(() =>
        {
            if (change is null)
            {
                Log($"Tweaks: ignored a change from the game that Arkive doesn't understand ({text.Replace('\t', '|')}).");
                return;
            }
            Log($"Tweaks: changed in game: {change}.");
            if (!_tweaksBusy)
                RefreshTweaks($"Changed in game: {change}. Saved.");
        });
    }
}
