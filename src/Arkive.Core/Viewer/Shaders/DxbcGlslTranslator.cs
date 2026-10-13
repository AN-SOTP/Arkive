using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Arkive.Core.Viewer.Shaders;

public sealed record DxbcSignatureElement(string Semantic, int Index, int Register);

/// <summary>A pixel shader translated to GLSL ES 3.00 for the preview.</summary>
public sealed record DxbcTranslation(
    string Declarations,
    string Function,
    IReadOnlyDictionary<int, int> ConstantBufferSizes,
    IReadOnlyList<int> Textures2D,
    IReadOnlyList<DxbcSignatureElement> Inputs,
    IReadOnlyList<int> Outputs,
    IReadOnlyDictionary<int, IReadOnlySet<string>> ConstantSwizzles,
    int InstructionCount);

public sealed class DxbcTranslationException(string message) : NotSupportedException(message);

/// <summary>
/// Translates D3D shader model 4/5 pixel-shader disassembly into GLSL. Registers are kept
/// as raw 32-bit lanes (uvec4) so float, integer and comparison instructions keep D3D's
/// exact bit semantics. The generated function writes <c>ark_o0..ark_oN</c>.
/// </summary>
public static partial class DxbcGlslTranslator
{
    private const int MaximumLoopIterations = 256;

    /// <param name="textureBound">Whether a material texture is bound to a t# slot (unbound slots read a constant).</param>
    /// <param name="srgb">Whether a slot's texture is sampled with sRGB decoding, as the game's sampler does.</param>
    public static DxbcTranslation Translate(string disassembly, Func<int, bool> textureBound, Func<int, bool>? srgb = null)
    {
        srgb ??= _ => false;
        ArgumentNullException.ThrowIfNull(disassembly);
        var lines = disassembly.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var inputs = ParseSignature(lines, "// Input signature:");
        var body = new StringBuilder();
        var declarations = new StringBuilder();
        var constantBuffers = new SortedDictionary<int, int>();
        var temps = 0;
        var indexable = new List<(int Index, int Size)>();
        var textures2D = new List<int>();
        var cubes = new HashSet<int>();
        var outputs = new SortedSet<int>();
        var swizzles = new Dictionary<int, HashSet<string>>();
        var instructions = 0;
        var depth = 1;
        var started = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!started)
            {
                started = line.StartsWith("ps_", StringComparison.Ordinal);
                if (!started && line.StartsWith("vs_", StringComparison.Ordinal))
                    throw new DxbcTranslationException("Only pixel shaders can be translated.");
                continue;
            }
            if (line.StartsWith("dcl_", StringComparison.Ordinal))
            {
                ParseDeclaration(line, constantBuffers, ref temps, indexable, textures2D, cubes, outputs);
                continue;
            }
            var (opcode, operands) = SplitInstruction(line);
            instructions++;
            foreach (var operand in operands)
                foreach (Match match in ConstantUseRegex().Matches(operand))
                {
                    var register = int.Parse(match.Groups["i"].Value, CultureInfo.InvariantCulture);
                    if (!swizzles.TryGetValue(register, out var set)) swizzles[register] = set = [];
                    set.Add(match.Groups["s"].Value);
                }
            var code = Emit(opcode, operands, cubes, textureBound, srgb, ref depth);
            if (code.Length == 0) continue;
            if (code.StartsWith('}')) depth = Math.Max(1, depth);
            body.Append(' ', depth * 2).Append(code).Append('\n');
            if (code.EndsWith('{')) depth++;
        }
        if (!started) throw new DxbcTranslationException("The disassembly has no pixel shader body.");

        for (var i = 0; i < Math.Max(6, outputs.DefaultIfEmpty(0).Max() + 1); i++)
            declarations.Append($"uvec4 ark_o{i};\n");
        // Bit-field insert with D3D semantics: widths and offsets use their low 5 bits, and a full-width field
        // replaces every bit.
        if (body.ToString().Contains("ark_bfi(", StringComparison.Ordinal))
            declarations.Append(
                "uvec4 ark_bfi(uvec4 width, uvec4 offset, uvec4 insert, uvec4 base) {\n" +
                "  uvec4 w = width & uvec4(31u); uvec4 o = offset & uvec4(31u);\n" +
                "  uvec4 mask = ((uvec4(1u) << w) - uvec4(1u)) << o;\n" +
                "  return ((insert << o) & mask) | (base & ~mask);\n" +
                "}\n");
        foreach (var input in inputs)
            declarations.Append($"uvec4 ark_in{input.Register};\n");
        var function = new StringBuilder("void ark_game_shader() {\n");
        for (var i = 0; i < temps; i++) function.Append($"  uvec4 r{i} = uvec4(0u);\n");
        foreach (var (index, size) in indexable) function.Append($"  uvec4 x{index}[{size}];\n");
        function.Append(body).Append("}\n");
        return new DxbcTranslation(
            declarations.ToString(),
            function.ToString(),
            constantBuffers,
            textures2D,
            inputs,
            outputs.ToArray(),
            swizzles.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value),
            instructions);
    }

    private static List<DxbcSignatureElement> ParseSignature(string[] lines, string header)
    {
        var result = new List<DxbcSignatureElement>();
        var start = Array.FindIndex(lines, line => line.Trim() == header);
        if (start < 0) return result;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("//", StringComparison.Ordinal)) break;
            var parts = line.TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && int.TryParse(parts[1], out var index) && int.TryParse(parts[3], out var register))
                result.Add(new DxbcSignatureElement(parts[0], index, register));
            else if (result.Count > 0 && line == "//") break;
        }
        return result;
    }

    private static void ParseDeclaration(
        string line, SortedDictionary<int, int> constantBuffers, ref int temps,
        List<(int, int)> indexable, List<int> textures2D, HashSet<int> cubes, SortedSet<int> outputs)
    {
        Match match;
        if ((match = ConstantBufferRegex().Match(line)).Success)
            constantBuffers[int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture)] =
                int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        else if ((match = TempsRegex().Match(line)).Success)
            temps = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        else if ((match = IndexableTempRegex().Match(line)).Success)
            indexable.Add((int.Parse(match.Groups["i"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture)));
        else if ((match = ResourceRegex().Match(line)).Success)
        {
            var slot = int.Parse(match.Groups["t"].Value, CultureInfo.InvariantCulture);
            if (match.Groups["k"].Value == "texture2d") textures2D.Add(slot);
            else if (match.Groups["k"].Value == "texturecube") cubes.Add(slot);
        }
        else if ((match = OutputRegex().Match(line)).Success)
            outputs.Add(int.Parse(match.Groups["o"].Value, CultureInfo.InvariantCulture));
        else if (line.StartsWith("dcl_immediateConstantBuffer", StringComparison.Ordinal))
            throw new DxbcTranslationException("Immediate constant buffers are not supported yet.");
    }

    private static (string Opcode, List<string> Operands) SplitInstruction(string line)
    {
        var space = line.IndexOf(' ');
        if (space < 0) return (line, []);
        var opcode = line[..space];
        var rest = line[(space + 1)..];
        var operands = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                operands.Add(rest[start..i].Trim());
                start = i + 1;
            }
        }
        operands.Add(rest[start..].Trim());
        return (opcode, operands);
    }

    private static string Emit(
        string opcode, List<string> ops, HashSet<int> cubes, Func<int, bool> textureBound, Func<int, bool> srgb, ref int depth)
    {
        var saturate = opcode.EndsWith("_sat", StringComparison.Ordinal);
        var name = saturate ? opcode[..^4] : opcode;
        var bare = name.Split('(')[0];

        string Float(int index) => FloatSource(ops[index]);
        string Bits(int index) => BitsSource(ops[index]);
        string Int(int index) => $"ivec4({BitsSource(ops[index], integer: true)})";
        string StoreFloat(string expression)
        {
            if (saturate) expression = $"clamp({expression}, 0.0, 1.0)";
            return Store(ops[0], $"floatBitsToUint({expression})");
        }

        switch (bare)
        {
            case "mov":
                return saturate || HasModifier(ops[1]) ? StoreFloat(Float(1)) : Store(ops[0], Bits(1));
            case "add": return StoreFloat($"({Float(1)} + {Float(2)})");
            case "mul": return StoreFloat($"({Float(1)} * {Float(2)})");
            case "div": return StoreFloat($"({Float(1)} / {Float(2)})");
            case "mad": return StoreFloat($"({Float(1)} * {Float(2)} + {Float(3)})");
            case "min": return StoreFloat($"min({Float(1)}, {Float(2)})");
            case "max": return StoreFloat($"max({Float(1)}, {Float(2)})");
            case "sqrt": return StoreFloat($"sqrt({Float(1)})");
            case "rsq": return StoreFloat($"inversesqrt({Float(1)})");
            case "rcp": return StoreFloat($"(1.0 / {Float(1)})");
            case "log": return StoreFloat($"log2({Float(1)})");
            case "exp": return StoreFloat($"exp2({Float(1)})");
            case "frc": return StoreFloat($"fract({Float(1)})");
            case "round_ni": return StoreFloat($"floor({Float(1)})");
            case "round_pi": return StoreFloat($"ceil({Float(1)})");
            case "round_ne": return StoreFloat($"roundEven({Float(1)})");
            case "round_z": return StoreFloat($"trunc({Float(1)})");
            case "deriv_rtx" or "deriv_rtx_coarse" or "deriv_rtx_fine": return StoreFloat($"dFdx({Float(1)})");
            case "deriv_rty" or "deriv_rty_coarse" or "deriv_rty_fine": return StoreFloat($"dFdy({Float(1)})");
            case "dp2": return StoreFloat($"vec4(dot(({Float(1)}).xy, ({Float(2)}).xy))");
            case "dp3": return StoreFloat($"vec4(dot(({Float(1)}).xyz, ({Float(2)}).xyz))");
            case "dp4": return StoreFloat($"vec4(dot({Float(1)}, {Float(2)}))");
            case "sincos":
            {
                var builder = new StringBuilder();
                if (ops[0] != "null") builder.Append(Store(ops[0], $"floatBitsToUint(sin({Float(2)}))"));
                if (ops[1] != "null") builder.Append(Store(ops[1], $"floatBitsToUint(cos({Float(2)}))"));
                return builder.ToString();
            }
            case "lt": return Store(ops[0], Mask($"lessThan({Float(1)}, {Float(2)})"));
            case "ge": return Store(ops[0], Mask($"greaterThanEqual({Float(1)}, {Float(2)})"));
            case "eq": return Store(ops[0], Mask($"equal({Float(1)}, {Float(2)})"));
            case "ne": return Store(ops[0], Mask($"notEqual({Float(1)}, {Float(2)})"));
            case "ilt": return Store(ops[0], Mask($"lessThan({Int(1)}, {Int(2)})"));
            case "ige": return Store(ops[0], Mask($"greaterThanEqual({Int(1)}, {Int(2)})"));
            case "ieq": return Store(ops[0], Mask($"equal({Bits(1)}, {Bits(2)})"));
            case "ine": return Store(ops[0], Mask($"notEqual({Bits(1)}, {Bits(2)})"));
            case "ult": return Store(ops[0], Mask($"lessThan({Bits(1)}, {Bits(2)})"));
            case "uge": return Store(ops[0], Mask($"greaterThanEqual({Bits(1)}, {Bits(2)})"));
            case "movc":
            {
                var condition = Mask($"notEqual({Bits(1)}, uvec4(0u))");
                var expression = $"(({Bits(2)} & {condition}) | ({Bits(3)} & ~{condition}))";
                return saturate ? StoreFloat($"uintBitsToFloat({expression})") : Store(ops[0], expression);
            }
            case "and": return Store(ops[0], $"({Bits(1)} & {Bits(2)})");
            case "or": return Store(ops[0], $"({Bits(1)} | {Bits(2)})");
            case "xor": return Store(ops[0], $"({Bits(1)} ^ {Bits(2)})");
            case "not": return Store(ops[0], $"(~{Bits(1)})");
            case "iadd": return Store(ops[0], $"({BitsSource(ops[1], integer: true)} + {BitsSource(ops[2], integer: true)})");
            case "imul":
                return ops[0] == "null"
                    ? Store(ops[1], $"uvec4({Int(2)} * {Int(3)})")
                    : throw new DxbcTranslationException("imul with a high-word destination is not supported.");
            case "imad": return Store(ops[0], $"uvec4({Int(1)} * {Int(2)} + {Int(3)})");
            case "ishl": return Store(ops[0], $"({Bits(1)} << ({Bits(2)} & uvec4(31u)))");
            case "ushr": return Store(ops[0], $"({Bits(1)} >> ({Bits(2)} & uvec4(31u)))");
            case "ishr": return Store(ops[0], $"uvec4({Int(1)} >> ivec4({Bits(2)} & uvec4(31u))))");
            case "imax": return Store(ops[0], $"uvec4(max({Int(1)}, {Int(2)}))");
            case "imin": return Store(ops[0], $"uvec4(min({Int(1)}, {Int(2)}))");
            case "umax": return Store(ops[0], $"max({Bits(1)}, {Bits(2)})");
            case "bfi": return Store(ops[0], $"ark_bfi({Bits(1)}, {Bits(2)}, {Bits(3)}, {Bits(4)})");
            case "ubfe": return Store(ops[0], $"(({Bits(3)} >> ({Bits(2)} & uvec4(31u))) & ((uvec4(1u) << ({Bits(1)} & uvec4(31u))) - uvec4(1u)))");
            case "countbits": return Store(ops[0], $"uvec4(bitCount({Bits(1)}))");
            case "bfrev": return Store(ops[0], $"bitfieldReverse({Bits(1)})");
            case "firstbit_lo": return Store(ops[0], $"uvec4(findLSB({Bits(1)}))");
            case "firstbit_hi": return Store(ops[0], $"uvec4(findMSB({Bits(1)}))");
            case "resinfo" or "resinfo_indexable": return ResourceInfo(opcode, ops, textureBound);
            case "umin": return Store(ops[0], $"min({Bits(1)}, {Bits(2)})");
            case "ineg": return Store(ops[0], $"uvec4(-{Int(1)})");
            case "ftoi": return Store(ops[0], $"uvec4(ivec4({Float(1)}))");
            case "ftou": return Store(ops[0], $"uvec4(max({Float(1)}, vec4(0.0)))");
            case "itof": return StoreFloat($"vec4({Int(1)})");
            case "utof": return StoreFloat($"vec4({Bits(1)})");
            case "f32tof16": return Store(ops[0], $"uvec4(packHalf2x16(vec2(({Float(1)}).x, 0.0)), packHalf2x16(vec2(({Float(1)}).y, 0.0)), packHalf2x16(vec2(({Float(1)}).z, 0.0)), packHalf2x16(vec2(({Float(1)}).w, 0.0)))");
            case "f16tof32": return StoreFloat($"vec4(unpackHalf2x16(({Bits(1)}).x).x, unpackHalf2x16(({Bits(1)}).y).x, unpackHalf2x16(({Bits(1)}).z).x, unpackHalf2x16(({Bits(1)}).w).x)");
            case "discard_nz": return $"if (({Bits(0)}).x != 0u) discard;";
            case "discard_z": return $"if (({Bits(0)}).x == 0u) discard;";
            case "if_nz": return $"if (({Bits(0)}).x != 0u) {{";
            case "if_z": return $"if (({Bits(0)}).x == 0u) {{";
            case "else": depth--; return "} else {";
            case "endif" or "endloop": depth--; return "}";
            case "loop": return $"for (int ark_loop{depth} = 0; ark_loop{depth} < {MaximumLoopIterations}; ark_loop{depth}++) {{";
            case "break": return "break;";
            case "breakc_nz": return $"if (({Bits(0)}).x != 0u) break;";
            case "breakc_z": return $"if (({Bits(0)}).x == 0u) break;";
            case "continue": return "continue;";
            case "continuec_nz": return $"if (({Bits(0)}).x != 0u) continue;";
            case "ret": return "return;";
            case "retc_nz": return $"if (({Bits(0)}).x != 0u) return;";
            case "retc_z": return $"if (({Bits(0)}).x == 0u) return;";
            case "nop": return string.Empty;
            case "sample" or "sample_indexable" or "sample_b" or "sample_b_indexable" or "sample_l" or "sample_l_indexable"
                or "sample_d" or "sample_d_indexable" or "sample_c" or "sample_c_indexable" or "sample_c_lz"
                or "sample_c_lz_indexable":
                return Sample(bare, ops, cubes, textureBound, srgb);
        }
        throw new DxbcTranslationException($"Unsupported instruction '{opcode}'.");
    }

    /// <summary>Texture dimensions (mip count reported as 1); unbound textures report 1 x 1.</summary>
    private static string ResourceInfo(string opcode, List<string> ops, Func<int, bool> textureBound)
    {
        var resource = ResourceOperandRegex().Match(ops[2]);
        if (!resource.Success) throw new DxbcTranslationException($"Unsupported texture operand '{ops[2]}'.");
        var slot = int.Parse(resource.Groups["t"].Value, CultureInfo.InvariantCulture);
        var swizzle = resource.Groups["s"].Success ? resource.Groups["s"].Value : "xyzw";
        var size = textureBound(slot) ? $"uvec2(textureSize(ark_gt{slot}, int(({BitsSource(ops[1])}).x)))" : "uvec2(1u)";
        var value = opcode.Contains("uint", StringComparison.Ordinal)
            ? $"uvec4({size}, 1u, 1u)"
            : $"floatBitsToUint(vec4(vec2({size}), 1.0, 1.0))";
        return Store(ops[0], $"({value}).{swizzle}");
    }

    private static string Sample(
        string bare, List<string> ops, HashSet<int> cubes, Func<int, bool> textureBound, Func<int, bool> srgb)
    {
        var resource = ResourceOperandRegex().Match(ops[2]);
        if (!resource.Success) throw new DxbcTranslationException($"Unsupported texture operand '{ops[2]}'.");
        var slot = int.Parse(resource.Groups["t"].Value, CultureInfo.InvariantCulture);
        var swizzle = resource.Groups["s"].Success ? resource.Groups["s"].Value : "xyzw";
        var coordinate = FloatSource(ops[1]);
        string sampled;
        if (bare.StartsWith("sample_c", StringComparison.Ordinal))
            sampled = "vec4(1.0)";
        else if (cubes.Contains(slot) || !textureBound(slot))
            sampled = cubes.Contains(slot) ? "vec4(0.2, 0.2, 0.22, 1.0)" : "vec4(0.0, 0.0, 0.0, 1.0)";
        else
        {
            var sampler = $"ark_gt{slot}";
            sampled = bare switch
            {
                "sample_b" or "sample_b_indexable" => $"texture({sampler}, ({coordinate}).xy, ({FloatSource(ops[4])}).x)",
                "sample_l" or "sample_l_indexable" => $"textureLod({sampler}, ({coordinate}).xy, ({FloatSource(ops[4])}).x)",
                "sample_d" or "sample_d_indexable" => $"textureGrad({sampler}, ({coordinate}).xy, ({FloatSource(ops[4])}).xy, ({FloatSource(ops[5])}).xy)",
                _ => $"texture({sampler}, ({coordinate}).xy)",
            };
            if (srgb(slot)) sampled = $"ark_srgb({sampled})";
        }
        return Store(ops[0], $"floatBitsToUint(({sampled}).{swizzle})");
    }

    private static string Mask(string booleanVector) => $"(uvec4({booleanVector}) * 0xFFFFFFFFu)";

    private static bool HasModifier(string operand) => operand.StartsWith('-') || operand.Contains('|');

    /// <summary>Writes the lanes named by a destination mask, e.g. <c>r0.xz = (expr).xz;</c>.</summary>
    private static string Store(string destination, string bitsExpression)
    {
        if (destination == "null") return string.Empty;
        var dot = destination.LastIndexOf('.');
        var target = dot > 0 && !destination.EndsWith(']') ? destination[..dot] : destination;
        var mask = dot > 0 && !destination.EndsWith(']') ? destination[(dot + 1)..] : "xyzw";
        if (target == "oMask") return string.Empty;
        return $"{Register(target, isDestination: true)}.{mask} = ({bitsExpression}).{mask};";
    }

    private static string FloatSource(string operand)
    {
        var negate = operand.StartsWith('-');
        var core = negate ? operand[1..] : operand;
        var absolute = core.StartsWith('|') && core.EndsWith('|') ? true : false;
        if (absolute) core = core[1..^1];
        var value = $"uintBitsToFloat({Swizzled(core)})";
        if (absolute) value = $"abs({value})";
        if (negate) value = $"(-{value})";
        return value;
    }

    private static string BitsSource(string operand, bool integer = false)
    {
        var negate = operand.StartsWith('-');
        var core = negate ? operand[1..] : operand;
        var absolute = core.StartsWith('|') && core.EndsWith('|');
        if (absolute) core = core[1..^1];
        var value = Swizzled(core);
        if (absolute) value = integer ? $"uvec4(abs(ivec4({value})))" : $"floatBitsToUint(abs(uintBitsToFloat({value})))";
        if (negate) value = integer ? $"uvec4(-ivec4({value}))" : $"floatBitsToUint(-uintBitsToFloat({value}))";
        return value;
    }

    /// <summary>An operand as a 4-lane uvec4, applying its source swizzle.</summary>
    private static string Swizzled(string operand)
    {
        if (operand.StartsWith("l(", StringComparison.Ordinal)) return Literal(operand);
        var bracket = operand.LastIndexOf(']');
        var dot = operand.LastIndexOf('.');
        string register;
        string swizzle;
        if (dot > bracket)
        {
            register = operand[..dot];
            swizzle = operand[(dot + 1)..];
        }
        else
        {
            register = operand;
            swizzle = "xyzw";
        }
        if (swizzle.Length == 1) swizzle = new string(swizzle[0], 4);
        else if (swizzle.Length < 4) swizzle = swizzle.PadRight(4, swizzle[^1]);
        return $"{Register(register, isDestination: false)}.{swizzle}";
    }

    private static string Register(string register, bool isDestination)
    {
        Match match;
        if ((match = SimpleRegisterRegex().Match(register)).Success)
        {
            var kind = match.Groups["k"].Value;
            var index = match.Groups["i"].Value;
            return kind switch
            {
                "r" => $"r{index}",
                "v" => $"ark_in{index}",
                "o" => $"ark_o{index}",
                _ => throw new DxbcTranslationException($"Unsupported register '{register}'."),
            };
        }
        if ((match = ConstantRegisterRegex().Match(register)).Success)
        {
            if (isDestination) throw new DxbcTranslationException("Constant buffers cannot be written.");
            return $"floatBitsToUint(ark_cb{match.Groups["b"].Value}[{Index(match.Groups["x"].Value)}])";
        }
        if ((match = IndexableRegisterRegex().Match(register)).Success)
            return $"x{match.Groups["i"].Value}[{Index(match.Groups["x"].Value)}]";
        if (register == "vCoverage") return "uvec4(0xFFFFFFFFu)";
        throw new DxbcTranslationException($"Unsupported register '{register}'.");
    }

    /// <summary>A constant or relative index such as <c>12</c> or <c>r0.x + 5</c>.</summary>
    private static string Index(string text)
    {
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return text;
        var plus = text.IndexOf('+');
        var register = plus < 0 ? text : text[..plus].Trim();
        var offset = plus < 0 ? "0" : text[(plus + 1)..].Trim();
        var dot = register.LastIndexOf('.');
        var lane = dot > 0 ? register[(dot + 1)..] : "x";
        var name = Register(dot > 0 ? register[..dot] : register, isDestination: false);
        return $"int({name}.{lane[0]}) + {offset}";
    }

    private static string Literal(string operand)
    {
        var values = operand[2..^1].Split(',', StringSplitOptions.TrimEntries);
        var lanes = values.Select(value =>
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return $"{uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)}u";
            // MSVC prints infinities and NaNs as 1.#INF00, -1.#INF00, 1.#QNAN0 and so on.
            if (value.Contains("#INF", StringComparison.OrdinalIgnoreCase))
                return value.StartsWith('-') ? "4286578688u" : "2139095040u";
            if (value.Contains("#QNAN", StringComparison.OrdinalIgnoreCase) || value.Contains("#IND", StringComparison.OrdinalIgnoreCase))
                return "2143289344u";
            if (value.Contains('.') || value.Contains('e') || value.Contains("inf", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("nan", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                return $"{BitConverter.SingleToUInt32Bits(parsed)}u";
            }
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return $"{uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)}u";
            return $"{unchecked((uint)int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture))}u";
        }).ToArray();
        if (lanes.Length == 1) return $"uvec4({lanes[0]})";
        if (lanes.Length != 4) throw new DxbcTranslationException($"Unsupported literal '{operand}'.");
        return $"uvec4({string.Join(", ", lanes)})";
    }

    [GeneratedRegex(@"cb0\[(?<i>\d+)\]\.(?<s>[xyzw]+)")]
    private static partial Regex ConstantUseRegex();

    [GeneratedRegex(@"^dcl_constantbuffer\s+CB(?<b>\d+)\[(?<n>\d+)\]")]
    private static partial Regex ConstantBufferRegex();

    [GeneratedRegex(@"^dcl_temps\s+(?<n>\d+)")]
    private static partial Regex TempsRegex();

    [GeneratedRegex(@"^dcl_indexableTemp\s+x(?<i>\d+)\[(?<n>\d+)\]")]
    private static partial Regex IndexableTempRegex();

    [GeneratedRegex(@"^dcl_resource_(?<k>texture2d|texturecube|texture3d|texture2darray)\s.*\bt(?<t>\d+)\s*$")]
    private static partial Regex ResourceRegex();

    [GeneratedRegex(@"^dcl_output(?:_\w+)?\s+o(?<o>\d+)")]
    private static partial Regex OutputRegex();

    [GeneratedRegex(@"^t(?<t>\d+)(?:\.(?<s>[xyzw]{4}))?$")]
    private static partial Regex ResourceOperandRegex();

    [GeneratedRegex(@"^(?<k>[rvo])(?<i>\d+)$")]
    private static partial Regex SimpleRegisterRegex();

    [GeneratedRegex(@"^cb(?<b>\d+)\[(?<x>[^\]]+)\]$")]
    private static partial Regex ConstantRegisterRegex();

    [GeneratedRegex(@"^x(?<i>\d+)\[(?<x>[^\]]+)\]$")]
    private static partial Regex IndexableRegisterRegex();
}
