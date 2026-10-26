namespace Arkive.Core.Interface.Abc;

/// <summary>
/// A game method to hook (its class's qualified name and the method) and the tweak's static handler for it
/// ("package.Class.method"). The wrapper calls the game's method, then the handler:
/// <c>args = [arguments]; result = this.original(arguments); return Handler(this, args, result);</c>
/// </summary>
public sealed record AbcHook(string ClassName, string MethodName, string Handler, bool Static = false)
{
    /// <summary>
    /// Developer builds only: the wrapper catches any error from the game's method or the handler and passes
    /// "ORIG id: error" / "TWEAK id: error" to the tweak code's static <c>arkive.tweaks.Diag.note</c> instead of throwing.
    /// </summary>
    public bool Diagnostic { get; init; }

    public string Id => ClassName + (Static ? "::" : ".") + MethodName;
}

/// <summary>
/// Hooks methods of a movie's own code the way the game's player is known to accept: the original keeps its body under
/// a new name (<see cref="RenamedSuffix"/>), and a new method with the original name and signature calls it directly
/// and then the tweak's static handler, with no dispatcher, no try blocks and no setup on the way. Existing
/// instructions are never rewritten, so every caller, override and super call keeps working.
/// </summary>
public static class AbcHookInjector
{
    public const string RenamedSuffix = "$arkive";

    /// <summary>Applies the hooks; returns the ids of hooks whose class or method this code doesn't have, or can't take.</summary>
    public static IReadOnlyList<string> Inject(AbcFile abc, IEnumerable<AbcHook> hooks)
    {
        var missing = new List<string>();
        var pool = abc.Pool;
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
            abc.Bodies.Add(new AbcMethodBody
            {
                Method = wrapperIndex,
                MaxStack = parameters + 4,
                LocalCount = parameters + 3,
                InitScopeDepth = body.InitScopeDepth,
                MaxScopeDepth = body.InitScopeDepth + 1,
                Code = WrapperCode(pool, hook.Handler, parameters, renamed, pool.NameOf(method.ReturnType) == "void"),
            });
            if (hook.Diagnostic)
            {
                var diagnostic = abc.Bodies[^1];
                (diagnostic.Code, var handlers) = DiagnosticCode(pool, hook.Handler, parameters, renamed,
                    pool.NameOf(method.ReturnType) == "void", hook.Id);
                diagnostic.Exceptions.AddRange(handlers);
                diagnostic.LocalCount = parameters + 4;
                diagnostic.MaxStack = parameters + 6;
            }

            // The game's trait stays where it is (keeping its attributes, such as override) and now runs the wrapper; the
            // original body moves to a new plain trait, which nothing above it overrides.
            traits.Add(new AbcTrait { Name = renamed, KindByte = AbcTrait.Method, Index = trait.Index });
            trait.Index = wrapperIndex;
        }
        return missing;
    }

    /// <summary>
    /// <c>args = [p1..pn]; result = this.original(p1..pn); return Handler(this, args, result);</c>
    /// (void methods pass undefined and ignore the handler's value).
    /// </summary>
    private static byte[] WrapperCode(AbcConstantPool pool, string handler, int parameters, int renamed, bool returnsVoid)
    {
        var split = handler.LastIndexOf('.');
        var owner = handler[..split];
        var dot = owner.LastIndexOf('.');
        var ownerName = pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, dot < 0 ? "" : owner[..dot]), owner[(dot + 1)..]);
        var method = pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, ""), handler[(split + 1)..]);
        var args = parameters + 1;
        var result = parameters + 2;
        var code = new AbcCode();
        code.Op(0xD0).Op(0x30); // getlocal_0; pushscope
        for (var local = 1; local <= parameters; local++) code.GetLocal(local);
        code.Op(0x56).U30(parameters).SetLocal(args); // args = [p1..pn]
        code.Op(0x21).SetLocal(result); // result = undefined
        code.Op(0xD0);
        for (var local = 1; local <= parameters; local++) code.GetLocal(local);
        if (returnsVoid) code.Op(0x4F).U30(renamed).U30(parameters); // this.original(p1..pn)
        else code.Op(0x46).U30(renamed).U30(parameters).SetLocal(result);
        code.Op(0x60).U30(ownerName).Op(0xD0).GetLocal(args).GetLocal(result);
        if (returnsVoid) code.Op(0x4F).U30(method).U30(3).Op(0x47); // Handler(this, args, result); returnvoid
        else code.Op(0x46).U30(method).U30(3).Op(0x48); // return Handler(this, args, result)
        return code.ToArray();
    }

    /// <summary>The wrapper with each call in a try block whose handler passes the error to Diag.note.</summary>
    private static (byte[] Code, AbcException[] Handlers) DiagnosticCode(
        AbcConstantPool pool, string handler, int parameters, int renamed, bool returnsVoid, string id)
    {
        var split = handler.LastIndexOf('.');
        var owner = handler[..split];
        var dot = owner.LastIndexOf('.');
        var publicNs = pool.AddNamespace(AbcNamespace.PackageNamespace, "");
        var ownerName = pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, dot < 0 ? "" : owner[..dot]), owner[(dot + 1)..]);
        var method = pool.AddQName(publicNs, handler[(split + 1)..]);
        var diag = pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, "arkive.tweaks"), "Diag");
        var note = pool.AddQName(publicNs, "note");
        var args = parameters + 1;
        var result = parameters + 2;
        var error = parameters + 3;
        var code = new AbcCode();
        var handlers = new List<AbcException>();
        code.Op(0xD0).Op(0x30);
        for (var local = 1; local <= parameters; local++) code.GetLocal(local);
        code.Op(0x56).U30(parameters).SetLocal(args);
        code.Op(0x21).SetLocal(result);

        void Guarded(Action<AbcCode> body, string label)
        {
            var start = code.Position;
            body(code);
            var end = code.Position;
            var over = code.Branch(0x10);
            var target = code.Position;
            code.SetLocal(error).Op(0xD0).Op(0x30); // keep the error; getlocal_0; pushscope
            code.Op(0x60).U30(diag); // Diag
            code.Op(0x2C).U30(pool.AddString(label + id + ": ")).GetLocal(error).Op(0xA0); // label + error
            code.Op(0x4F).U30(note).U30(1); // Diag.note(…)
            code.Land(over);
            handlers.Add(new AbcException(start, end, target, 0, 0));
        }

        Guarded(c =>
        {
            c.Op(0xD0);
            for (var local = 1; local <= parameters; local++) c.GetLocal(local);
            if (returnsVoid) c.Op(0x4F).U30(renamed).U30(parameters);
            else c.Op(0x46).U30(renamed).U30(parameters).SetLocal(result);
        }, "ORIG ");
        Guarded(c =>
        {
            c.Op(0x60).U30(ownerName).Op(0xD0).GetLocal(args).GetLocal(result);
            if (returnsVoid) c.Op(0x4F).U30(method).U30(3);
            else c.Op(0x46).U30(method).U30(3).SetLocal(result);
        }, "TWEAK ");
        if (returnsVoid) code.Op(0x47);
        else code.GetLocal(result).Op(0x48);
        return (code.ToArray(), handlers.ToArray());
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

    public int Position => _bytes.Count;

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
