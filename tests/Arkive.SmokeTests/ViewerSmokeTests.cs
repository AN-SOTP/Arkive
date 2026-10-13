using Arkive.Core.Viewer;
using Arkive.Core.Viewer.Shaders;

namespace Arkive.SmokeTests;

internal static class ViewerSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("parses UModel property text with nested arrays and inline structs", TestPropertyTree),
        new("flattens material parameters along same-named parent materials", TestMaterialDescription),
        new("compiles colour recipes to GLSL and evaluates them identically on the CPU", TestRecipeLanguage),
        new("matches shipped colour recipes to master materials and falls back to stored textures", TestRecipeCatalog),
        new("reads and evaluates a shader map's uniform expressions", TestUniformExpressions),
        new("keys shader permutations by their static parameter set", TestStaticParameterSet),
        new("translates pixel shader disassembly to GLSL with D3D register semantics", TestDxbcTranslation),
        new("reads texture sampling settings from cooked properties", TestTextureProbe),
        new("keys and trims the model viewer cache", TestViewerCache),
        new("rebuilds a viewer scene when any package it read changes", TestPackageStamps),
    ];

    private static void TestPropertyTree()
    {
        const string text = """
            Parent = MaterialInstanceConstant'mat.master_mi'
            ScalarParameterValues[1] =
            {
                ScalarParameterValues[0] =
                {
                    ParameterInfo =
                    ParameterValue = 0.5
                    ParameterName = edge
                }
            }
            Sockets[1] =
            {
                Sockets[0] = { Name=fx_head, Bone=b_head, Offset={ X=1, Y=2 } }
            }
            """;
        var tree = UmodelPropertyTree.Parse(text);
        Assert(UmodelPropertyTree.ParseObjectReference(tree.ChildValue("Parent")) is ("MaterialInstanceConstant", "mat.master_mi", "master_mi"),
            "object references were not parsed");
        var scalar = tree.Elements("ScalarParameterValues").Single();
        Assert(scalar.ChildValue("ParameterName") == "edge" && scalar.ChildValue("ParameterValue") == "0.5",
            "nested array elements were not parsed");
        var socket = tree.Elements("Sockets").Single();
        Assert(socket.ChildValue("Name") == "fx_head" && socket.ChildValue("Bone") == "b_head" &&
               socket.ChildValue("Offset") == "{ X=1, Y=2 }",
            "inline struct fields (including nested braces) were not parsed");
    }

    private static void TestMaterialDescription()
    {
        var root = Path.Combine(fixtureDirectory, "viewer-materials");
        Write(root, "MaterialInstanceConstant", "leaf_mi", """
            Parent = MaterialInstanceConstant'mat.hair_msk'
            VectorParameterValues[1] =
            {
                VectorParameterValues[0] =
                {
                    ParameterValue = { R=0.25, G=0.5, B=0.75, A=1 }
                    ParameterName = haircolor
                }
            }
            """);
        Write(root, "MaterialInstanceConstant", "hair_msk", """
            Parent = Material3'ch.hair_msk'
            ScalarParameterValues[1] =
            {
                ScalarParameterValues[0] =
                {
                    ParameterValue = 2
                    ParameterName = power
                }
            }
            VectorParameterValues[1] =
            {
                VectorParameterValues[0] =
                {
                    ParameterValue = { R=1, G=1, B=1, A=1 }
                    ParameterName = haircolor
                }
            }
            """);
        Write(root, "Material3", "hair_msk", """
            TwoSided = true
            bIsMasked = true
            BlendMode = BLEND_Masked (1)
            OpacityMaskClipValue = 0.001
            ReferencedTextures[1] =
            {
                ReferencedTextures[0] = Texture2D'tex.diffuse'
            }
            """);
        var materials = MaterialInstanceDescriptionReader.ReadExportFolder(root);
        var leaf = materials[MaterialInstanceDescriptionReader.Key("MaterialInstanceConstant", "leaf_mi")];
        Assert(leaf.ParentChain.SequenceEqual(["hair_msk", "hair_msk"]) && leaf.RootMaterial == "hair_msk",
            "a master instance and its same-named base material were not both followed");
        Assert(leaf.Vectors["haircolor"].SequenceEqual([0.25f, 0.5f, 0.75f, 1f]) && leaf.Scalars["power"] == 2,
            "the leaf did not win over parent parameters, or inherited parameters were lost");
        Assert(leaf.TwoSided && leaf.Masked && leaf.OpacityMaskClipValue == 0.001f &&
               leaf.ReferencedTextures!.SequenceEqual(["diffuse"]),
            "base material flags and referenced textures were not read");

        static void Write(string root, string className, string name, string text)
        {
            var directory = Path.Combine(root, "PKG", className);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".props.txt"), text);
        }
    }

    private static void TestRecipeLanguage()
    {
        var bindings = new Bindings(
            new Dictionary<string, float[]> { ["tint"] = [0.5f, 0.25f, 1f, 1f] },
            new Dictionary<string, float> { ["mix"] = 0.5f },
            ["diffuse"]);
        var defines = new Dictionary<string, string> { ["d"] = "tex('diffuse')", ["base"] = "$d.rgb * vec('tint').rgb" };
        var node = RecipeParser.Parse("lerp($base, vec3(1.0), scalar('mix')) + srgb(0.5) * 0.0 + uv(1).x * 0.0", defines);
        var compiler = new RecipeGlslCompiler(bindings);
        var glsl = compiler.Compile(node, 3);
        Assert(compiler.UsesSecondUv && compiler.Samplers.ContainsKey("diffuse") &&
               compiler.Uniforms.Any(uniform => uniform.Parameter == "tint" && uniform.Value[1] == 0.25f) &&
               glsl.Contains("texture2D(ark_t0, vUv)", StringComparison.Ordinal) && glsl.Contains("ark_vUv1", StringComparison.Ordinal),
            "recipe GLSL did not bind its texture, vector, scalar and second UV set");
        var evaluator = new RecipeEvaluator(bindings, (_, _, _) => [0.8f, 0.4f, 0.2f, 1f]);
        var value = evaluator.Evaluate(node, 0.5f, 0.5f, 3);
        // lerp((0.4, 0.1, 0.2), 1, 0.5)
        Assert(Math.Abs(value[0] - 0.7f) < 1e-5 && Math.Abs(value[1] - 0.55f) < 1e-5 && Math.Abs(value[2] - 0.6f) < 1e-5,
            "CPU evaluation of a recipe disagreed with its definition");
        var refused = false;
        try { RecipeParser.Parse("vec('a').rgbx"); }
        catch (RecipeSyntaxException) { refused = true; }
        Assert(refused, "an invalid swizzle was accepted");
    }

    private static void TestRecipeCatalog()
    {
        var catalog = MaterialRecipeCatalog.LoadEmbedded();
        var hair = Material("npc_hair_fur_msk");
        var plan = catalog.Plan(hair, _ => true);
        Assert(plan.FamilyId == "npc-hair-fur-masked" && plan.Status == MaterialRecipeStatus.Verified && plan.UsesSecondUv,
            "the verified hair recipe did not match its master material");
        var unknown = catalog.Plan(Material("something_new_msk"), _ => true);
        Assert(unknown.Status == MaterialRecipeStatus.Fallback, "an unknown master did not fall back to the stored texture");
        var stored = catalog.Plan(hair, _ => true, storedTextureOnly: true);
        Assert(stored.Status == MaterialRecipeStatus.Fallback, "the stored-texture view used a recipe");

        static MaterialInstanceDescription Material(string root) => new(
            "leaf_mi", "MaterialInstanceConstant", [root], root,
            new Dictionary<string, float>(), new Dictionary<string, float[]>(),
            new Dictionary<string, string> { ["texture_diffuse"] = "leaf_d", ["texture_normal"] = "leaf_n" },
            new Dictionary<string, bool>(), false, true, "BLEND_Masked", 0.001f);
    }

    private static void TestUniformExpressions()
    {
        string[] names =
        [
            "fmaterialuniformexpressionvectorparameter", "fmaterialuniformexpressionscalarparameter",
            "fmaterialuniformexpressionappendvector", "fmaterialuniformexpressionconstant",
            "fmaterialuniformexpressionfoldedmath", "fmaterialuniformexpressionclamp",
            "fmaterialuniformexpressiontextureparameter", "haircolor", "edge", "texture_diffuse",
        ];
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            void Name(int index) { writer.Write(index); writer.Write(0); }
            void Color(float r, float g, float b, float a) { writer.Write(r); writer.Write(g); writer.Write(b); writer.Write(a); }
            // Vectors: haircolor, append(constant(0.5), edge)
            writer.Write(2);
            Name(0); Name(7); Color(1, 1, 1, 1);
            Name(2); Name(3); Color(0.5f, 0, 0, 0); writer.Write((byte)1); Name(1); Name(8); writer.Write(0.25f); writer.Write(1);
            // Scalars: clamp(edge * edge, 0.1, 1)
            writer.Write(1);
            Name(5); Name(4); Name(1); Name(8); writer.Write(0.25f); Name(1); Name(8); writer.Write(0.25f); writer.Write((byte)2);
            Name(3); Color(0.1f, 0, 0, 0); writer.Write((byte)1); Name(3); Color(1, 0, 0, 0); writer.Write((byte)1);
            // Textures: texture_diffuse in material texture 3
            writer.Write(1);
            Name(6); Name(9); writer.Write(3);
        }
        stream.Position = 0;
        var reader = new UniformExpressionReader(stream, names);
        var expressions = new ShaderMaterialExpressions(reader.ReadList(), reader.ReadList(), reader.ReadList());
        Assert(expressions.Textures.Single() is UniformTexture { Parameter: "texture_diffuse", TextureIndex: 3 },
            "texture parameter expressions were not read");
        float[]? Vector(string name) => name == "haircolor" ? [0.3f, 0.2f, 0.1f, 1] : null;
        float? Scalar(string name) => name == "edge" ? 0.2f : null;
        var colour = UniformExpressionEvaluator.Evaluate(expressions.Vectors[0], Vector, Scalar);
        var append = UniformExpressionEvaluator.Evaluate(expressions.Vectors[1], Vector, Scalar);
        var clamp = UniformExpressionEvaluator.Evaluate(expressions.Scalars[0], Vector, Scalar);
        Assert(colour.SequenceEqual([0.3f, 0.2f, 0.1f, 1]) && append[0] == 0.5f && Math.Abs(append[1] - 0.2f) < 1e-6 &&
               Math.Abs(clamp[0] - 0.1f) < 1e-6,
            "uniform expressions did not evaluate with the material's parameters");
    }

    private static void TestStaticParameterSet()
    {
        var baseId = Guid.NewGuid().ToByteArray();
        var dyeing = Guid.NewGuid().ToByteArray();
        var masked = Guid.NewGuid().ToByteArray();
        byte[] Set(params (byte[] Expression, int Name, bool Value, bool Override)[] switches)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(baseId);
            writer.Write(switches.Length);
            foreach (var (expression, name, value, overridden) in switches)
            {
                writer.Write(name); writer.Write(0); writer.Write(value ? 1 : 0); writer.Write(overridden ? 1 : 0);
                writer.Write(expression);
            }
            writer.Write(0); writer.Write(0); writer.Write(0);
            writer.Write(11);
            return stream.ToArray();
        }

        // The cache and an instance list the same switches with different name indices (each package has its
        // own name table) and override flags, in any order: the permutation is the same.
        var cache = Set((dyeing, 7, false, false), (masked, 9, true, false));
        var instance = Set((masked, 40, true, true), (dyeing, 31, false, false));
        Assert(StaticParameterSet.TryRead(cache, 0, out var cached, out var end) && end == cache.Length - 4 &&
               BitConverter.ToInt32(cache, end) == 11, "a static parameter set was not read to its end");
        Assert(StaticParameterSet.TryRead(instance, 0, out var used, out _) && used.Key == cached.Key &&
               used.OnSwitches.SequenceEqual([Convert.ToHexString(masked)[..8]]),
            "equal static permutations were keyed differently");
        var other = Set((dyeing, 7, true, true), (masked, 9, true, false));
        Assert(StaticParameterSet.TryRead(other, 0, out var different, out _) && different.Key != cached.Key,
            "a different switch value kept the same permutation key");
        var broken = Set((dyeing, 7, false, false));
        broken[16 + 4 + 8] = 2;
        Assert(!StaticParameterSet.TryRead(broken, 0, out _, out _), "a switch value that is not a boolean was accepted");
    }

    private static void TestDxbcTranslation()
    {
        const string disassembly = """
            //
            // Input signature:
            //
            // Name                 Index   Mask Register SysValue  Format   Used
            // -------------------- ----- ------ -------- -------- ------- ------
            // TEXCOORD                 0   xyzw        4     NONE   float   xy
            //
            ps_5_0
            dcl_globalFlags refactoringAllowed
            dcl_constantbuffer CB0[4], immediateIndexed
            dcl_sampler s0, mode_default
            dcl_resource_texture2d (float,float,float,float) t0
            dcl_resource_texture2d (float,float,float,float) t1
            dcl_input_ps linear v4.xy
            dcl_output o3.xyzw
            dcl_temps 2
            sample_b_indexable(texture2d)(float,float,float,float) r0.xyzw, v4.xyxx, t0.zxyw, s0, l(0.000000)
            lt r1.x, r0.w, l(0.500000)
            discard_nz r1.x
            mad_sat r0.xyz, r0.xyzx, cb0[2].xyzx, -cb0[3].xxxx
            dp3 r1.y, r0.xyzx, l(0.300000, 0.590000, 0.110000, 0.000000)
            if_nz r1.x
              movc r0.xyz, r1.xxxx, l(0,0,0,0), r0.xyzx
            else
              sample_indexable(texture2d)(float,float,float,float) r0.w, v4.xyxx, t1.wxyz, s0
            endif
            mov o3.xyzw, r0.xyzw
            ret
            """;
        var translation = DxbcGlslTranslator.Translate(disassembly, slot => slot == 0, slot => slot == 0);
        var glsl = translation.Function;
        Assert(translation.ConstantBufferSizes[0] == 4 && translation.Outputs.SequenceEqual([3]) &&
               translation.Inputs.Single() is { Semantic: "TEXCOORD", Index: 0, Register: 4 },
            "declarations and signatures were not read");
        Assert(glsl.Contains("ark_srgb(texture(ark_gt0", StringComparison.Ordinal) && glsl.Contains(".zxyw", StringComparison.Ordinal) &&
               glsl.Contains("discard;", StringComparison.Ordinal) && glsl.Contains("clamp(", StringComparison.Ordinal) &&
               glsl.Contains("} else {", StringComparison.Ordinal) && glsl.Contains("ark_cb0[2]", StringComparison.Ordinal) &&
               glsl.Contains("vec4(0.0, 0.0, 0.0, 1.0)", StringComparison.Ordinal) && glsl.Contains("ark_o3.xyzw", StringComparison.Ordinal),
            "the translated GLSL is missing sampling, sRGB, discard, saturate, branching or unbound-texture handling");
        var refused = false;
        try { DxbcGlslTranslator.Translate("ps_5_0\nlds_bogus r0.x, r1.x\n", _ => true); }
        catch (DxbcTranslationException) { refused = true; }
        Assert(refused, "an unknown instruction was translated instead of refused");
    }

    private static void TestTextureProbe()
    {
        var names = new[] { "None", "SRGB", "BoolProperty", "CompressionSettings", "ByteProperty", "TextureCompressionSettings", "TC_NormalmapBC5" }
            .Select((value, index) => new UnrealNameEntry(index, value, 0)).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(-1);
            WriteNameReference(writer, 3); WriteNameReference(writer, 4); writer.Write(8); writer.Write(0);
            WriteNameReference(writer, 5); WriteNameReference(writer, 6);
            WriteNameReference(writer, 1); WriteNameReference(writer, 2); writer.Write(0); writer.Write(0); writer.Write((byte)1);
            WriteNameReference(writer, 0);
        }
        var info = TexturePropertyProbe.Read(stream.ToArray(), names);
        Assert(info.Srgb && info.CompressionSettings == "TC_NormalmapBC5" && !TexturePropertyProbe.SamplesSrgb(info),
            "normal-map compression did not force linear sampling");
        Assert(TexturePropertyProbe.SamplesSrgb(new TextureSamplingInfo(true, "TC_Default", "PF_DXT5")) &&
               !TexturePropertyProbe.SamplesSrgb(new TextureSamplingInfo(false, "TC_Default", "PF_DXT5")),
            "default textures did not follow their SRGB flag");
    }

    private static void TestPackageStamps()
    {
        var root = Path.Combine(Path.GetTempPath(), "arkive-viewer-stamps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var texturePackage = Path.Combine(root, "textures.upk");
            File.WriteAllBytes(texturePackage, [1, 2, 3]);
            var stamp = ModelViewerPackageStamp.Of(texturePackage);
            Assert(stamp.Unchanged, "an untouched package was reported as changed");
            File.WriteAllBytes(texturePackage, [1, 2, 3, 4]);
            File.SetLastWriteTimeUtc(texturePackage, DateTime.UtcNow.AddMinutes(1));
            Assert(!stamp.Unchanged, "a rewritten texture package did not invalidate the scene");
            File.Delete(texturePackage);
            Assert(!stamp.Unchanged, "a removed package did not invalidate the scene");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void TestViewerCache()
    {
        var root = Path.Combine(fixtureDirectory, "viewer-cache");
        var package = Path.Combine(fixtureDirectory, "viewer-package.upk");
        File.WriteAllBytes(package, new byte[16]);
        var key = ModelViewerCache.Key(package, 5, "mesh_sk", 1);
        File.SetLastWriteTimeUtc(package, DateTime.UtcNow.AddMinutes(1));
        Assert(key != ModelViewerCache.Key(package, 5, "mesh_sk", 1), "the cache key ignored a changed package");
        var cache = new ModelViewerCache(root, 64L << 20);
        for (var i = 0; i < 3; i++)
        {
            var directory = cache.Prepare($"scene{i}");
            File.WriteAllBytes(Path.Combine(directory, "scene.json"), new byte[30 << 20]);
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddMinutes(i));
        }
        cache.Trim(keepKey: "scene0");
        Assert(cache.IsReady("scene0") && cache.IsReady("scene2") && !cache.IsReady("scene1") && cache.CurrentBytes() <= 64L << 20,
            "the cache did not evict the least recently used scene while keeping the requested one");
    }

    private sealed class Bindings(
        IReadOnlyDictionary<string, float[]> vectors,
        IReadOnlyDictionary<string, float> scalars,
        IReadOnlyCollection<string> textures) : IRecipeBindings
    {
        public bool HasTexture(string parameter) => textures.Contains(parameter);
        public float[]? Vector(string parameter) => vectors.GetValueOrDefault(parameter);
        public float? Scalar(string parameter) => scalars.TryGetValue(parameter, out var value) ? value : null;
        public bool? Switch(string parameter) => null;
    }
}
