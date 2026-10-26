namespace Arkive.Core.Interface.Abc;

/// <summary>A method to route through the tweak dispatcher: the class's qualified name, the method, and the id tweaks register under.</summary>
public sealed record AbcHook(string ClassName, string MethodName, bool Static = false)
{
    public string Id => ClassName + (Static ? "::" : ".") + MethodName;
}

/// <summary>
/// Routes methods of a movie's own code through a dispatcher in Arkive's tweak code. The original method keeps its
/// body under a new name (<see cref="RenamedSuffix"/>); a new method with the original name and signature calls
/// <c>Dispatcher.run(id, this, [arguments], original)</c> and returns what it returns. Existing instructions are never
/// rewritten, so every caller, override and super call keeps working.
/// </summary>
public static class AbcHookInjector
{
    public const string RenamedSuffix = "$arkive";
    public const string DispatcherPackage = "arkive.tweaks";
    public const string DispatcherClass = "Hooks";
    public const string DispatcherMethod = "run";

    /// <summary>Applies the hooks; returns the ids of hooks whose class or method this code doesn't have.</summary>
    public static IReadOnlyList<string> Inject(AbcFile abc, IEnumerable<AbcHook> hooks)
    {
        var missing = new List<string>();
        var pool = abc.Pool;
        var dispatcher = pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, DispatcherPackage), DispatcherClass);
        var run = pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, ""), DispatcherMethod);
        foreach (var hook in hooks)
        {
            var classIndex = abc.FindClass(hook.ClassName);
            var traits = classIndex < 0 ? null : hook.Static ? abc.Classes[classIndex].Traits : abc.Instances[classIndex].Traits;
            var trait = traits?.FirstOrDefault(item => item.Kind == AbcTrait.Method && pool.NameOf(item.Name) == hook.MethodName);
            if (traits is null || trait is null)
            {
                missing.Add(hook.Id);
                continue;
            }
            var original = pool.Multinames[trait.Name];
            var renamed = pool.AddQName(original.Namespace, hook.MethodName + RenamedSuffix);
            if (traits.Any(item => item.Name == renamed)) continue;

            var method = abc.Methods[trait.Index];
            var body = abc.BodyOf(trait.Index) ?? throw new InvalidDataException($"{hook.Id} has no body to keep.");
            var wrapper = new AbcMethod
            {
                ReturnType = method.ReturnType,
                Flags = (byte)(method.Flags & (AbcMethod.HasOptional | AbcMethod.NeedRest)),
            };
            wrapper.ParameterTypes.AddRange(method.ParameterTypes);
            wrapper.Optional.AddRange(method.Optional);
            abc.Methods.Add(wrapper);
            var wrapperIndex = abc.Methods.Count - 1;

            var parameters = method.ParameterTypes.Count + ((method.Flags & AbcMethod.NeedRest) != 0 ? 1 : 0);
            var returnsVoid = pool.NameOf(method.ReturnType) == "void";
            abc.Bodies.Add(new AbcMethodBody
            {
                Method = wrapperIndex,
                MaxStack = parameters + 5,
                LocalCount = parameters + 1,
                InitScopeDepth = body.InitScopeDepth,
                MaxScopeDepth = body.InitScopeDepth + 1,
                Code = WrapperCode(dispatcher, pool.AddString(hook.Id), parameters, renamed, run, returnsVoid),
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

    private static byte[] WrapperCode(int dispatcher, int id, int parameters, int renamed, int run, bool returnsVoid)
    {
        var code = new AbcCode();
        code.Op(0xD0); // getlocal_0
        code.Op(0x30); // pushscope
        code.Op(0x60).U30(dispatcher); // getlex Hooks
        code.Op(0x2C).U30(id); // pushstring id
        code.Op(0xD0); // this
        for (var local = 1; local <= parameters; local++)
        {
            if (local <= 3) code.Op((byte)(0xD0 + local));
            else code.Op(0x62).U30(local); // getlocal n
        }
        code.Op(0x56).U30(parameters); // newarray
        code.Op(0xD0);
        code.Op(0x66).U30(renamed); // getproperty original (a bound method closure)
        code.Op(0x46).U30(run).U30(4); // callproperty run, 4
        if (returnsVoid)
        {
            code.Op(0x29); // pop
            code.Op(0x47); // returnvoid
        }
        else
        {
            code.Op(0x48); // returnvalue
        }
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

    public byte[] ToArray() => [.. _bytes];
}
