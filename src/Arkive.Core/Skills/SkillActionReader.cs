using System.Buffers.Binary;
using System.Text;

namespace Arkive.Core.Skills;

/// <summary>A named value a skill hands to an effect it starts (Color, Size, Lifetime, Alpha, Spawn...).</summary>
public sealed record SkillEffectParameter(string Name, int Type, float Value, float X, float Y, float Z)
{
    public bool IsVector => Type == 3;
}

/// <summary>One event of a skill stage: an animation, an effect, a sound, a camera shake...</summary>
/// <param name="Kind">The notify class without its prefix, e.g. PlayParticleEffect, Anim, AKEvent.</param>
/// <param name="Start">Seconds after the stage starts.</param>
/// <param name="Duration">Seconds the event lasts; 0 for one-shot events.</param>
/// <param name="Asset">The particle system, animation or sound event it uses ("FX_PC_DDK_03.Par_S_DDK_NotosCrash_01").</param>
/// <param name="Socket">The socket or bone an effect follows, when it names one.</param>
public sealed record SkillActionNotify(
    string Kind,
    float Start,
    float Duration,
    string? Name,
    string? Asset,
    string? Socket,
    IReadOnlyList<SkillEffectParameter> Parameters)
{
    public bool IsParticle => Kind is "PlayParticleEffect" or "PlayCameraParticleEffect";

    public bool IsCamera => Kind == "PlayCameraParticleEffect";

    /// <summary>An effect's offset from its socket (or the character) in Unreal units, when the action sets one.</summary>
    public float[]? Offset { get; init; }

    /// <summary>An effect's rotation from its socket as pitch, yaw and roll in degrees, when the action sets one.</summary>
    public float[]? Rotation { get; init; }

    /// <summary>An effect's scale, when the action sets one.</summary>
    public float[]? Scale { get; init; }
}

public sealed record SkillActionStage(string Name, float Duration, float Rate, IReadOnlyList<SkillActionNotify> Notifies);

/// <summary>A skill as the client plays it: its stages in order, each a timeline of notifies.</summary>
/// <param name="Id">The skill's id (EFTable_Skill PrimaryKey).</param>
/// <param name="Name">The developers' name, usually Korean "new name (구) old name".</param>
public sealed record SkillAction(int Id, string Name, IReadOnlyList<SkillActionStage> Stages)
{
    public IEnumerable<SkillActionNotify> Notifies => Stages.SelectMany(stage => stage.Notifies);

    /// <summary>Every particle system the skill starts, as "Package.Object" paths without groups.</summary>
    public IEnumerable<string> ParticleSystems => Notifies.Where(notify => notify.IsParticle && notify.Asset is not null)
        .Select(notify => notify.Asset!).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Reads a class's skill actions (XmlData\Action\CLASS.loa in data3.lpk). The file is the client's own binary
/// serialisation: objects are written as their class name followed by their fields, strings as an int32 length (a
/// negative length means UTF-16) and the characters with a terminating zero. Arkive reads the parts it understands by
/// their position relative to these strings: action id and name, stage name and length, and each notify's start time,
/// length, asset, socket and effect parameters.
/// </summary>
public static class SkillActionReader
{
    private const string ActionClass = "CEFActionObject";
    private const string StageClass = "CEFActionStage";
    private const string NotifyPrefix = "CEFActionNotify_";
    private const string ParticlePrefix = "ParticleSystem'";

    private readonly record struct Token(int Offset, int End, string Value, bool Wide);

    public static IReadOnlyList<SkillAction> Read(ReadOnlySpan<byte> data)
    {
        var tokens = Tokenise(data);
        var actions = new List<SkillAction>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Value != ActionClass) continue;
            var next = NextIndex(tokens, index, ActionClass);
            var action = ReadAction(data, tokens, index, next);
            if (action is not null) actions.Add(action);
            index = next - 1;
        }
        return actions;
    }

    private static SkillAction? ReadAction(ReadOnlySpan<byte> data, List<Token> tokens, int start, int end)
    {
        var head = tokens[start];
        if (head.End + 8 > data.Length) return null;
        var id = BinaryPrimitives.ReadInt32LittleEndian(data[(head.End + 4)..]);
        var name = start + 1 < end && tokens[start + 1].Offset - head.End <= 16 ? tokens[start + 1].Value : string.Empty;
        var stages = new List<SkillActionStage>();
        for (var index = start + 1; index < end; index++)
        {
            if (tokens[index].Value != StageClass) continue;
            var next = NextIndex(tokens, index, StageClass, end);
            stages.Add(ReadStage(data, tokens, index, next));
            index = next - 1;
        }
        return new SkillAction(id, name, stages);
    }

    private static SkillActionStage ReadStage(ReadOnlySpan<byte> data, List<Token> tokens, int start, int end)
    {
        var head = tokens[start];
        string name = string.Empty;
        float duration = 0, rate = 1;
        // The stage's name follows its 48-byte header; its length and play rate follow the name.
        if (start + 1 < end && tokens[start + 1].Offset - head.End is >= 40 and <= 64 &&
            !tokens[start + 1].Value.StartsWith(NotifyPrefix, StringComparison.Ordinal))
        {
            var nameToken = tokens[start + 1];
            name = nameToken.Value;
            duration = Float(data, nameToken.End);
            rate = Float(data, nameToken.End + 4);
        }
        var notifies = new List<SkillActionNotify>();
        for (var index = start + 1; index < end; index++)
        {
            if (!tokens[index].Value.StartsWith(NotifyPrefix, StringComparison.Ordinal)) continue;
            var next = index + 1;
            while (next < end && !tokens[next].Value.StartsWith(NotifyPrefix, StringComparison.Ordinal)) next++;
            notifies.Add(ReadNotify(data, tokens, index, next));
            index = next - 1;
        }
        return new SkillActionStage(name, Finite(duration), Finite(rate, 1), notifies);
    }

    private static SkillActionNotify ReadNotify(ReadOnlySpan<byte> data, List<Token> tokens, int start, int end)
    {
        var head = tokens[start];
        var kind = head.Value[NotifyPrefix.Length..];
        // A notify starts with a 40-byte header: its start time at 16 and its length at 24.
        var startTime = Finite(Float(data, head.End + 16));
        var duration = Finite(Float(data, head.End + 24));
        var name = start + 1 < end && tokens[start + 1].Offset == head.End + 40 ? tokens[start + 1].Value : null;
        string? asset = null, socket = null;
        var transformStart = -1;
        var parameters = new List<SkillEffectParameter>();
        for (var index = start + 1; index < end; index++)
        {
            var token = tokens[index];
            if (token.Value.StartsWith(ParticlePrefix, StringComparison.Ordinal) && token.Value.EndsWith('\''))
            {
                if (asset is not null) continue;
                asset = PackageAndObject(token.Value[ParticlePrefix.Length..^1]);
                // The socket the effect follows is the next plain name, when there is one before its parameters.
                var next = index + 1;
                // A socket's name sits about 60 bytes after the path; an effect without one has its fields there instead.
                if (next < end && tokens[next].Offset - token.End <= 80 && tokens[next].Value != "None" &&
                    IsName(tokens[next].Value) && !IsParameter(data, tokens, next, end))
                {
                    socket = tokens[next].Value;
                    transformStart = tokens[next].End;
                }
                else transformStart = token.End;
            }
            else if (IsParameter(data, tokens, index, end))
            {
                var type = BinaryPrimitives.ReadInt32LittleEndian(data[token.End..]);
                parameters.Add(new SkillEffectParameter(token.Value, type, Finite(Float(data, token.End + 4)),
                    Finite(Float(data, token.End + 12)), Finite(Float(data, token.End + 16)), Finite(Float(data, token.End + 20))));
            }
            else if (kind == "Anim" && asset is null && token.Value != "Anim" && token.Value != "None" && IsName(token.Value))
            {
                asset = token.Value;
            }
            else if (kind == "AKEvent" && token.Value.StartsWith("AkEvent'", StringComparison.Ordinal) && token.Value.EndsWith('\''))
            {
                asset ??= token.Value[8..^1];
            }
        }
        var notify = new SkillActionNotify(kind, startTime, duration, name, asset, socket, parameters);
        return transformStart > 0 ? WithTransform(data, notify, transformStart) : notify;
    }

    // After an effect's path (and socket) come a few flags, two 1.0 floats, then its offset (3 floats), rotation
    // (pitch, yaw, roll in degrees), nine unused floats and its scale (3). The fields are not always 4-byte aligned, so
    // the pair of 1.0s marks where they start.
    private static SkillActionNotify WithTransform(ReadOnlySpan<byte> data, SkillActionNotify notify, int start)
    {
        ReadOnlySpan<byte> marker = [0, 0, 0x80, 0x3F, 0, 0, 0x80, 0x3F];
        var window = data[start..Math.Min(data.Length, start + 120)];
        var at = window.IndexOf(marker);
        if (at < 0 || start + at + 8 + 72 > data.Length) return notify;
        var fields = start + at + 8;
        var offset = Vector(data, fields);
        var rotation = Vector(data, fields + 12);
        var scale = Vector(data, fields + 60);
        static bool Sane(float value) => value == 0 || Math.Abs(value) is > 1e-4f and < 1e5f;
        if (!offset.Concat(rotation).All(Sane) || !scale.All(value => value is > 1e-3f and < 1e3f)) return notify;
        return notify with { Offset = offset, Rotation = rotation, Scale = scale };
    }

    private static float[] Vector(ReadOnlySpan<byte> data, int offset) =>
        [Finite(Float(data, offset)), Finite(Float(data, offset + 4)), Finite(Float(data, offset + 8))];

    // A parameter is a name, its 56-byte value (type, float, vector) and "None".
    private static bool IsParameter(ReadOnlySpan<byte> data, List<Token> tokens, int index, int end)
    {
        var token = tokens[index];
        if (index + 1 >= end || tokens[index + 1].Value != "None" || tokens[index + 1].Offset != token.End + 56) return false;
        var type = BinaryPrimitives.ReadInt32LittleEndian(data[token.End..]);
        return type is >= 0 and <= 8 && token.Value != "None" && IsName(token.Value);
    }

    // "FX_Post.FX_Par.Par_J_RGBNoise_01" -> "FX_Post.Par_J_RGBNoise_01": the index names objects by package and name.
    private static string PackageAndObject(string path)
    {
        var parts = path.Split('.');
        return parts.Length <= 2 ? path : $"{parts[0]}.{parts[^1]}";
    }

    private static bool IsName(string value) =>
        value.Length is > 0 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static int NextIndex(List<Token> tokens, int index, string value, int end = int.MaxValue)
    {
        var next = index + 1;
        while (next < tokens.Count && next < end && tokens[next].Value != value) next++;
        return Math.Min(next, Math.Min(end, tokens.Count));
    }

    private static float Float(ReadOnlySpan<byte> data, int offset) =>
        offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadSingleLittleEndian(data[offset..]) : 0;

    private static float Finite(float value, float fallback = 0) => float.IsFinite(value) && Math.Abs(value) < 1e7 ? value : fallback;

    /// <summary>Every string of a file in the client's serialisation, in order (look files, actions...).</summary>
    public static IReadOnlyList<string> Strings(ReadOnlySpan<byte> data) => Tokenise(data).Select(token => token.Value).ToArray();

    // Every length-prefixed string in the file, scanning byte by byte so fields between them are skipped.
    private static List<Token> Tokenise(ReadOnlySpan<byte> data)
    {
        var tokens = new List<Token>(data.Length / 40);
        var position = 0;
        while (position + 6 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
            if (length is >= 2 and <= 512 && position + 4 + length <= data.Length && data[position + 3 + length] == 0 &&
                IsPrintable(data.Slice(position + 4, length - 1)))
            {
                var end = position + 4 + length;
                tokens.Add(new Token(position, end, Encoding.ASCII.GetString(data.Slice(position + 4, length - 1)), false));
                position = end;
                continue;
            }
            if (length is <= -2 and >= -512 && position + 4 - 2L * length <= data.Length)
            {
                var characters = data.Slice(position + 4, -2 * length);
                if (characters[^1] == 0 && characters[^2] == 0 && IsWide(characters[..^2]))
                {
                    var end = position + 4 - 2 * length;
                    tokens.Add(new Token(position, end, Encoding.Unicode.GetString(characters[..^2]), true));
                    position = end;
                    continue;
                }
            }
            position++;
        }
        return tokens;
    }

    private static bool IsPrintable(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
            if (character is < 32 or > 126) return false;
        return true;
    }

    private static bool IsWide(ReadOnlySpan<byte> value)
    {
        for (var index = 0; index + 1 < value.Length; index += 2)
        {
            var character = (char)(value[index] | value[index + 1] << 8);
            if (character < 32 || char.IsSurrogate(character) || character is '￾' or '￿') return false;
        }
        return true;
    }
}
