namespace Arkive.Core.Interface.Abc;

/// <summary>
/// A game method to hook (its class's qualified name and the method) and the tweak's static handler for it
/// ("package.Class.method"). The wrapper calls the game's method, then the handler:
/// <c>args = [arguments]; result = this.original(arguments); try { result = Handler(this, args, result); } catch {}
/// return result;</c>
/// </summary>
public sealed record AbcHook(string ClassName, string MethodName, string Handler, bool Static = false)
{
    /// <summary>
    /// Developer builds only: errors from the handler, and from the game's method too, are passed as
    /// "ORIG id: error" / "TWEAK id: error" to the tweak code's static <c>arkive.tweaks.Diag.note</c>.
    /// </summary>
    public bool Diagnostic { get; init; }

    public string Id => ClassName + (Static ? "::" : ".") + MethodName;
}

/// <summary>
/// Hooks methods of a movie's own code the way the game's player is known to accept: the original keeps its body under
/// a new name (<see cref="RenamedSuffix"/>), and a new method with the original name and signature calls it directly
/// and then the tweak's static handler, with no dispatcher and no setup on the way. The handler call is guarded: a
/// tweak that throws (as Lobby CP did on empty party slots) leaves the game's window working instead of breaking it.
/// Existing instructions are never rewritten, so every caller, override and super call keeps working.
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
                MaxStack = parameters + 6,
                LocalCount = parameters + 4,
                InitScopeDepth = body.InitScopeDepth,
                MaxScopeDepth = body.InitScopeDepth + 1,
            });
            var wrapperBody = abc.Bodies[^1];
            (wrapperBody.Code, var handlers) = WrapperCode(pool, hook, parameters, renamed, pool.NameOf(method.ReturnType) == "void");
            wrapperBody.Exceptions.AddRange(handlers);

            // The game's trait stays where it is (keeping its attributes, such as override) and now runs the wrapper; the
            // original body moves to a new plain trait, which nothing above it overrides.
            traits.Add(new AbcTrait { Name = renamed, KindByte = AbcTrait.Method, Index = trait.Index });
            trait.Index = wrapperIndex;
        }
        return missing;
    }

    /// <summary>
    /// <c>args = [p1..pn]; result = this.original(p1..pn); try { result = Handler(this, args, result); } catch {} return result;</c>
    /// Void methods ignore the handler's value. Diagnostic hooks also guard the game's call, and note every error.
    /// </summary>
    private static (byte[] Code, AbcException[] Handlers) WrapperCode(
        AbcConstantPool pool, AbcHook hook, int parameters, int renamed, bool returnsVoid)
    {
        var handler = hook.Handler;
        var id = hook.Id;
        var split = handler.LastIndexOf('.');
        var owner = handler[..split];
        var dot = owner.LastIndexOf('.');
        var publicNs = pool.AddNamespace(AbcNamespace.PackageNamespace, "");
        var ownerName = pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, dot < 0 ? "" : owner[..dot]), owner[(dot + 1)..]);
        var method = pool.AddQName(publicNs, handler[(split + 1)..]);
        var diag = hook.Diagnostic ? pool.AddQName(pool.AddNamespace(AbcNamespace.PackageNamespace, "arkive.tweaks"), "Diag") : 0;
        var note = hook.Diagnostic ? pool.AddQName(publicNs, "note") : 0;
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
            if (hook.Diagnostic)
            {
                code.Op(0x60).U30(diag); // Diag
                code.Op(0x2C).U30(pool.AddString(label + id + ": ")).GetLocal(error).Op(0xA0); // label + error
                code.Op(0x4F).U30(note).U30(1); // Diag.note(…)
            }
            code.Land(over);
            handlers.Add(new AbcException(start, end, target, 0, 0));
        }

        void Original(AbcCode c)
        {
            c.Op(0xD0);
            for (var local = 1; local <= parameters; local++) c.GetLocal(local);
            if (returnsVoid) c.Op(0x4F).U30(renamed).U30(parameters);
            else c.Op(0x46).U30(renamed).U30(parameters).SetLocal(result);
        }

        if (hook.Diagnostic) Guarded(Original, "ORIG ");
        else Original(code);
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
