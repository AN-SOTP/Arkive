using System.Globalization;
using System.Text;

namespace Arkive.Core.Viewer;

/// <summary>
/// A small, side-effect-free expression language for material colour recipes.
/// One parsed expression is both evaluated on the CPU (texture baking, tests) and
/// emitted as GLSL for the GPU preview, so the two can never disagree.
/// </summary>
public abstract record RecipeNode;

public sealed record RecipeNumber(float Value) : RecipeNode;
public sealed record RecipeString(string Value) : RecipeNode;
public sealed record RecipeCall(string Name, IReadOnlyList<RecipeNode> Arguments) : RecipeNode;
public sealed record RecipeSwizzle(RecipeNode Target, string Components) : RecipeNode;
public sealed record RecipeBinary(char Operator, RecipeNode Left, RecipeNode Right) : RecipeNode;
public sealed record RecipeNegate(RecipeNode Operand) : RecipeNode;

public sealed class RecipeSyntaxException(string message) : FormatException(message);

public static class RecipeParser
{
    /// <summary>
    /// Expands <c>$name</c> references using <paramref name="defines"/> (which may refer to
    /// earlier defines), then parses the expression.
    /// </summary>
    public static RecipeNode Parse(string text, IReadOnlyDictionary<string, string>? defines)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(Expand(text, defines, 0));
    }

    private static string Expand(string text, IReadOnlyDictionary<string, string>? defines, int depth)
    {
        if (defines is null || !text.Contains('$')) return text;
        if (depth > 16) throw new RecipeSyntaxException("Recipe defines refer to each other in a loop.");
        return System.Text.RegularExpressions.Regex.Replace(text, @"\$([A-Za-z_][A-Za-z0-9_]*)", match =>
            defines.TryGetValue(match.Groups[1].Value, out var body)
                ? "(" + Expand(body, defines, depth + 1) + ")"
                : throw new RecipeSyntaxException($"Unknown recipe define ${match.Groups[1].Value}."));
    }

    public static RecipeNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        var node = parser.ParseExpression();
        parser.ExpectEnd();
        return node;
    }

    private sealed class Parser(string text)
    {
        private int _position;

        public void ExpectEnd()
        {
            SkipSpace();
            if (_position != text.Length) throw Error($"Unexpected '{text[_position]}'");
        }

        public RecipeNode ParseExpression() => ParseAdditive();

        private RecipeNode ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (TryOperator('+', '-') is { } op) left = new RecipeBinary(op, left, ParseMultiplicative());
            return left;
        }

        private RecipeNode ParseMultiplicative()
        {
            var left = ParseUnary();
            while (TryOperator('*', '/') is { } op) left = new RecipeBinary(op, left, ParseUnary());
            return left;
        }

        private RecipeNode ParseUnary()
        {
            SkipSpace();
            if (Peek() == '-')
            {
                _position++;
                return new RecipeNegate(ParseUnary());
            }
            return ParsePostfix(ParsePrimary());
        }

        private RecipeNode ParsePostfix(RecipeNode node)
        {
            while (true)
            {
                SkipSpace();
                if (Peek() != '.') return node;
                _position++;
                var components = ReadIdentifier();
                if (components.Length is < 1 or > 4 ||
                    !(components.All(c => "xyzw".Contains(c)) || components.All(c => "rgba".Contains(c))))
                    throw Error($"Invalid swizzle '.{components}'");
                node = new RecipeSwizzle(node, components.Replace('r', 'x').Replace('g', 'y').Replace('b', 'z').Replace('a', 'w'));
            }
        }

        private RecipeNode ParsePrimary()
        {
            SkipSpace();
            var c = Peek();
            if (c == '(')
            {
                _position++;
                var inner = ParseExpression();
                Expect(')');
                return inner;
            }
            if (c == '\'')
            {
                _position++;
                var start = _position;
                while (_position < text.Length && text[_position] != '\'') _position++;
                if (_position >= text.Length) throw Error("Unterminated string");
                var value = text[start.._position];
                _position++;
                return new RecipeString(value);
            }
            if (char.IsDigit(c) || c == '.')
            {
                var start = _position;
                while (_position < text.Length && (char.IsDigit(text[_position]) || text[_position] is '.' or 'e' or 'E' ||
                       (text[_position] is '+' or '-' && text[_position - 1] is 'e' or 'E'))) _position++;
                if (!float.TryParse(text[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw Error($"Invalid number '{text[start.._position]}'");
                return new RecipeNumber(number);
            }
            if (char.IsLetter(c) || c == '_')
            {
                var name = ReadIdentifier();
                Expect('(');
                var arguments = new List<RecipeNode>();
                SkipSpace();
                if (Peek() != ')')
                {
                    do arguments.Add(ParseExpression());
                    while (TryChar(','));
                }
                Expect(')');
                return new RecipeCall(name.ToLowerInvariant(), arguments);
            }
            throw Error(_position >= text.Length ? "Unexpected end of expression" : $"Unexpected '{c}'");
        }

        private string ReadIdentifier()
        {
            SkipSpace();
            var start = _position;
            while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_')) _position++;
            if (start == _position) throw Error("Expected a name");
            return text[start.._position];
        }

        private char? TryOperator(char a, char b)
        {
            SkipSpace();
            var c = Peek();
            if (c != a && c != b) return null;
            _position++;
            return c;
        }

        private bool TryChar(char expected)
        {
            SkipSpace();
            if (Peek() != expected) return false;
            _position++;
            return true;
        }

        private void Expect(char expected)
        {
            if (!TryChar(expected)) throw Error($"Expected '{expected}'");
        }

        private char Peek() => _position < text.Length ? text[_position] : '\0';

        private void SkipSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
        }

        private RecipeSyntaxException Error(string message) =>
            new($"{message} at position {_position} in recipe expression \"{text}\".");
    }
}

/// <summary>Values a recipe reads from one material instance.</summary>
public interface IRecipeBindings
{
    bool HasTexture(string parameter);
    float[]? Vector(string parameter);
    float? Scalar(string parameter);
    bool? Switch(string parameter);
}

/// <summary>One uniform the GLSL needs, with the value already resolved for the material.</summary>
public sealed record RecipeUniform(string Uniform, string Kind, string Parameter, float[] Value);

/// <summary>Compiles recipe expressions for one material, sharing uniforms between outputs.</summary>
public sealed class RecipeGlslCompiler(IRecipeBindings bindings)
{
    private readonly Dictionary<string, RecipeUniform> _uniforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _samplers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<RecipeUniform> Uniforms => _uniforms.Values;

    /// <summary>True when an expression reads the second UV set (needs an extra varying).</summary>
    public bool UsesSecondUv { get; private set; }

    /// <summary>Texture parameter name for each sampler uniform.</summary>
    public IReadOnlyDictionary<string, string> Samplers => _samplers;

    public const string Prelude =
        "vec3 ark_srgb(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }\n" +
        "vec4 ark_srgb(vec4 c) { return vec4(ark_srgb(c.rgb), c.a); }\n" +
        "float ark_srgb(float c) { return ark_srgb(vec3(c)).x; }\n";

    /// <summary>Emits GLSL whose result is converted to <paramref name="width"/> components.</summary>
    public string Compile(RecipeNode node, int width)
    {
        var (code, actual) = Emit(node);
        return Convert(code, actual, width);
    }

    private (string Code, int Width) Emit(RecipeNode node)
    {
        switch (node)
        {
            case RecipeNumber number:
                return (FormatFloat(number.Value), 1);
            case RecipeNegate negate:
            {
                var (code, width) = Emit(negate.Operand);
                return ($"(-{code})", width);
            }
            case RecipeSwizzle swizzle:
            {
                var (code, width) = Emit(swizzle.Target);
                var highest = swizzle.Components.Max(c => "xyzw".IndexOf(c)) + 1;
                if (width == 1)
                    return swizzle.Components.Length == 1 ? (code, 1) : ($"vec{swizzle.Components.Length}({code})", swizzle.Components.Length);
                if (highest > width) throw new RecipeSyntaxException($"Swizzle .{swizzle.Components} reads beyond a {width}-component value.");
                return ($"({code}).{swizzle.Components}", swizzle.Components.Length);
            }
            case RecipeBinary binary:
            {
                var (left, leftWidth) = Emit(binary.Left);
                var (right, rightWidth) = Emit(binary.Right);
                var width = Combine(leftWidth, rightWidth, binary.Operator.ToString());
                return ($"({left} {binary.Operator} {right})", width);
            }
            case RecipeCall call:
                return EmitCall(call);
            case RecipeString text:
                throw new RecipeSyntaxException($"A string ('{text.Value}') can only be a parameter name.");
        }
        throw new RecipeSyntaxException($"Unsupported recipe node {node.GetType().Name}.");
    }

    private (string Code, int Width) EmitCall(RecipeCall call)
    {
        var args = call.Arguments;
        switch (call.Name)
        {
            case "tex":
            {
                var parameter = ResolveTexture(StringArgument(call, 0));
                if (parameter is null) return (DefaultTexture(call), 4);
                if (!_samplers.ContainsKey(parameter))
                    _samplers[parameter] = $"ark_t{_samplers.Count}";
                return ($"texture2D({_samplers[parameter]}, vUv)", 4);
            }
            case "has":
                return (ResolveTexture(StringArgument(call, 0)) is null ? "0.0" : "1.0", 1);
            case "vec":
            {
                var name = StringArgument(call, 0);
                var value = bindings.Vector(name) ?? DefaultVector(call);
                return (AddUniform("vec4", name, value), 4);
            }
            case "scalar":
            {
                var name = StringArgument(call, 0);
                var value = bindings.Scalar(name) ?? (args.Count > 1 ? ConstantValue(args[1]) : 0f);
                return (AddUniform("float", name, [value]), 1);
            }
            case "switch":
            {
                var name = StringArgument(call, 0);
                var value = bindings.Switch(name) ?? (args.Count > 1 && ConstantValue(args[1]) != 0);
                return (value ? "1.0" : "0.0", 1);
            }
            case "vec2" or "vec3" or "vec4":
            {
                var target = call.Name[3] - '0';
                var parts = args.Select(Emit).ToArray();
                if (parts.Length == 1) return (Convert(parts[0].Code, parts[0].Width, target), target);
                if (parts.Sum(part => part.Width) != target)
                    throw new RecipeSyntaxException($"{call.Name}(...) needs exactly {target} components.");
                return ($"{call.Name}({string.Join(", ", parts.Select(part => part.Code))})", target);
            }
            case "lerp" or "mix":
            {
                Arity(call, 3);
                var a = Emit(args[0]);
                var b = Emit(args[1]);
                var t = Emit(args[2]);
                var width = Combine(a.Width, b.Width, call.Name);
                if (t.Width != 1 && t.Width != width) throw new RecipeSyntaxException("lerp's factor must be a scalar or match its inputs.");
                return ($"mix({Convert(a.Code, a.Width, width)}, {Convert(b.Code, b.Width, width)}, {t.Code})", width);
            }
            case "saturate":
            {
                Arity(call, 1);
                var x = Emit(args[0]);
                return ($"clamp({x.Code}, 0.0, 1.0)", x.Width);
            }
            case "clamp":
            {
                Arity(call, 3);
                var x = Emit(args[0]);
                return ($"clamp({x.Code}, {Compile(args[1], 1)}, {Compile(args[2], 1)})", x.Width);
            }
            case "min" or "max" or "pow" or "step":
            {
                Arity(call, 2);
                var a = Emit(args[0]);
                var b = Emit(args[1]);
                var width = Combine(a.Width, b.Width, call.Name);
                return ($"{call.Name}({Convert(a.Code, a.Width, width)}, {Convert(b.Code, b.Width, width)})", width);
            }
            case "abs" or "sqrt" or "fract" or "floor":
            {
                Arity(call, 1);
                var x = Emit(args[0]);
                return ($"{call.Name}({x.Code})", x.Width);
            }
            case "uv":
            {
                Arity(call, 1);
                var set = (int)ConstantValue(args[0]);
                if (set is not 0 and not 1) throw new RecipeSyntaxException("uv() supports UV sets 0 and 1.");
                if (set == 1) UsesSecondUv = true;
                return (set == 0 ? "vUv" : "ark_vUv1", 2);
            }
            case "dot":
            {
                Arity(call, 2);
                var a = Emit(args[0]);
                var b = Emit(args[1]);
                var width = Combine(a.Width, b.Width, "dot");
                return ($"dot({Convert(a.Code, a.Width, width)}, {Convert(b.Code, b.Width, width)})", 1);
            }
            case "lum":
            {
                Arity(call, 1);
                return ($"dot({Compile(args[0], 3)}, vec3(0.2126, 0.7152, 0.0722))", 1);
            }
            case "srgb":
            {
                Arity(call, 1);
                var x = Emit(args[0]);
                if (x.Width == 2) throw new RecipeSyntaxException("srgb() needs 1, 3 or 4 components.");
                return ($"ark_srgb({x.Code})", x.Width);
            }
        }
        throw new RecipeSyntaxException($"Unknown recipe function {call.Name}().");
    }

    private string? ResolveTexture(string alternatives) =>
        alternatives.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(bindings.HasTexture);

    /// <summary><c>tex('name', default)</c>: the value used when the material has no such texture.</summary>
    private static string DefaultTexture(RecipeCall call) =>
        call.Arguments.Count > 1 ? $"vec4({FormatFloat(ConstantValue(call.Arguments[1]))})" : "vec4(1.0)";

    private static float[] DefaultVector(RecipeCall call)
    {
        var values = call.Arguments.Skip(1).Select(ConstantValue).ToArray();
        return values.Length switch
        {
            0 => [0, 0, 0, 1],
            1 => [values[0], values[0], values[0], 1],
            3 => [values[0], values[1], values[2], 1],
            4 => values,
            _ => throw new RecipeSyntaxException("vec() defaults take 1, 3 or 4 numbers."),
        };
    }

    private string AddUniform(string kind, string parameter, float[] value)
    {
        var key = kind + ":" + parameter;
        if (!_uniforms.TryGetValue(key, out var uniform))
        {
            uniform = new RecipeUniform($"ark_{(kind == "vec4" ? "v" : "s")}{_uniforms.Count}", kind, parameter, value);
            _uniforms[key] = uniform;
        }
        return uniform.Uniform;
    }

    internal static float ConstantValue(RecipeNode node) => node switch
    {
        RecipeNumber number => number.Value,
        RecipeNegate { Operand: RecipeNumber number } => -number.Value,
        _ => throw new RecipeSyntaxException("Defaults must be plain numbers."),
    };

    internal static string StringArgument(RecipeCall call, int index) =>
        call.Arguments.Count > index && call.Arguments[index] is RecipeString text
            ? text.Value
            : throw new RecipeSyntaxException($"{call.Name}() needs a quoted parameter name.");

    private static void Arity(RecipeCall call, int count)
    {
        if (call.Arguments.Count != count)
            throw new RecipeSyntaxException($"{call.Name}() takes {count} argument(s).");
    }

    internal static int Combine(int left, int right, string operation)
    {
        if (left == right || right == 1) return left;
        if (left == 1) return right;
        throw new RecipeSyntaxException($"Cannot combine {left} and {right} components with {operation}.");
    }

    private static string Convert(string code, int from, int to)
    {
        if (from == to) return code;
        if (from == 1) return to == 1 ? code : $"vec{to}({code})";
        if (to == 1) return $"({code}).x";
        if (to < from) return $"({code}).{"xyzw"[..to]}";
        return to switch
        {
            3 when from == 2 => $"vec3({code}, 0.0)",
            4 when from == 2 => $"vec4({code}, 0.0, 1.0)",
            4 when from == 3 => $"vec4({code}, 1.0)",
            _ => throw new RecipeSyntaxException($"Cannot convert {from} to {to} components."),
        };
    }

    private static string FormatFloat(float value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
    }
}

/// <summary>Evaluates recipe expressions on the CPU, one texel at a time.</summary>
public sealed class RecipeEvaluator(IRecipeBindings bindings, Func<string, float, float, float[]> sample)
{
    private float _u;
    private float _v;
    private float _u1;
    private float _v1;

    /// <summary>Evaluates at UV set 0 coordinates; UV set 1 defaults to the same point.</summary>
    public float[] Evaluate(RecipeNode node, float u, float v, int width, float? u1 = null, float? v1 = null)
    {
        _u = u;
        _v = v;
        _u1 = u1 ?? u;
        _v1 = v1 ?? v;
        return Resize(Eval(node), width);
    }

    private float[] Eval(RecipeNode node)
    {
        switch (node)
        {
            case RecipeNumber number: return [number.Value];
            case RecipeNegate negate: return Map(Eval(negate.Operand), x => -x);
            case RecipeSwizzle swizzle:
            {
                var value = Eval(swizzle.Target);
                return swizzle.Components.Select(c => value.Length == 1 ? value[0] : value["xyzw".IndexOf(c)]).ToArray();
            }
            case RecipeBinary binary:
            {
                var left = Eval(binary.Left);
                var right = Eval(binary.Right);
                return Zip(left, right, binary.Operator switch
                {
                    '+' => (a, b) => a + b,
                    '-' => (a, b) => a - b,
                    '*' => (a, b) => a * b,
                    _ => (a, b) => b == 0 ? 0 : a / b,
                });
            }
            case RecipeCall call: return EvalCall(call);
        }
        throw new RecipeSyntaxException($"Unsupported recipe node {node.GetType().Name}.");
    }

    private float[] EvalCall(RecipeCall call)
    {
        var args = call.Arguments;
        switch (call.Name)
        {
            case "tex":
            {
                var parameter = RecipeGlslCompiler.StringArgument(call, 0)
                    .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(bindings.HasTexture);
                if (parameter is null)
                    return Enumerable.Repeat(args.Count > 1 ? RecipeGlslCompiler.ConstantValue(args[1]) : 1f, 4).ToArray();
                return sample(parameter, _u, _v);
            }
            case "has":
                return [RecipeGlslCompiler.StringArgument(call, 0).Split('|').Any(name => bindings.HasTexture(name.Trim())) ? 1 : 0];
            case "vec":
            {
                var value = bindings.Vector(RecipeGlslCompiler.StringArgument(call, 0));
                if (value is not null) return value;
                var defaults = args.Skip(1).Select(RecipeGlslCompiler.ConstantValue).ToArray();
                return defaults.Length switch
                {
                    0 => [0, 0, 0, 1],
                    1 => [defaults[0], defaults[0], defaults[0], 1],
                    3 => [defaults[0], defaults[1], defaults[2], 1],
                    _ => defaults,
                };
            }
            case "scalar":
                return [bindings.Scalar(RecipeGlslCompiler.StringArgument(call, 0)) ??
                        (args.Count > 1 ? RecipeGlslCompiler.ConstantValue(args[1]) : 0)];
            case "switch":
                return [(bindings.Switch(RecipeGlslCompiler.StringArgument(call, 0)) ??
                         (args.Count > 1 && RecipeGlslCompiler.ConstantValue(args[1]) != 0)) ? 1 : 0];
            case "vec2" or "vec3" or "vec4":
            {
                var target = call.Name[3] - '0';
                var parts = args.Select(Eval).ToArray();
                return parts.Length == 1 ? Resize(parts[0], target) : parts.SelectMany(part => part).ToArray();
            }
            case "lerp" or "mix":
            {
                var a = Eval(args[0]);
                var b = Eval(args[1]);
                var t = Eval(args[2]);
                var width = Math.Max(a.Length, b.Length);
                return Enumerable.Range(0, width).Select(i =>
                {
                    var x = a[a.Length == 1 ? 0 : i];
                    var y = b[b.Length == 1 ? 0 : i];
                    var f = t[t.Length == 1 ? 0 : i];
                    return x + (y - x) * f;
                }).ToArray();
            }
            case "saturate": return Map(Eval(args[0]), x => Math.Clamp(x, 0, 1));
            case "clamp":
            {
                var low = Eval(args[1])[0];
                var high = Eval(args[2])[0];
                return Map(Eval(args[0]), x => Math.Clamp(x, low, high));
            }
            case "min": return Zip(Eval(args[0]), Eval(args[1]), Math.Min);
            case "max": return Zip(Eval(args[0]), Eval(args[1]), Math.Max);
            case "pow": return Zip(Eval(args[0]), Eval(args[1]), (a, b) => MathF.Pow(Math.Max(a, 0), b));
            case "step": return Zip(Eval(args[0]), Eval(args[1]), (edge, x) => x < edge ? 0 : 1);
            case "abs": return Map(Eval(args[0]), Math.Abs);
            case "sqrt": return Map(Eval(args[0]), x => MathF.Sqrt(Math.Max(x, 0)));
            case "fract": return Map(Eval(args[0]), x => x - MathF.Floor(x));
            case "floor": return Map(Eval(args[0]), MathF.Floor);
            case "uv": return RecipeGlslCompiler.ConstantValue(args[0]) == 1 ? [_u1, _v1] : [_u, _v];
            case "dot":
            {
                var product = Zip(Eval(args[0]), Eval(args[1]), (a, b) => a * b);
                return [product.Sum()];
            }
            case "lum":
            {
                var c = Resize(Eval(args[0]), 3);
                return [c[0] * 0.2126f + c[1] * 0.7152f + c[2] * 0.0722f];
            }
            case "srgb":
            {
                var c = Eval(args[0]);
                return c.Select((x, i) => c.Length == 4 && i == 3 ? x : SrgbToLinear(x)).ToArray();
            }
        }
        throw new RecipeSyntaxException($"Unknown recipe function {call.Name}().");
    }

    public static float SrgbToLinear(float c) => c < 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float[] Map(float[] values, Func<float, float> map) => values.Select(map).ToArray();

    private static float[] Zip(float[] left, float[] right, Func<float, float, float> map)
    {
        var width = RecipeGlslCompiler.Combine(left.Length, right.Length, "operation");
        var result = new float[width];
        for (var i = 0; i < width; i++)
            result[i] = map(left[left.Length == 1 ? 0 : i], right[right.Length == 1 ? 0 : i]);
        return result;
    }

    private static float[] Resize(float[] value, int width)
    {
        if (value.Length == width) return value;
        if (value.Length == 1) return Enumerable.Repeat(value[0], width).ToArray();
        if (width < value.Length) return value[..width];
        var result = new float[width];
        value.CopyTo(result, 0);
        if (width == 4) result[3] = 1;
        return result;
    }
}

public static class RecipeText
{
    /// <summary>Formats a parse tree back into recipe syntax (for diagnostics and reports).</summary>
    public static string Format(RecipeNode node) => node switch
    {
        RecipeNumber number => number.Value.ToString("R", CultureInfo.InvariantCulture),
        RecipeString text => $"'{text.Value}'",
        RecipeNegate negate => "-" + Format(negate.Operand),
        RecipeSwizzle swizzle => Format(swizzle.Target) + "." + swizzle.Components,
        RecipeBinary binary => $"({Format(binary.Left)} {binary.Operator} {Format(binary.Right)})",
        RecipeCall call => new StringBuilder(call.Name).Append('(')
            .AppendJoin(", ", call.Arguments.Select(Format)).Append(')').ToString(),
        _ => node.ToString(),
    };
}
