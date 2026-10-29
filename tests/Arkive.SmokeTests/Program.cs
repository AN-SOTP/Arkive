using Arkive.SmokeTests;

var failures = new List<string>();
IReadOnlyList<IReadOnlyList<SmokeTestCase>> groups =
[
    PackageArchiveSmokeTests.Cases,
    MeshSmokeTests.Cases,
    ViewerSmokeTests.Cases,
    ProportionSmokeTests.Cases,
    OutfitSwapSmokeTests.Cases,
    MaterialReferenceSmokeTests.Cases,
    OutfitLookSmokeTests.Cases,
    WorkingFileSmokeTests.Cases,
    TextureSmokeTests.Cases,
    MaterialSmokeTests.Cases,
    ModWorkflowSmokeTests.Cases,
    ArkiveModSmokeTests.Cases,
    ModAuthoringSmokeTests.Cases,
    DiscoverySmokeTests.Cases,
    ZoneSmokeTests.Cases,
    AssetTypeSmokeTests.Cases,
    ParticleSmokeTests.Cases,
    PhysicsSmokeTests.Cases,
    SkillActionSmokeTests.Cases,
    InterfaceSmokeTests.Cases,
    TweakSmokeTests.Cases,
    DatabaseSmokeTests.Cases,
    ExternalArtifactSmokeTests.Cases,
    UpdateSmokeTests.Cases,
    SoundSmokeTests.Cases,
    RecolourSmokeTests.Cases,
    LoginScreenSmokeTests.Cases,
    FontSmokeTests.Cases,
    LanguageSmokeTests.Cases,
    GameDataSmokeTests.Cases,
    UnexpectedErrorSmokeTests.Cases,
];

var filter = args.Length > 0 ? args[0] : null;
var selected = 0;
SmokeTestEnvironment.Initialize();
try
{
    foreach (var group in groups)
    foreach (var test in group)
    {
        if (filter is not null && !test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
        selected++;
        SmokeTestRunner.Run(test, failures);
    }
}
finally
{
    SmokeTestEnvironment.Cleanup();
}

if (selected == 0)
{
    Console.Error.WriteLine($"No smoke test name contains '{filter}'.");
    return 1;
}

if (failures.Count == 0)
{
    Console.WriteLine(filter is null
        ? "All Arkive smoke tests passed."
        : $"All {selected:N0} selected Arkive smoke tests passed.");
    return 0;
}

Console.Error.WriteLine($"{failures.Count} smoke test(s) failed:");
foreach (var failure in failures)
    Console.Error.WriteLine($"  {failure}");

return 1;
