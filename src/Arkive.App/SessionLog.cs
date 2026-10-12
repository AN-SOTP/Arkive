using System.IO;
using System.Text;
using Arkive.Core.External;

namespace Arkive.App;

internal sealed class SessionLog
{
    private readonly object _sync = new();

    public static SessionLog? Current { get; private set; }

    public SessionLog()
    {
        var root = ArkiveRuntimePaths.ResolveDataRoot();
        var directory = Path.Combine(root, "logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"arkive-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(FilePath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Current = this;
    }

    public string FilePath { get; }

    public string Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        lock (_sync)
        {
            File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
        }

        return line;
    }

    public static void WriteUnhandled(string source, Exception exception)
    {
        try
        {
            var message = $"Unhandled application exception ({source}): {exception}";
            if (Current is not null)
            {
                Current.Write(message);
                return;
            }

            var root = ArkiveRuntimePaths.ResolveDataRoot();
            var directory = Path.Combine(root, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"arkive-crash-{DateTime.Now:yyyyMMdd-HHmmssfff}.log");
            File.WriteAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Crash logging must never mask the original exception.
        }
    }
}
