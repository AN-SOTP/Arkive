namespace Arkive.Core.Interface.Abc;

/// <summary>When a hook's handler runs, and what its return value can do (the four wrapper shapes Mari's hooks use).</summary>
public enum AbcHookKind
{
    /// <summary>After the game's method: <c>Handler(this, args, result)</c>; its return value is ignored.</summary>
    After,

    /// <summary>
    /// After the game's method, able to change what it returns: <c>Handler(this, args, result)</c> returns null to keep
    /// the result or <c>{value: x}</c> to return x instead.
    /// </summary>
    AfterValue,

    /// <summary>
    /// Before the game's method: <c>Handler(this, args)</c> returns null to go on, or <c>{return: true}</c> (with
    /// <c>value: x</c> for a method that returns something) to skip the game's method. The game's method gets
    /// <c>args[i]</c>, so the handler can change the arguments.
    /// </summary>
    Before,
}

/// <summary>
/// A game method to hook (its class's qualified name and the method) and the tweak's static handler for it
/// ("package.Class.method"). By default the wrapper calls the game's method, then the handler:
/// <c>args = [arguments]; result = this.original(arguments); try { Handler(this, args, result); } catch {}
/// return result;</c>. <see cref="Kind"/> picks the other shapes.
/// </summary>
public sealed record AbcHook(string ClassName, string MethodName, string Handler, bool Static = false, AbcHookKind Kind = AbcHookKind.After)
{
    /// <summary>
    /// Developer builds only: errors from the handler, and from the game's method too, are passed as
    /// "ORIG id: error" / "TWEAK id: error" to the tweak code's static <c>arkive.tweaks.Diag.note</c>.
    /// </summary>
    public bool Diagnostic { get; init; }

    /// <summary>
    /// Private or protected methods and fields of the hooked class that the handler reaches by plain name (as Mari's
    /// "exposed" members): a method gets a second, public trait bound to the same method, with no new body; a field
    /// gets a public getter and setter.
    /// </summary>
    public IReadOnlyList<string> Expose { get; init; } = [];

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

    /// <summary>
    /// Applies the hooks; returns the ids of hooks whose class or method this code doesn't have, or can't take. Several
    /// tweaks may hook one method: each further hook wraps the previous wrapper (kept as <c>m$arkive2</c>, <c>m$arkive3</c>,
    /// …), so every handler runs, in the order given. Code that already holds a hook's wrapper is left as it is.
    /// </summary>
    public static IReadOnlyList<string> Inject(AbcFile abc, IEnumerable<AbcHook> hooks)
    {
        var missing = new List<string>();
        var pool = abc.Pool;
        var all = hooks.Distinct().ToArray();
        var layers = new Dictionary<string, int>();
        foreach (var hook in all)
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
            var layer = layers.GetValueOrDefault(hook.Id) + 1;
            layers[hook.Id] = layer;
            var renamed = pool.AddQName(original.Namespace, hook.MethodName + RenamedSuffix + (layer == 1 ? "" : layer.ToString()));
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
                LocalCount = parameters + 5,
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
        foreach (var hook in all)
            missing.AddRange(Expose(abc, hook));
        return missing;
    }

    /// <summary>
    /// Adds a public trait for each of the hook's <see cref="AbcHook.Expose"/> methods, bound to the method the
    /// existing trait runs (the wrapper, when that method is hooked too); returns "Class.method" for those not found.
    /// </summary>
    private static IEnumerable<string> Expose(AbcFile abc, AbcHook hook)
    {
        if (hook.Expose.Count == 0) yield break;
        var pool = abc.Pool;
        var classIndex = abc.FindClass(hook.ClassName);
        var traits = classIndex < 0 ? null : hook.Static ? abc.Classes[classIndex].Traits : abc.Instances[classIndex].Traits;
        var publicNs = pool.AddNamespace(AbcNamespace.PackageNamespace, "");
        foreach (var name in hook.Expose)
        {
            var exposed = pool.AddQName(publicNs, name);
            if (traits is not null && traits.Any(item => item.Name == exposed)) continue;
            var trait = traits?.FirstOrDefault(item => item.Kind is AbcTrait.Method or AbcTrait.Slot or AbcTrait.Const &&
                                                       pool.NameOf(item.Name) == name);
            if (traits is null || trait is null)
            {
                yield return hook.ClassName + "." + name;
                continue;
            }
            if (trait.Kind == AbcTrait.Method) traits.Add(new AbcTrait { Name = exposed, KindByte = AbcTrait.Method, Index = trait.Index });
            else ExposeField(abc, traits, trait, exposed);
        }
    }

    /// <summary>
    /// A field gets a public getter and setter of the field's type that read and write it by its own name (Mari's
    /// accessors for RollingTreeList._nextIndex): <c>get: return this.field;</c> <c>set: this.field = value;</c>
    /// They start at the scope depth of the class's own methods.
    /// </summary>
    private static void ExposeField(AbcFile abc, List<AbcTrait> traits, AbcTrait field, int exposed)
    {
        var scope = traits.Where(item => item.Kind == AbcTrait.Method).Select(item => abc.BodyOf(item.Index)).FirstOrDefault(body => body is not null)
            ?.InitScopeDepth ?? throw new InvalidDataException("The class has no method to take a scope depth from.");
        void Accessor(byte kind, AbcMethod method, AbcCode code, int locals)
        {
            abc.Methods.Add(method);
            abc.Bodies.Add(new AbcMethodBody
            {
                Method = abc.Methods.Count - 1, MaxStack = 2, LocalCount = locals,
                InitScopeDepth = scope, MaxScopeDepth = scope + 1, Code = code.ToArray(),
            });
            traits.Add(new AbcTrait { Name = exposed, KindByte = kind, Index = abc.Methods.Count - 1 });
        }
        Accessor(AbcTrait.Getter, new AbcMethod { ReturnType = field.Index },
            new AbcCode().Op(0xD0).Op(0x30).Op(0xD0).Op(0x66).U30(field.Name).Op(0x48), 1);
        var setter = new AbcMethod();
        setter.ParameterTypes.Add(field.Index);
        Accessor(AbcTrait.Setter, setter, new AbcCode().Op(0xD0).Op(0x30).Op(0xD0).Op(0xD1).Op(0x61).U30(field.Name).Op(0x47), 2);
    }

    /// <summary>
    /// After: <c>args = [p1..pn]; result = this.original(p1..pn); try { Handler(this, args, result); } catch {} return result;</c>
    /// AfterValue: <c>… try { o = Handler(this, args, result); } catch {} if (o) result = o.value; return result;</c>
    /// Before: <c>args = [p1..pn]; try { o = Handler(this, args); } catch {} if (o &amp;&amp; o["return"]) return o.value;
    /// return this.original(args[0]..args[n-1]);</c>
    /// Diagnostic hooks also guard the game's call, and note every error.
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
        var answer = parameters + 4;
        var before = hook.Kind == AbcHookKind.Before;
        var index = before ? pool.AddMultinameL(publicNs) : 0;
        var valueName = hook.Kind == AbcHookKind.After ? 0 : pool.AddQName(publicNs, "value");
        var returnName = before ? pool.AddQName(publicNs, "return") : 0;
        var code = new AbcCode();
        var handlers = new List<AbcException>();
        code.Op(0xD0).Op(0x30);
        for (var local = 1; local <= parameters; local++) code.GetLocal(local);
        code.Op(0x56).U30(parameters).SetLocal(args);
        code.Op(0x21).SetLocal(result);
        code.Op(0x21).SetLocal(answer);

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

        // After hooks pass the wrapper's own parameters; before hooks pass args[i], which the handler may have changed.
        void Original(AbcCode c)
        {
            c.Op(0xD0);
            for (var local = 1; local <= parameters; local++)
            {
                if (before) c.GetLocal(args).PushInt(local - 1).Op(0x66).U30(index);
                else c.GetLocal(local);
            }
            if (returnsVoid) c.Op(0x4F).U30(renamed).U30(parameters);
            else c.Op(0x46).U30(renamed).U30(parameters).SetLocal(result);
        }

        void Handler(AbcCode c)
        {
            c.Op(0x60).U30(ownerName).Op(0xD0).GetLocal(args);
            if (!before) c.GetLocal(result);
            var count = before ? 2 : 3;
            if (hook.Kind == AbcHookKind.After) c.Op(0x4F).U30(method).U30(count);
            else c.Op(0x46).U30(method).U30(count).SetLocal(answer);
        }

        void Return(AbcCode c)
        {
            if (returnsVoid) c.Op(0x47);
            else c.GetLocal(result).Op(0x48);
        }

        if (before)
        {
            Guarded(Handler, "TWEAK ");
            var noAnswer = code.GetLocal(answer).Branch(0x12); // iffalse
            var goOn = code.GetLocal(answer).Op(0x66).U30(returnName).Branch(0x12);
            if (!returnsVoid) code.GetLocal(answer).Op(0x66).U30(valueName).SetLocal(result);
            Return(code);
            code.Land(noAnswer);
            code.Land(goOn);
        }
        if (hook.Diagnostic) Guarded(Original, "ORIG ");
        else Original(code);
        if (!before)
        {
            Guarded(Handler, "TWEAK ");
            if (hook.Kind == AbcHookKind.AfterValue)
            {
                var keep = code.GetLocal(answer).Branch(0x12);
                code.GetLocal(answer).Op(0x66).U30(valueName).SetLocal(result);
                code.Land(keep);
            }
        }
        Return(code);
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

    /// <summary>pushbyte for small values, pushshort otherwise (0..32767).</summary>
    public AbcCode PushInt(int value) => value < 128 ? Op(0x24).Op((byte)value) : Op(0x25).U30(value);

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
