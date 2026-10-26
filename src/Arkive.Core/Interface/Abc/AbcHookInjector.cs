namespace Arkive.Core.Interface.Abc;

/// <summary>A method to route through the tweak dispatcher: the class's qualified name, the method, and the id tweaks register under.</summary>
public sealed record AbcHook(string ClassName, string MethodName, bool Static = false)
{
    public string Id => ClassName + (Static ? "::" : ".") + MethodName;
}

/// <summary>
/// Routes methods of a movie's own code through Arkive's tweak code. The original method keeps its body under a new
/// name (<see cref="RenamedSuffix"/>); a new method with the original name and signature does
/// <c>args = [arguments]; result = undefined; if (Hooks.before(id, this, args)) result = this.original(args[0], ...);
/// return Hooks.after(id, this, args, result);</c>. The original is called directly, as the game would, and existing
/// instructions are never rewritten, so every caller, override and super call keeps working.
/// </summary>
public static class AbcHookInjector
{
    public const string RenamedSuffix = "$arkive";
    public const string DispatcherPackage = "arkive.tweaks";
    public const string DispatcherClass = "Hooks";
    public const string BeforeMethod = "before";
    public const string AfterMethod = "after";

    /// <summary>Applies the hooks; returns the ids of hooks whose class or method this code doesn't have, or can't take.</summary>
    public static IReadOnlyList<string> Inject(AbcFile abc, IEnumerable<AbcHook> hooks)
    {
        var missing = new List<string>();
        var pool = abc.Pool;
        var publicNs = pool.AddNamespace(AbcNamespace.PackageNamespace, "");
        var dispatcher = pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, DispatcherPackage), DispatcherClass);
        var before = pool.AddQName(publicNs, BeforeMethod);
        var after = pool.AddQName(publicNs, AfterMethod);
        int? element = null;
        foreach (var hook in hooks)
        {
            var classIndex = abc.FindClass(hook.ClassName);
            var traits = classIndex < 0 ? null : hook.Static ? abc.Classes[classIndex].Traits : abc.Instances[classIndex].Traits;
            var trait = traits?.FirstOrDefault(item => item.Kind == AbcTrait.Method && pool.NameOf(item.Name) == hook.MethodName);
            if (traits is null || trait is null || (abc.Methods[trait.Index].Flags & AbcMethod.NeedRest) != 0)
            {
                missing.Add(hook.Id);
                continue;
            }
            var original = pool.Multinames[trait.Name];
            var renamed = pool.AddQName(original.Namespace, hook.MethodName + RenamedSuffix);
            if (traits.Any(item => item.Name == renamed)) continue;
            element ??= ElementName(pool, publicNs);

            var method = abc.Methods[trait.Index];
            var body = abc.BodyOf(trait.Index) ?? throw new InvalidDataException($"{hook.Id} has no body to keep.");
            var wrapper = new AbcMethod
            {
                ReturnType = method.ReturnType,
                Flags = (byte)(method.Flags & AbcMethod.HasOptional),
            };
            wrapper.ParameterTypes.AddRange(method.ParameterTypes);
            wrapper.Optional.AddRange(method.Optional);
            abc.Methods.Add(wrapper);
            var wrapperIndex = abc.Methods.Count - 1;

            var parameters = method.ParameterTypes.Count;
            var returnsVoid = pool.NameOf(method.ReturnType) == "void";
            abc.Bodies.Add(new AbcMethodBody
            {
                Method = wrapperIndex,
                MaxStack = parameters + 6,
                LocalCount = parameters + 3,
                InitScopeDepth = body.InitScopeDepth,
                MaxScopeDepth = body.InitScopeDepth + 1,
                Code = WrapperCode(dispatcher, pool.AddString(hook.Id), parameters, renamed, before, after, element.Value, returnsVoid),
            });

            // The kept original loses "override": nothing above it has the new name. The wrapper takes its place.
            traits.Add(new AbcTrait
            {
                Name = trait.Name,
                KindByte = (byte)(trait.KindByte & ~(AbcTrait.AttributeMetadata << 4)),
                Index = wrapperIndex,
            });
            trait.Name = renamed;
            trait.KindByte = (byte)(trait.KindByte & ~(AbcTrait.AttributeOverride << 4));
            trait.Id = 0;
        }
        return missing;
    }

    /// <summary>A runtime-named property in the public namespace, for reading args[i].</summary>
    private static int ElementName(AbcConstantPool pool, int publicNs)
    {
        pool.NamespaceSets.Add([publicNs]);
        pool.EmptyCounts[5] = false;
        return pool.AddMultiname(new AbcMultiname(AbcMultiname.MultinameL) { NamespaceSet = pool.NamespaceSets.Count - 1 });
    }

    private static byte[] WrapperCode(int dispatcher, int id, int parameters, int renamed, int before, int after, int element, bool returnsVoid)
    {
        var args = parameters + 1;
        var result = parameters + 2;
        var code = new AbcCode();
        code.Op(0xD0).Op(0x30); // getlocal_0; pushscope
        for (var local = 1; local <= parameters; local++) code.GetLocal(local);
        code.Op(0x56).U30(parameters).SetLocal(args); // args = [p1..pn]
        code.Op(0x21).SetLocal(result); // result = undefined
        code.Op(0x60).U30(dispatcher).Op(0x2C).U30(id).Op(0xD0).GetLocal(args);
        code.Op(0x46).U30(before).U30(3); // Hooks.before(id, this, args)
        var skip = code.Branch(0x12); // iffalse -> after
        code.Op(0xD0);
        for (var index = 0; index < parameters; index++)
            code.GetLocal(args).Op(0x24).Op((byte)index).Op(0x66).U30(element); // args[index]
        if (returnsVoid) code.Op(0x4F).U30(renamed).U30(parameters); // callpropvoid original
        else code.Op(0x46).U30(renamed).U30(parameters).SetLocal(result); // result = original(...)
        code.Land(skip);
        code.Op(0x60).U30(dispatcher).Op(0x2C).U30(id).Op(0xD0).GetLocal(args).GetLocal(result);
        code.Op(0x46).U30(after).U30(4); // Hooks.after(id, this, args, result)
        if (returnsVoid) code.Op(0x29).Op(0x47); // pop; returnvoid
        else code.Op(0x48); // returnvalue
        return code.ToArray();
    }
}

/// <summary>A tiny AVM2 instruction writer for generated method bodies.</summary>
public sealed class AbcCode
{
    private readonly List<byte> _bytes = [];

    public AbcCode Op(byte opcode)
    {
        _bytes.Add(opcode);
        return this;
    }

    public AbcCode U30(int value)
    {
        var remaining = (uint)value;
        do
        {
            var next = (byte)(remaining & 0x7F);
            remaining >>= 7;
            if (remaining != 0) next |= 0x80;
            _bytes.Add(next);
        } while (remaining != 0);
        return this;
    }

    public AbcCode GetLocal(int local) => local <= 3 ? Op((byte)(0xD0 + local)) : Op(0x62).U30(local);

    public AbcCode SetLocal(int local) => local <= 3 ? Op((byte)(0xD4 + local)) : Op(0x63).U30(local);

    /// <summary>Writes a branch with a placeholder offset; <see cref="Land"/> points it at the current position.</summary>
    public int Branch(byte opcode)
    {
        Op(opcode);
        _bytes.AddRange([0, 0, 0]);
        return _bytes.Count;
    }

    public void Land(int branchEnd)
    {
        var offset = _bytes.Count - branchEnd;
        _bytes[branchEnd - 3] = (byte)offset;
        _bytes[branchEnd - 2] = (byte)(offset >> 8);
        _bytes[branchEnd - 1] = (byte)(offset >> 16);
    }

    public byte[] ToArray() => [.. _bytes];
}
