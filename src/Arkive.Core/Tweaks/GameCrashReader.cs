using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Arkive.Core.Tweaks;

/// <summary>What one of the game's crash dumps says: where it died, and which interface code was running.</summary>
/// <param name="Code">The Windows exception code (0xC0000005 is an access violation).</param>
/// <param name="Module">The file the crash address is in ("EFEngine.dll"), or null.</param>
/// <param name="BadAddress">For an access violation, the address the game tried to use.</param>
/// <param name="Clues">Names found through the crashed thread's stack, nearest frame first: ActionScript classes,
/// methods, library symbols and display paths (root1.arkui...), each with the tweak whose code holds it, if any.</param>
/// <param name="Tweaks">Arkive tweak classes whose code the crashed thread's stack points into, nearest first.</param>
/// <param name="Frames">Code addresses on the crashed thread's stack ("EFEngine.dll+0x1138318"), nearest first.</param>
public sealed record GameCrash(
    string DumpPath,
    DateTimeOffset Time,
    uint Code,
    string CodeName,
    string? Module,
    ulong Offset,
    string? Access,
    ulong? BadAddress,
    IReadOnlyList<string> Clues,
    IReadOnlyList<string> Tweaks,
    IReadOnlyList<string> Frames)
{
    public string Where => Module is null ? $"0x{Offset:x}" : $"{Module}+0x{Offset:x}";
}

/// <summary>
/// Reads the minidumps Lost Ark's own crash handler writes to EFGame\Logs ("[x64]Ark-&lt;launch time&gt;.dmp", plain
/// Windows minidumps beside the encrypted .ui logs). Nothing is attached to the game and nothing is decrypted: the
/// dump holds the crashed thread's stack and the memory its pointers lead to, and the strings found there name the
/// interface code that was running (ActionScript names, symbol names, and Arkive's own tweak code).
/// </summary>
public static class GameCrashReader
{
    private const int MaxClues = 40, MaxFrames = 24, StackBytes = 0x4000;

    private static readonly Regex Name = new(@"^[A-Za-z_$][A-Za-z0-9_$.:]{4,199}$", RegexOptions.CultureInvariant);

    /// <summary>The game's crash dumps, newest first, from a game folder (the one holding EFGame) or its Logs folder.</summary>
    public static IReadOnlyList<string> FindDumps(string gameOrLogs)
    {
        var logs = Directory.Exists(Path.Combine(gameOrLogs, "EFGame", "Logs")) ? Path.Combine(gameOrLogs, "EFGame", "Logs") : gameOrLogs;
        if (!Directory.Exists(logs)) return [];
        return new DirectoryInfo(logs).GetFiles("*.dmp").OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName).ToArray();
    }

    /// <summary>Reads a dump; with <paramref name="tweakBlocks"/> (see <see cref="TweakCode.ReadSwfBlocks"/>) it also names tweak code.</summary>
    public static GameCrash Read(string dumpPath, IReadOnlyList<(string Name, byte[] Abc)>? tweakBlocks = null)
    {
        var dump = new Minidump(File.ReadAllBytes(dumpPath));
        var time = DateTimeOffset.FromUnixTimeSeconds(dump.Time);
        if (dump.Exception is not { } exception)
            return new GameCrash(dumpPath, time, 0, "no exception recorded", null, 0, null, null, [], [], []);

        var module = dump.ModuleOf(exception.Address);
        string? access = null;
        ulong? bad = null;
        if (exception.Code == 0xC0000005 && exception.Parameters.Length >= 2)
        {
            access = exception.Parameters[0] switch { 0 => "read", 1 => "write", 8 => "execute", _ => "access" };
            bad = exception.Parameters[1];
        }

        var stack = dump.StackFrom(exception.StackPointer, StackBytes);
        var frames = new List<string>();
        var clues = new List<string>();
        var tweaks = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset + 8 <= stack.Length; offset += 8)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(stack.AsSpan(offset));
            if (frames.Count < MaxFrames && dump.ModuleOf(value) is { } frame) frames.Add($"{frame.Name}+0x{frame.Offset:x}");
            foreach (var pointer in Pointers(dump, value))
            {
                var tweak = TweakAt(dump, pointer, tweakBlocks);
                if (tweak is not null && !tweaks.Contains(tweak)) tweaks.Add(tweak);
                if (clues.Count < MaxClues && StringAt(dump, pointer) is { } text && Name.IsMatch(text) && seen.Add(text))
                    clues.Add(tweak is null ? text : $"{text} ({tweak})");
            }
        }
        return new GameCrash(dumpPath, time, exception.Code, CodeName(exception.Code), module?.Name,
            module?.Offset ?? exception.Address, access, bad, clues, tweaks, frames);
    }

    /// <summary>A few lines for people: when, what, where, and the names found on the stack.</summary>
    public static string Describe(GameCrash crash)
    {
        var text = new StringBuilder();
        text.AppendLine($"{Path.GetFileName(crash.DumpPath)}  ({crash.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss})");
        text.Append($"  {crash.CodeName} (0x{crash.Code:X8}) at {crash.Where}");
        if (crash.BadAddress is { } bad) text.Append($", {crash.Access} of 0x{bad:x}");
        text.AppendLine();
        if (crash.Tweaks.Count > 0) text.AppendLine($"  Arkive tweak code on the stack: {string.Join(", ", crash.Tweaks)}");
        if (crash.Clues.Count > 0) text.AppendLine($"  Names on the stack: {string.Join(", ", crash.Clues)}");
        if (crash.Frames.Count > 0) text.AppendLine($"  Code addresses: {string.Join(" ", crash.Frames.Take(8))}");
        return text.ToString();
    }

    public static string CodeName(uint code) => code switch
    {
        0xC0000005 => "access violation",
        0x80000003 => "breakpoint",
        0xC00000FD => "stack overflow",
        0xC0000409 => "fail fast",
        0xC0000374 => "heap corruption",
        0xE06D7363 => "C++ exception",
        0xC0000094 => "integer divide by zero",
        0xC000001D => "illegal instruction",
        _ => "exception",
    };

    /// <summary>The value itself and the pointers in the 64 bytes it points at (strings often sit one step away).</summary>
    private static IEnumerable<ulong> Pointers(Minidump dump, ulong value)
    {
        if (value < 0x10000) yield break;
        var near = dump.Read(value, 64);
        if (near.Length == 0) yield break;
        yield return value;
        for (var offset = 0; offset + 8 <= near.Length; offset += 8)
        {
            var inner = BinaryPrimitives.ReadUInt64LittleEndian(near.AsSpan(offset));
            if (inner >= 0x10000 && inner != value) yield return inner;
        }
    }

    private static string? StringAt(Minidump dump, ulong pointer)
    {
        var bytes = dump.Read(pointer, 200);
        var ascii = 0;
        while (ascii < bytes.Length && bytes[ascii] is >= 0x20 and < 0x7F) ascii++;
        if (ascii >= 5) return Encoding.ASCII.GetString(bytes, 0, ascii);
        var wide = 0;
        while (wide + 1 < bytes.Length && bytes[wide] is >= 0x20 and < 0x7F && bytes[wide + 1] == 0) wide += 2;
        return wide >= 10 ? Encoding.Unicode.GetString(bytes, 0, wide) : null;
    }

    /// <summary>The tweak class whose loaded bytecode this pointer lands in (24 bytes from it match that class's code).</summary>
    private static string? TweakAt(Minidump dump, ulong pointer, IReadOnlyList<(string Name, byte[] Abc)>? blocks)
    {
        if (blocks is null) return null;
        var bytes = dump.Read(pointer, 24);
        if (bytes.Length < 24 || bytes.All(b => b == 0)) return null;
        foreach (var (name, abc) in blocks)
            if (abc.AsSpan().IndexOf(bytes) >= 0) return name.Split('/', '.').Last();
        return null;
    }

    private sealed record DumpException(uint Code, ulong Address, ulong[] Parameters, ulong StackPointer);

    /// <summary>The parts of the minidump format this reader needs (x64 only, as Lost Ark is).</summary>
    private sealed class Minidump
    {
        private readonly byte[] data;
        private readonly List<(ulong Base, ulong Size, string Name)> modules = [];
        private readonly List<(ulong Start, ulong Size, long Offset)> memory = [];
        private readonly List<(ulong Start, ulong Size)> stacks = [];

        public Minidump(byte[] data)
        {
            this.data = data;
            if (data.Length < 32 || Encoding.ASCII.GetString(data, 0, 4) != "MDMP") throw new InvalidDataException("Not a minidump.");
            Time = U32(20);
            var count = U32(8);
            var directory = U32(12);
            for (var index = 0u; index < count; index++)
            {
                var entry = (int)(directory + index * 12);
                var type = U32(entry);
                var rva = (int)U32(entry + 8);
                switch (type)
                {
                    case 3: // ThreadListStream
                        for (var thread = 0; thread < U32(rva); thread++)
                        {
                            var at = rva + 4 + thread * 48;
                            stacks.Add((U64(at + 24), U32(at + 32)));
                        }
                        break;
                    case 4: // ModuleListStream
                        for (var item = 0; item < U32(rva); item++)
                        {
                            var at = rva + 4 + item * 108;
                            var name = Utf16At((int)U32(at + 20));
                            modules.Add((U64(at), U32(at + 8), Path.GetFileName(name)));
                        }
                        break;
                    case 5: // MemoryListStream
                        for (var range = 0; range < U32(rva); range++)
                        {
                            var at = rva + 4 + range * 16;
                            memory.Add((U64(at), U32(at + 8), U32(at + 12)));
                        }
                        break;
                    case 9: // Memory64ListStream
                        var offset = (long)U64(rva + 8);
                        for (var range = 0UL; range < U64(rva); range++)
                        {
                            var at = rva + 16 + (int)range * 16;
                            memory.Add((U64(at), U64(at + 8), offset));
                            offset += (long)U64(at + 8);
                        }
                        break;
                    case 6: // ExceptionStream: thread id, MINIDUMP_EXCEPTION at +8, context location at +160
                        var parameters = new ulong[Math.Min(U32(rva + 8 + 24), 15u)];
                        for (var item = 0; item < parameters.Length; item++) parameters[item] = U64(rva + 8 + 32 + item * 8);
                        var context = (int)U32(rva + 164);
                        Exception = new DumpException(U32(rva + 8), U64(rva + 8 + 16), parameters, U64(context + 0x98));
                        break;
                }
            }
        }

        public uint Time { get; }
        public DumpException? Exception { get; }

        public (string Name, ulong Offset)? ModuleOf(ulong address)
        {
            foreach (var (start, size, name) in modules)
                if (address >= start && address - start < size) return (name, address - start);
            return null;
        }

        /// <summary>Up to <paramref name="count"/> captured bytes from this address, or none.</summary>
        public byte[] Read(ulong address, int count)
        {
            foreach (var (start, size, offset) in memory)
            {
                if (address < start || address - start >= size) continue;
                var length = (int)Math.Min((ulong)count, size - (address - start));
                var from = offset + (long)(address - start);
                if (from < 0 || from + length > data.Length) return [];
                return data.AsSpan((int)from, length).ToArray();
            }
            return [];
        }

        /// <summary>The crashed thread's stack from its stack pointer up (to the thread's stack end when known).</summary>
        public byte[] StackFrom(ulong pointer, int count)
        {
            foreach (var (start, size) in stacks)
                if (pointer >= start && pointer - start < size) count = (int)Math.Min((ulong)count, size - (pointer - start));
            return Read(pointer, count);
        }

        private uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        private ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at));

        private string Utf16At(int rva) => Encoding.Unicode.GetString(data, rva + 4, (int)U32(rva));
    }
}
