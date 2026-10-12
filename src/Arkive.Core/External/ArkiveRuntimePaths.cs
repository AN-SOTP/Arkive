namespace Arkive.Core.External;

public static class ArkiveRuntimePaths
{
    public const string InstallationMarker = "arkive-installation.json";

    public static string ResolveDataRoot() => ResolveDataRoot(
        Environment.GetEnvironmentVariable("ARKIVE_DATA_ROOT"),
        AppContext.BaseDirectory,
        UmodelLocator.FindProjectRoot(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.CurrentDirectory);

    public static string ResolveDataRoot(
        string? overridePath, string applicationDirectory, string? projectRoot,
        string localApplicationData, string currentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
        if (File.Exists(Path.Combine(applicationDirectory, InstallationMarker)))
            return Path.GetFullPath(Path.Combine(localApplicationData, "Arkive"));
        return Path.GetFullPath(projectRoot ?? currentDirectory);
    }
}
