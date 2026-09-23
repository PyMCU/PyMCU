/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

// Compile-time sequences: the one shape a driver uses to hold several pins, several
// devices or a table of numbers.
//
// There is no heap, and a ZCA instance is flattened to its constant fields, so a list of
// instances cannot be a run-time object. What it IS, everywhere, is a BASE KEY whose
// elements live at `<base>__0`, `<base>__1`, ... with the length in `arraySizes[base]`.
// `objs = [A(1), A(2)]` at module level already built that shape; this file makes the same
// shape reachable through a parameter and through a `self` field, which is how a driver is
// actually written:
//
//     class Bar:
//         def __init__(self, pins):
//             self._pins = pins            <- the field aliases the sequence base
//         def all_on(self):
//             for p in self._pins:         <- unrolls over <base>__k
//                 p.value = 1
//
//     Bar([Pin("PD5", Pin.OUT), Pin("PD6", Pin.OUT)])
//
// A list of NUMBERS is the other half: it has a run-time value, so it stays a
// constSequenceBindings entry (folded subscript, unrolled `for`, constant `len`) and the
// field records the same binding rather than becoming a scalar that reads zero.
public partial class IRGenerator
{
    // Resolves a bare name to the key its value is filed under, following the alias chain
    // to the end. The candidate order is the one every other lookup here uses: inline
    // expansion first, then the enclosing function, then the module, then the bare name.
    private string ResolveNameKey(string name)
    {
        string?[] candidates =
        {
            !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name : null,
            !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : null,
            !string.IsNullOrEmpty(currentModulePrefix) ? currentModulePrefix + name : null,
            name,
        };

        foreach (var candidate in candidates)
        {
            if (candidate == null) continue;
            // A candidate that exists AT ALL stops the search, including a plain local or
            // parameter. Skipping those let an inner scope fall through to an outer name that
            // happened to match: `UDR0.value = data` inside the UART HAL resolved its own byte
            // parameter `data` to the caller's array of the same name, and the register write
            // was replaced by an alias. The first scope that HAS the name is the one that owns
            // it, whatever it holds.
            if (!variableAliases.ContainsKey(candidate) && !instanceClasses.ContainsKey(candidate)
                && !arraySizes.ContainsKey(candidate) && !constSequenceBindings.ContainsKey(candidate)
                && !listLiteralParams.ContainsKey(candidate)
                && !variableTypes.ContainsKey(candidate) && !constantVariables.ContainsKey(candidate)
                && !bytearrayParams.Contains(candidate))
                continue;
            return FollowAliases(candidate);
        }

        return name;
    }

    private string FollowAliases(string key)
    {
        for (int depth = 0; depth < 20; depth++)
        {
            if (!variableAliases.TryGetValue(key, out var next) || next == null) break;
            key = next;
        }
        return key;
    }

    /// <summary>
    /// The key a sequence-valued expression is filed under: a bare name, or `obj.field`
    /// flattened to the `&lt;instance&gt;_&lt;field&gt;` key ZCA fields already use. Null for
    /// anything else. Emits nothing -- it is a name lookup, not an evaluation.
    /// </summary>
    private string? SequenceKeyOf(Expression e)
    {
        switch (e)
        {
            case VariableExpr ve:
                return ResolveNameKey(ve.Name);
            case MemberAccessExpr { Object: VariableExpr ov } mae:
            {
                string owner = ResolveNameKey(ov.Name);
                if (string.IsNullOrEmpty(owner)) return null;
                return FollowAliases(owner + "_" + mae.Member);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The flattened key of `obj.field` -- the same `&lt;instance&gt;_&lt;field&gt;` ZCA fields
    /// already use -- WITHOUT following the alias chain, so it names the field itself and not
    /// whatever it was previously pointed at. Null when the owner is not a name.
    /// </summary>
    private string? MemberFlatKey(MemberAccessExpr mae)
    {
        if (mae.Object is not VariableExpr ov) return null;
        string owner = ResolveNameKey(ov.Name);
        // Only a real instance HAS fields. A chip register is reached with the same syntax
        // (`UDR0.value = b`), and treating that as a field turned a register write into a
        // compile-time binding that emitted nothing.
        if (string.IsNullOrEmpty(owner) || !instanceClasses.ContainsKey(owner)) return null;
        return owner + "_" + mae.Member;
    }

    /// <summary>
    /// The class key that owns the receiver of `recv.field` -- `self` inside a method (inlined
    /// or outlined), or a named instance at module level. Null when the receiver has no class.
    /// </summary>
    private string? ClassObjectFieldOwner(MemberAccessExpr fieldAccess)
    {
        if (fieldAccess.Object is not VariableExpr recv) return null;
        string? recvCls = InstanceClassOfName(recv.Name);
        if (string.IsNullOrEmpty(recvCls))
            foreach (string? key in new[]
                     {
                         string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + recv.Name,
                         string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + recv.Name,
                         recv.Name,
                     })
            {
                if (key == null) continue;
                recvCls = ReceiverClassThroughAliases(key);
                if (!string.IsNullOrEmpty(recvCls)) break;
            }
        if (string.IsNullOrEmpty(recvCls) && recv.Name == "self")
        {
            // An outlined body has no inline self binding: ask the method's declared class.
            if (!string.IsNullOrEmpty(currentFunction)
                && methodInstanceTypes.TryGetValue(currentFunction, out var mt)) recvCls = mt;
            if (string.IsNullOrEmpty(recvCls) && inlineStack.Count > 0
                && !string.IsNullOrEmpty(inlineStack[^1].CalleeName)
                && methodInstanceTypes.TryGetValue(inlineStack[^1].CalleeName, out var mt2))
                recvCls = mt2;
            // Last resort: any recorded owner whose methods this function belongs to.
            if (string.IsNullOrEmpty(recvCls) && !string.IsNullOrEmpty(currentFunction))
                foreach (var kv in classObjectFields)
                {
                    int bar = kv.Key.IndexOf('|');
                    if (bar <= 0 || kv.Key[(bar + 1)..] != fieldAccess.Member) continue;
                    string ownerKey = kv.Key[..bar];
                    if (currentFunction.StartsWith(ownerKey + "_", StringComparison.Ordinal))
                    { recvCls = ownerKey; break; }
                }
        }
        return string.IsNullOrEmpty(recvCls) ? null : recvCls;
    }

    /// <summary>
    /// The candidate classes `recv.field` can hold as a class OBJECT, in the order its writes
    /// were scanned -- the index IS the tag stored in the field's layout byte. Null when the
    /// field is not a class-object field of the receiver's class.
    /// </summary>
    private List<string>? ClassObjectFieldClasses(MemberAccessExpr fieldAccess)
    {
        if (classObjectFields.Count == 0) return null;
        if (ClassObjectFieldOwner(fieldAccess) is not { } owner) return null;
        if (classObjectFields.TryGetValue(owner + "|" + fieldAccess.Member, out var cands)
            && cands.Count > 0)
            return cands;
        string resolved = ResolveCallee(owner);
        if (resolved != owner
            && classObjectFields.TryGetValue(resolved + "|" + fieldAccess.Member, out var cands2)
            && cands2.Count > 0)
            return cands2;
        return null;
    }

    /// <summary>
    /// `self.f = SomeClass`: record the bound class (scan may have missed a name that only
    /// resolves at lower time) and store its tag in the field's layout byte. A right side
    /// that is NOT a compile-time-known class is the spec'd refusal -- the field name and
    /// what it holds are both said, because silently compiling a scalar is how this bug
    /// looked before.
    /// </summary>
    private void EmitClassObjectFieldStore(MemberAccessExpr target, string flatKey,
                                           Expression rhs, string ownerCls)
    {
        string key = ownerCls + "|" + target.Member;
        if (ClassObjectExprClass(rhs) is not { } boundCls)
        {
            string held = classObjectFields.TryGetValue(key, out var heldCands)
                ? string.Join(", ", heldCands)
                : "a class object";
            throw UserError(
                $"field '{target.Member}' is bound to a class object ({held}) elsewhere; "
                + "this assignment is not a class known at compile time. Every write to a "
                + "class-object field must name a class.", rhs);
        }
        if (!classObjectFields.TryGetValue(key, out var cands))
            classObjectFields[key] = cands = new List<string>();
        int tag = cands.IndexOf(boundCls);
        if (tag < 0) { cands.Add(boundCls); tag = cands.Count - 1; }
        Emit(new Copy(new Constant(tag), new Variable(flatKey, DataType.UINT8)));
    }

    /// <summary>
    /// A compile-time sequence of ZCA instances reachable from <paramref name="e"/>: the base
    /// key and how many elements it has. The elements are `base__0`.., each registered in
    /// instanceClasses, which is what the `for` unroll and the constant subscript both index.
    /// </summary>
    private bool TryResolveInstanceSequence(Expression e, out string baseKey, out int count)
    {
        baseKey = "";
        count = 0;
        if (SequenceKeyOf(e) is not { } key) return false;
        if (!arraySizes.TryGetValue(key, out int n) || n <= 0) return false;
        if (!instanceClasses.ContainsKey(key + "__0")) return false;
        baseKey = key;
        count = n;
        return true;
    }

    /// <summary>
    /// The number of elements a compile-time sequence reachable from <paramref name="e"/>
    /// has -- the same set of shapes <c>len()</c> already folds in an expression context,
    /// answered here without emitting any IR so a size/address evaluator can ask for it.
    /// An instance-sequence FIELD (<c>self.i2c_device</c>, the <c>base__k</c> slots) counts:
    /// its length is fixed the moment the binding is recorded.
    /// </summary>
    private bool TryConstSeqLength(Expression e, out int count)
    {
        count = 0;
        switch (e)
        {
            case ListExpr le: count = le.Elements.Count; return true;
            case TupleExpr te: count = te.Elements.Count; return true;
            case Frontend.DictExpr de: count = de.Entries.Count; return true;
            case Frontend.SetExpr se: count = se.Elements.Count; return true;
            case StringLiteral sl: count = sl.Value.Length; return true;
            case MemberAccessExpr mem:
                if (TryResolveInstanceSequence(mem, out _, out int seqN)) { count = seqN; return true; }
                if (ResolveMemberArrayName(mem) is { } flat && arraySizes.TryGetValue(flat, out int msz))
                { count = LogicalArrayLen(flat, msz); return true; }
                if (ResolveConstSequenceExpr(mem) is { } mseq) { count = mseq.Count; return true; }
                return false;
            case VariableExpr ve:
                if (TryResolveInstanceSequence(ve, out _, out int isn)) { count = isn; return true; }
                if (TryGetDictBinding(ve.Name, out var db)) { count = db.Entries.Count; return true; }
                if (TryGetSetBinding(ve.Name, out var sb)) { count = sb.Elements.Count; return true; }
                if (ResolveListLiteralParam(ve.Name) is ListExpr lp) { count = lp.Elements.Count; return true; }
                if (ResolveConstSequence(ve.Name) is { } vseq) { count = vseq.Count; return true; }
                foreach (var k in new[]
                {
                    string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + ve.Name,
                    string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + ve.Name,
                    ve.Name,
                })
                {
                    if (k != null && arraySizes.TryGetValue(k, out int sz))
                    { count = LogicalArrayLen(k, sz); return true; }
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// The elements of a constant list/tuple reachable from <paramref name="e"/> -- through a
    /// name, through a parameter binding, or through a `self` field that was assigned one.
    /// </summary>
    private List<Expression>? ResolveConstSequenceExpr(Expression e)
    {
        if (e is VariableExpr nameVe)
        {
            // The parameter binding comes FIRST: it is the innermost scope, and it lives in a
            // different map from the named one, so the ordinary prefix walk cannot rank them.
            // With the walk first, `Table([10, 20, 30])` read a module-level `levels = [7, 8, 9]`
            // that merely shared the parameter's name, and both drivers reported the same table.
            if (ResolveListLiteralParam(nameVe.Name) is { } asParam) return asParam.Elements;
            if (ResolveConstSequence(nameVe.Name) is { } byName) return byName;
        }

        if (SequenceKeyOf(e) is { } key
            && constSequenceBindings.TryGetValue(key, out var elements))
            return elements;

        // `Cls.attr` where the attribute is a class-level tuple/list: SequenceKeyOf spells
        // the access `<recv>_<member>`, but the binding is filed under the class's own
        // prefix (`adafruit_seesaw_samd09_SAMD09_Pinmap_analog_pins`). ClassAttrKey walks
        // classModuleMap the same way a dict attribute already does.
        if (e is MemberAccessExpr { Object: VariableExpr cov } cam
            && ClassNameOf(cov) is { } camCls
            && constSequenceBindings.TryGetValue(ClassAttrKey(camCls, cam.Member),
                                                 out var camSeq))
            return camSeq;
        // `self.POSITIONS[i]` on a subclass: the tuple is filed under the class that
        // DECLARED it (`_AbstractSeg7x4.POSITIONS` on Seg7x4's base), so the receiver's
        // own prefix misses it. Walk the MRO exactly as TryGetDictFor does.
        if (e is MemberAccessExpr { Object: VariableExpr seqOv } seqMa
            && (ReceiverNameForLookup(seqOv) ?? ResolveNameKey(seqOv.Name)) is { } seqBase
            && ReceiverClassThroughAliases(seqBase) is { } seqCls)
        {
            string? seqCur = seqCls;
            for (int depth = 0; seqCur != null && depth < 20; depth++)
            {
                foreach (var cand in new[]
                {
                    classModuleMap.TryGetValue(seqCur, out var seqPfx)
                        ? seqPfx + seqCur + "_" + seqMa.Member : null,
                    seqCur + "_" + seqMa.Member,
                    ClassAttrKey(seqCur, seqMa.Member),
                })
                {
                    if (cand != null && constSequenceBindings.TryGetValue(cand, out var seqFound))
                        return seqFound;
                }
                seqCur = BaseClassOf(seqCur);
            }
        }
        if (e is MemberAccessExpr { Object: MemberAccessExpr { Object: VariableExpr modV } modCls } mcm)
        {
            // `module.Cls.attr`: resolve the module-qualified class name, then the
            // attribute under its own prefix.
            string realMod = TryImportedAlias(modV.Name, out var rm) && rm != null
                ? rm : modV.Name;
            string mangledCls = realMod.Replace('.', '_') + "_" + modCls.Member;
            if ((classFieldLayout.ContainsKey(mangledCls) || classDirectMethods.ContainsKey(mangledCls))
                && constSequenceBindings.TryGetValue(mangledCls + "_" + mcm.Member, out var mcmSeq))
                return mcmSeq;
        }
        return null;
    }

    /// <summary>
    /// The dict literal an expression denotes: a bare name, or a `self` field that was
    /// assigned one. Emits nothing.
    /// </summary>
    private bool TryGetDictFor(Expression e, out Frontend.DictExpr dict)
    {
        if (e is VariableExpr ve && TryGetDictBinding(ve.Name, out dict!)) return true;
        dict = null!;
        if (e is not MemberAccessExpr mae) return false;
        if (SequenceKeyOf(e) is { } key && dictLiteralBindings.TryGetValue(key, out dict!))
            return true;
        // A class-level dict (`gain_values = {ALS_GAIN_2: 2, ...}`) is filed under the
        // class prefix, not the instance-flattened SequenceKeyOf. `self.gain_values[k]`
        // has to find it the same way a descriptor finds a class attribute.
        if (mae.Object is VariableExpr ov)
        {
            if (ClassNameOf(ov) is { } clsOwner)
            {
                string classDictKey = ClassAttrKey(clsOwner, mae.Member);
                if (dictLiteralBindings.TryGetValue(classDictKey, out dict!))
                    return true;
            }
            string? baseName = ReceiverNameForLookup(ov) ?? ResolveNameKey(ov.Name);
            if (TryFindClassAttribute(baseName, mae.Member, out _, out var fullName)
                && dictLiteralBindings.TryGetValue(fullName, out dict!))
                return true;
            if (ReceiverClassThroughAliases(baseName ?? ov.Name) is { } cls)
            {
                string? cur = cls;
                for (int depth = 0; cur != null && depth < 20; depth++)
                {
                    foreach (var cand in new[]
                    {
                        classModuleMap.TryGetValue(cur, out var pfx) ? pfx + cur + "_" + mae.Member : null,
                        cur + "_" + mae.Member,
                    })
                    {
                        if (cand != null && dictLiteralBindings.TryGetValue(cand, out dict!))
                            return true;
                    }
                    cur = BaseClassOf(cur);
                }
            }
        }
        return false;
    }

    /// <summary>The set literal an expression denotes, by name or through a field.</summary>
    private bool TryGetSetFor(Expression e, out Frontend.SetExpr set)
    {
        if (e is VariableExpr ve && TryGetSetBinding(ve.Name, out set!)) return true;
        set = null!;
        if (e is not MemberAccessExpr) return false;
        return SequenceKeyOf(e) is { } key && setLiteralBindings.TryGetValue(key, out set!);
    }

    /// <summary>
    /// The class a constructor call builds, or null when the call is not a constructor of a
    /// class with a field layout. Both spellings count: the bare `Pin(...)` and the dotted
    /// `digitalio.DigitalInOut(...)` a CircuitPython program writes.
    /// </summary>
    private string? CtorClassOfCall(Expression e)
    {
        if (e is not CallExpr call) return null;

        switch (call.Callee)
        {
            case VariableExpr cv:
            {
                string resolved = ResolveCallee(cv.Name);
                return classFieldLayout.ContainsKey(resolved) ? resolved : null;
            }
            case MemberAccessExpr { Object: VariableExpr mv } cm:
            {
                // `digitalio.DigitalInOut(...)`: the module qualifier says where the class came
                // from. Try the mangled module name the call path itself builds, then the bare
                // member, so a class re-exported under an alias still resolves.
                string realMod = TryImportedAlias(mv.Name, out var rm) && rm != null
                    ? rm : mv.Name;
                string mangled = realMod.Replace('.', '_') + "_" + cm.Member;
                if (classFieldLayout.ContainsKey(mangled)) return mangled;
                string bare = ResolveCallee(cm.Member);
                return classFieldLayout.ContainsKey(bare) ? bare : null;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The class an annotation names, or null when it does not name one. Both spellings
    /// count: the bare <c>DigitalInOut</c> and the dotted <c>digitalio.DigitalInOut</c>
    /// a CircuitPython parameter is written with.
    /// </summary>
    private string? ClassKeyFromAnnotation(string type)
    {
        if (string.IsNullOrEmpty(type) || IsTypingOnlyName(type)) return null;
        if (classFieldLayout.ContainsKey(type)) return type;
        int lastDot = type.LastIndexOf('.');
        if (lastDot > 0)
        {
            string head = type[..type.IndexOf('.')];
            string tail = type[(lastDot + 1)..];
            string realMod = TryImportedAlias(head, out var rm) && rm != null ? rm : head;
            string mangled = realMod.Replace('.', '_') + "_" + tail;
            if (classFieldLayout.ContainsKey(mangled)) return mangled;
            string bare = ResolveCallee(tail);
            if (classFieldLayout.ContainsKey(bare)) return bare;
            foreach (var c in classFieldLayout.Keys)
                if (c.EndsWith("_" + tail, StringComparison.Ordinal)) return c;
        }
        string resolved = ResolveCallee(type);
        if (classFieldLayout.ContainsKey(resolved)) return resolved;
        foreach (var c in classFieldLayout.Keys)
            if (c == type || c.EndsWith("_" + type, StringComparison.Ordinal)) return c;
        return null;
    }

    /// <summary>
    /// True when every element of the literal denotes a ZCA instance: a constructor call, or a
    /// name already bound to one. `[Pin("PD5", Pin.OUT), Pin("PD6", Pin.OUT)]` and `[a, b]` both
    /// qualify; `[1, 2, 3]` does not, and neither does a mixture.
    /// </summary>
    private bool IsInstanceSequenceLiteral(ListExpr lit)
    {
        if (lit.Elements.Count == 0) return false;
        foreach (var element in lit.Elements)
        {
            if (CtorClassOfCall(element) != null) continue;
            if (element is VariableExpr ve)
            {
                string key = ResolveNameKey(ve.Name);
                if (instanceClasses.ContainsKey(key)) continue;
                if (AliasedInstanceName(key) is { } aliased && instanceClasses.ContainsKey(aliased))
                    continue;
                return false;
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// The values a comprehension's loop variable takes, as expressions, or null when the
    /// iterable is not a compile-time sequence. A filter or a second iterable disqualifies it:
    /// both decide the length somewhere this cannot see.
    /// </summary>
    private List<Expression>? ComprehensionValues(ListCompExpr comp)
    {
        if (comp.Filter != null || comp.Iterable2 != null) return null;
        switch (comp.Iterable)
        {
            case ListExpr le: return le.Elements.Count > 0 ? le.Elements : null;
            case TupleExpr te: return te.Elements.Count > 0 ? te.Elements : null;
            case VariableExpr ve:
                if (ResolveListLiteralParam(ve.Name) is { } lp && lp.Elements.Count > 0)
                    return lp.Elements;
                if (ResolveConstSequence(ve.Name) is { Count: > 0 } cs) return cs;
                return null;
            case CallExpr { Callee: VariableExpr { Name: "range" } } rc when rc.Args.Count is 1 or 2 or 3:
            {
                var bounds = new List<int>();
                foreach (var a in rc.Args)
                {
                    if (a is IntegerLiteral il) bounds.Add(il.Value);
                    else if (a is UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral n })
                        bounds.Add(-n.Value);
                    else return null;
                }
                int start = bounds.Count > 1 ? bounds[0] : 0;
                int stop = bounds.Count > 1 ? bounds[1] : bounds[0];
                int step = bounds.Count > 2 ? bounds[2] : 1;
                if (step == 0) return null;
                var values = new List<Expression>();
                for (int v = start; step > 0 ? v < stop : v > stop; v += step)
                {
                    values.Add(new IntegerLiteral(Math.Abs(v)) is var _ && v >= 0
                        ? new IntegerLiteral(v)
                        : (Expression)new UnaryExpr(Frontend.UnaryOp.Negate, new IntegerLiteral(-v)));
                    if (values.Count > 64) return null;
                }
                return values.Count > 0 ? values : null;
            }
            default: return null;
        }
    }

    /// <summary>
    /// True when the comprehension builds instances over a compile-time sequence, which is the
    /// literal of constructions written once instead of N times.
    /// </summary>
    private bool IsInstanceComprehension(ListCompExpr comp)
        => CtorClassOfCall(comp.Element) != null && ComprehensionValues(comp) != null;

    /// <summary>
    /// Builds the elements of an instance COMPREHENSION once, in the scope it was written in,
    /// binding the loop variable to each compile-time value in turn, and returns the base key.
    /// Nothing is substituted: the element expression is visited once per value with the loop
    /// variable bound, which is what the unrolled `for` over the same sequence already does.
    /// </summary>
    private string HoistInstanceComprehension(ListCompExpr comp)
    {
        var values = ComprehensionValues(comp)!;
        string name = "__ctseq" + (++ctSequenceCounter);
        string baseKey = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);
        string loopKey = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + comp.VarName
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + comp.VarName : comp.VarName);

        string savedCtorTarget = pendingConstructorTarget;
        pendingConstructorTarget = "";
        bool hadConst = constantVariables.TryGetValue(loopKey, out int savedConst);
        bool hadStr = strConstantVariables.TryGetValue(loopKey, out var savedStr);

        for (int k = 0; k < values.Count; k++)
        {
            BindComprehensionVar(loopKey, values[k]);
            VisitStatement(new AssignStmt(new VariableExpr(name + "__" + k), comp.Element));
        }

        constantVariables.Remove(loopKey);
        strConstantVariables.Remove(loopKey);
        if (hadConst) constantVariables[loopKey] = savedConst;
        if (hadStr) strConstantVariables[loopKey] = savedStr!;
        pendingConstructorTarget = savedCtorTarget;

        arraySizes[baseKey] = values.Count;
        bufferLogicalLen[baseKey] = values.Count;
        arrayElemTypes[baseKey] = DataType.UINT8;
        return baseKey;
    }

    private void BindComprehensionVar(string loopKey, Expression value)
    {
        constantVariables.Remove(loopKey);
        strConstantVariables.Remove(loopKey);
        switch (value)
        {
            case IntegerLiteral il: constantVariables[loopKey] = il.Value; break;
            case BooleanLiteral bl: constantVariables[loopKey] = bl.Value ? 1 : 0; break;
            case UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral n }:
                constantVariables[loopKey] = -n.Value; break;
            case StringLiteral sl: strConstantVariables[loopKey] = sl.Value; break;
            default:
                if (VisitExpression(value) is Constant c) constantVariables[loopKey] = c.Value;
                break;
        }
    }

    // Names the hoisted sequences apart from anything a user can write.
    private int ctSequenceCounter;

    /// <summary>
    /// Builds the elements of an instance-list literal ONCE, in the scope the literal was
    /// written in, and returns the base key they were filed under. Without this the literal
    /// stays raw AST bound to the parameter and every subscript re-evaluates it: `ps[0]`
    /// constructed a second, anonymous Pin, so `ps[0].value = 1` configured a pin nobody could
    /// read back and the write looked like it had vanished.
    /// </summary>
    private string HoistInstanceSequence(ListExpr lit)
    {
        string name = "__ctseq" + (++ctSequenceCounter);
        string baseKey = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);

        // The enclosing assignment's target must not claim these constructions: `b = Bar([Pin(),
        // Pin()])` would register both pins as `main.b` and the sequence would have no elements
        // to find. Same save/restore the ordinary argument evaluation does.
        string savedCtorTarget = pendingConstructorTarget;
        pendingConstructorTarget = "";

        for (int k = 0; k < lit.Elements.Count; k++)
        {
            var element = lit.Elements[k];
            string elementKey = baseKey + "__" + k;
            if (element is VariableExpr ve && instanceClasses.ContainsKey(ResolveNameKey(ve.Name)))
            {
                // Already an instance: carry it, do not rebuild it. The alias keeps the ONE
                // instance the caller named, so a write through the sequence and a write
                // through the original name land on the same pin. The compile-time state has
                // to come with it: the `for` unroll reads the element's OWN fields, and with
                // only the alias a nested instance (a DigitalInOut's `_pin`) lost its constant
                // bit and the write was refused as a run-time bit index.
                string src = ResolveNameKey(ve.Name);
                PropagateCtState(src, elementKey);
                variableAliases[elementKey] = src;
                continue;
            }

            VisitStatement(new AssignStmt(new VariableExpr(name + "__" + k), element));
        }

        pendingConstructorTarget = savedCtorTarget;
        arraySizes[baseKey] = lit.Elements.Count;
        bufferLogicalLen[baseKey] = lit.Elements.Count;
        arrayElemTypes[baseKey] = DataType.UINT8;
        return baseKey;
    }

    /// <summary>
    /// A compile-time sequence bound to a parameter has no storage of its own: the binding
    /// exists so `for x in p`, `p[k]` and `len(p)` fold without a buffer. A real subroutine
    /// indexes SRAM at the address it is handed, so handing it the bare name passed whatever
    /// the slot happened to hold -- `i2c.write(bytes([register & 0xFF]))` in adafruit_bmp280
    /// sent 0x00 as the register pointer because `register` had folded to a constant and the
    /// sequence bound without a buffer behind it. Lay the elements out under a hidden name
    /// and answer its base.
    /// </summary>
    private ArrayBase? MaterializeSequenceArg(List<Expression> elements)
    {
        var values = new List<int>(elements.Count);
        foreach (var e in elements)
        {
            if (!TryEvalElemConst(e, out int v)) return null;
            values.Add(v);
        }

        string name = "__seqarg" + (++ctSequenceCounter);
        string key = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);
        // An empty sequence still needs a base to hand the callee -- `writeto(addr, b"")`
        // probes with a zero-length buffer that is never read but must still address
        // somewhere.
        int size = Math.Max(values.Count, 1);
        arraySizes[key] = values.Count;
        bufferLogicalLen[key] = values.Count;
        arrayElemTypes[key] = DataType.UINT8;
        variableTypes[key] = DataType.UINT8;
        arraysWithVariableIndex.Add(key);
        for (int k = 0; k < size; ++k)
            Emit(new ArrayStore(key, new Constant(k), new Constant(k < values.Count ? values[k] : 0),
                DataType.UINT8, size));
        return new ArrayBase(key);
    }

    /// <summary>
    /// Points <paramref name="targetKey"/> at the compile-time sequence <paramref name="baseKey"/>,
    /// clearing the scalar bindings a previous call at the same key may have left. The sequence
    /// itself is not copied: a field and a parameter are two names for the same elements.
    /// </summary>
    private void BindSequenceAlias(string targetKey, string baseKey)
    {
        variableAliases[targetKey] = baseKey;
        constantVariables.Remove(targetKey);
        strConstantVariables.Remove(targetKey);
        floatConstantVariables.Remove(targetKey);
        listLiteralParams.Remove(targetKey);
        constSequenceBindings.Remove(targetKey);
    }

    /// <summary>
    /// Alias <paramref name="targetKey"/> to array storage AND copy the size
    /// (and view window, if any) onto the new name. A second <c>super()</c>
    /// hop receives <c>VisitVariable</c> as the parameter's own key, not the
    /// view; without the copied size that hop Copies a scalar and
    /// <c>len(framebuf.buf)</c> dies (ssd1306: I2C -> _SSD1306 -> FrameBuffer).
    /// </summary>
    private void BindArrayAlias(string targetKey, string baseKey)
    {
        string src = baseKey;
        if (!arraySizes.ContainsKey(src) && TryResolveArrayStorageKey(src, out var sk))
            src = sk;
        src = FollowAliases(src);
        BindSequenceAlias(targetKey, src);
        if (!arraySizes.TryGetValue(src, out int n)) return;
        arraySizes[targetKey] = n;
        bufferLogicalLen[targetKey] = LogicalArrayLen(src, n);
        if (arrayElemTypes.TryGetValue(src, out var dt))
            arrayElemTypes[targetKey] = dt;
        if (arrayViewBase.TryGetValue(src, out var vb))
        {
            arrayViewBase[targetKey] = vb;
            arrayViewOffset[targetKey] = arrayViewOffset.TryGetValue(src, out var vo) ? vo : 0;
        }
        if (arraysWithVariableIndex.Contains(src) || arrayViewBase.ContainsKey(src)
            || arrayViewBase.ContainsKey(targetKey))
            arraysWithVariableIndex.Add(targetKey);
        if (bytearrayParams.Contains(src)) bytearrayParams.Add(targetKey);
    }

    private bool TryArraySource(string name, out string src)
    {
        src = FollowAliases(name);
        if (arraySizes.ContainsKey(src)) return true;
        if (TryResolveArrayStorageKey(src, out var sk) && arraySizes.ContainsKey(sk))
        {
            src = sk;
            return true;
        }
        if (arraySizes.ContainsKey(name))
        {
            src = name;
            return true;
        }
        if (TryResolveArrayStorageKey(name, out sk) && arraySizes.ContainsKey(sk))
        {
            src = sk;
            return true;
        }
        return false;
    }

    /// <summary>
    /// `t = f()` where f's expansion just delivered a tuple through
    /// <c>lastTupleResults</c>: materialise one fixed slot per element --
    /// `t__0`, `t__1`, ... -- and register the name as a tuple-valued variable.
    /// The values are COPIED out of the call's `iret_` slots, which are shared
    /// scratch reused by the next call at the same depth; aliasing them would
    /// let a later `g()` overwrite what `t` still names.
    /// </summary>
    private void BindNamedTuple(string name)
    {
        string key = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction)
                ? currentFunction + "." + name : name);

        variableAliases.Remove(key);
        constantVariables.Remove(key);
        strConstantVariables.Remove(key);
        floatConstantVariables.Remove(key);
        listLiteralParams.Remove(key);
        constSequenceBindings.Remove(key);

        var elems = new List<string>();
        DataType widest = DataType.UINT8;
        for (int k = 0; k < lastTupleResults.Count; ++k)
        {
            string src = lastTupleResults[k];
            string dst = key + "__" + k;
            DataType dt = variableTypes.TryGetValue(src, out var sdt)
                ? sdt
                : constantVariables.TryGetValue(src, out int srcConst)
                    ? WidestElemType(new List<int> { srcConst })
                    : DataType.UINT8;
            Emit(new Copy(new Variable(src, dt), new Variable(dst, dt)));
            variableTypes[dst] = dt;
            if (constantVariables.TryGetValue(src, out int cv)) constantVariables[dst] = cv;
            else constantVariables.Remove(dst);
            if (dt == DataType.UINT16 || dt == DataType.INT16) widest = DataType.UINT16;
            else if (dt == DataType.UINT32 || dt == DataType.INT32) widest = DataType.UINT32;
            else if (dt == DataType.FLOAT) widest = DataType.FLOAT;
            elems.Add(dst);
        }
        namedTupleElements[key] = elems;
        arraySizes[key] = elems.Count;
        bufferLogicalLen[key] = elems.Count;
        arrayElemTypes[key] = widest;
    }

    /// <summary>
    /// The element expressions of a name bound to a tuple return (`t = f()`), or null for
    /// any other name. Each element is spelled `t__k` -- the LOCAL spelling, not the slot's
    /// qualified key: a VariableExpr re-resolves through the current prefix chain, and an
    /// already-qualified name would double-prefix (`main.main.t__0`).
    /// </summary>
    private List<Expression>? NamedTupleElemsOf(string name)
    {
        string key = ResolveNameKey(name);
        if (!namedTupleElements.TryGetValue(key, out var slots)) return null;
        return slots.Select((_, k) => (Expression)new VariableExpr(name + "__" + k)).ToList();
    }
}
