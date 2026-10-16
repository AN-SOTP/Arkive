using System.Text.Json;

namespace Arkive.Core.Proportions;

/// <summary>A named set of slider values for one body, reusable on any of that body's outfits.</summary>
public sealed record BodyProportionPreset(
    string Name,
    string ProfileId,
    IReadOnlyDictionary<string, double> Settings,
    bool LooseClothingFollows,
    bool HairFollows,
    DateTimeOffset SavedAt)
{
    public BodyProportionFollow Follow => new(LooseClothingFollows, HairFollows);

    public override string ToString() => Name;
}

/// <summary>Saves presets as one JSON file each, named after the body and the preset.</summary>
public sealed class BodyProportionPresetStore(string rootDirectory)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    /// <summary>Every readable preset, optionally only one body's, by name.</summary>
    public IReadOnlyList<BodyProportionPreset> Load(string? profileId = null)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var presets = new List<BodyProportionPreset>();
        foreach (var file in Directory.EnumerateFiles(RootDirectory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<BodyProportionPreset>(File.ReadAllText(file), Options) is { } preset &&
                    !string.IsNullOrWhiteSpace(preset.Name) && preset.Settings is not null &&
                    (profileId is null || preset.ProfileId == profileId))
                    presets.Add(preset);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
        return presets.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>Saves the preset, replacing one of the same name for the same body.</summary>
    public BodyProportionPreset Save(BodyProportionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var name = preset.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the preset a name.", nameof(preset));
        var profile = BodyProfiles.All.FirstOrDefault(candidate => candidate.Id == preset.ProfileId)
            ?? throw new ArgumentException($"Unknown body {preset.ProfileId}.", nameof(preset));
        var zones = profile.Zones.ToDictionary(zone => zone.Id, StringComparer.Ordinal);
        var settings = preset.Settings
            .Where(pair => zones.ContainsKey(pair.Key) && Math.Abs(pair.Value) > 1e-9)
            .ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, zones[pair.Key].Minimum, zones[pair.Key].Maximum),
                StringComparer.Ordinal);
        var saved = preset with { Name = name, Settings = settings, SavedAt = DateTimeOffset.Now };
        Directory.CreateDirectory(RootDirectory);
        var path = PathFor(saved.ProfileId, name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(saved, Options));
        File.Move(temporary, path, overwrite: true);
        return saved;
    }

    public void Delete(BodyProportionPreset preset)
    {
        var path = PathFor(preset.ProfileId, preset.Name);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string profileId, string name)
    {
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Path.Combine(RootDirectory, $"{profileId}--{safe}.json");
    }
}
