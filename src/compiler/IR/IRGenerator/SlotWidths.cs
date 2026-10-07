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
        if (WidthSeeds?.AtRunStart(key) is not { } seed || !WidthSeeds.IsInt(chosen)) return chosen;
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
                if (WidthSeeds.AtRunStart(key + "#" + p.Name) is not { } ps) continue;
                var have = DataTypeExtensions.StringToDataType(p.Type);
                string was = p.Type;
                p.Type = TypeName(p.Type.Length == 0 ? ps : WidthSeeds.Join(have, ps));
                // An inferred Optional[int] (a None reaches it) keeps its None member; the
                // payload member is the one that widens.
                if (p.UnionMembers != null)
                    p.UnionMembers = p.UnionMembers.Select(m => m == was ? p.Type : m).ToList();
            }
            if (unannotatedReturns.Contains(f) && WidthSeeds.AtRunStart(key + "->") is { } rs)
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

    /// `constantVariables` stores the raw 32-bit pattern a folded name holds (a plain
    /// `Dictionary&lt;string, int&gt;`), and keeps no sign of its own: `unsignedConstNames`
    /// is the separate mark `MarkUnsignedName` reads before handing the pattern back as
    /// a value (the divmod() quotient of 0xFFFFFFFF / 1 registers both: the pattern -1
    /// in `constantVariables`, and its name in `unsignedConstNames`). Reading the raw
    /// pattern alone as a range answers -1 instead of 4294967295, which then asked a
    /// later store to go SIGNED to fit it -- the width-seed mechanism "fixing" a uint32
    /// local into an int32 one, which is what actually lost the quotient's Unsigned
    /// mark three steps downstream, not the print path itself (oracle probe 632).
    private long ConstantVariableAsLong(string name, int raw) =>
        raw < 0 && unsignedConstNames.Contains(name) ? raw & 0xFFFFFFFFL : raw;

    /// What an integer value can hold, as far as the generator knows here.
    private (long Min, long Max)? SlotValueRange(Val v) => v switch
    {
        // AsLong, not Value: Value is the raw bit pattern (0xFFFFFFFF reads as the
        // int32 -1), AsLong is the Unsigned-aware reading ValRange's own Constant
        // case already uses. A uint32 literal recorded via .Value here fed a
        // negative "proven" dividend range into DivModResultRange and lost the
        // quotient's Unsigned mark downstream (oracle probe 632).
        Constant c => (c.AsLong, c.AsLong),
        Temporary t when WidthSeeds.IsInt(t.Type) =>
            tempRanges.TryGetValue(t.Name, out var r) ? r : RangeOfType(t.Type),
        Variable vv when WidthSeeds.IsInt(vv.Type) =>
            constantVariables.TryGetValue(vv.Name, out int scv)
                ? (ConstantVariableAsLong(vv.Name, scv), ConstantVariableAsLong(vv.Name, scv))
            : !ForeignFlowRead(vv.Name) && localConstantValues.TryGetValue(vv.Name, out int slv)
                ? (slv, slv)
            : !ForeignGlobalRead(vv.Name) && variableRanges.TryGetValue(vv.Name, out var svr) ? svr
            : RangeOfType(vv.Type),
        MemoryAddress m when WidthSeeds.IsInt(m.Type) => RangeOfType(m.Type),
        _ => null,
    };

    /// A fact about the VALUE a Val carries, never a guess from its declared type: a
    /// Constant is exact, a Temporary answers only from a recorded computed range, and a
    /// Variable answers only from a tracked constant or proven-range entry. Null for
    /// everything SlotValueRange would otherwise default to the type's full range for --
    /// that default is sound as a BOUND on the source, but wrong to copy forward as a fact
    /// about the DESTINATION: `n: uint16 = GPIOR0.value` is a real uint8 register with no
    /// entry of its own, so SlotValueRange(GPIOR0) falls back to its declared [0, 255], and
    /// recording that as a fact about `n` silently narrowed a uint16 loop bound the project
    /// deliberately leaves at its declared width when the source is not provably exact
    /// (RangeCounterTypeTests.RuntimeBounds_SizeTheCounterFromTheirTypes).
    private (long Min, long Max)? ProvenSourceFact(Val v) => v switch
    {
        Constant c => (c.AsLong, c.AsLong),
        Temporary t => tempRanges.TryGetValue(t.Name, out var tr) ? tr : null,
        Variable vv =>
            constantVariables.TryGetValue(vv.Name, out int scv)
                ? (ConstantVariableAsLong(vv.Name, scv), ConstantVariableAsLong(vv.Name, scv))
            : !ForeignFlowRead(vv.Name) && localConstantValues.TryGetValue(vv.Name, out int slv) ? (slv, slv)
            : !ForeignGlobalRead(vv.Name) && variableRanges.TryGetValue(vv.Name, out var svr) ? svr
            : null,
        _ => null,
    };

    /// Keeps variableRanges honest at every emitted instruction: a Copy records the
    /// proven range of what it carried into the slot (the slot's own type range when the
    /// value's range exceeds it, which is exactly what a truncating store leaves), and
    /// any other write to a named slot drops the fact -- an augmented assign's operand is
    /// not the stored range, a call's result is not bounded by the call, a bit write
    /// changes one bit, and a fused op's arithmetic is the same answer a Copy would ask.
    /// Globals that a function or a second site also writes never record a range: their
    /// reads in other frames cannot rely on the last store this walk happened to pass.
    private void TrackVariableRange(Instruction inst)
    {
        switch (inst)
        {
            case Copy { Dst: Variable dv } cp:
                if (!WidthSeeds.IsInt(dv.Type)
                    || optionalMembersByName.ContainsKey(dv.Name)
                    || reassignedGlobals.Contains(dv.Name)
                    || functionWrittenGlobals.Contains(dv.Name)
                    || cp.Src is Constant { Text: not null })
                {
                    variableRanges.Remove(dv.Name);
                    break;
                }
                // ProvenSourceFact, not SlotValueRange: a source that is itself just "some
                // variable of some type" (GPIOR0, a uint8 register with no entry of its own)
                // must not hand the destination its declared type's range as if it were a
                // proven value -- that silently narrowed a uint16 loop bound read from an
                // 8-bit register (RangeCounterTypeTests.RuntimeBounds_SizeTheCounterFromTheirTypes).
                if (ProvenSourceFact(cp.Src) is var (cpLo, cpHi)
                    && RangeOfType(dv.Type) is var (cpSL, cpSH)
                    && cpLo >= cpSL && cpHi <= cpSH)
                {
                    // Only a value that already fits this slot's current type is a fact:
                    // one that does not is the width-seed mechanism still widening the slot
                    // across runs, not a true bound on the variable. Recording the slot's
                    // own (too-narrow) range here would make NoteStoreRange's accumulator
                    // check believe the overflow already fits, and the seed this store needs
                    // would never be filed -- see the InferredSlotWidthTests this broke.
                    variableRanges[dv.Name] = (cpLo, cpHi);
                }
                else variableRanges.Remove(dv.Name);
                break;
            case AugAssign { Target: Variable av }:
                variableRanges.Remove(av.Name);
                break;
            default:
                if (VariableWriteTarget(inst) is { } wn) variableRanges.Remove(wn);
                break;
        }
    }

    /// The named slot an instruction writes, when the write is not a Copy/AugAssign --
    /// those carry their own case. Dsts are almost always fresh temporaries; the switch
    /// exists for the rare fused emission that drops the fact instead of trusting it.
    private static string? VariableWriteTarget(Instruction inst) => inst switch
    {
        Binary b => (b.Dst as Variable)?.Name,
        Unary u => (u.Dst as Variable)?.Name,
        Bitcast b => (b.Dst as Variable)?.Name,
        Call c => (c.Dst as Variable)?.Name,
        IndirectCall ic => (ic.Dst as Variable)?.Name,
        BitCheck b => (b.Dst as Variable)?.Name,
        BitSet b => (b.Target as Variable)?.Name,
        BitClear b => (b.Target as Variable)?.Name,
        BitWrite b => (b.Target as Variable)?.Name,
        LoadIndirect l => (l.Dst as Variable)?.Name,
        ArrayLoad a => (a.Dst as Variable)?.Name,
        ArrayLoadFlash a => (a.Dst as Variable)?.Name,
        FlashLoadPtr f => (f.Dst as Variable)?.Name,
        BytearrayLoad b => (b.Dst as Variable)?.Name,
        GcAlloc g => (g.Dst as Variable)?.Name,
        _ => null,
    };

    private void NoteStore(string key, DataType slot, Val value, bool readsItself)
    {
        if (WidthSeeds == null || !WidthSeeds.IsInt(slot)) return;
        if (readsItself && value is Temporary gt && binaryOperands.TryGetValue(gt.Name, out var gops)
            && gops.Grows)
            WarnLoopAccumulator(key, slot);
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
            if (variableRanges.TryGetValue(key, out var proven))
            {
                // A proven value range names exactly what the slot can ever hold: this
                // store's range joined with what it held before is the narrowest honest
                // ask -- sign and width at once, where the operand-type join below had to
                // jump straight to int32 for a u16 slot that dips negative.
                need = NarrowestTypeFor(Math.Min(proven.Min, lo), Math.Max(proven.Max, hi));
            }
            else
            {
                // An accumulator answers for its sign and for the width of what feeds it, the way
                // `total = total + r` is as wide as r -- not for the promoted sum, which would ask
                // for one step more on every run.
                need = value is { } v && OperandsType(v, 0) is { } fed ? fed : slot;
                if (lo < 0 && !WidthSeeds.IsSigned(slot))
                    need = PyMCU.Common.WidthSeeds.Join(need, DataType.INT8);
            }
        }
        else need = NarrowestTypeFor(lo, hi);
        RequireSlot(key, slot, need);
    }

    // The operands of each arithmetic temporary, so an accumulator's store can be judged by
    // what feeds it rather than by the promoted result.
    private readonly Dictionary<string, (Val A, Val B, bool Grows)> binaryOperands = new();

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
        if (op is BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.LShift
            && operand is not Constant { Value: 0 })
            WarnLoopAccumulator(key, slot);
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

    // Slots already warned about, so a loop body lowered more than once says it once.
    private readonly HashSet<string> accumulatorWarned = new();

    /// A store that adds to its own unannotated slot inside a loop. No width holds every
    /// value a loop can count to, so the width the evidence chose stays -- and the program is
    /// told which one it is, where the store is, instead of wrapping in silence
    /// (`c = GPIOR0.value` then `c += 1` three hundred times printed 44). Only the program's
    /// own files: an installed library's accumulators are its author's decision. A 32-bit
    /// slot is the widest there is and says nothing.
    private void WarnLoopAccumulator(string key, DataType slot)
    {
        if (loopStack.Count == 0 || slot is DataType.INT32 or DataType.UINT32) return;
        if (!string.IsNullOrEmpty(currentSourcePath)
            && !projectModules.Any(m => modulePaths.TryGetValue(m, out var mp) && mp == currentSourcePath))
            return;
        if (!accumulatorWarned.Add(key)) return;
        string shown = key.StartsWith("F:") ? "self." + key[(key.LastIndexOf('.') + 1)..]
            : key.Contains('#') ? key[(key.LastIndexOf('#') + 1)..]
            : key[(key.LastIndexOf('.') + 1)..];
        var (lo, hi) = RangeOfType(slot);
        string t = slot.ToString().ToLowerInvariant();
        int line = inlineTracksCalleeLine && inlineCalleeStmtLine > 0 ? inlineCalleeStmtLine
            : currentStmtLine > 0 ? currentStmtLine : lastLine;
        Diagnostic.Warning(line: line, file: LocatedFile, code: "unannotated-accumulator", text:
            $"line {line}: '{shown}' has no annotation and this store adds to it inside a loop. "
            + $"It was given {t} ({lo}..{hi}) from what is stored into it, and a loop that counts "
            + $"past that wraps. Annotate it with the width it needs (`{shown}: uint16 = ...` or "
            + "wider) to say how far it counts.");
    }

    /// Whether <paramref name="e"/> reads the name <paramref name="name"/>.
    private static bool ReadsName(Expression? e, string name) =>
        e != null && TypeInference.WalkExpressionTree(e).Any(x => x is VariableExpr v && v.Name == name);
}
