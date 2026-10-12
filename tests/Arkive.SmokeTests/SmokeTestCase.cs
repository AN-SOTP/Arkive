namespace Arkive.SmokeTests;

internal sealed record SmokeTestCase(
    string Name,
    Action Execute,
    Func<SmokeTestPrerequisiteResult>? Prerequisite = null);

internal sealed record SmokeTestPrerequisiteResult(bool IsAvailable, string Reason)
{
    internal static SmokeTestPrerequisiteResult Available { get; } = new(true, string.Empty);
}

internal static class SmokeTestPrerequisites
{
    internal static SmokeTestPrerequisiteResult UnityCrunchEncoder()
    {
        var path = CrunchEncoderLocator.FindExecutable();
        return path is not null
            ? SmokeTestPrerequisiteResult.Available
            : new(false, "tools/external/crunch_x64.exe is not installed in this checkout");
    }
}

internal static class SmokeTestRunner
{
    public static void Run(SmokeTestCase test, ICollection<string> failures)
    {
        var prerequisite = test.Prerequisite?.Invoke() ?? SmokeTestPrerequisiteResult.Available;
        if (!prerequisite.IsAvailable)
        {
            Console.WriteLine($"SKIP: {test.Name} ({prerequisite.Reason})");
            return;
        }

        try
        {
            test.Execute();
            Console.WriteLine($"PASS: {test.Name}");
        }
        catch (Exception exception)
        {
            failures.Add($"{test.Name}: {exception.Message}");
            Console.Error.WriteLine($"FAIL: {test.Name}");
            Console.Error.WriteLine(exception);
        }
    }
}
