namespace Arkive.SmokeTests;

internal static class SmokeTestEnvironment
{
    internal static string fixtureDirectory { get; private set; } = string.Empty;
    internal static byte[] testKey { get; } = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

    internal static void Initialize()
    {
        fixtureDirectory = Path.Combine(Path.GetTempPath(), "Arkive.SmokeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        CrunchFixtures.WriteTo(Path.Combine(AppContext.BaseDirectory, "Fixtures"));
    }

    internal static void Cleanup()
    {
        CryptographicOperations.ZeroMemory(testKey);
        if (!string.IsNullOrWhiteSpace(fixtureDirectory) && Directory.Exists(fixtureDirectory))
            Directory.Delete(fixtureDirectory, recursive: true);
    }
}
