namespace Arkive.Core.External;

public static class UmodelLocator
{
    public const string EnvironmentVariable = "ARKIVE_UMODEL_PATH";

    public static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "tools", "external");
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var executable = Directory
                .EnumerateFiles(directory, "umodel_lostark*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (executable is not null)
            {
                return Path.GetFullPath(executable);
            }
        }

        return null;
    }

    public static string? FindProjectRoot()
    {
        foreach (var root in CandidateRoots())
        {
            if (File.Exists(Path.Combine(root, "Arkive.slnx")))
            {
                return root;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(seed));
            for (var level = 0; current is not null && level < 10; level++, current = current.Parent)
            {
                if (seen.Add(current.FullName))
                {
                    yield return current.FullName;
                }
            }
        }
    }
}
