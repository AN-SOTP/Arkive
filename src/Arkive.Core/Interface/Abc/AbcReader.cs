using System.Buffers.Binary;
using System.Text;

namespace Arkive.Core.Interface.Abc;

internal sealed class AbcReader(byte[] data)
{
    private int _position;

    public AbcFile ReadFile()
    {
        var file = new AbcFile { MinorVersion = U16(), MajorVersion = U16() };
        ReadPool(file.Pool);
        var methodCount = U30();
        for (var index = 0; index < methodCount; index++) file.Methods.Add(ReadMethod());
        var metadataCount = U30();
        for (var index = 0; index < metadataCount; index++)
        {
            var metadata = new AbcMetadata { Name = U30() };
            var items = U30();
            var keys = new int[items];
            for (var item = 0; item < items; item++) keys[item] = U30();
            for (var item = 0; item < items; item++) metadata.Items.Add((keys[item], U30()));
            file.Metadata.Add(metadata);
        }
        var classCount = U30();
        for (var index = 0; index < classCount; index++) file.Instances.Add(ReadInstance());
        for (var index = 0; index < classCount; index++)
        {
            var type = new AbcClass { Initializer = U30() };
            ReadTraits(type.Traits);
            file.Classes.Add(type);
        }
        var scriptCount = U30();
        for (var index = 0; index < scriptCount; index++)
        {
            var script = new AbcScript { Initializer = U30() };
            ReadTraits(script.Traits);
            file.Scripts.Add(script);
        }
        var bodyCount = U30();
        for (var index = 0; index < bodyCount; index++) file.Bodies.Add(ReadBody());
        if (_position != data.Length) throw new InvalidDataException($"The ABC file has {data.Length - _position} unread bytes.");
        return file;
    }

    private void ReadPool(AbcConstantPool pool)
    {
        var count = U30();
        pool.EmptyCounts[0] = count == 0;
        for (var index = 1; index < count; index++) pool.Integers.Add((int)U32());
        count = U30();
        pool.EmptyCounts[1] = count == 0;
        for (var index = 1; index < count; index++) pool.UnsignedIntegers.Add(U32());
        count = U30();
        pool.EmptyCounts[2] = count == 0;
        for (var index = 1; index < count; index++)
        {
            pool.Doubles.Add(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_position)));
            _position += 8;
        }
        count = U30();
        pool.EmptyCounts[3] = count == 0;
        for (var index = 1; index < count; index++)
        {
            var length = U30();
            pool.Strings.Add(Encoding.UTF8.GetString(data, _position, length));
            _position += length;
        }
        count = U30();
        pool.EmptyCounts[4] = count == 0;
        for (var index = 1; index < count; index++) pool.Namespaces.Add(new AbcNamespace(data[_position++], U30()));
        count = U30();
        pool.EmptyCounts[5] = count == 0;
        for (var index = 1; index < count; index++)
        {
            var set = new int[U30()];
            for (var item = 0; item < set.Length; item++) set[item] = U30();
            pool.NamespaceSets.Add(set);
        }
        count = U30();
        pool.EmptyCounts[6] = count == 0;
        for (var index = 1; index < count; index++)
        {
            var multiname = new AbcMultiname(data[_position++]);
            switch (multiname.Kind)
            {
                case AbcMultiname.QName or AbcMultiname.QNameA:
                    multiname.Namespace = U30();
                    multiname.Name = U30();
                    break;
                case AbcMultiname.RTQName or AbcMultiname.RTQNameA:
                    multiname.Name = U30();
                    break;
                case AbcMultiname.RTQNameL or AbcMultiname.RTQNameLA:
                    break;
                case AbcMultiname.Multiname or AbcMultiname.MultinameA:
                    multiname.Name = U30();
                    multiname.NamespaceSet = U30();
                    break;
                case AbcMultiname.MultinameL or AbcMultiname.MultinameLA:
                    multiname.NamespaceSet = U30();
                    break;
                case AbcMultiname.TypeName:
                    multiname.TypeDefinition = U30();
                    multiname.TypeParameters = new int[U30()];
                    for (var item = 0; item < multiname.TypeParameters.Length; item++) multiname.TypeParameters[item] = U30();
                    break;
                default:
                    throw new InvalidDataException($"Unknown multiname kind 0x{multiname.Kind:X2}.");
            }
            pool.Multinames.Add(multiname);
        }
    }

    private AbcMethod ReadMethod()
    {
        var parameterCount = U30();
        var method = new AbcMethod { ReturnType = U30() };
        for (var index = 0; index < parameterCount; index++) method.ParameterTypes.Add(U30());
        method.Name = U30();
        method.Flags = data[_position++];
        if ((method.Flags & AbcMethod.HasOptional) != 0)
        {
            var count = U30();
            for (var index = 0; index < count; index++) method.Optional.Add((U30(), data[_position++]));
        }
        if ((method.Flags & AbcMethod.HasParamNames) != 0)
            for (var index = 0; index < parameterCount; index++) method.ParameterNames.Add(U30());
        return method;
    }

    private AbcInstance ReadInstance()
    {
        var instance = new AbcInstance { Name = U30(), SuperName = U30(), Flags = data[_position++] };
        if ((instance.Flags & AbcInstance.ProtectedNs) != 0) instance.ProtectedNamespace = U30();
        var interfaces = U30();
        for (var index = 0; index < interfaces; index++) instance.Interfaces.Add(U30());
        instance.Initializer = U30();
        ReadTraits(instance.Traits);
        return instance;
    }

    private void ReadTraits(List<AbcTrait> traits)
    {
        var count = U30();
        for (var index = 0; index < count; index++)
        {
            var trait = new AbcTrait { Name = U30(), KindByte = data[_position++] };
            trait.Id = U30();
            trait.Index = U30();
            if (trait.Kind is AbcTrait.Slot or AbcTrait.Const)
            {
                trait.ValueIndex = U30();
                if (trait.ValueIndex != 0) trait.ValueKind = data[_position++];
            }
            if ((trait.Attributes & AbcTrait.AttributeMetadata) != 0)
            {
                var metadata = U30();
                for (var item = 0; item < metadata; item++) trait.Metadata.Add(U30());
            }
            traits.Add(trait);
        }
    }

    private AbcMethodBody ReadBody()
    {
        var body = new AbcMethodBody
        {
            Method = U30(), MaxStack = U30(), LocalCount = U30(), InitScopeDepth = U30(), MaxScopeDepth = U30(),
        };
        var length = U30();
        body.Code = data.AsSpan(_position, length).ToArray();
        _position += length;
        var exceptions = U30();
        for (var index = 0; index < exceptions; index++)
            body.Exceptions.Add(new AbcException(U30(), U30(), U30(), U30(), U30()));
        ReadTraits(body.Traits);
        return body;
    }

    private ushort U16()
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_position));
        _position += 2;
        return value;
    }

    private int U30() => (int)U32();

    private uint U32()
    {
        uint result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var value = data[_position++];
            result |= (uint)(value & 0x7F) << shift;
            if ((value & 0x80) == 0) break;
        }
        return result;
    }
}
