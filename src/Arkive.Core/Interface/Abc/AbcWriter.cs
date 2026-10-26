using System.Buffers.Binary;
using System.Text;

namespace Arkive.Core.Interface.Abc;

internal sealed class AbcWriter
{
    private readonly MemoryStream _output = new();

    public byte[] ToArray() => _output.ToArray();

    public void WriteFile(AbcFile file)
    {
        U16(file.MinorVersion);
        U16(file.MajorVersion);
        WritePool(file.Pool);
        U30(file.Methods.Count);
        foreach (var method in file.Methods) WriteMethod(method);
        U30(file.Metadata.Count);
        foreach (var metadata in file.Metadata)
        {
            U30(metadata.Name);
            U30(metadata.Items.Count);
            foreach (var item in metadata.Items) U30(item.Key);
            foreach (var item in metadata.Items) U30(item.Value);
        }
        U30(file.Instances.Count);
        foreach (var instance in file.Instances)
        {
            U30(instance.Name);
            U30(instance.SuperName);
            _output.WriteByte(instance.Flags);
            if ((instance.Flags & AbcInstance.ProtectedNs) != 0) U30(instance.ProtectedNamespace);
            U30(instance.Interfaces.Count);
            foreach (var item in instance.Interfaces) U30(item);
            U30(instance.Initializer);
            WriteTraits(instance.Traits);
        }
        foreach (var type in file.Classes)
        {
            U30(type.Initializer);
            WriteTraits(type.Traits);
        }
        U30(file.Scripts.Count);
        foreach (var script in file.Scripts)
        {
            U30(script.Initializer);
            WriteTraits(script.Traits);
        }
        U30(file.Bodies.Count);
        foreach (var body in file.Bodies)
        {
            U30(body.Method);
            U30(body.MaxStack);
            U30(body.LocalCount);
            U30(body.InitScopeDepth);
            U30(body.MaxScopeDepth);
            U30(body.Code.Length);
            _output.Write(body.Code);
            U30(body.Exceptions.Count);
            foreach (var exception in body.Exceptions)
            {
                U30(exception.From);
                U30(exception.To);
                U30(exception.Target);
                U30(exception.ExceptionType);
                U30(exception.VariableName);
            }
            WriteTraits(body.Traits);
        }
    }

    private void WritePool(AbcConstantPool pool)
    {
        Count(pool.Integers.Count, pool.EmptyCounts[0]);
        foreach (var value in pool.Integers.Skip(1)) U32((uint)value);
        Count(pool.UnsignedIntegers.Count, pool.EmptyCounts[1]);
        foreach (var value in pool.UnsignedIntegers.Skip(1)) U32(value);
        Count(pool.Doubles.Count, pool.EmptyCounts[2]);
        Span<byte> buffer = stackalloc byte[8];
        foreach (var value in pool.Doubles.Skip(1))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            _output.Write(buffer);
        }
        Count(pool.Strings.Count, pool.EmptyCounts[3]);
        foreach (var value in pool.Strings.Skip(1))
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            U30(bytes.Length);
            _output.Write(bytes);
        }
        Count(pool.Namespaces.Count, pool.EmptyCounts[4]);
        foreach (var value in pool.Namespaces.Skip(1))
        {
            _output.WriteByte(value.Kind);
            U30(value.Name);
        }
        Count(pool.NamespaceSets.Count, pool.EmptyCounts[5]);
        foreach (var set in pool.NamespaceSets.Skip(1))
        {
            U30(set.Length);
            foreach (var item in set) U30(item);
        }
        Count(pool.Multinames.Count, pool.EmptyCounts[6]);
        foreach (var multiname in pool.Multinames.Skip(1))
        {
            _output.WriteByte(multiname.Kind);
            switch (multiname.Kind)
            {
                case AbcMultiname.QName or AbcMultiname.QNameA:
                    U30(multiname.Namespace);
                    U30(multiname.Name);
                    break;
                case AbcMultiname.RTQName or AbcMultiname.RTQNameA:
                    U30(multiname.Name);
                    break;
                case AbcMultiname.Multiname or AbcMultiname.MultinameA:
                    U30(multiname.Name);
                    U30(multiname.NamespaceSet);
                    break;
                case AbcMultiname.MultinameL or AbcMultiname.MultinameLA:
                    U30(multiname.NamespaceSet);
                    break;
                case AbcMultiname.TypeName:
                    U30(multiname.TypeDefinition);
                    U30(multiname.TypeParameters.Length);
                    foreach (var item in multiname.TypeParameters) U30(item);
                    break;
            }
        }
    }

    private void Count(int count, bool wasEmpty) => U30(count == 1 && wasEmpty ? 0 : count);

    private void WriteMethod(AbcMethod method)
    {
        U30(method.ParameterTypes.Count);
        U30(method.ReturnType);
        foreach (var type in method.ParameterTypes) U30(type);
        U30(method.Name);
        _output.WriteByte(method.Flags);
        if ((method.Flags & AbcMethod.HasOptional) != 0)
        {
            U30(method.Optional.Count);
            foreach (var (value, kind) in method.Optional)
            {
                U30(value);
                _output.WriteByte(kind);
            }
        }
        if ((method.Flags & AbcMethod.HasParamNames) != 0)
            foreach (var name in method.ParameterNames) U30(name);
    }

    private void WriteTraits(List<AbcTrait> traits)
    {
        U30(traits.Count);
        foreach (var trait in traits)
        {
            U30(trait.Name);
            _output.WriteByte(trait.KindByte);
            U30(trait.Id);
            U30(trait.Index);
            if (trait.Kind is AbcTrait.Slot or AbcTrait.Const)
            {
                U30(trait.ValueIndex);
                if (trait.ValueIndex != 0) _output.WriteByte(trait.ValueKind);
            }
            if ((trait.Attributes & AbcTrait.AttributeMetadata) != 0)
            {
                U30(trait.Metadata.Count);
                foreach (var item in trait.Metadata) U30(item);
            }
        }
    }

    private void U16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        _output.Write(buffer);
    }

    private void U30(int value) => U32((uint)value);

    private void U32(uint value)
    {
        do
        {
            var next = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) next |= 0x80;
            _output.WriteByte(next);
        } while (value != 0);
    }
}
