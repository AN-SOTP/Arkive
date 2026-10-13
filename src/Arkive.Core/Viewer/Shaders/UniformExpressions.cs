using System.Buffers.Binary;

namespace Arkive.Core.Viewer.Shaders;

/// <summary>A UE3 material uniform expression (FMaterialUniformExpression*) as stored in a shader map.</summary>
public abstract record UniformExpression;

public sealed record UniformConstant(float[] Value) : UniformExpression;
public sealed record UniformVectorParameter(string Name, float[] Default) : UniformExpression;
public sealed record UniformScalarParameter(string Name, float Default) : UniformExpression;
public sealed record UniformTime(bool Real) : UniformExpression;
public sealed record UniformAppend(UniformExpression A, UniformExpression B, int ComponentsA) : UniformExpression;
public sealed record UniformMath(int Operation, UniformExpression A, UniformExpression B) : UniformExpression;
public sealed record UniformUnary(string Function, UniformExpression X) : UniformExpression;
public sealed record UniformClamp(UniformExpression X, UniformExpression Minimum, UniformExpression Maximum) : UniformExpression;
public sealed record UniformTexture(string? Parameter, int TextureIndex) : UniformExpression;

/// <summary>The pixel shader's uniform expression lists, in register order.</summary>
public sealed record ShaderMaterialExpressions(
    IReadOnlyList<UniformExpression> Vectors,
    IReadOnlyList<UniformExpression> Scalars,
    IReadOnlyList<UniformExpression> Textures);

public sealed class UniformExpressionFormatException(string message) : FormatException(message);

/// <summary>Reads FUniformExpressionSet lists (Lost Ark UE3 layout).</summary>
public sealed class UniformExpressionReader(Stream stream, IReadOnlyList<string> names)
{
    private const int MaximumDepth = 64;
    private int _depth;

    public IReadOnlyList<UniformExpression> ReadList()
    {
        var count = ReadInt32();
        if (count is < 0 or > 1024) throw new UniformExpressionFormatException($"Invalid uniform expression count {count}.");
        var result = new UniformExpression[count];
        for (var index = 0; index < count; index++) result[index] = ReadExpression();
        return result;
    }

    private UniformExpression ReadExpression()
    {
        if (++_depth > MaximumDepth) throw new UniformExpressionFormatException("Uniform expressions nest too deeply.");
        try
        {
            var kind = ReadName();
            const string prefix = "fmaterialuniformexpression";
            if (!kind.StartsWith(prefix, StringComparison.Ordinal))
                throw new UniformExpressionFormatException($"Unexpected uniform expression class '{kind}'.");
            switch (kind[prefix.Length..])
            {
                case "constant":
                {
                    var value = ReadColor();
                    ReadByte();
                    return new UniformConstant(value);
                }
                case "vectorparameter": return new UniformVectorParameter(ReadName(), ReadColor());
                case "scalarparameter": return new UniformScalarParameter(ReadName(), ReadSingle());
                case "time": return new UniformTime(false);
                case "realtime": return new UniformTime(true);
                case "appendvector":
                {
                    var a = ReadExpression();
                    var b = ReadExpression();
                    return new UniformAppend(a, b, ReadInt32());
                }
                case "foldedmath":
                {
                    var a = ReadExpression();
                    var b = ReadExpression();
                    return new UniformMath(ReadByte(), a, b);
                }
                case "sine":
                {
                    var x = ReadExpression();
                    return new UniformUnary(ReadInt32() != 0 ? "cos" : "sin", x);
                }
                case "clamp": return new UniformClamp(ReadExpression(), ReadExpression(), ReadExpression());
                case "max": return new UniformMath(10, ReadExpression(), ReadExpression());
                case "min": return new UniformMath(11, ReadExpression(), ReadExpression());
                case "periodic": return new UniformUnary("periodic", ReadExpression());
                case "abs": return new UniformUnary("abs", ReadExpression());
                case "floor": return new UniformUnary("floor", ReadExpression());
                case "ceil": return new UniformUnary("ceil", ReadExpression());
                case "frac": return new UniformUnary("frac", ReadExpression());
                case "squareroot": return new UniformUnary("sqrt", ReadExpression());
                case "length": return new UniformUnary("length", ReadExpression());
                case "texture": return new UniformTexture(null, ReadInt32());
                case "textureparameter":
                case "flipbooktextureparameter":
                {
                    var name = ReadName();
                    return new UniformTexture(name, ReadInt32());
                }
                default:
                    throw new UniformExpressionFormatException($"Unsupported uniform expression '{kind}'.");
            }
        }
        finally
        {
            _depth--;
        }
    }

    private string ReadName()
    {
        var index = ReadInt32();
        var number = ReadInt32();
        if (index < 0 || index >= names.Count) throw new UniformExpressionFormatException($"Invalid name index {index}.");
        return number == 0 ? names[index] : $"{names[index]}_{number - 1}";
    }

    private float[] ReadColor() => [ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle()];

    private byte ReadByte()
    {
        var value = stream.ReadByte();
        if (value < 0) throw new EndOfStreamException();
        return (byte)value;
    }

    private int ReadInt32()
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());
}

/// <summary>Evaluates uniform expressions with a material's parameter values (time is frozen at zero).</summary>
public static class UniformExpressionEvaluator
{
    public static float[] Evaluate(
        UniformExpression expression,
        Func<string, float[]?> vector,
        Func<string, float?> scalar,
        float time = 0)
    {
        switch (expression)
        {
            case UniformConstant constant: return (float[])constant.Value.Clone();
            case UniformVectorParameter parameter: return (float[])(vector(parameter.Name) ?? parameter.Default).Clone();
            case UniformScalarParameter parameter:
            {
                var value = scalar(parameter.Name) ?? parameter.Default;
                return [value, value, value, value];
            }
            case UniformTime: return [time, time, time, time];
            case UniformAppend append:
            {
                var a = Evaluate(append.A, vector, scalar, time);
                var b = Evaluate(append.B, vector, scalar, time);
                var result = new float[4];
                var n = Math.Clamp(append.ComponentsA, 0, 4);
                for (var i = 0; i < n; i++) result[i] = a[i];
                for (var i = n; i < 4; i++) result[i] = b[i - n];
                return result;
            }
            case UniformMath math:
            {
                var a = Evaluate(math.A, vector, scalar, time);
                var b = Evaluate(math.B, vector, scalar, time);
                var result = new float[4];
                for (var i = 0; i < 4; i++)
                    result[i] = math.Operation switch
                    {
                        0 => a[i] + b[i],
                        1 => a[i] - b[i],
                        2 => a[i] * b[i],
                        3 => b[i] == 0 ? 0 : a[i] / b[i],
                        4 => a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3],
                        5 => a[i] * b[i],
                        10 => Math.Max(a[i], b[i]),
                        11 => Math.Min(a[i], b[i]),
                        _ => a[i],
                    };
                return result;
            }
            case UniformClamp clamp:
            {
                var x = Evaluate(clamp.X, vector, scalar, time);
                var low = Evaluate(clamp.Minimum, vector, scalar, time);
                var high = Evaluate(clamp.Maximum, vector, scalar, time);
                return Enumerable.Range(0, 4).Select(i => Math.Min(Math.Max(x[i], low[i]), high[i])).ToArray();
            }
            case UniformUnary unary:
            {
                var x = Evaluate(unary.X, vector, scalar, time);
                Func<float, float> f = unary.Function switch
                {
                    "sin" => MathF.Sin,
                    "cos" => MathF.Cos,
                    "abs" => MathF.Abs,
                    "floor" => MathF.Floor,
                    "ceil" => MathF.Ceiling,
                    "frac" or "periodic" => v => v - MathF.Floor(v),
                    "sqrt" => v => MathF.Sqrt(Math.Max(0, v)),
                    _ => v => v,
                };
                if (unary.Function == "length")
                {
                    var length = MathF.Sqrt(x.Sum(v => v * v));
                    return [length, length, length, length];
                }
                return x.Select(f).ToArray();
            }
            default:
                return [0, 0, 0, 0];
        }
    }

    public static IEnumerable<string> ParameterNames(UniformExpression expression) => expression switch
    {
        UniformVectorParameter vector => [vector.Name],
        UniformScalarParameter scalar => [scalar.Name],
        UniformTexture { Parameter: { } texture } => [texture],
        UniformAppend append => ParameterNames(append.A).Concat(ParameterNames(append.B)),
        UniformMath math => ParameterNames(math.A).Concat(ParameterNames(math.B)),
        UniformClamp clamp => ParameterNames(clamp.X).Concat(ParameterNames(clamp.Minimum)).Concat(ParameterNames(clamp.Maximum)),
        UniformUnary unary => ParameterNames(unary.X),
        _ => [],
    };
}
