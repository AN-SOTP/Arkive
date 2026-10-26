namespace Arkive.Core.Interface.Abc;

/// <summary>
/// An ActionScript 3 bytecode file (the payload of a DoABC tag), read into lists that mirror the AVM2 format one to
/// one so it writes back byte for byte. Method bodies keep their code as bytes; the hook injector only adds methods
/// and traits and renames, so it never needs to rewrite existing instructions.
/// </summary>
public sealed class AbcFile
{
    public ushort MinorVersion { get; set; }
    public ushort MajorVersion { get; set; }
    public AbcConstantPool Pool { get; } = new();
    public List<AbcMethod> Methods { get; } = [];
    public List<AbcMetadata> Metadata { get; } = [];
    public List<AbcInstance> Instances { get; } = [];
    public List<AbcClass> Classes { get; } = [];
    public List<AbcScript> Scripts { get; } = [];
    public List<AbcMethodBody> Bodies { get; } = [];

    public static AbcFile Read(ReadOnlySpan<byte> data) => new AbcReader(data.ToArray()).ReadFile();

    public byte[] Write()
    {
        var writer = new AbcWriter();
        writer.WriteFile(this);
        return writer.ToArray();
    }

    /// <summary>The body of a method, or null for a native or interface method.</summary>
    public AbcMethodBody? BodyOf(int method) => Bodies.FirstOrDefault(body => body.Method == method);

    /// <summary>The class whose instance name is this qualified name ("com.example.Foo" or "Foo"), or -1.</summary>
    public int FindClass(string qualifiedName)
    {
        for (var index = 0; index < Instances.Count; index++)
            if (Pool.QualifiedName(Instances[index].Name) == qualifiedName) return index;
        return -1;
    }
}

public sealed class AbcConstantPool
{
    /// <summary>Signed integers, index 0 unused (kept as a placeholder so indices match the file).</summary>
    public List<int> Integers { get; } = [0];
    public List<uint> UnsignedIntegers { get; } = [0];
    public List<ulong> Doubles { get; } = [0];
    public List<string> Strings { get; } = [""];
    public List<AbcNamespace> Namespaces { get; } = [new(0, 0)];
    public List<int[]> NamespaceSets { get; } = [[]];
    public List<AbcMultiname> Multinames { get; } = [new(0)];

    /// <summary>The flags that say whether each list was empty in the file (count 0) rather than holding only the placeholder.</summary>
    internal bool[] EmptyCounts { get; } = new bool[7];

    public int AddString(string value)
    {
        var index = Strings.IndexOf(value, 1);
        if (index > 0) return index;
        Strings.Add(value);
        EmptyCounts[3] = false;
        return Strings.Count - 1;
    }

    public int AddNamespace(byte kind, string name)
    {
        var nameIndex = AddString(name);
        for (var index = 1; index < Namespaces.Count; index++)
            if (Namespaces[index].Kind == kind && Namespaces[index].Name == nameIndex) return index;
        Namespaces.Add(new AbcNamespace(kind, nameIndex));
        EmptyCounts[4] = false;
        return Namespaces.Count - 1;
    }

    public int AddQName(int namespaceIndex, string name)
    {
        var nameIndex = AddString(name);
        for (var index = 1; index < Multinames.Count; index++)
        {
            var existing = Multinames[index];
            if (existing.Kind == AbcMultiname.QName && existing.Namespace == namespaceIndex && existing.Name == nameIndex) return index;
        }
        Multinames.Add(new AbcMultiname(AbcMultiname.QName) { Namespace = namespaceIndex, Name = nameIndex });
        EmptyCounts[6] = false;
        return Multinames.Count - 1;
    }

    /// <summary>A run-time name (MultinameL) over a set holding just this namespace, as `array[index]` reads use.</summary>
    public int AddMultinameL(int namespaceIndex)
    {
        var set = NamespaceSets.FindIndex(1, item => item.Length == 1 && item[0] == namespaceIndex);
        if (set < 0)
        {
            NamespaceSets.Add([namespaceIndex]);
            EmptyCounts[5] = false;
            set = NamespaceSets.Count - 1;
        }
        for (var index = 1; index < Multinames.Count; index++)
            if (Multinames[index].Kind == AbcMultiname.MultinameL && Multinames[index].NamespaceSet == set) return index;
        return AddMultiname(new AbcMultiname(AbcMultiname.MultinameL) { NamespaceSet = set });
    }

    public int AddMultiname(AbcMultiname multiname)
    {
        Multinames.Add(multiname);
        EmptyCounts[6] = false;
        return Multinames.Count - 1;
    }

    public string? NameOf(int multiname) =>
        multiname > 0 && multiname < Multinames.Count && Multinames[multiname].Name is var name && name > 0 ? Strings[name] : null;

    public string? NamespaceNameOf(int multiname) =>
        multiname > 0 && multiname < Multinames.Count && Multinames[multiname].Namespace is var ns && ns > 0 && Namespaces[ns].Name > 0
            ? Strings[Namespaces[ns].Name]
            : null;

    /// <summary>"package.Name" for a QName, the bare name otherwise.</summary>
    public string? QualifiedName(int multiname)
    {
        var name = NameOf(multiname);
        if (name is null) return null;
        var ns = NamespaceNameOf(multiname);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}

public sealed record AbcNamespace(byte Kind, int Name)
{
    public const byte Namespace = 0x08;
    public const byte PackageNamespace = 0x16;
    public const byte PackageInternalNs = 0x17;
    public const byte ProtectedNamespace = 0x18;
    public const byte ExplicitNamespace = 0x19;
    public const byte StaticProtectedNs = 0x1A;
    public const byte PrivateNs = 0x05;
}

/// <summary>A multiname; which fields mean something depends on <see cref="Kind"/>.</summary>
public sealed class AbcMultiname(byte kind)
{
    public const byte QName = 0x07, QNameA = 0x0D, RTQName = 0x0F, RTQNameA = 0x10, RTQNameL = 0x11, RTQNameLA = 0x12;
    public const byte Multiname = 0x09, MultinameA = 0x0E, MultinameL = 0x1B, MultinameLA = 0x1C, TypeName = 0x1D;

    public byte Kind { get; } = kind;
    public int Namespace { get; set; }
    public int Name { get; set; }
    public int NamespaceSet { get; set; }
    public int TypeDefinition { get; set; }
    public int[] TypeParameters { get; set; } = [];
}

public sealed class AbcMethod
{
    public const byte NeedArguments = 0x01, NeedActivation = 0x02, NeedRest = 0x04, HasOptional = 0x08, SetDxns = 0x40, HasParamNames = 0x80;

    public int ReturnType { get; set; }
    public List<int> ParameterTypes { get; } = [];
    public int Name { get; set; }
    public byte Flags { get; set; }
    public List<(int Value, byte Kind)> Optional { get; } = [];
    public List<int> ParameterNames { get; } = [];
}

public sealed class AbcMetadata
{
    public int Name { get; set; }
    public List<(int Key, int Value)> Items { get; } = [];
}

public sealed class AbcTrait
{
    public const byte Slot = 0, Method = 1, Getter = 2, Setter = 3, Class = 4, Function = 5, Const = 6;
    public const byte AttributeFinal = 0x1, AttributeOverride = 0x2, AttributeMetadata = 0x4;

    public int Name { get; set; }
    public byte KindByte { get; set; }
    public byte Kind => (byte)(KindByte & 0x0F);
    public byte Attributes => (byte)(KindByte >> 4);

    /// <summary>Slot id, or dispatch id for methods, getters and setters.</summary>
    public int Id { get; set; }

    /// <summary>Type name (slot/const), class index (class), or method index (method/getter/setter/function).</summary>
    public int Index { get; set; }

    public int ValueIndex { get; set; }
    public byte ValueKind { get; set; }
    public List<int> Metadata { get; } = [];
}

public sealed class AbcInstance
{
    public const byte Sealed = 0x01, Final = 0x02, Interface = 0x04, ProtectedNs = 0x08;

    public int Name { get; set; }
    public int SuperName { get; set; }
    public byte Flags { get; set; }
    public int ProtectedNamespace { get; set; }
    public List<int> Interfaces { get; } = [];
    public int Initializer { get; set; }
    public List<AbcTrait> Traits { get; } = [];
}

public sealed class AbcClass
{
    public int Initializer { get; set; }
    public List<AbcTrait> Traits { get; } = [];
}

public sealed class AbcScript
{
    public int Initializer { get; set; }
    public List<AbcTrait> Traits { get; } = [];
}

public sealed class AbcMethodBody
{
    public int Method { get; set; }
    public int MaxStack { get; set; }
    public int LocalCount { get; set; }
    public int InitScopeDepth { get; set; }
    public int MaxScopeDepth { get; set; }
    public byte[] Code { get; set; } = [];
    public List<AbcException> Exceptions { get; } = [];
    public List<AbcTrait> Traits { get; } = [];
}

public sealed record AbcException(int From, int To, int Target, int ExceptionType, int VariableName);
