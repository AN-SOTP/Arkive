using System.Reflection;
using Arkive.App;
using Arkive.Core.Particles;

/// <summary>The skill preview draws effects with the mod's edited textures, on sprites and shader samplers alike.</summary>
internal static class SkillPreviewEditChecks
{
    public static void Verify()
    {
        var shader = new ParticleShader("ps", string.Empty, string.Empty, new Dictionary<string, float[]>(),
            new Dictionary<string, string> { ["ark_gt0"] = "fx/export/FX_TEX_01/Texture2D/fx_c_cloud_015.png", ["ark_gt1"] = ParticleShaderPlanner.SceneDepth },
            [], 1);
        var found = new ParticleEditorAssets(
            new Dictionary<string, ParticleEditorTexture>
            {
                ["fx_mi"] = new("fx/export/FX_TEX_01/Texture2D/fx_c_cloud_015.png", "additive")
                {
                    Shader = new ParticleShaderSet("map", true, "additive", false, new Dictionary<string, string>(),
                        new Dictionary<string, ParticleShader> { ["ps"] = shader }, []),
                },
                ["other_mi"] = new("other/export/fx_d_noise_014.png", "translucent"),
            },
            new Dictionary<string, string> { ["fm_a_cylinder_002"] = "meshes/fm_a_cylinder_002/export/m.gltf" });
        var type = typeof(SkillPreviewService);
        var names = ((IEnumerable<string>)type.GetMethod("TextureNames", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [found])!).ToArray();
        Program.Check(names.Order().SequenceEqual(["fx_c_cloud_015", "fx_d_noise_014"]),
            "The skill preview did not list the effects' textures by name: " + string.Join(", ", names));
        var edited = (ParticleEditorAssets)type.GetMethod("WithTextures", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [found, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["FX_C_CLOUD_015"] = "edits/1/fx_c_cloud_015.png" }])!;
        var samplers = edited.Textures["fx_mi"].Shader!.Shaders["ps"].Samplers;
        Program.Check(edited.Textures["fx_mi"].RelativePath == "edits/1/fx_c_cloud_015.png" &&
                      samplers["ark_gt0"] == "edits/1/fx_c_cloud_015.png" && samplers["ark_gt1"] == ParticleShaderPlanner.SceneDepth &&
                      edited.Textures["other_mi"].RelativePath == "other/export/fx_d_noise_014.png" &&
                      edited.Meshes.SequenceEqual(found.Meshes),
            "The skill preview did not swap the edited texture into the effect and its shader, or changed something else.");
        Console.WriteLine("PASS: the skill preview draws effects with the mod's edited textures and leaves the rest as the game has them");
    }
}
