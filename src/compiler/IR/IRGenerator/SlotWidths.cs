// SPDX-License-Identifier: MIT
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

// Unannotated slots whose width a later write can outgrow (see WidthSeeds for why the answer
// is a new run rather than a wider store). A slot is registered where its width is chosen
// without a written annotation, and every store into it is checked against the value it
// receives. A slot that is too narrow records the width it needs under a key that names it the
// same way in the next run, and the choice point takes that width from the start.
public partial class IRGenerator
{
    /// Filled by the pipeline; null in the unit tests that drive the generator directly, which
    /// then behave exactly as before.
    public WidthSeeds? WidthSeeds { get; set; }

    // Variable names of the slots whose width was inferred rather than written, mapped to the
    // key their seed is filed under. For a local or a global the key is the name itself.
    private readonly Dictionary<string, string> inferredSlots = new();

    // Set while VisitAssign stores a value computed from the target itself (`c = c + 1`). Such
    // a store only ever asks for a sign: its width is the accumulator question, which the
    // promoted width of the sum cannot answer (each run would ask for one step more).
    private bool storeReadsItsTarget;

    // The parameters and returns written without an annotation, taken before TypeInference
    // fills some of them in, and the seed key of each function they belong to.
    private readonly HashSet<Param> unannotatedParams = new();
    private readonly HashSet<FunctionDef> unannotatedReturns = new();
    private readonly HashSet<FunctionDef> returnsNone = new();
    private readonly Dictionary<FunctionDef, string> defSeedKeys = new();
    // IR function name -> the def it was compiled from, for a store to find its seed key.
    private readonly Dictionary<string, FunctionDef> slotDefs = new();

    /// The width a slot starts at in this run: what its own evidence chose, widened by what an
    /// earlier run found it needs.
    private DataType SeedSlot(string key, DataType chosen)
    {
        if (WidthSeeds?.Get(key) is not { } seed || !WidthSeeds.IsInt(chosen)) return chosen;
        return WidthSeeds.Join(chosen, seed);
    }

    /// Chooses and registers the width of an unannotated local or global: the seed, if any,
    /// widens it, and later stores into the name are checked.
    private DataType InferredSlot(string name, DataType chosen)
    {
        if (WidthSeeds == null) return chosen;
        inferredSlots[name] = name;
        return SeedSlot(name, chosen);
    }

    /// Every def whose parameters or return can be unannotated, before TypeInference writes
    /// into them. The key is what the next run finds the same def by: its module, position
    /// and name.
    private void RecordUnannotatedSignatures(ProgramNode main, IReadOnlyDictionary<string, ProgramNode> imported)
    {
        if (WidthSeeds == null) return;
        void Walk(string module, ProgramNode prog)
        {
            foreach (var f in prog.Functions.Concat(TypeInference.ClassMethods(prog)))
            {
                if (f.IsInline || f.IsExtern) continue;
                defSeedKeys[f] = $"{module}:{f.Line}:{f.Column}:{f.Name}";
                foreach (var p in f.Params)
                    if (p.Type.Length == 0 && !p.IsVarArg) unannotatedParams.Add(p);
                if (f.ReturnType is "" or "void") unannotatedReturns.Add(f);
                // A None return keeps the function on the RFC 0009 inference: a width seeded
                // onto it would turn the None into a refused `return None` of a `-> X`.
                if (TypeInference.WalkStatements(f.Body.Statements).OfType<ReturnStmt>()
                        .Any(r => r.Value is null or NoneLiteral))
                    returnsNone.Add(f);
            }
        }
        Walk("", main);
        foreach (var kv in imported) Walk(kv.Key, kv.Value);
    }

    /// After TypeInference: a parameter or return an earlier run found too narrow takes the
    /// width it needs, as if it had been annotated with it -- so return inference and the
    /// field layout, which read these annotations, see it too.
    private void ApplySignatureSeeds()
    {
        if (WidthSeeds == null) return;
        foreach (var (f, key) in defSeedKeys)
        {
            foreach (var p in f.Params)
            {
                if (!unannotatedParams.Contains(p)) continue;
                if (WidthSeeds.Get(key + "#" + p.Name) is not { } ps) continue;
                var have = DataTypeExtensions.StringToDataType(p.Type);
                p.Type = TypeName(p.Type.Length == 0 ? ps : WidthSeeds.Join(have, ps));
            }
            if (unannotatedReturns.Contains(f) && WidthSeeds.Get(key + "->") is { } rs)
            {
                var have = DataTypeExtensions.StringToDataType(f.ReturnType);
                f.ReturnType = TypeName(WidthSeeds.IsInt(have) ? WidthSeeds.Join(have, rs) : rs);
            }
        }
    }

    /// Whether every value of <paramref name="value"/> fits <paramref name="slot"/>.
    private static bool TypeHolds(DataType slot, DataType value)
    {
        var (sLo, sHi) = RangeOfType(slot);
        var (vLo, vHi) = RangeOfType(value);
        return vLo >= sLo && vHi <= sHi;
    }

    private static string TypeName(DataType t) => t switch
    {
        DataType.UINT8 => "uint8", DataType.INT8 => "int8",
        DataType.UINT16 => "uint16", DataType.INT16 => "int16",
        DataType.UINT32 => "uint32", _ => "int32",
    };

    /// Files the def an IR function was compiled from, and registers its unannotated
    /// parameters as slots checked at every call.
    private void RecordSlotDef(string irName, FunctionDef def)
    {
        if (WidthSeeds == null || !defSeedKeys.TryGetValue(def, out var key)) return;
        slotDefs[irName] = def;
        foreach (var p in def.Params)
            if (unannotatedParams.Contains(p))
                inferredSlots[irName + "." + p.Name] = key + "#" + p.Name;
    }

    /// The seed key of the return of IR function <paramref name="irName"/>, when it was
    /// written without an annotation.
    private string? UnannotatedReturnKey(string irName) =>
        WidthSeeds != null && slotDefs.TryGetValue(irName, out var def)
        && unannotatedReturns.Contains(def) && defSeedKeys.TryGetValue(def, out var key)
            ? key + "->" : null;

    /// A value about to be stored into parameter <paramref name="paramVar"/> of a call.
    /// Checked BEFORE the argument is narrowed to the parameter's width, which is where the
    /// truncation happens.
    private void NoteArgumentStore(string paramVar, DataType paramType, Val arg)
    {
        if (inferredSlots.TryGetValue(paramVar, out var key))
            NoteStore(key, paramType, arg, readsItself: false);
    }

    private void NoteReturnStore(string irFunction, DataType returnType, Val value)
    {
        if (UnannotatedReturnKey(irFunction) is { } key)
            NoteStore(key, returnType, value, readsItself: false);
    }

    /// A value returned from an unannotated function whose inference found no type: the
    /// caller treats the call as void and reads the return register at whatever width its
    /// own expression wants, so a byte came back with a garbage high byte (#519). The
    /// function is declared with the value's width in the next run.
    private void NoteUntypedReturn(string irFunction, Val value)
    {
        if (UnannotatedReturnKey(irFunction) is not { } key
            || !slotDefs.TryGetValue(irFunction, out var def) || returnsNone.Contains(def)) return;
        // Only a number: a string's interned id, a character, or an instance's handle is
        // not a width, and those callers already read them as what they are.
        if (value is Constant { Text: not null } || charReturningFunctions.Contains(irFunction)
            || value is Variable iv && (instanceClasses.ContainsKey(iv.Name)
                || NamesInstanceAnchor(iv.Name) || strConstantVariables.ContainsKey(iv.Name))
            || value is Temporary tv && strConstantVariables.ContainsKey(tv.Name)) return;
        var t = GetValType(value);
        if (value is Constant c) t = NarrowestTypeFor(c.Value, c.Value);
        if (!WidthSeeds.IsInt(t) || value is NoneVal) return;
        Logger.Verbose("width", $"'{key}' returns a {t.ToString().ToLower()} with no return type: "
            + "compiling again with it declared");
        WidthSeeds!.Require(key, t);
    }

    /// The store checks of Emit: a Copy or an AugAssign into a registered slot.
    private void CheckSlotWrite(Instruction inst)
    {
        if (inferredSlots.Count == 0) return;
        switch (inst)
        {
            case Copy { Dst: Variable dv } cp when inferredSlots.TryGetValue(dv.Name, out var ck):
                NoteStore(ck, dv.Type, cp.Src, storeReadsItsTarget);
                break;
            case AugAssign { Target: Variable av } aa when inferredSlots.TryGetValue(av.Name, out var ak):
                NoteAugStore(ak, av.Type, aa.Op, aa.Operand);
                break;
        }
    }

    /// What an integer value can hold, as far as the generator knows here.
    private (long Min, long Max)? SlotValueRange(Val v) => v switch
    {
        Constant c => (c.Value, c.Value),
        Temporary t when WidthSeeds.IsInt(t.Type) =>
            tempRanges.TryGetValue(t.Name, out var r) ? r : RangeOfType(t.Type),
        Variable vv when WidthSeeds.IsInt(vv.Type) => RangeOfType(vv.Type),
        MemoryAddress m when WidthSeeds.IsInt(m.Type) => RangeOfType(m.Type),
        _ => null,
    };

    private void NoteStore(string key, DataType slot, Val value, bool readsItself)
    {
        if (WidthSeeds == null || !WidthSeeds.IsInt(slot)) return;
        if (SlotValueRange(value) is not var (lo, hi)) return;
        NoteStoreRange(key, slot, lo, hi, readsItself, value);
    }

    private void NoteStoreRange(string key, DataType slot, long lo, long hi, bool readsItself,
                                Val? value = null)
    {
        var (sMin, sMax) = RangeOfType(slot);
        if (lo >= sMin && hi <= sMax) return;
        DataType need;
        if (readsItself)
        {
            // An accumulator answers for its sign and for the width of what feeds it, the way
            // `total = total + r` is as wide as r -- not for the promoted sum, which would ask
            // for one step more on every run.
            need = value is { } v && OperandsType(v, 0) is { } fed ? fed : slot;
            if (lo < 0 && !WidthSeeds.IsSigned(slot))
                need = PyMCU.Common.WidthSeeds.Join(need, DataType.INT8);
        }
        else need = NarrowestTypeFor(lo, hi);
        RequireSlot(key, slot, need);
    }

    // The operands of each arithmetic temporary, so an accumulator's store can be judged by
    // what feeds it rather than by the promoted result.
    private readonly Dictionary<string, (Val A, Val B)> binaryOperands = new();

    /// The narrowest type holding every leaf operand of <paramref name="v"/>'s arithmetic.
    /// The slot's own read is one of the leaves and answers the slot's own width, which
    /// widens nothing.
    private DataType? OperandsType(Val v, int depth)
    {
        if (depth < 8 && v is Temporary t && binaryOperands.TryGetValue(t.Name, out var ops))
        {
            var a = OperandsType(ops.A, depth + 1);
            var b = OperandsType(ops.B, depth + 1);
            return a is null ? b : b is null ? a : PyMCU.Common.WidthSeeds.Join(a.Value, b.Value);
        }
        return SlotValueRange(v) is var (lo, hi) ? NarrowestTypeFor(lo, hi) : null;
    }

    private void NoteAugStore(string key, DataType slot, BinaryOp op, Val operand)
    {
        if (WidthSeeds == null || !WidthSeeds.IsInt(slot)) return;
        if (SlotValueRange(operand) is not var (oLo, oHi)) return;
        var (sMin, sMax) = RangeOfType(slot);
        // `x += v` and `x -= v` are accumulators too, so they answer for their sign; the
        // operand's own width is the evidence for the rest, the way `total = total + r` is as
        // wide as r at module level.
        long lo = op switch
        {
            BinaryOp.Add => sMin + oLo,
            BinaryOp.Sub => sMin - oHi,
            BinaryOp.Mul => Math.Min(Math.Min(sMin * oLo, sMin * oHi), Math.Min(sMax * oLo, sMax * oHi)),
            _ => 0,
        };
        if (op is BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul)
        {
            var operandType = NarrowestTypeFor(oLo, oHi);
            if (lo < 0 && !WidthSeeds.IsSigned(slot))
                RequireSlot(key, slot, WidthSeeds.Join(DataType.INT8, operandType));
            else if (operandType.SizeOf() > slot.SizeOf())
                RequireSlot(key, slot, operandType);
        }
    }

    private void RequireSlot(string key, DataType slot, DataType need)
    {
        var want = PyMCU.Common.WidthSeeds.Join(slot, need);
        if (want == slot) return;   // nothing wider holds it (uint32 with a sign)
        Logger.Verbose("width", $"'{key}' is {slot.ToString().ToLower()} and is stored "
            + $"{want.ToString().ToLower()}: compiling again with it wider");
        WidthSeeds!.Require(key, want);
    }

    // `<class key>.<field>` of every field whose width no write annotated.
    private readonly HashSet<string> unannotatedFields = new();

    /// The width an unannotated scalar field is laid out at: the layout's own choice, widened
    /// by what an earlier run found it needs.
    private string SeedFieldType(string classKey, string field, string type)
    {
        if (WidthSeeds == null || !IsNumericWidthName(type)) return type;
        unannotatedFields.Add(classKey + "." + field);
        var chosen = DataTypeExtensions.StringToDataType(type);
        var seeded = SeedSlot("F:" + classKey + "." + field, chosen);
        return seeded == chosen ? type : TypeName(seeded);
    }

    /// A value about to be stored into `obj.field`, checked against the field's layout width
    /// when the field is unannotated. Every representation of the store (a flattened name, an
    /// outlined method's field parameter, a slot) passes through here first.
    private void NoteFieldStore(MemberAccessExpr target, Expression? source, Val value)
    {
        if (FieldSlot(target) is not var (key, type)) return;
        // `self.n = self.n - 3` is an accumulator.
        bool reads = source != null && TypeInference.WalkExpressionTree(source).Any(x =>
            x is MemberAccessExpr { Object: VariableExpr o } m
            && m.Member == target.Member && target.Object is VariableExpr r && o.Name == r.Name);
        NoteStore(key, type, value, reads);
    }

    /// `obj.field OP= v`: the read-modify-write computes at the field's own width, so the
    /// check has to see the operand rather than the result.
    private void NoteFieldAugStore(MemberAccessExpr target, BinaryOp op, Val operand)
    {
        if (FieldSlot(target) is var (key, type)) NoteAugStore(key, type, op, operand);
    }

    // `<subclass>.<field>` -> `<base>.<field>` for an unannotated field the subclass inherits.
    private readonly Dictionary<string, string> inheritedFieldSlots = new();

    private void InheritFieldSlot(string subclassKey, string baseKey, string field)
    {
        string owner = inheritedFieldSlots.TryGetValue(baseKey + "." + field, out var o)
            ? o : baseKey + "." + field;
        if (unannotatedFields.Contains(owner))
            inheritedFieldSlots[subclassKey + "." + field] = owner;
    }

    /// The seed key and layout width of an unannotated scalar field written as `obj.field`.
    private (string Key, DataType Type)? FieldSlot(MemberAccessExpr target)
    {
        if (WidthSeeds == null || unannotatedFields.Count == 0
            || target.Object is not VariableExpr recv) return null;
        string? cls = InstanceClassOfName(recv.Name);
        if (cls == null && recv.Name == "self")
        {
            string frame = inlineStack.Count > 0 && !string.IsNullOrEmpty(inlineStack[^1].CalleeName)
                ? inlineStack[^1].CalleeName : currentFunction;
            methodInstanceTypes.TryGetValue(frame, out cls);
        }
        if (cls == null || !classFieldLayout.TryGetValue(cls, out var layout)) return null;
        string slot = cls + "." + target.Member;
        if (inheritedFieldSlots.TryGetValue(slot, out var owner)) slot = owner;
        else if (!unannotatedFields.Contains(slot)) return null;
        foreach (var (f, t, _) in layout)
            if (f == target.Member)
                return ("F:" + slot, DataTypeExtensions.StringToDataType(t));
        return null;
    }

    /// `for x in range(...)` over a name whose width an earlier store inferred: the counter's
    /// values are one more store into the slot. Null when the name was declared, or when no
    /// run is being checked -- the loop then keeps the declared-type rule.
    private DataType? InferredLoopVarType(string bareName, DataType current, long lo, long hi)
    {
        if (WidthSeeds == null) return null;
        foreach (var key in new[]
                 {
                     string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + bareName,
                     string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + bareName,
                     bareName,
                 })
        {
            if (key == null || !inferredSlots.TryGetValue(key, out var seedKey)) continue;
            NoteStoreRange(seedKey, current, lo, hi, readsItself: false);
            return PyMCU.Common.WidthSeeds.Join(current, NarrowestTypeFor(lo, hi));
        }
        return null;
    }

    /// Whether <paramref name="e"/> reads the name <paramref name="name"/>.
    private static bool ReadsName(Expression? e, string name) =>
        e != null && TypeInference.WalkExpressionTree(e).Any(x => x is VariableExpr v && v.Name == name);
}
