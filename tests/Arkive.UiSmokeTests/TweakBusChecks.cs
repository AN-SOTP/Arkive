using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Arkive.App;
using Arkive.Core.Tweaks;
using static Program;

/// <summary>
/// A closed window lets go of its Data Bus files. One that kept them made the next window on the same data folder fight
/// it for every file, which held that window's opening (and the remembered Mods list) back for up to minutes.
/// </summary>
internal static class TweakBusChecks
{
    public static string SessionLogPath(MainWindow window)
    {
        var log = typeof(MainWindow).GetField("_sessionLog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        return (string)log.GetType().GetProperty("FilePath")!.GetValue(log)!;
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <param name="open">The window open now on <paramref name="root"/>.</param>
    /// <param name="closedLog">The session log of a window on the same data folder that has closed.</param>
    public static async Task VerifyAsync(MainWindow open, string root, string closedLog)
    {
        // Spell a message the way tweak code does: read "go", one file per digit, then "end", a new set each time.
        const string text = "UI bus check";
        var folder = Path.Combine(root, "tweaks", "bus");
        string[] symbols = ["go", .. TweakBus.Encode("titleicons", text).Select(digit => digit.ToString()), "end"];
        await Task.Run(() =>
        {
            for (var at = 0; at < symbols.Length; at++)
            {
                var set = TweakSignalFolder.Sets[at % TweakSignalFolder.Sets.Count];
                using (var stream = new FileStream(Path.Combine(folder, TweakSignalFolder.FileName(set, symbols[at])),
                           FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    stream.ReadByte();
                // The game reads a file again only sixteen reads later; give each watch time to lock its file again.
                Thread.Sleep(20);
            }
        });
        var line = $"Title Icons Only, title from the game: {text}";
        var openLog = SessionLogPath(open);
        Check(!openLog.Equals(closedLog, StringComparison.OrdinalIgnoreCase), "Both windows wrote the same session log.");
        await UntilAsync(() => ReadLog(openLog).Contains(line, StringComparison.Ordinal),
            () => "The open window did not read a message spelled on its Data Bus (a closed window still holding the " +
                  "files breaks the reads up).");
        // Every watcher noted the last read before that read could finish, and logs through the window's thread.
        await open.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(!ReadLog(closedLog).Contains(line, StringComparison.Ordinal),
            "A closed window still read the Data Bus files of its data folder.");
        Console.WriteLine("PASS: a closed window lets go of its Data Bus files, so only the open window reads them");
    }
}
