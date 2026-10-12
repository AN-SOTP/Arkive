namespace Arkive.SmokeTests;

internal static class CrunchFixtures
{
    private static readonly IReadOnlyDictionary<string, string> Encoded = new Dictionary<string, string>
    {
        ["unity-crunch-bc5.crn"] = "SHgASq7gAAABBTh1ABAAEAEBBwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAASgAAJQAQAABvAABXABEAIwAAxgAAAOkEAfGAwAAQxEh4qdS0R6ZcxFv7r2ShmrjQDhURxfNn5unoMyfgAQIoAMMIQQQTNnI/fKjwX1lkqLJVCBYcIAyWSuKp9Q2KxA0UfLvleUxI7N0+zFnc5fix7c1guSo4/566da7CVoSiW5psz/PZafQQgAUKyhOXRIADAAAYAAZgAAAAAAAAEAIRYAYAAQQYYU1XhjAARiAkAAAwgA/AGHBVEoURac3EtX5aXDD6ey9RqBxn+/VJ7YybYCEL",
        ["unity-crunch-dxt1.crn"] = "SHgASiv6AAAA+nLCABAAEAEBAAAAAAAAAAAAAAAAAAAAAABKAABVAA4AAJ8AADAADgAAAAAAAAAAAAAAAAAAAAAAIQAAzwAAAPAAgeBAgAggxCs7s0GFLx5XIIB++UBAAAhirxwTi4anwepHApTWrbdHdCv7y27L9iQ14hS4DNVH6ID5hrO38u67GqLAFDmVJR6eiL86mK5nhxhgf5KKAEJsAIAMQIMATkflwohAHDla67Vf6C+BzDrdPSTBjQAwzN0DnvHvZ1v1ttt2SSSQAgZpAAAAAAAIIo+xADpoAIAAAAAAH6gAc8BAAAAAhJs4Z4mNbpavjr3gnw==",
        ["unity-crunch-dxt5.crn"] = "SHgASoQhAAABg5QuABAAEAEBAgAAAAAAAAAAAAAAAAAAAABKAABVAA4AAJ8AADAADgAAzwAAJgAOAAD1AABDAAsAOAABOAAAAXAAgeBAgAggxCs7s0GFLx5XIIB++UBAAAhirxwTi4anwepHApTWrbdHdCv7y27L9iQ14hS4DNVH6ID5hrO38u67GqLAFDmVJR6eiL86mK5nhxhgf5KKAEJsAIAMQIMATkflwohAHDla67Vf6C+BzDrdPSTBjQAwzN0DnvHvZ1v1ttt2SSSQBAHxgIAAIIQfCUJxfILHrEHYVxB9rgKxzqVh2W38zAKc2U6SPgABAi4AgAwgwBOg4MbMIFQRhfFuDWY2366ROc9KAVCFb6VeKShVClq/2XyLCrlFocAyLzF+9a3vEIwQN3Oncgl3iGaQAgZpAAAAAAAIIo+xADpoAIAAAAAAH6gAc8BAAAAAhJs4AOmgAgAAAAAgsQW0ALiAEAAAAggrAFB5PgjENqddSHYs+aDldt8gTHv8",
    };

    internal static void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, data) in Encoded)
            File.WriteAllBytes(Path.Combine(directory, name), Convert.FromBase64String(data));
    }
}
