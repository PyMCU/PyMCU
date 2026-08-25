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

using PyMCU.Common;
using PyMCU.Frontend;
using PyMCU.IR;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    private void VisitAssign(AssignStmt stmt)
    {
        // Assigning to a plain name binds it, whatever the right-hand side turns out to be and
        // whichever of the shapes below claims the statement. The undefined-name check reads
        // this: an unannotated `x = f()` files no type anywhere, and without the record a later
        // read of `x` would look exactly like a typo.
        if (stmt.Target is VariableExpr bindTgt)
            boundNames.Add(!string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + bindTgt.Name
                : (!string.IsNullOrEmpty(currentFunction)
                    ? currentFunction + "." + bindTgt.Name
                    : bindTgt.Name));

        // `f = a` where `a` is a function: bind the NAME, do not evaluate it as a value. A
        // function could be passed as a Callable argument but not stored, and `f()` then said
        // "'f' is not callable (it is a value, not a function)" -- which is what the compiler
        // had made of it. The binding is compile-time, so the later call is direct and costs
        // nothing; a run-time function pointer is what funcref() is for.
        if (stmt.Target is VariableExpr fnTgt && stmt.Value is VariableExpr fnSrc)
        {
            string srcKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + fnSrc.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + fnSrc.Name : fnSrc.Name);
            bool srcIsVariable = variableTypes.ContainsKey(srcKey) || variableTypes.ContainsKey(fnSrc.Name)
                || mutableGlobals.ContainsKey(currentModulePrefix + fnSrc.Name)
                || constantVariables.ContainsKey(srcKey);
            if (!srcIsVariable)
            {
                string resolvedFn = ResolveCallee(fnSrc.Name);
                if (functionParams.ContainsKey(resolvedFn) || inlineFunctions.ContainsKey(resolvedFn))
                {
                    string tgtKey = !string.IsNullOrEmpty(currentInlinePrefix)
                        ? currentInlinePrefix + fnTgt.Name
                        : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + fnTgt.Name : fnTgt.Name);

                    // The binding is compile-time, so it can only mean ONE function. Rebinding
                    // the name to a different one, or binding it inside a runtime branch, is a
                    // dispatch table: taking the last binding seen would compile a program that
                    // silently ignores the condition, which is worse than refusing it.
                    bool rebound = loopFunctionAliases.TryGetValue(tgtKey, out var already)
                                   && already != resolvedFn;
                    if (rebound || _runtimeBranchDepth > 0)
                        throw UserError(
                            $"'{fnTgt.Name}' is bound to a function at compile time, so it cannot "
                            + (rebound
                                ? $"be rebound to a different one ('{already}' then '{resolvedFn}')."
                                : "be bound inside a run-time branch.")
                            + " For a dispatch table, declare the parameter or array as Callable "
                            + "and pass the function, or take its address with funcref().");

                    loopFunctionAliases[tgtKey] = resolvedFn;
                    boundNames.Add(tgtKey);
                    return;
                }
            }
        }

        // A name declared with a `const[...]` annotation is immutable; reassigning it is a
        // user error (previously this was silently accepted, overwriting the constant).
        if (stmt.Target is VariableExpr constTgt && declaredConstants.Contains(constTgt.Name))
            throw UserError($"cannot assign to constant '{constTgt.Name}' (declared const)");

        // `s = f"..."` with runtime interpolations: expand into a fixed buffer + strfmt calls.
        if (stmt.Target is VariableExpr fsvTgt && TryExpandFStringValue(fsvTgt.Name, stmt.Value))
            return;

        // `s = sep.join([...])`: constant fold for all-static strings, and the canonical
        // bytes-to-string idiom `''.join([chr(b) for b in buf])` as a runtime string.
        if (stmt.Target is VariableExpr joinTgt && TryEmitJoinAssign(joinTgt.Name, stmt.Value))
            return;

        // `msg = "hello"`: remember the text against the name. Only the ANNOTATED form
        // (`msg: str = "hello"`) recorded it, so print(msg) could not tell this was a string
        // and streamed the flash id as a decimal number: the program printed 256 for "hello",
        // clean build, no diagnostic. Re-binding the name to anything else clears it.
        if (stmt.Target is VariableExpr strTgt)
        {
            string strKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + strTgt.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + strTgt.Name : strTgt.Name);
            if (stmt.Value is StringLiteral strLit)
                strConstantVariables[strKey] = strLit.Value;
            else
                strConstantVariables.Remove(strKey);
        }

        // `objs = [A(s), A(s + 1)]`: a list of instances. Build each element as an instance of
        // its own under `<name>__<k>` and record the count, which is the shape `for o in objs`
        // already knows how to unroll. Written straight into the `for` the same literal is
        // rejected ("elements must be compile-time integer constants"); through a name it used
        // to compile and read every field as zero, because nothing ever constructed the
        // elements and the loop copied out of an array that was never filled.
        if (stmt.Target is VariableExpr instSeqTgt && stmt.Value is ListExpr instSeqList
            && instSeqList.Elements.Count is > 0 and <= ConstSequenceUnrollLimit
            && instSeqList.Elements.All(e => e is CallExpr { Callee: VariableExpr ce }
                                             && classFieldLayout.ContainsKey(ResolveCallee(ce.Name))))
        {
            string instSeqKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + instSeqTgt.Name
                : (!string.IsNullOrEmpty(currentFunction)
                    ? currentFunction + "." + instSeqTgt.Name
                    : instSeqTgt.Name);

            for (int k = 0; k < instSeqList.Elements.Count; k++)
                VisitStatement(new AssignStmt(
                    new VariableExpr(instSeqTgt.Name + "__" + k), instSeqList.Elements[k]));

            arraySizes[instSeqKey] = instSeqList.Elements.Count;
            arrayElemTypes[instSeqKey] = DataType.UINT8;
            return;
        }

        // `pins = [11, 12, 13]` / `(11, 12, 13)`: remember the elements against the name so a
        // later `for p in pins:` unrolls, which is what the same literal written inline at the
        // `for` already does. Only short, all-constant sequences qualify -- past that the loop
        // is better off as a loop, and a non-constant element has no compile-time value to bind.
        if (stmt.Target is VariableExpr seqTgt && stmt.Value is ListExpr or TupleExpr)
        {
            var seqElements = stmt.Value is ListExpr sl ? sl.Elements : ((TupleExpr)stmt.Value).Elements;
            string seqKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + seqTgt.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + seqTgt.Name : seqTgt.Name);
            if (seqElements.Count is > 0 and <= ConstSequenceUnrollLimit
                && seqElements.All(e => TryEvalConstElement(e, out _)))
            {
                constSequenceBindings[seqKey] = seqElements;

                // A tuple has no run-time value on this target, so evaluating the right-hand
                // side would reject the program ("tuples are not supported as runtime
                // values"). The binding IS the whole meaning of the statement: record it and
                // emit nothing, the way a dict or set literal binding does.
                if (stmt.Value is TupleExpr) return;
            }
            else
            {
                constSequenceBindings.Remove(seqKey);
            }
        }

        // `d = {...}` binds a compile-time lookup table (dict) or membership set: register
        // the literal AST against the name; nothing runs at runtime.
        if (stmt.Target is VariableExpr dsTgt && stmt.Value is DictExpr or SetExpr)
        {
            RegisterDictSetBinding(dsTgt.Name, stmt.Value);
            return;
        }

        // Unannotated `name = bytearray(N)` / `= bytearray([...])`: MicroPython declares
        // buffers without an annotation. Route through the VarDecl path so the fixed
        // buffer is laid out instead of evaluating bytearray() as a runtime call.
        if (stmt.Target is VariableExpr baTgt
            && stmt.Value is CallExpr { Callee: VariableExpr { Name: "bytearray" } })
        {
            VisitVarDecl(new VarDecl(baTgt.Name, "bytearray", stmt.Value) { Line = stmt.Line });
            return;
        }

        // Mutating a dict-literal binding (`d[k] = v`) has no runtime structure to write to.
        if (stmt.Target is IndexExpr { Target: VariableExpr mutVe }
            && TryGetDictBinding(mutVe.Name, out _))
            throw UserError(
                $"'{mutVe.Name}' is a compile-time dict literal (read-only lookup table). " +
                "For a mutable dict use pymcu.collections.FixedDict(capacity) -- fixed " +
                "footprint, no heap.");

        // A65: when `c = a OP b` invokes an operator dunder that returns a SLOT-class instance,
        // the result is built as a Model-A (flattened) instance, so a later method call on c
        // passes the fields where a self pointer is expected. Remember the target/class so that,
        // after the construction, c is materialized into a real slot (see end of VisitAssign).
        string slotMatName = "";    // unqualified target name
        string slotMatCls = "";     // slot class of the result

        // RFC 0001 Model B (SRAM slot): `s = MultiFieldZCA(a, b)`. Box the instance into a
        // fixed SRAM slot and store each field at its offset. Handled as a self-contained
        // path (early return) so it never touches the virtual-constructor machinery.
        if (stmt.Target is VariableExpr slotTgt && stmt.Value is CallExpr slotCall
            && slotCall.Callee is VariableExpr slotCallee
            && slotClasses.Contains(ResolveCallee(slotCallee.Name)))
        {
            string slotCls = ResolveCallee(slotCallee.Name);
            if (classInitCallsSuper.Contains(slotCls))
            {
                // The ctor delegates to super().__init__(): the positional slot fill can't see a
                // base-set field (it has no param of this class). Run the real __init__ via the
                // normal (flattened) constructor machinery -- where super expansion works -- then
                // materialize the resulting fields into the slot (see the slotMat hook below).
                slotMatName = slotTgt.Name;
                slotMatCls = slotCls;
            }
            else
            {
                // The positional EmitSlotConstruction shortcut only fills fields that
                // are initialised directly from a constructor parameter (self.x = p).
                // If any field is a constant or a computed expression (self.y = 100000,
                // self.z = a + b), run the REAL __init__ via the flattened machinery and
                // materialize the fields into the slot, so they get their actual values.
                bool allFromParam = classFieldLayout.TryGetValue(slotCls, out var slotLay)
                    && slotLay.Count > 0
                    && slotLay.All(f => !string.IsNullOrEmpty(f.SourceParam));
                if (allFromParam)
                {
                    EmitSlotConstruction(slotTgt, slotCls, slotCall.Args);
                    return;
                }
                slotMatName = slotTgt.Name;
                slotMatCls = slotCls;
            }
        }

        // RFC 0001 Model B (sret): `s = make(args)` where make is a non-@inline factory
        // returning a MULTI-field (slot) ZCA. The caller allocates the slot, passes its address
        // as the hidden __self pointer, and tracks s as a slot instance. (Single-field factories
        // return a register handle instead -- handled in the factory block below.)
        if (stmt.Target is VariableExpr sfTgt && stmt.Value is CallExpr sfCall
            && sfCall.Callee is VariableExpr sfCallee)
        {
            string sfFn = ResolveCallee(sfCallee.Name);
            if (functionReturnTypes.TryGetValue(sfFn, out var sfRt) && sfRt != null
                && slotClasses.Contains(sfRt) && !inlineFunctions.ContainsKey(sfFn))
            {
                EmitSlotFactoryCall(sfTgt, sfFn, sfRt, sfCall.Args);
                return;
            }
        }

        // RFC 0001 Model B (Class[N]): `arr[i] = C(args)` constructs into element i of an
        // instance array -- store each field at i*stride + offset. Constant index uses a flat
        // ArrayStore; a runtime index computes the element address and stores through it.
        if (stmt.Target is IndexExpr ciTgt && ciTgt.Target is VariableExpr ciArr
            && stmt.Value is CallExpr ciCall && ciCall.Callee is VariableExpr ciCallee)
        {
            string ciQ = string.IsNullOrEmpty(currentFunction) ? ciArr.Name : currentFunction + "." + ciArr.Name;
            if (!instanceArrayClass.ContainsKey(ciQ) && instanceArrayClass.ContainsKey(ciArr.Name)) ciQ = ciArr.Name;
            if (instanceArrayClass.TryGetValue(ciQ, out var ciCls)
                && ResolveCallee(ciCallee.Name) == ciCls)
            {
                EmitInstanceArrayStore(ciQ, ciCls, ciTgt.Index, ciCall.Args);
                return;
            }
        }

        // `b = a[lo:hi]` with no array annotation: infer `b` as a fixed-size array of the slice's
        // length and copy its elements (Python needs no annotation). Without this the slice built
        // element temps that were never bound to `b`, so a later `b[i]` silently read 0.
        if (stmt.Target is VariableExpr sliceTgt
            && stmt.Value is IndexExpr { Index: SliceExpr sliceIdx, Target: VariableExpr sliceSrc }
            && TryEmitInferredSliceArray(sliceTgt, sliceSrc, sliceIdx))
            return;

        if (stmt.Target is IndexExpr indexExpr) { EmitIndexAssign(stmt, indexExpr); return; }

        if (stmt.Target is VariableExpr varExprCtor) { EmitConstructorTargetSetup(stmt, varExprCtor); }

        if (!string.IsNullOrEmpty(pendingConstructorTarget))
        {
        }
        else if (stmt.Target is VariableExpr varExprBin)
        {
            if (stmt.Value is BinaryExpr binExpr)
            {
                VariableExpr? lhsVar = binExpr.Left as VariableExpr;
                if (lhsVar != null)
                {
                    string lhsQ = !string.IsNullOrEmpty(currentInlinePrefix)
                        ? currentInlinePrefix + lhsVar.Name
                        : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + lhsVar.Name : lhsVar.Name);
                    if (instanceClasses.TryGetValue(lhsQ, out var cls))
                    {
                        string dunder = binExpr.Op switch
                        {
                            Frontend.BinaryOp.Add => "__add__",
                            Frontend.BinaryOp.Sub => "__sub__",
                            Frontend.BinaryOp.Mul => "__mul__",
                            Frontend.BinaryOp.FloorDiv => "__floordiv__",
                            Frontend.BinaryOp.Mod => "__mod__",
                            Frontend.BinaryOp.BitAnd => "__and__",
                            Frontend.BinaryOp.BitOr => "__or__",
                            Frontend.BinaryOp.BitXor => "__xor__",
                            Frontend.BinaryOp.LShift => "__lshift__",
                            Frontend.BinaryOp.RShift => "__rshift__",
                            _ => ""
                        };
                        if (!string.IsNullOrEmpty(dunder))
                        {
                            var funcKey = cls + "_" + dunder;
                            if (inlineFunctions.TryGetValue(funcKey, out var dfunc))
                            {
                                var returnsCtor = false;
                                if (dfunc?.Body.Statements != null)
                                    foreach (var bs in dfunc.Body.Statements)
                                    {
                                        if (bs is not ReturnStmt ret || ret.Value is not CallExpr rc ||
                                            rc.Callee is not VariableExpr rv) continue;
                                        var resolved = ResolveCallee(rv.Name);
                                        if (inlineFunctions.ContainsKey(resolved + "___init__") ||
                                            overloadedFunctions.Contains(resolved + "___init__"))
                                            returnsCtor = true;
                                    }

                                if (returnsCtor)
                                {
                                    var qualifiedName = !string.IsNullOrEmpty(currentInlinePrefix)
                                        ? currentInlinePrefix + varExprBin.Name
                                        : (!string.IsNullOrEmpty(currentFunction)
                                            ? currentFunction + "." + varExprBin.Name
                                            : varExprBin.Name);
                    // Use the global name when the target is a module-level mutable global.
                                    if (!string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix))
                                    {
                                        string mutableGlobalKey = currentModulePrefix + varExprBin.Name;
                                        if (mutableGlobals.ContainsKey(mutableGlobalKey))
                                            qualifiedName = mutableGlobalKey;
                                    }
                                    instanceClasses[qualifiedName] = cls;
                                    pendingConstructorTarget = qualifiedName;
                                    virtualInstances.Add(qualifiedName);
                                    // A65: a slot class can't live as flattened Model-A fields and
                                    // still answer method calls (which pass a self pointer).
                                    // Materialize it into a slot after the construction.
                                    if (slotClasses.Contains(cls)) { slotMatName = varExprBin.Name; slotMatCls = cls; }
                                }
                            }
                        }
                    }
                }
            }
        }

        if (stmt.Target is MemberAccessExpr memExpr)
        {
            if (stmt.Value is CallExpr call)
            {
                if (call.Callee is VariableExpr calleeVar)
                {
                    string resolvedClass = ResolveCallee(calleeVar.Name);
                    if (inlineFunctions.ContainsKey(resolvedClass + "___init__") ||
                        overloadedFunctions.Contains(resolvedClass + "___init__"))
                    {
                        var objVal = VisitExpression(memExpr.Object);
                        var baseName = objVal is Variable v ? v.Name : (objVal is Temporary t ? t.Name : "");
                        if (!string.IsNullOrEmpty(baseName))
                        {
                            while (baseName != null && variableAliases.TryGetValue(baseName, out var alias))
                                baseName = alias;
                            var flattenedName = baseName + "_" + memExpr.Member;
                            instanceClasses[flattenedName] = resolvedClass;
                            pendingConstructorTarget = flattenedName;
                            virtualInstances.Add(flattenedName);
                        }
                    }
                }
            }
        }

        if (stmt.Target is MemberAccessExpr memTarget
            && (propertySetters.Count > 0 || propertyGetters.Count > 0)
            && EmitPropertySetterAssign(stmt, memTarget)) return;

        if (stmt.Value is LambdaExpr lamRhs)
        {
            if (stmt.Target is VariableExpr ve)
            {
                pendingLambdaKey = "";
                VisitLambdaExpr(lamRhs);
                string qname = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + ve.Name
                    : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + ve.Name : ve.Name);
                if (!string.IsNullOrEmpty(pendingLambdaKey))
                    lambdaVariableNames[qname] = pendingLambdaKey;
                pendingLambdaKey = "";
                return;
            }
        }

        // Untyped assignment of a list / list-comprehension to a name, e.g.
        //   outs = [digitalio.DigitalInOut(p) for p in (board.D5, board.D6, board.D7)]
        // The compile-time-unrolled array path (slots name__k + instanceClasses for ZCA
        // elements) is normally reached only through an annotated target; handle the plain
        // form here so CircuitPython-style code compiles.
        if (stmt.Target is VariableExpr listTarget)
        {
            List<Expression>? elemExprs = stmt.Value switch
            {
                ListExpr le => le.Elements,
                ListCompExpr lc => ExpandCtListComp(lc),
                _ => null
            };
            if (elemExprs != null && TryVisitCtListAssign(listTarget, elemExprs)) return;
        }

        Val value = VisitExpression(stmt.Value);

        if (stmt.Target is VariableExpr varExpr) { EmitScalarVarAssign(stmt, varExpr, value); }
        else if (stmt.Target is MemberAccessExpr memExpr2) { EmitMemberAssign(stmt, memExpr2, value); }
        else if (stmt.Target is UnaryExpr unExpr && unExpr.Op == Frontend.UnaryOp.Deref)
        {
            Val ptr = VisitExpression(unExpr.Operand);
            Emit(new StoreIndirect(value, ptr, RuntimePtrElem(ptr)));
        }
        else throw UserError("Invalid assignment target");

        if (!string.IsNullOrEmpty(slotMatName))
            MaterializeSlotFromFlattened(slotMatName, slotMatCls);
    }

    // A65: turn a slot-class instance that was built as Model-A flattened fields (the result of an
    // operator dunder, e.g. `c = a + b`) into a real SRAM slot: read each field at its current
    // (flattened) location, allocate the slot, store the fields, and register c as a slot instance
    // so method calls pass the slot pointer instead of the flattened fields. Reads happen first,
    // while the flattened alias is still in place.
    private void MaterializeSlotFromFlattened(string name, string cls)
    {
        if (!classFieldLayout.TryGetValue(cls, out var layout)) return;
        string qn = SlotInstanceKey(name);
        if (slotInstances.ContainsKey(qn)) return;   // already a slot

        // Snapshot current field values (flattened/aliased) before we repoint the instance.
        var fieldVals = new List<(int Off, DataType Ty, Val V)>();
        int off = 0;
        foreach (var (field, type, _) in layout)
        {
            DataType dt = DataTypeExtensions.StringToDataType(type);
            Val v = VisitExpression(new MemberAccessExpr(new VariableExpr(name), field));
            fieldVals.Add((off, dt, v));
            off += dt.SizeOf();
        }

        int total = off;
        string slot = qn + "__slot";
        arraySizes[slot] = total;
        arrayElemTypes[slot] = DataType.UINT8;
        moduleSramArrays.Add(slot);

        // Repoint the instance to the slot BEFORE storing, so the stores (and later reads) resolve
        // through the slot rather than the now-stale flattened alias.
        variableAliases.Remove(qn);
        virtualInstances.Remove(qn);
        instanceClasses[qn] = cls;
        slotInstances[qn] = slot;

        foreach (var (foff, fty, fv) in fieldVals)
            EmitSlotFieldStore(slot, false, foff, fty, fv, total, byteWise: true);
    }

    // `obj.prop = v` where prop has a registered @property setter: expand the setter.
    // Returns true when a matching setter was applied; false to fall through to the
    // normal member/assignment handling.
    private bool EmitPropertySetterAssign(AssignStmt stmt, MemberAccessExpr memTarget)
    {
        bool isCtor = false;
        if (stmt.Value is CallExpr call)
        {
            if (call.Callee is VariableExpr cv)
            {
                string rc = ResolveCallee(cv.Name);
                if (!string.IsNullOrEmpty(rc) && (inlineFunctions.ContainsKey(rc + "___init__") ||
                                                  overloadedFunctions.Contains(rc + "___init__")))
                    isCtor = true;
            }
        }

        if (!isCtor)
            return TryExpandPropertySetter(memTarget, () => VisitExpression(stmt.Value));
        return false;
    }

    // Expand a `@property` setter for `obj.prop` when one is registered for the instance's
    // class, binding `self` to the instance and the setter's value param to `getArg()`.
    // `getArg` is invoked lazily, only once a matching setter is confirmed (so the rhs is not
    // evaluated for a non-property member). Returns true when a setter was applied. Shared by
    // plain assignment (`obj.prop = v`) and augmented assignment (`obj.prop OP= v`).
    private bool TryExpandPropertySetter(MemberAccessExpr memTarget, Func<Val> getArg)
    {
        var objVal = VisitExpression(memTarget.Object);
        var @base = objVal is Variable v ? v.Name : (objVal is Temporary t ? t.Name : "");
        while (!string.IsNullOrEmpty(@base) && variableAliases.TryGetValue(@base, out var alias))
            @base = alias;
        if (string.IsNullOrEmpty(@base) || !instanceClasses.TryGetValue(@base, out var cls))
            return false;
        if (!propertySetters.TryGetValue(cls + "." + memTarget.Member, out string? inlineKey))
        {
            // No setter. If the member IS a @property getter, the assignment targets a read-only
            // property -- Python raises AttributeError. Reject clearly instead of silently writing
            // a phantom field that then shadows the getter (r.value = 200 used to "stick" as 200).
            if (propertyGetters.Contains(cls + "." + memTarget.Member))
                throw UserError(
                    $"cannot assign to read-only property '{memTarget.Member}': it has a @property " +
                    $"getter but no @{memTarget.Member}.setter");
            return false;
        }

        var argVal = getArg();
        if (inlineKey == null) return true;
        var setter = inlineFunctions[inlineKey];
        var exitLabel = MakeLabel();
        var newDepth = inlineDepth + 1;
        var newPrefix = $"inline{newDepth}.{setter?.Name}__setter.";

        variableAliases[newPrefix + "self"] = @base;
        instanceClasses[newPrefix + "self"] = cls;

        if (setter is { Params.Count: >= 2 })
        {
            var paramName = newPrefix + setter.Params[1].Name;
            var paramType = DataTypeExtensions.StringToDataType(setter.Params[1].Type);
            variableTypes[paramName] = paramType;
            constantVariables.Remove(paramName);
            variableAliases.Remove(paramName);
            switch (argVal)
            {
                case Constant c:
                    constantVariables[paramName] = c.Value;
                    break;
                case Variable vv:
                    variableAliases[paramName] = vv.Name;
                    break;
                case Temporary tt:
                    // Materialize the runtime value into the param's own SRAM slot.
                    // A bare alias (val -> tmp_N) would resolve to a dead temporary
                    // across the inline boundary, so the setter body would read an
                    // uninitialized variable (always 0) -- the root cause of
                    // `led.value = buf[0] & 1` collapsing to a single branch.
                    Emit(new Copy(tt, new Variable(paramName, paramType)));
                    break;
            }
        }

        inlineDepth++;
        var savedPrefix = currentInlinePrefix;
        var savedModulePrefix = currentModulePrefix;
        currentInlinePrefix = newPrefix;
        currentModulePrefix = cls + "_";

        inlineStack.Add(new InlineContext { ExitLabel = exitLabel });
        if (setter?.Body != null) VisitBlock(setter.Body);
        Emit(new Label(exitLabel));
        inlineStack.RemoveAt(inlineStack.Count - 1);

        inlineDepth--;
        currentInlinePrefix = savedPrefix;
        currentModulePrefix = savedModulePrefix;

        return true;
    }

    // `x = value` to a plain (scalar) variable target: type/alias resolution, constant
    // tracking and the Copy. Receives the pre-computed rhs `value`. Terminal for the
    // chain (completes -> VisitAssign returns).
    private bool IsMemoryAddressGlobal(string name) =>
        globals.TryGetValue(name, out var sym) && sym.IsMemoryAddress;

    private void EmitScalarVarAssign(AssignStmt stmt, VariableExpr varExpr, Val value)
    {
        if (stmt.AnnotatedType is { Length: > 0 } declared
            && !declared.Contains("ptr") && !declared.Contains("PIORegister"))
            RejectBareRegisterRead(stmt.Value);

        // Assigning to a name that is a ptr[T] register alias NEVER writes the register:
        // it rebinds the Python name and the store is silently dead-code-eliminated.
        // This broke Timer.set_compare (OCR1AH = hi) in the stdlib itself, so make it
        // a located error instead of a silent no-op.
        {
            string q = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + varExpr.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + varExpr.Name : varExpr.Name);
            bool isRegister = constantAddressVariables.ContainsKey(varExpr.Name)
                || constantAddressVariables.ContainsKey(q);
            if (!isRegister && !variableTypes.ContainsKey(q))
                isRegister = IsMemoryAddressGlobal(varExpr.Name)
                    || IsMemoryAddressGlobal(currentModulePrefix + varExpr.Name);
            if (isRegister)
                throw UserError(
                    $"assigning to '{varExpr.Name}' rebinds the name and never writes the register; " +
                    $"use {varExpr.Name}.value = ... to write the whole register, or {varExpr.Name}[bit] = ... for one bit");
        }

        // A write creates a NEW binding: kill the target's value-tracking alias BEFORE
        // resolving it (else the store itself is redirected through a stale alias), and
        // every alias that resolves TO it (their recorded value is about to change).
        // Without this, `free = i` inside an @inline loop left free -> i standing while i
        // kept changing, and the sibling expansion's `free = 255` even WROTE into i (the
        // FixedDict.__setitem__ corruption). Nonlocal write-through aliases are exempt.
        InvalidateAliasesForWrite(varExpr.Name);

        Val target;
        if (!string.IsNullOrEmpty(currentFunction))
        {
            if (currentFunctionGlobals.Contains(varExpr.Name))
            {
                target = ResolveBinding(varExpr.Name);
            }
            else
            {
                if (!string.IsNullOrEmpty(currentInlinePrefix)) target = ResolveBinding(varExpr.Name);
                else
                {
                    // Check if the variable is a module-level mutable global.
                    // e.g. _millis_count defined at module scope should be accessed
                    // as a global even when assigned inside a non-inline function.
                    string moduleGlobalName = currentModulePrefix + varExpr.Name;
                    if (mutableGlobals.ContainsKey(moduleGlobalName))
                    {
                        // Assigning to a module-level global inside a regular function
                        // without a `global` declaration is the silent-divergence trap:
                        // Python would create a local; PyMCU would write the global. Make
                        // the user choose. `main` is exempt — it is the module's top-level
                        // scope, where bare assignment to a global is the expected init.
                        if (currentFunction != "main")
                            throw new NameError(
                                $"'{varExpr.Name}' is a module-level global; to assign it inside " +
                                $"'{currentFunction}' add a 'global {varExpr.Name}' declaration, " +
                                "or rename the variable if a local was intended",
                                stmt.Line > 0 ? stmt.Line : lastLine, 1);

                        // First top-level store into an unannotated global that ScanGlobals
                        // could not type: adopt the RHS's real width. As uint8, a uint16
                        // getter result wrapped at the store (pwm.freq() printed 232 for
                        // 1000 on a real Uno).
                        if (widenableGlobals.Remove(moduleGlobalName))
                        {
                            DataType rhsT = value switch
                            {
                                Temporary wt => wt.Type,
                                Variable wv => wv.Type,
                                Constant when stmt.Value is CallExpr && lastInlineReturnType != DataType.UNKNOWN
                                    => lastInlineReturnType,
                                _ => mutableGlobals[moduleGlobalName]
                            };
                            if (rhsT.SizeOf() > mutableGlobals[moduleGlobalName].SizeOf())
                                mutableGlobals[moduleGlobalName] = rhsT;
                        }

                        target = new Variable(moduleGlobalName, mutableGlobals[moduleGlobalName]);
                    }
                    else
                    {
                        string qualifiedName = currentFunction + "." + varExpr.Name;
                        DataType type = DataType.UINT8;
                        if (variableTypes.TryGetValue(qualifiedName, out var t)) type = t;
                        else
                        {
                            if (value is Temporary tmp) type = tmp.Type;
                            else if (value is Variable vv) type = vv.Type;
                            else if (stmt.Value is IntegerLiteral)
                                type = literalOnlyLocalWidths.TryGetValue(varExpr.Name, out var litW)
                                    ? litW
                                    : DataType.INT32;
                            else if (value is Constant
                                     && stmt.Value is CallExpr { Callee: VariableExpr castFn }
                                     && CastTypes.TryGetValue(castFn.Name, out var castDt))
                                type = castDt;
                            else if (value is Constant && stmt.Value is not IntegerLiteral)
                                type = DataType.INT32;
                            variableTypes[qualifiedName] = type;
                            if (type != DataType.UINT8 && string.IsNullOrEmpty(currentInlinePrefix))
                                Logger.Verbose("IRGen", $"'{varExpr.Name}' inferred as {type.ToString().ToLower()}; annotate explicitly to suppress");
                        }

                        target = new Variable(qualifiedName, type);
                    }
                }
            }
        }
        else
        {
            target = ResolveBinding(varExpr.Name);
        }

        if (!(value is NoneVal)) Emit(new Copy(value, target));

        if (value is Variable vv2 && target is Variable tv2)
        {
            variableAliases[tv2.Name] = vv2.Name;
            valueTrackingAliases.Add(tv2.Name);
        }
        else if (value is Temporary tSrc && target is Variable tDst)
        {
            variableAliases[tDst.Name] = tSrc.Name;
            valueTrackingAliases.Add(tDst.Name);
        }

        if (string.IsNullOrEmpty(currentFunction))
        {
            if (value is Constant c && target is Variable tv3)
            {
                if (!mutableGlobals.ContainsKey(tv3.Name)) constantVariables[tv3.Name] = c.Value;
            }
        }
        else
        {
            if (target is Variable tv4) constantVariables.Remove(tv4.Name);
        }
    }

    // `x = ClassName(args)` constructor target: set up the (virtual) constructor
    // expansion state. Falls through to the inline-expansion path that follows.
    private void EmitConstructorTargetSetup(AssignStmt stmt, VariableExpr varExprCtor)
    {
        if (stmt.Value is CallExpr call)
        {
            string resolvedClass = "";
            if (call.Callee is VariableExpr calleeVar)
            {
                resolvedClass = ResolveCallee(calleeVar.Name);
            }
            else if (call.Callee is MemberAccessExpr calleeMem && calleeMem.Object is VariableExpr objVar)
            {
                if (modules.ContainsKey(objVar.Name))
                {
                    // Resolve a module alias (import machine as m) to the real module
                    // name so `m.Pin(...)` resolves the machine_Pin class.
                    string realMod = importedAliases.TryGetValue(objVar.Name, out var rm) && rm != null
                        ? rm : objVar.Name;
                    string mangled = realMod.Replace('.', '_');
                    resolvedClass = mangled + "_" + calleeMem.Member;
                }
            }

            // Factory: `a = setup()` where @inline setup returns ClassName(...). Resolve
            // to the returned ZCA class so the tracking below treats `a` as that
            // instance and its methods inline (otherwise `a.read()` mangles to an
            // undefined flattened name like main.a_read and fails at link).
            if (!string.IsNullOrEmpty(resolvedClass)
                && !inlineFunctions.ContainsKey(resolvedClass + "___init__")
                && !overloadedFunctions.Contains(resolvedClass + "___init__")
                && inlineFunctions.TryGetValue(resolvedClass, out var factoryFn)
                && factoryFn?.Body?.Statements != null)
            {
                foreach (var bs in factoryFn.Body.Statements)
                    if (bs is ReturnStmt r && r.Value is CallExpr rcall && rcall.Callee is VariableExpr rcv)
                    {
                        var rc = ResolveCallee(rcv.Name);
                        if (inlineFunctions.ContainsKey(rc + "___init__") || overloadedFunctions.Contains(rc + "___init__"))
                            resolvedClass = rc;
                    }
            }

            if (!string.IsNullOrEmpty(resolvedClass) && (inlineFunctions.ContainsKey(resolvedClass + "___init__") ||
                                                         overloadedFunctions.Contains(resolvedClass + "___init__")))
            {
                string qualifiedName = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + varExprCtor.Name
                    : (!string.IsNullOrEmpty(currentFunction)
                        ? currentFunction + "." + varExprCtor.Name
                        : varExprCtor.Name);
                // When the target variable is a module-level mutable global (e.g. declared at
                // top level in an entrypoint-less script), use its global name so that later
                // method lookups on the global variable resolve the class type correctly.
                if (!string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix))
                {
                    string mutableGlobalKey = currentModulePrefix + varExprCtor.Name;
                    if (mutableGlobals.ContainsKey(mutableGlobalKey))
                        qualifiedName = mutableGlobalKey;
                }
                instanceClasses[qualifiedName] = resolvedClass;
                pendingConstructorTarget = qualifiedName;
                virtualInstances.Add(qualifiedName);
            }
            else if (call.Callee is VariableExpr facVar
                     && functionReturnTypes.TryGetValue(ResolveCallee(facVar.Name), out var facRt)
                     && facRt != null && zcaFactoryClasses.ContainsKey(facRt)
                     && !inlineFunctions.ContainsKey(ResolveCallee(facVar.Name)))
            {
                // RFC 0001 Model B: `x = make()` where make is a non-@inline factory
                // returning a single-field ZCA. The call yields the packed field as a
                // scalar; track `x` as a handle instance so x.method() (which must be
                // @outline) passes that scalar as the field arg. Crucially we do NOT set
                // pendingConstructorTarget or add to virtualInstances -- the assignment
                // proceeds normally so `x` actually receives the returned handle.
                string qn = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + varExprCtor.Name
                    : (!string.IsNullOrEmpty(currentFunction)
                        ? currentFunction + "." + varExprCtor.Name
                        : varExprCtor.Name);
                instanceClasses[qn] = facRt;
                factoryHandleInstances.Add(qn);
            }
        }
    }

    // `obj.member = v`: assign through a member-access target — ZCA field stores
    // (slot/Model A), property-less member writes, and the related dispatch.
    private void EmitMemberAssign(AssignStmt stmt, MemberAccessExpr memExpr2, Val value)
    {
        // Class variable write: `ClassName.attr = value`. The read side resolves ClassName.attr
        // to the mutable class global (via classModuleMap); mirror it here with a real store.
        // Without this the write fell through to the ZCA-field path and was constant-folded into
        // oblivion -- `Counter.count = Counter.count + 1` emitted nothing and the counter stayed 0.
        if (memExpr2.Object is VariableExpr clsVar && classNames.Contains(clsVar.Name))
        {
            string cvPfx = classModuleMap.TryGetValue(clsVar.Name, out var p) ? p : currentModulePrefix;
            string cvName = cvPfx + clsVar.Name + "_" + memExpr2.Member;
            if (mutableGlobals.TryGetValue(cvName, out var cvType))
            {
                Emit(new Copy(value, new Variable(cvName, cvType)));
                constantVariables.Remove(cvName);
                return;
            }
        }

        // RFC 0001 Model B (SRAM slot): inside a slot method, `self.<field> = v` stores back to
        // the instance slot via the `self` pointer at the field's byte offset. The read side
        // (VisitMemberAccess) had this but the write side did not, so a multi-field ZCA's mutating
        // method compiled to nothing -- the mutation was silently dropped (move() left x/y intact).
        if (memExpr2.Object is VariableExpr slotSelf && slotSelf.Name == "self"
            && slotMethodFieldOffsets.TryGetValue(currentFunction, out var slotOffs)
            && slotOffs.TryGetValue(memExpr2.Member, out int slotOff))
        {
            EmitSlotFieldStore(currentFunction + ".self", true, slotOff,
                SlotMethodFieldType(currentFunction, memExpr2.Member), value, 0);
            return;
        }

        // Direct field write on a slot instance outside a method (`p.x = v`): store into the
        // instance slot, mirroring the direct read in VisitMemberAccess. Otherwise it wrote a
        // flattened `p_x` variable disjoint from the slot the methods read.
        if (memExpr2.Object is VariableExpr slotInst)
        {
            // Resolve the instance like the read side does: qualify with the inline prefix /
            // current function FIRST (so `self` inside a force-inlined method resolves to the
            // bound slot instance), then fall back to the bare name. Without the qualified form,
            // `self._field = v` in a force-inlined method chased a non-existent alias for bare
            // "self" and the slot store was silently dropped (e.g. sample()'s self._reads += 1).
            string Chase(string s) { while (s != null && variableAliases.TryGetValue(s, out var a)) s = a; return s; }
            string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + slotInst.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + slotInst.Name : slotInst.Name);
            string sb = Chase(qualified);
            if (sb == null || !slotInstances.ContainsKey(sb)) sb = Chase(slotInst.Name);
            if (sb != null && slotInstances.TryGetValue(sb, out var slotArrW)
                && instanceClasses.TryGetValue(sb, out var slotClsW)
                && TryGetSlotFieldOffset(slotClsW, memExpr2.Member, out int slotOffW, out var slotTyW))
            {
                // Direct SRAM array (not a pointer): byte-offset store, matching construction.
                int slotTotW = arraySizes.TryGetValue(slotArrW, out var tszW) ? tszW : 0;
                EmitSlotFieldStore(slotArrW, false, slotOffW, slotTyW, value, slotTotW);
                return;
            }
        }

        // RFC 0001 Model B (Class[N]): a direct field write on an instance-array element,
        // `arr[i].x = v`. Store through the computed element field address.
        if (memExpr2.Object is IndexExpr iaIdxW
            && TryInstanceArrayFieldAddr(iaIdxW, memExpr2.Member, out _) is { } iaAddrW)
        {
            Emit(new StoreIndirect(value, iaAddrW));
            return;
        }

        if (memExpr2.Member == "value")
        {
            var target = VisitExpression(memExpr2.Object);
            var varType = DataType.UINT8;
            var originalName = memExpr2.Object is VariableExpr veObj ? veObj.Name : null;

            // Runtime pointer (from ptr(<runtime addr>), e.g. ptr(BASE + x)): the target
            // Val holds a 16-bit address computed at runtime, so write through it with a
            // StoreIndirect rather than to a compile-time MemoryAddress.
            string? rptName = target switch { Variable rv => rv.Name, Temporary rt => rt.Name, _ => null };
            // The target is a runtime pointer if either the resolved Val or the source
            // variable name is registered as one. Prefer the annotated variable's
            // element width (`p: ptr[uint32]`) over a bare ptr() temp's UINT8 default,
            // so the store uses the declared width instead of a truncated byte.
            DataType? rptElem = null;
            if (rptName != null && runtimePtrVars.TryGetValue(rptName, out var e1)) rptElem = e1;
            if (originalName != null)
            {
                foreach (var k in new[]
                {
                    string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + originalName,
                    string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + originalName,
                    originalName,
                })
                {
                    if (k != null && runtimePtrVars.TryGetValue(k, out var e2)) { rptElem = e2; break; }
                }
            }
            if (rptElem != null)
            {
                Temporary sv = MakeTemp(rptElem.Value);
                Emit(new Copy(value, sv));
                Emit(new StoreIndirect(sv, target, rptElem.Value));
                return;
            }

            // Resolve local ptr[T] compile-time constant address variable
            if (target is Variable ptrVar && constantAddressVariables.TryGetValue(ptrVar.Name, out int ptrAddr))
            {
                DataType elemType = DataType.UINT8;
                if (variableTypes.TryGetValue(ptrVar.Name, out var et)) elemType = et;
                target = new MemoryAddress(ptrAddr, elemType);
                varType = elemType;
            }
            else if (originalName != null && variableTypes.TryGetValue(originalName, out var typeGlob))
                varType = typeGlob;
            else if (originalName != null && !string.IsNullOrEmpty(currentInlinePrefix) &&
                     variableTypes.TryGetValue(currentInlinePrefix + originalName, out var typeInline))
                varType = typeInline;
            else if (target is Variable v2 && variableTypes.TryGetValue(v2.Name, out var vt2))
                varType = vt2;
            else if (target is MemoryAddress m2)
                varType = m2.Type;

            var byteCount = varType.SizeOf();
            switch (byteCount)
            {
                case 1 when target is MemoryAddress ma:
                    Emit(new Copy(value, new MemoryAddress(ma.Address, varType)));
                    break;
                case 1 when target is Variable:
                    Emit(new Copy(value, target));
                    break;
                case 1:
                    throw UserError("Cannot assign to .value of this expression type");
                case 2 when target is MemoryAddress addr:
                {
                    // On 32-bit cores (ARM/RISC-V) MMIO must be a single word-
                    // aligned access; splitting a constant into byte stores would
                    // both break peripheral semantics and miss the atomic register
                    // aliases. Only split on 8-bit AVR (PointerWidth == 2).
                    if (value is Constant constVal && DataTypeExtensions.PointerWidth < 4)
                    {
                        int fullValue = constVal.Value;
                        int lowByte = fullValue & 0xFF;
                        int highByte = (fullValue >> 8) & 0xFF;
                        Emit(new Copy(new Constant(lowByte), new MemoryAddress(addr.Address, DataType.UINT8)));
                        Emit(new Copy(new Constant(highByte), new MemoryAddress(addr.Address + 1, DataType.UINT8)));
                    }
                    else
                    {
                        Emit(new Copy(value, new MemoryAddress(addr.Address, DataType.UINT16)));
                    }

                    break;
                }
                case 2:
                    throw UserError("16-bit .value assignment requires constant address");
                case 4 when target is MemoryAddress addr32:
                {
                    // See the 16-bit case: keep 32-bit MMIO stores atomic on
                    // 32-bit targets; only AVR byte-splits constant words.
                    if (value is Constant constVal32 && DataTypeExtensions.PointerWidth < 4)
                    {
                        Emit(new Copy(new Constant(constVal32.Value & 0xFF),         new MemoryAddress(addr32.Address,     DataType.UINT8)));
                        Emit(new Copy(new Constant((constVal32.Value >> 8)  & 0xFF), new MemoryAddress(addr32.Address + 1, DataType.UINT8)));
                        Emit(new Copy(new Constant((constVal32.Value >> 16) & 0xFF), new MemoryAddress(addr32.Address + 2, DataType.UINT8)));
                        Emit(new Copy(new Constant((constVal32.Value >> 24) & 0xFF), new MemoryAddress(addr32.Address + 3, DataType.UINT8)));
                    }
                    else
                    {
                        Emit(new Copy(value, new MemoryAddress(addr32.Address, DataType.UINT32)));
                    }
                    break;
                }
                case 4:
                    throw UserError("32-bit .value assignment requires constant address");
                default:
                    throw UserError("Unsupported type size for .value assignment");
            }
        }
        else
        {
            var objVal = VisitExpression(memExpr2.Object);
            var baseName = objVal is Variable v3 ? v3.Name : (objVal is Temporary t3 ? t3.Name : "");
            if (string.IsNullOrEmpty(baseName))
                throw UserError("Unknown member access in assignment: " + memExpr2.Member);
            while (baseName != null && variableAliases.TryGetValue(baseName, out var alias)) baseName = alias;
            var flattenedName = baseName + "_" + memExpr2.Member;

            // A field assigned None has no runtime value; record the flattened name so
            // `obj.field is None` folds to True (IsNoneValued checks this set). A later non-None
            // write clears the mark (the field now holds a real value). Without this the field
            // read 0 and `is None` silently returned False (broke optional/sentinel fields).
            if (value is NoneVal)
            {
                noneValuedNames.Add(flattenedName);
                constantVariables.Remove(flattenedName);
                return;
            }
            noneValuedNames.Remove(flattenedName);

            // RFC 0001 (write-back): a field mutated by a write-back method needs a real runtime
            // home, not a folded compile-time constant -- otherwise the write-back copy has
            // nowhere to land and later reads (including loop iterations) would see the stale
            // constant. Promote it here: emit a real store at construction and stop tracking it
            // as a constant. Narrowly scoped to write-back fields, so non-mutated ZCA fields keep
            // their exact zero-cost folding.
            if (TryGetWriteBackFieldType(baseName, memExpr2.Member, out var wbType))
            {
                Emit(new Copy(value, new Variable(flattenedName, wbType)));
                constantVariables.Remove(flattenedName);
                killedConstants.Add(flattenedName);
                variableTypes[flattenedName] = wbType;
                return;
            }

            if (value is Constant c)
            {
                if (baseName != null && !virtualInstances.Contains(baseName))
                {
                    // Only track the constant if this field has not been written
                    // before. A field written with two different constant values
                    // at different points is a mutable runtime field (e.g.
                    // sensor.failed) — tracking it as a compile-time constant
                    // would cause the compiler to DCE branches incorrectly.
                    if (!killedConstants.Contains(flattenedName))
                    {
                        if (constantVariables.TryGetValue(flattenedName, out int existing) && existing != c.Value)
                        {
                            // Second write with a different value → mutable field.
                            constantVariables.Remove(flattenedName);
                            killedConstants.Add(flattenedName);
                        }
                        else if (!constantVariables.ContainsKey(flattenedName))
                        {
                            constantVariables[flattenedName] = c.Value;
                        }
                        // If existing == c.Value, keep the existing entry as-is.
                    }
                }
                else if (stringIdToStr.TryGetValue(c.Value, out var value1))
                {
                    constantVariables[flattenedName] = c.Value;
                    strConstantVariables[flattenedName] = value1;
                    return;
                }
                else
                {
                    // Virtual (ZCA) instance, non-string constant (e.g. bit
                    // index assigned in _PinRegs.__init__). Store it so that
                    // subscript expressions like self._ddr[self._bit] can
                    // resolve the bit index at compile time.
                    //
                    // Inline-prefixed temporaries (baseName starts with
                    // "inline") are short-lived per-call-site objects.  The
                    // same inline prefix can be reused across multiple call
                    // sites (e.g. _PinRegs for pin 13 then pin 2 both land
                    // in "inline2.hal_gpio_Pin_init._r").  Treating the
                    // second write as a "different-value mutation" would
                    // incorrectly kill the constant and break codegen.
                    // For these temporaries always overwrite — they are
                    // never truly mutable across call sites.
                    //
                    // For top-level virtual instances (sensor, data_pin._pin
                    // etc.) apply the killedConstants guard so that genuinely
                    // mutable fields (sensor.failed) are not folded away.
                    bool isInlineTemp = baseName != null && baseName.StartsWith("inline");
                    if (isInlineTemp)
                    {
                        constantVariables[flattenedName] = c.Value;
                    }
                    else if (!killedConstants.Contains(flattenedName))
                    {
                        if (constantVariables.TryGetValue(flattenedName, out int existingZca) && existingZca != c.Value)
                        {
                            constantVariables.Remove(flattenedName);
                            killedConstants.Add(flattenedName);
                        }
                        else if (!constantVariables.ContainsKey(flattenedName))
                        {
                            constantVariables[flattenedName] = c.Value;
                        }
                    }
                }
            }

            if (value is MemoryAddress ma2)
            {
                constantAddressVariables[flattenedName] = ma2.Address;
                return;
            }

            var folded = value switch
            {
                Temporary t4 => TryTempName(t4.Name),
                Variable v4 => TryTempName(v4.Name),
                _ => false
            };
            if (folded) return;

            // Non-constant runtime assignment: if this field was previously
            // tracked as a compile-time constant (e.g. self.humidity = 0 in
            // __init__), kill the stale constant so later reads use the
            // runtime value.  Guard with "value is not Constant" to avoid
            // incorrectly killing constants like _bit that are set once as a
            // constant and never reassigned with a runtime value.
            if (value is not Constant && constantVariables.ContainsKey(flattenedName))
            {
                constantVariables.Remove(flattenedName);
                killedConstants.Add(flattenedName);
            }

            if (value is Variable vVal)
            {
                var clsKey = vVal.Name;
                var isZcaInstance = false;
                for (var depth = 0; depth < 20; ++depth)
                {
                    if (clsKey != null && instanceClasses.ContainsKey(clsKey))
                    {
                        isZcaInstance = true;
                        instanceClasses[flattenedName] = instanceClasses[clsKey];
                        virtualInstances.Add(flattenedName);
                        break;
                    }

                    if (clsKey != null && variableAliases.TryGetValue(clsKey, out var ak)) clsKey = ak;
                    else break;
                }

                if (isZcaInstance)
                {
                    variableAliases[flattenedName] = vVal.Name;
                    return;
                }
            }

            // Store the flattened field at its declared width; hard-coding uint8 truncated a
            // uint16/uint32 field (a no-method multi-field struct's `total` read back as total&0xFF).
            DataType fdt = FlattenedFieldType(baseName, memExpr2.Member);
            Emit(new Copy(value, new Variable(flattenedName, fdt)));
            if (fdt != DataType.UINT8) variableTypes[flattenedName] = fdt;
            return;

            bool TryTempName(string tname)
            {
                if (constantAddressVariables.TryGetValue(tname, out int cv))
                {
                    constantAddressVariables[flattenedName] = cv;
                    // The element width travels with the address: a ptr[T] aliased
                    // into a field is still T wide at its use sites.
                    if (variableTypes.TryGetValue(tname, out var tempType))
                        variableTypes[flattenedName] = tempType;
                    return true;
                }

                if (!constantVariables.TryGetValue(tname, out int cv2)) return false;
                constantVariables[flattenedName] = cv2;
                return true;
            }
        }
    }

    // RFC 0001 (write-back): true (with the field's declared type) when `<baseName>.<member>`
    // is a field that a write-back mutator updates -- such fields must live at runtime, not as
    // a folded constant. Follows instance aliases to find the owning class, then matches the
    // field against zcaWriteBackFields and reads its type from the class layout.
    private bool TryGetWriteBackFieldType(string? baseName, string member, out DataType type)
    {
        type = DataType.UINT8;
        string? key = baseName;
        for (int depth = 0; depth < 20 && key != null; ++depth)
        {
            if (instanceClasses.TryGetValue(key, out var cls)
                && zcaWriteBackFields.TryGetValue(cls, out var fields)
                && fields.Contains(member))
            {
                if (classFieldLayout.TryGetValue(cls, out var layout))
                    foreach (var (f, t, _) in layout)
                        if (f == member) { type = DataTypeExtensions.StringToDataType(t); break; }
                return true;
            }

            if (variableAliases.TryGetValue(key, out var next)) key = next;
            else break;
        }

        return false;
    }

    // Declared type of a flattened (non-slot) ZCA field `<inst>.<member>`, from the owning class's
    // layout. The flattened store path otherwise hard-codes uint8, truncating a uint16/uint32 field.
    private DataType FlattenedFieldType(string? baseName, string member)
    {
        string? key = baseName;
        for (int d = 0; d < 20 && key != null; ++d)
        {
            if (instanceClasses.TryGetValue(key, out var cls) && classFieldLayout.TryGetValue(cls, out var layout))
                foreach (var (f, t, _) in layout)
                    if (f == member) return DataTypeExtensions.StringToDataType(t);
            if (variableAliases.TryGetValue(key, out var nx)) key = nx;
            else break;
        }

        return DataType.UINT8;
    }

    // RFC 0001 Model B (Class[N]): the runtime address of `arr[idx].<member>` for an instance
    // array -- base + idx*stride + fieldOffset. Returns null when arr is not an instance array.
    // Mirrors the element-address computation used for arr[i].method() calls.
    private Val? TryInstanceArrayFieldAddr(IndexExpr idx, string member, out DataType fieldType)
    {
        fieldType = DataType.UINT8;
        if (idx.Target is not VariableExpr arrVe) return null;
        string q = !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + arrVe.Name : arrVe.Name;
        if (!instanceArrayClass.ContainsKey(q) && instanceArrayClass.ContainsKey(arrVe.Name)) q = arrVe.Name;
        if (!instanceArrayClass.TryGetValue(q, out var cls)) return null;
        if (!TryGetSlotFieldOffset(cls, member, out int fieldOff, out fieldType)) return null;

        int stride = instanceArrayStride[q];
        Val idxV = VisitExpression(idx.Index);
        Temporary baseT = MakeTemp(DataType.UINT16);
        Emit(new Copy(new ArrayBase(q), baseT));
        Temporary scaled = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, idxV, new Constant(stride), scaled));
        Temporary elemAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, baseT, scaled, elemAddr));
        if (fieldOff == 0) return elemAddr;
        Temporary fieldAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, elemAddr, new Constant(fieldOff), fieldAddr));
        return fieldAddr;
    }

    // RFC 0001 Model B (SRAM slot): byte offset and declared type of <field> within <cls>'s slot,
    // matching the layout order used by EmitSlotConstruction and the outlined methods. Used to
    // resolve a direct field access on a slot instance (outside a method) to a slot load/store.
    private bool TryGetSlotFieldOffset(string? cls, string field, out int offset, out DataType type)
    {
        offset = 0;
        type = DataType.UINT8;
        if (cls == null || !classFieldLayout.TryGetValue(cls, out var layout)) return false;
        int off = 0;
        foreach (var (f, ty, _) in layout)
        {
            var dt = DataTypeExtensions.StringToDataType(ty);
            if (f == field) { offset = off; type = dt; return true; }
            off += dt.SizeOf();
        }

        return false;
    }

    // The declared type of an outlined slot method's field (for multi-byte slot access). The
    // method's field layout carries the types; slotMethodFieldOffsets only carries offsets.
    private DataType SlotMethodFieldType(string method, string field)
    {
        if (outlineFieldLayout.TryGetValue(method, out var layout))
            foreach (var (f, t, _) in layout)
                if (f == field) return DataTypeExtensions.StringToDataType(t);
        return DataType.UINT8;
    }

    // RFC 0001 Model B (SRAM slot): load a field of `fieldTy` at BYTE offset `off`. The slot is
    // byte-packed, so a multi-byte field is assembled from consecutive bytes (b0 | b1<<8 | ...).
    // `isPtr`: arrName is a `self` pointer (BytearrayLoad); else it is the slot array (ArrayLoad).
    // The address of BYTE offset `off` inside a slot: the self pointer variable (outlined
    // slot methods) or the slot array's base, plus the constant offset. Pointer-width typed
    // so ARM's 32-bit addresses survive.
    private Val SlotFieldAddr(string arrName, bool isPtr, int off)
    {
        Val basePtr = isPtr ? new Variable(arrName, FlashPtrType) : new ArrayBase(arrName);
        if (off == 0 && isPtr) return basePtr;
        Temporary addr = MakeTemp(FlashPtrType);
        Emit(new Binary(BinaryOp.Add, basePtr, new Constant(off), addr));
        return addr;
    }

    private Val EmitSlotFieldLoad(string arrName, bool isPtr, int off, DataType fieldTy, int slotTotal)
    {
        int sz = fieldTy.SizeOf();
        if (sz <= 1)
        {
            Temporary b = MakeTemp(fieldTy);
            if (isPtr) Emit(new BytearrayLoad(arrName, new Constant(off), b));
            else Emit(new ArrayLoad(arrName, new Constant(off), b, DataType.UINT8, slotTotal));
            return b;
        }

        // Multi-byte field: ONE typed indirect load through the field's address. The
        // slot bytes are contiguous little-endian, exactly what LoadIndirect(Elem)
        // reads -- the old per-byte load + widen + shift + OR chain cost ~50
        // instructions per uint32 access on AVR.
        Temporary dst = MakeTemp(fieldTy);
        Emit(new LoadIndirect(SlotFieldAddr(arrName, isPtr, off), dst, fieldTy));
        return dst;
    }

    // RFC 0001 Model B (SRAM slot): store a field of `fieldTy` at BYTE offset `off`.
    // Multi-byte fields go through ONE typed StoreIndirect. `byteWise: true` keeps the
    // legacy per-byte ArrayStore split -- construction sites use it because those
    // ArrayStores carry the slot's Count and are the size/declaration channel the
    // backends' allocators and the ARM array-declaration scan read.
    private void EmitSlotFieldStore(string arrName, bool isPtr, int off, DataType fieldTy,
        Val value, int slotTotal, bool byteWise = false)
    {
        void StoreByte(int boff, Val b)
        {
            if (isPtr) Emit(new BytearrayStore(arrName, new Constant(boff), b));
            else Emit(new ArrayStore(arrName, new Constant(boff), b, DataType.UINT8, slotTotal));
        }

        int sz = fieldTy.SizeOf();
        if (sz <= 1) { StoreByte(off, value); return; }

        if (!byteWise)
        {
            Emit(new StoreIndirect(value, SlotFieldAddr(arrName, isPtr, off), fieldTy));
            return;
        }

        for (int i = 0; i < sz; ++i)
        {
            Temporary b = MakeTemp(DataType.UINT8);
            if (i == 0)
            {
                Emit(new Copy(value, b));   // low byte (truncating copy)
            }
            else
            {
                // Byte i = (value >> 8*i) & 0xFF. The truncating Copy to UINT8 keeps the low byte,
                // which equals byte i of value regardless of the shift's sign behaviour.
                Temporary sh = MakeTemp(fieldTy);
                Emit(new Binary(BinaryOp.RShift, value, new Constant(8 * i), sh));
                Emit(new Copy(sh, b));
            }

            StoreByte(off + i, b);
        }
    }

    // `b = a[lo:hi]` without an array annotation. Infer `b` as a fixed-size array whose length is
    // the (compile-time) slice length and copy the selected elements, mirroring the annotated
    // `b: T[N] = a[lo:hi]` path but inferring N and the element type from the source. Returns false
    // when the source is not a known array (the normal scalar path then handles/reports it). A
    // target that needs SRAM (it is indexed by a runtime value elsewhere) requires the annotated
    // form's allocation, so that case is reported clearly rather than mis-lowered.
    private bool TryEmitInferredSliceArray(VariableExpr target, VariableExpr src, SliceExpr sl)
    {
        string srcQ = string.IsNullOrEmpty(currentFunction) ? src.Name : currentFunction + "." + src.Name;
        if (!arraySizes.ContainsKey(srcQ) && arraySizes.ContainsKey(src.Name)) srcQ = src.Name;
        if (!arraySizes.TryGetValue(srcQ, out int srcSize)) return false;

        string qualified = string.IsNullOrEmpty(currentFunction) ? target.Name : currentFunction + "." + target.Name;

        if (arraysWithVariableIndex.Contains(qualified) || moduleSramArrays.Contains(qualified))
            throw UserError($"'{target.Name}' is indexed by a runtime value, so a slice assigned to "
                + $"it needs an explicit fixed-size annotation: '{target.Name}: <type>[N] = ...'");

        DataType srcEdt = arrayElemTypes[srcQ];
        int start = sl.Start != null ? EvaluateConstantExpr(sl.Start) : 0;
        int stop = sl.Stop != null ? EvaluateConstantExpr(sl.Stop) : srcSize;
        int step = sl.Step != null ? EvaluateConstantExpr(sl.Step) : 1;
        if (step == 0) throw UserError("Slice step cannot be zero");
        if (start < 0) start += srcSize;
        if (stop < 0) stop += srcSize;
        start = Math.Max(0, Math.Min(start, srcSize));
        stop = Math.Max(0, Math.Min(stop, srcSize));
        int count = 0;
        for (int i = start; step > 0 ? i < stop : i > stop; i += step) ++count;

        arraySizes[qualified] = count;
        arrayElemTypes[qualified] = srcEdt;
        variableTypes[qualified] = srcEdt;

        bool srcSram = arraysWithVariableIndex.Contains(srcQ) || moduleSramArrays.Contains(srcQ);
        int k = 0;
        for (int i = start; step > 0 ? i < stop : i > stop; i += step, ++k)
        {
            string dstElem = qualified + "__" + k;
            variableTypes[dstElem] = srcEdt;
            Val srcVal;
            if (srcSram)
            {
                Temporary tmp = MakeTemp(srcEdt);
                Emit(new ArrayLoad(srcQ, new Constant(i), tmp, srcEdt, srcSize));
                srcVal = tmp;
            }
            else srcVal = new Variable(srcQ + "__" + i, srcEdt);
            Emit(new Copy(srcVal, new Variable(dstElem, srcEdt)));
        }
        return true;
    }

    // `s = sep.join(<list>)`. Two supported shapes:
    //   1. Every element is a compile-time string: fold to one constant string.
    //   2. `''.join([chr(b) for b in buf])` over a known-size byte buffer -- the
    //      canonical MicroPython/CircuitPython bytes-to-string idiom. Lowered to a
    //      runtime string: a NUL-capped buffer copy registered in runtimeStrVars, so
    //      print()/len() treat the result exactly like an f-string-as-value.
    private bool TryEmitJoinAssign(string target, Expression value)
    {
        if (value is not CallExpr { Callee: MemberAccessExpr { Member: "join" } jm } jc) return false;
        string? sep = StaticStringOf(jm.Object);
        if (sep == null || jc.Args.Count != 1) return false;

        if (jc.Args[0] is ListExpr jle && jle.Elements.All(e => StaticStringOf(e) != null))
        {
            string joined = string.Join(sep, jle.Elements.Select(e => StaticStringOf(e)!));
            string cq = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + target
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + target : target);
            strConstantVariables[cq] = joined;
            constantVariables.Remove(cq);
            variableAliases.Remove(cq);
            return true;
        }

        if (sep.Length == 0
            && jc.Args[0] is ListCompExpr { Filter: null, Iterable2: null } lc
            && lc.Element is CallExpr { Callee: VariableExpr { Name: "chr" } } chrCall
            && chrCall.Args is [VariableExpr chrArg] && chrArg.Name == lc.VarName
            && lc.Iterable is VariableExpr srcVe
            && ResolveArrayVar(srcVe.Name) is { } srcArr)
        {
            int n = srcArr.Size;
            int bound = n + 1;
            string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + target
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + target : target);
            string lenVar = "__jnlen_" + target;

            if (runtimeStrVars.TryGetValue(qualified, out var existing))
            {
                if (bound > existing.Capacity)
                    throw UserError(
                        $"'{target}' is re-assigned a join result needing {bound} bytes but its " +
                        $"buffer was sized {existing.Capacity} by an earlier assignment");
                lenVar = existing.LenVar;
            }
            else
            {
                VisitStatement(new VarDecl(target, "bytearray",
                    new CallExpr(new VariableExpr("bytearray"),
                                 new List<Expression> { new IntegerLiteral(bound) })));
                VisitStatement(new VarDecl(lenVar, "uint16", new IntegerLiteral(0)));
                runtimeStrVars[qualified] = (lenVar, bound);
            }

            for (int i = 0; i < n; i++)
                VisitStatement(new AssignStmt(
                    new IndexExpr(new VariableExpr(target), new IntegerLiteral(i)),
                    new IndexExpr(srcVe, new IntegerLiteral(i))));
            VisitStatement(new AssignStmt(
                new IndexExpr(new VariableExpr(target), new IntegerLiteral(n)),
                new IntegerLiteral(0)));
            VisitStatement(new AssignStmt(new VariableExpr(lenVar), new IntegerLiteral(n)));
            return true;
        }

        return false;
    }

    // Compile-time __len__ of a class, when its body is a single constant return
    // (e.g. _NVM.__len__ -> 1024). Null when absent or not statically known.
    private int? DunderConstLen(string cls)
    {
        if (!inlineFunctions.TryGetValue(cls + "_" + "__len__", out var lenFn)) return null;
        if (lenFn.Body.Statements is [ReturnStmt { Value: IntegerLiteral il }]) return il.Value;
        return null;
    }

    // `obj[a:b] = <list/bytes literal>` where obj's class defines __setitem__: unroll
    // to one obj[i] = v per element (each dispatches the dunder), so the canonical
    // CircuitPython `microcontroller.nvm[0:4] = b'...'` compiles to four EEPROM writes.
    // Bounds come from the slice; negative/omitted bounds need a compile-time __len__.
    private bool TryEmitDunderSliceAssign(AssignStmt stmt, IndexExpr tgt, SliceExpr sl)
    {
        Val tgtVal = VisitExpression(tgt.Target);
        string cls = GetValClass(tgtVal);
        if (string.IsNullOrEmpty(cls) || !inlineFunctions.ContainsKey(cls + "_" + "__setitem__"))
            return false;

        int? len = DunderConstLen(cls);
        if (len is null && (sl.Start is null || sl.Stop is null))
            throw UserError(
                "slice assignment on this object needs explicit start and stop " +
                "(its __len__ is not a compile-time constant)");
        List<int> idx;
        try { idx = SliceIndices(sl, len ?? int.MaxValue); }
        catch (Exception) { return false; }

        if (stmt.Value is not ListExpr le)
            throw UserError(
                "slice assignment to an object with __setitem__ needs a bytes or list " +
                "literal source of the same length");
        if (le.Elements.Count != idx.Count)
            throw UserError(
                $"slice assignment length mismatch: target selects {idx.Count} " +
                $"element(s), source has {le.Elements.Count}");

        for (int k = 0; k < idx.Count; k++)
            VisitStatement(new AssignStmt(
                new IndexExpr(tgt.Target, new IntegerLiteral(idx[k])), le.Elements[k]));
        return true;
    }

    // Resolve a slice's [start, stop, step) over a known array size (Python semantics:
    // negatives from the end, clamped). Returns the ordered index list.
    private List<int> SliceIndices(SliceExpr sl, int size)
    {
        int start = sl.Start != null ? EvaluateConstantExpr(sl.Start) : 0;
        int stop = sl.Stop != null ? EvaluateConstantExpr(sl.Stop) : size;
        int step = sl.Step != null ? EvaluateConstantExpr(sl.Step) : 1;
        if (step == 0) throw UserError("Slice step cannot be zero");
        if (start < 0) start += size;
        if (stop < 0) stop += size;
        start = Math.Max(0, Math.Min(start, size));
        stop = Math.Max(0, Math.Min(stop, size));
        var idx = new List<int>();
        for (int i = start; step > 0 ? i < stop : i > stop; i += step) idx.Add(i);
        return idx;
    }

    // Qualified array name + size for a variable, or null when it is not a known array.
    private (string Name, int Size)? ResolveArrayVar(string name)
    {
        foreach (var k in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + name,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + name,
            name,
        })
            if (k != null && arraySizes.TryGetValue(k, out int sz)) return (k, sz);
        return null;
    }

    // `arr[a:b] = <same-length source>` — element-wise copy. Compile-time indices only.
    // When source and destination are the SAME array (possibly overlapping ranges), the
    // source elements are snapshotted into temporaries first, Python-style.
    private bool TryEmitSliceAssign(AssignStmt stmt, IndexExpr tgt, SliceExpr sl)
    {
        if (tgt.Target is not VariableExpr arrVe) return false;
        var dst = ResolveArrayVar(arrVe.Name);
        if (dst == null) return false;

        List<int> dstIdx;
        try { dstIdx = SliceIndices(sl, dst.Value.Size); }
        catch (Exception) { return false; }   // runtime indices -> generic message

        // Source: list literal | whole array | array slice.
        switch (stmt.Value)
        {
            case ListExpr le:
                if (le.Elements.Count != dstIdx.Count)
                    throw UserError(
                        $"slice assignment length mismatch: target selects {dstIdx.Count} " +
                        $"element(s), source list has {le.Elements.Count}");
                for (int k = 0; k < dstIdx.Count; k++)
                    VisitStatement(new AssignStmt(
                        new IndexExpr(tgt.Target, new IntegerLiteral(dstIdx[k])), le.Elements[k]));
                return true;

            case VariableExpr srcVe when ResolveArrayVar(srcVe.Name) is { } srcWhole:
            {
                var srcIdx = Enumerable.Range(0, srcWhole.Size).ToList();
                return EmitSliceCopy(tgt.Target, dstIdx, srcVe, srcIdx,
                    sameArray: srcWhole.Name == dst.Value.Name);
            }

            case IndexExpr { Index: SliceExpr srcSl, Target: VariableExpr srcVe2 }
                when ResolveArrayVar(srcVe2.Name) is { } srcArr:
            {
                List<int> srcIdx;
                try { srcIdx = SliceIndices(srcSl, srcArr.Size); }
                catch (Exception) { return false; }
                return EmitSliceCopy(tgt.Target, dstIdx, srcVe2, srcIdx,
                    sameArray: srcArr.Name == dst.Value.Name);
            }

            default:
                return false;
        }
    }

    private bool EmitSliceCopy(Expression dstArr, List<int> dstIdx,
        VariableExpr srcArr, List<int> srcIdx, bool sameArray)
    {
        if (srcIdx.Count != dstIdx.Count)
            throw UserError(
                $"slice assignment length mismatch: target selects {dstIdx.Count} " +
                $"element(s), source selects {srcIdx.Count}");

        if (!sameArray)
        {
            for (int k = 0; k < dstIdx.Count; k++)
                VisitStatement(new AssignStmt(
                    new IndexExpr(dstArr, new IntegerLiteral(dstIdx[k])),
                    new IndexExpr(srcArr, new IntegerLiteral(srcIdx[k]))));
            return true;
        }

        // Same array: snapshot the source first so overlapping ranges copy Python-style.
        var temps = new List<Val>();
        foreach (int j in srcIdx)
        {
            Val v = VisitExpression(new IndexExpr(srcArr, new IntegerLiteral(j)));
            var t = MakeTemp(GetValType(v));
            Emit(new Copy(v, t));
            temps.Add(t);
        }
        var arr = ResolveArrayVar(srcArr.Name)!.Value;
        var elemType = arrayElemTypes.TryGetValue(arr.Name, out var et) ? et : DataType.UINT8;
        for (int k = 0; k < dstIdx.Count; k++)
            Emit(new ArrayStore(arr.Name, new Constant(dstIdx[k]), temps[k], elemType, arr.Size));
        return true;
    }

    // `arr[i] = v` / `port[bit] = v`: array/bytearray store, runtime/constant bit
    // subscript on a register, with target-address resolution. Always terminal.
    private void EmitIndexAssign(AssignStmt stmt, IndexExpr indexExpr)
    {
        // Slice assignment: supported for compile-time indices and a MATCHING-length
        // source (list literal, whole array, or array slice) — an element-wise copy loop.
        // Differing lengths would need a memmove/realloc (insert/delete), which has no
        // bare-metal representation; that case still reports clearly.
        if (indexExpr.Index is SliceExpr slA)
        {
            if (TryEmitSliceAssign(stmt, indexExpr, slA)) return;
            if (TryEmitDunderSliceAssign(stmt, indexExpr, slA)) return;
            throw UserError(
                "slice assignment needs compile-time indices and a source of the SAME length " +
                "(list literal, array, or array slice); inserting/deleting via slices is not " +
                "supported — restructure with explicit element assignments");
        }

        if (indexExpr.Target is VariableExpr ve)
        {
            string qualified = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
            if (!arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(ve.Name))
                qualified = ve.Name;

            // When inside an inline expansion, the target may be a parameter aliased to a
            // caller-side array (e.g., `buf` → `main.line`). Resolve the alias so the
            // array-store path fires instead of falling through to the bit-subscript path.
            if (!arraySizes.ContainsKey(qualified) && !bytearrayParams.Contains(qualified)
                && !string.IsNullOrEmpty(currentInlinePrefix))
            {
                string inlineQ = currentInlinePrefix + ve.Name;
                if (variableAliases.TryGetValue(inlineQ, out string? resolvedQ) && resolvedQ != null)
                    qualified = resolvedQ;
                else if (arraySizes.ContainsKey(inlineQ) || bytearrayParams.Contains(inlineQ))
                    qualified = inlineQ;
            }

            // Bytearray parameter: indirect store through pointer.
            if (bytearrayParams.Contains(qualified))
            {
                Val idxVal = VisitExpression(indexExpr.Index);
                Val srcVal = VisitExpression(stmt.Value);
                Emit(new BytearrayStore(qualified, idxVal, srcVal));
                return;
            }

            // list[T] index assignment: x[i] = val → store at GC heap offset 2 + i*elemSize
            {
                string listQ = listVarElemTypes.ContainsKey(qualified) ? qualified
                             : listVarElemTypes.ContainsKey(ve.Name) ? ve.Name
                             : "";
                if (!string.IsNullOrEmpty(listQ))
                {
                    DataType elemDt = listVarElemTypes[listQ];
                    Val listPtr = new Variable(listQ, DataType.GC_REF);
                    Val idxVal = VisitExpression(indexExpr.Index);
                    Val srcVal = VisitExpression(stmt.Value);
                    Temporary elemAddr = EmitElemAddr(listPtr, idxVal, elemDt.SizeOf());
                    Emit(new StoreIndirect(srcVal, elemAddr, elemDt));
                    return;
                }
            }

            if (arraySizes.ContainsKey(qualified))
            {
                if (arraysWithVariableIndex.Contains(qualified) || moduleSramArrays.Contains(qualified))
                {
                    Val idxVal = VisitExpression(indexExpr.Index);
                    Val srcVal = VisitExpression(stmt.Value);
                    Emit(new ArrayStore(qualified, idxVal, srcVal, arrayElemTypes[qualified],
                        arraySizes[qualified]));
                }
                else
                {
                    // Accept either a literal subscript or a variable that folds to a
                    // compile-time constant (e.g. the index from an unrolled
                    // `for i, _ in enumerate(buf)` loop, where `i` is constant per
                    // iteration). This lets inline functions write into a caller's
                    // fixed array via constant-index stores without SRAM indexing.
                    int elemIdx;
                    if (indexExpr.Index is IntegerLiteral c)
                        elemIdx = c.Value;
                    else if (VisitExpression(indexExpr.Index) is Constant cc)
                        elemIdx = cc.Value;
                    else
                        throw UnrolledArrayIndexError(qualified);
                    string elemName = qualified + "__" + elemIdx;
                    Val srcVal = VisitExpression(stmt.Value);
                    Emit(new Copy(srcVal, new Variable(elemName, arrayElemTypes[qualified])));
                }

                return;
            }
        }

        // Instance-member array store: self._buf[i] = val (i runtime), where
        // self._buf was declared as a per-instance SRAM framebuffer.
        if (indexExpr.Target is MemberAccessExpr memStore
            && ResolveMemberArrayName(memStore) is string flatStore)
        {
            Val idxVal = VisitExpression(indexExpr.Index);
            Val srcVal = VisitExpression(stmt.Value);
            Emit(new ArrayStore(flatStore, idxVal, srcVal, arrayElemTypes[flatStore], arraySizes[flatStore]));
            return;
        }

        {
            Val tgtVal = VisitExpression(indexExpr.Target);
            string cls = GetValClass(tgtVal);
            if (!string.IsNullOrEmpty(cls))
            {
                string funcKey = cls + "_" + "__setitem__";
                // Outlined __setitem__: dispatch as a method call. Falling through wrote
                // through the built-in indexed-store path, which the later read did not see.
                if (!inlineFunctions.ContainsKey(funcKey)
                    && indexExpr.Target is VariableExpr sv
                    && stmt.Value is not ListExpr && stmt.Value is not TupleExpr
                    && TryResolveInstanceMethodAst(sv.Name, "__setitem__") != null)
                {
                    VisitCall(new CallExpr(
                        new MemberAccessExpr(sv, "__setitem__"),
                        new List<Expression> { indexExpr.Index, stmt.Value }) { Line = stmt.Line });
                    return;
                }

                if (inlineFunctions.ContainsKey(funcKey))
                {
                    string selfName = tgtVal is Variable v ? v.Name : (tgtVal is Temporary t ? t.Name : "");
                    Val idxVal = VisitExpression(indexExpr.Index);
                    // A tuple/list literal RHS (pixels[i] = (r, g, b)) is bound to the
                    // color parameter as a sequence literal so __setitem__ can read it
                    // by constant subscript; otherwise evaluate a scalar value.
                    ListExpr? seqRhs = stmt.Value as ListExpr
                        ?? (stmt.Value is TupleExpr tup ? new ListExpr(tup.Elements) : null);
                    if (seqRhs != null)
                    {
                        EmitDunderCall(selfName, cls, funcKey, new List<Val> { idxVal, new NoneVal() },
                            new Dictionary<int, ListExpr> { { 1, seqRhs } });
                    }
                    else
                    {
                        Val srcVal = VisitExpression(stmt.Value);
                        EmitDunderCall(selfName, cls, funcKey, new List<Val> { idxVal, srcVal });
                    }
                    return;
                }
            }
        }

        var target = VisitExpression(indexExpr.Target);
        var indexVal = VisitExpression(indexExpr.Index);

        target = ResolveTargetAddr(target);

        var bit = 0;
        if (indexVal is Constant c2)
        {
            bit = c2.Value;
        }
        else
        {
            bool TryConst(string name)
            {
                if (!constantVariables.TryGetValue(name, out int cv)) return false;
                bit = cv;
                return true;
            }

            var resolved = indexVal switch
            {
                Temporary t => TryConst(t.Name),
                Variable v => TryConst(v.Name),
                _ => false
            };
            if (!resolved)
            {
                // Runtime bit index (e.g. `PORTB[i] = 1`). SBI/CBI require a constant
                // bit, so build a runtime mask (1 << i) and read-modify-write the
                // register. Supported for a MemoryAddress target (a chip register —
                // every real use). A runtime POINTER target (a ptr held in a variable,
                // e.g. a boxed Pin's port) would need a dereferenced LD/ST and proper
                // pointer typing; reject it clearly for now instead of miscompiling
                // the pointer value as if it were the port data.
                if (target is not MemoryAddress)
                    throw new TypeError(
                        "runtime bit index is only supported on a chip register (a constant " +
                        "port address); indexing a bit through a runtime pointer is not yet supported",
                        stmt.Line > 0 ? stmt.Line : lastLine, 1);
                Val rmwVal = VisitExpression(stmt.Value);
                Temporary mask = MakeTemp(DataType.UINT8);
                Emit(new Binary(BinaryOp.LShift, new Constant(1), indexVal, mask));
                Temporary cur = MakeTemp(DataType.UINT8);
                Emit(new Copy(target, cur));
                Temporary res = MakeTemp(DataType.UINT8);
                if (rmwVal is Constant rc && rc.Value == 0)
                {
                    Temporary inv = MakeTemp(DataType.UINT8);
                    Emit(new Unary(UnaryOp.BitNot, mask, inv));            // ~mask
                    Emit(new Binary(BinaryOp.BitAnd, cur, inv, res));      // clear bit
                }
                else if (rmwVal is Constant)
                {
                    Emit(new Binary(BinaryOp.BitOr, cur, mask, res));      // set bit
                }
                else
                {
                    // res = (cur & ~mask) | ((val & 1) * mask)
                    Temporary inv = MakeTemp(DataType.UINT8);
                    Emit(new Unary(UnaryOp.BitNot, mask, inv));
                    Temporary cleared = MakeTemp(DataType.UINT8);
                    Emit(new Binary(BinaryOp.BitAnd, cur, inv, cleared));
                    Temporary vbit = MakeTemp(DataType.UINT8);
                    Emit(new Binary(BinaryOp.BitAnd, rmwVal, new Constant(1), vbit));
                    Temporary vmask = MakeTemp(DataType.UINT8);
                    Emit(new Binary(BinaryOp.Mul, vbit, mask, vmask));
                    Emit(new Binary(BinaryOp.BitOr, cleared, vmask, res));
                }
                Emit(new Copy(res, target));
                return;
            }
        }

        var val = VisitExpression(stmt.Value);

        if (val is Constant cv2)
        {
            if (cv2.Value != 0) Emit(new BitSet(target, bit));
            else Emit(new BitClear(target, bit));
        }
        else
        {
            Emit(new BitWrite(target, bit, val));
        }

        return;

        Val ResolveTargetAddr(Val val)
        {
            string? name = val is Temporary t ? t.Name : (val is Variable vv ? vv.Name : null);
            if (name != null && constantAddressVariables.TryGetValue(name, out int addr))
            {
                DataType dt = DataType.UINT8;
                if (!string.IsNullOrEmpty(currentInlinePrefix) && variableTypes.TryGetValue(currentInlinePrefix + name, out var typeInline))
                    dt = typeInline;
                else if (variableTypes.TryGetValue(name, out var typeGlob))
                    dt = typeGlob;
                
                return new MemoryAddress(addr, dt);
            }
            return val;
        }
    }

    // Folds a literal integer expression to its value for the out-of-range check. Handles direct
    // literals (decimal/hex/bool, optionally negated) AND pure ARITHMETIC (+, -, *) of such, so an
    // overflowing constant value like `uint8 = 50 * 20` (= 1000) is caught exactly like a bare
    // out-of-range literal. Bitwise and shift operators are deliberately NOT folded here, so idioms
    // that intentionally use the full width (`~0`, `0xFFFF & 0xFF`, `1 << 7`) are never false-
    // flagged. The explicit `uint8(...)` cast is a CallExpr (not folded here), so it stays the
    // escape hatch for intentional wraparound. long arithmetic avoids masking the true magnitude.
    private static long? TryLiteralInt(Expression e) => e switch
    {
        IntegerLiteral il                                  => il.Value,
        BooleanLiteral b                                   => b.Value ? 1 : 0,
        UnaryExpr { Op: Frontend.UnaryOp.Negate } u when TryLiteralInt(u.Operand) is { } v => -v,
        BinaryExpr { Op: Frontend.BinaryOp.Add } a when TryLiteralInt(a.Left) is { } l && TryLiteralInt(a.Right) is { } r => l + r,
        BinaryExpr { Op: Frontend.BinaryOp.Sub } a when TryLiteralInt(a.Left) is { } l && TryLiteralInt(a.Right) is { } r => l - r,
        BinaryExpr { Op: Frontend.BinaryOp.Mul } a when TryLiteralInt(a.Left) is { } l && TryLiteralInt(a.Right) is { } r => l * r,
        _                                                  => null,
    };

    // Rejects an integer literal that cannot be represented in its annotated type
    // (e.g. `x: uint8 = 300`), which the backend would otherwise truncate silently.
    private void CheckIntLiteralRange(Expression? init, DataType type, int line)
    {
        if (init == null) return;
        if (TryLiteralInt(init) is not { } v) return;

        (long Min, long Max, string Name)? range = type switch
        {
            DataType.UINT8  => (0L, 255L, "uint8"),
            DataType.INT8   => (-128L, 127L, "int8"),
            DataType.UINT16 => (0L, 65535L, "uint16"),
            DataType.INT16  => (-32768L, 32767L, "int16"),
            DataType.UINT32 => (0L, 4294967295L, "uint32"),
            DataType.INT32  => (-2147483648L, 2147483647L, "int32"),
            _               => null,
        };
        if (range is not { } r) return;

        // A uint32 literal above 2^31-1 (e.g. 4000000000) arrives as its wrapped 32-bit
        // bit pattern (IntegerLiteral carries int); its UNSIGNED reading is the value
        // the user wrote, and every 32-bit pattern is representable in uint32.
        long shown = type == DataType.UINT32 && v < 0 ? (long)(uint)v : v;

        if (shown < r.Min || shown > r.Max)
            throw new ValueError(
                $"integer literal {shown} is out of range for {r.Name} (valid range {r.Min}..{r.Max})",
                line, 1);
    }

    // `s = f"..."` with runtime interpolations -- the f-string as a VALUE. Expands to a
    // compiler-managed fixed bytearray (named `s`, sized by the f-string's static bound) plus
    // chained pymcu.strfmt calls threading a length variable, then a NUL terminator:
    //     s: bytearray = bytearray(N)
    //     __fslen_s = _fs_text(s, 0, "t=");  __fslen_s = _fs_u32(s, __fslen_s, t);  ...
    //     s[__fslen_s] = 0
    // The surface stays pure CPython (no new syntax/builtins); the buffer size needs no
    // annotation because every part has a static width bound (literal length; 11 chars covers
    // any 32-bit decimal; a format spec bounds by max(width, natural-width-for-base)).
    // Returns false for fully-constant f-strings so the existing const-string path keeps
    // producing an interned string.
    private bool TryExpandFStringValue(string target, Expression value)
    {
        if (value is not FStringExpr topFs) return false;

        // Flatten nested unspecced f-string parts into one part list.
        var parts = new List<FStringPart>();
        void Flatten(FStringExpr f)
        {
            foreach (var p in f.Parts)
            {
                if (p.IsExpr && p.Expr is FStringExpr nf && string.IsNullOrEmpty(p.FormatSpec)) Flatten(nf);
                else parts.Add(p);
            }
        }
        Flatten(topFs);

        // Fully constant (literals / static strings / declared consts): keep the const path.
        bool IsConstPart(FStringPart p) =>
            !p.IsExpr
            || StaticStringOf(p.Expr!) != null
            || p.Expr is IntegerLiteral
            || (p.Expr is VariableExpr cv &&
                (declaredConstants.Contains(cv.Name)
                 || constantVariables.ContainsKey(currentInlinePrefix + cv.Name)
                 || constantVariables.ContainsKey(cv.Name)));
        if (parts.All(IsConstPart)) return false;

        // The strfmt helpers must be loaded (pymcu build injects the import on detection).
        string? strfmtMod = null;
        foreach (var kv in importedAliases)
            if (kv.Value == "pymcu.strfmt") { strfmtMod = kv.Key; break; }
        if (strfmtMod == null)
            throw UserError(
                "assigning an f-string with runtime values needs the pymcu.strfmt helpers; " +
                "`pymcu build` injects them automatically -- if invoking the compiler by hand, " +
                "add `import pymcu.strfmt as _pymcu_strfmt` to the entry file.");

        // Static size bound (type-free, conservative): a plain interpolation is at most 11
        // chars (sign + 10 decimal digits of a 32-bit value); a spec part is bounded by
        // max(width, natural digits for its base) + a possible sign.
        int bound = 1;   // NUL terminator
        foreach (var p in parts)
        {
            if (!p.IsExpr) { bound += p.Text.Length; continue; }
            string? st = StaticStringOf(p.Expr!);
            if (st != null) { bound += st.Length; continue; }
            if (p.Expr is IntegerLiteral pil) { bound += pil.Value.ToString().Length; continue; }
            if (!string.IsNullOrEmpty(p.FormatSpec))
            {
                var (w, radix, _, _) = ParseFormatSpec(p.FormatSpec);
                int natural = radix switch { 2 => 32, 8 => 11, 16 => 8, _ => 11 };
                bound += Math.Max(w, natural) + 1;
            }
            else bound += 11;
        }

        string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + target
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + target : target);
        string lenVar = "__fslen_" + target;

        if (runtimeStrVars.TryGetValue(qualified, out var existing))
        {
            // Re-assignment: reuse the buffer when it fits; a bigger later f-string would
            // need a retroactively larger buffer, which a single pass cannot provide.
            if (bound > existing.Capacity)
                throw UserError(
                    $"'{target}' is re-assigned an f-string needing {bound} bytes but its " +
                    $"buffer was sized {existing.Capacity} by an earlier assignment; assign " +
                    "the longest f-string first (buffer size is fixed at the first assignment).");
            lenVar = existing.LenVar;
            VisitStatement(new AssignStmt(new VariableExpr(lenVar), new IntegerLiteral(0)));
        }
        else
        {
            VisitStatement(new VarDecl(target, "bytearray",
                new CallExpr(new VariableExpr("bytearray"),
                             new List<Expression> { new IntegerLiteral(bound) })));
            VisitStatement(new VarDecl(lenVar, "uint16", new IntegerLiteral(0)));
            runtimeStrVars[qualified] = (lenVar, bound);
        }

        var buf = new VariableExpr(target);
        var pos = new VariableExpr(lenVar);
        void EmitFsCall(string fn, List<Expression> args) =>
            VisitStatement(new AssignStmt(new VariableExpr(lenVar),
                new CallExpr(new MemberAccessExpr(new VariableExpr(strfmtMod), fn), args)));

        string pending = "";
        void FlushLit()
        {
            if (pending.Length == 0) return;
            EmitFsCall("_fs_text", new List<Expression> { buf, pos, new StringLiteral(pending) });
            pending = "";
        }

        foreach (var p in parts)
        {
            if (!p.IsExpr) { pending += p.Text; continue; }
            string? st = StaticStringOf(p.Expr!);
            if (st != null) { pending += st; continue; }
            if (p.Expr is IntegerLiteral il2 && string.IsNullOrEmpty(p.FormatSpec))
            { pending += il2.Value.ToString(); continue; }
            FlushLit();
            if (!string.IsNullOrEmpty(p.FormatSpec))
            {
                var (w, radix, padc, upper) = ParseFormatSpec(p.FormatSpec);
                int flags = (upper ? 0x01 : 0)
                          | (LooksSigned(p.Expr!) ? 0x02 : 0)
                          | (padc == '0' ? 0x04 : 0);
                EmitFsCall("_fs_fmt", new List<Expression>
                {
                    buf, pos, p.Expr!,
                    new IntegerLiteral(radix), new IntegerLiteral(w), new IntegerLiteral(flags),
                });
            }
            else
            {
                EmitFsCall(LooksSigned(p.Expr!) ? "_fs_i32" : "_fs_u32",
                           new List<Expression> { buf, pos, p.Expr! });
            }
        }
        FlushLit();

        // NUL terminator (the bound reserves its byte).
        VisitStatement(new AssignStmt(new IndexExpr(buf, pos), new IntegerLiteral(0)));
        return true;
    }

    // Syntactic signedness of an interpolated expression: a declared-signed variable, a
    // negative literal or a unary minus anywhere in it. Conservative -- unsigned by default
    // (u32 keeps values >= 2^31 correct; a signed-typed variable routes through _fs_i32).
    private bool LooksSigned(Expression e) => e switch
    {
        IntegerLiteral il => il.Value < 0,
        UnaryExpr { Op: Frontend.UnaryOp.Negate } => true,
        UnaryExpr u => LooksSigned(u.Operand),
        BinaryExpr b => LooksSigned(b.Left) || LooksSigned(b.Right),
        VariableExpr v => LookupDeclaredType(v.Name) is DataType.INT8 or DataType.INT16 or DataType.INT32,
        _ => false,
    };

    private DataType? LookupDeclaredType(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)
            && variableTypes.TryGetValue(currentInlinePrefix + name, out var ti)) return ti;
        if (!string.IsNullOrEmpty(currentFunction)
            && variableTypes.TryGetValue(currentFunction + "." + name, out var tf)) return tf;
        return variableTypes.TryGetValue(name, out var t) ? t : null;
    }

    // Invalidate the alias entries a WRITE to `name` (in the current scope) kills.
    // Two parts, deliberately asymmetric:
    //   - The name's OWN value-tracking alias is removed under every qualification it may
    //     have been recorded with (a write creates a new binding; the name always has real
    //     storage to fall back to). Nonlocal write-through aliases are exempt -- there the
    //     alias IS the storage.
    //   - The REVERSE invalidation (aliases whose recorded source is the written name)
    //     applies ONLY to the name this write actually targets in the current scope.
    //     Sweeping all qualifications here destroyed zero-cost @inline param bindings: a
    //     write to the expansion-local `inline1.write_hex.hi` must not kill the caller's
    //     `byte -> main.hi` param alias.
    private void InvalidateAliasesForWrite(string name)
    {
        foreach (var k in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + name,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + name,
            name,
        })
            if (k != null && !writeThroughAliases.Contains(k))
                variableAliases.Remove(k);

        string written = !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name
                       : !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name
                       : name;
        List<string>? stale = null;
        foreach (var kv in variableAliases)
            if (kv.Value == written && !writeThroughAliases.Contains(kv.Key))
                (stale ??= new List<string>()).Add(kv.Key);
        if (stale != null)
            foreach (var k in stale) variableAliases.Remove(k);
    }

    // Register `name = {...}` (dict or set literal) with the standard qualification.
    private void RegisterDictSetBinding(string name, Expression literal)
    {
        string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);
        if (literal is DictExpr de) dictLiteralBindings[qualified] = de;
        else if (literal is SetExpr se) setLiteralBindings[qualified] = se;
    }

    private void VisitVarDecl(VarDecl stmt)
    {
        CheckAnnotationNames(stmt.VarType);

        if (stmt.Init != null && !stmt.VarType.Contains("ptr")
            && !stmt.VarType.Contains("PIORegister"))
            RejectBareRegisterRead(stmt.Init);

        // `d: ... = {...}`: dict/set literal binding, same as the unannotated form.
        if (stmt.Init is DictExpr or SetExpr)
        {
            RegisterDictSetBinding(stmt.Name, stmt.Init);
            return;
        }

        // `s: bytearray = f"..."` (or any annotation) with runtime interpolations: expand
        // into a fixed buffer + strfmt calls, same as the unannotated assignment form.
        if (stmt.Init != null && TryExpandFStringValue(stmt.Name, stmt.Init))
            return;

        // `c: ClassName = ClassName(...)` — a type-annotated instance construction (a typed
        // local parses as a VarDecl). The annotation is just the (redundant) declared type;
        // route through the normal assignment path so the instance->class link and constructor
        // lowering are set up exactly like the unannotated `c = ClassName(...)`. Without this,
        // the annotated form fell through to the scalar path and never registered the instance,
        // so a later `c.method()` mangled to an undefined `c_method` and failed at link.
        if (stmt.Init is CallExpr vdCtor && vdCtor.Callee is VariableExpr vdCallee)
        {
            string vdClass = ResolveCallee(vdCallee.Name);
            if (classNames.Contains(vdClass)
                || inlineFunctions.ContainsKey(vdClass + "___init__")
                || overloadedFunctions.Contains(vdClass + "___init__"))
            {
                VisitAssign(new AssignStmt(new VariableExpr(stmt.Name), stmt.Init) { Line = stmt.Line });
                return;
            }
        }

        // `x: <scalar> = None` is a type error: None is the null value, not an
        // integer. (Reference/Callable/class-typed locals defaulting to None are
        // handled where such optionals are bound, not here.)
        if (stmt.Init is NoneLiteral)
        {
            DataType vt = DataTypeExtensions.StringToDataType(stmt.VarType);
            if (vt is DataType.UINT8 or DataType.INT8 or DataType.UINT16 or DataType.INT16
                  or DataType.UINT32 or DataType.INT32 or DataType.FLOAT)
                throw new TypeError(
                    $"None is not a value of type {stmt.VarType}; None is only valid for " +
                    "comparisons (is/== None) and optional reference parameters",
                    stmt.Line > 0 ? stmt.Line : lastLine, 1);
            string qn = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + stmt.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.Name : stmt.Name);
            noneValuedNames.Add(qn);
            return;
        }

        // A float literal assigned to an integer-typed variable is a mistake — it would
        // otherwise be silently dropped (the store never materializes). Require an explicit
        // cast (e.g. uint8(3.5)) to make the truncation intentional.
        if (stmt.Init is FloatLiteral)
        {
            DataType ft = DataTypeExtensions.StringToDataType(stmt.VarType);
            if (ft is DataType.UINT8 or DataType.INT8 or DataType.UINT16 or DataType.INT16
                  or DataType.UINT32 or DataType.INT32)
                throw new TypeError(
                    $"cannot assign a float literal to integer variable '{stmt.Name}' of type " +
                    $"{stmt.VarType}; use {stmt.VarType}(...) to truncate",
                    stmt.Line > 0 ? stmt.Line : lastLine, 1);
        }

        // A bare `const` (no explicit width) infers its scalar width from the
        // value's magnitude, so a 16/32-bit compile-time constant (e.g. a PWM TOP
        // = clk/freq) is neither rejected by the range check nor truncated to uint8.
        DataType declType = DataTypeExtensions.StringToDataType(stmt.VarType);
        if (stmt.VarType == "const" && stmt.Init != null)
        {
            try
            {
                int cv = EvaluateConstantExpr(stmt.Init);
                if (cv < 0) declType = cv < short.MinValue ? DataType.INT32 : DataType.INT16;
                else if (cv > 0xFFFF) declType = DataType.UINT32;
                else if (cv > 0xFF) declType = DataType.UINT16;
                else declType = DataType.UINT8;
            }
            catch { /* non-constant initializer: keep the default */ }
        }

        CheckIntLiteralRange(stmt.Init, declType, stmt.Line);

        // `bytes` is the immutable spelling of the same fixed buffer, and a b"..." literal
        // reaches the IR as a list of byte values. Both used to fall through to the scalar
        // path and die as "Unknown Expression type: ListExpr" -- a phase name and an AST class
        // name, about the way protocol constants are written on an MCU.
        if (stmt.VarType == "bytearray" || stmt.VarType == "bytes")
        {
            int count = 0;
            var initVals = new List<int>();
            bool isInput = false;
            string inputPrompt = "";
            int inputMaxLen = 64;

            if (stmt.Init != null)
            {
                // A bytes literal carries its own size: b"ab" is two bytes.
                if (stmt.Init is ListExpr bytesLit)
                {
                    count = bytesLit.Elements.Count;
                    foreach (var e in bytesLit.Elements)
                        initVals.Add(TryEvalElemConst(e, out int bv) ? bv : 0);
                }
                else if (stmt.Init is CallExpr call && call.Callee is VariableExpr callee)
                {
                    if (callee.Name == "bytearray" && call.Args.Count > 0)
                    {
                        Expression arg0 = call.Args[0];
                        if (arg0 is ListExpr le)
                        {
                            count = le.Elements.Count;
                            foreach (var e in le.Elements)
                                initVals.Add(TryEvalElemConst(e, out int v) ? v : 0);
                        }
                        // Integer literal or any compile-time constant (bytearray(WINDOW)).
                        else if (TryEvalElemConst(arg0, out int constN))
                        {
                            count = constN;
                            initVals.AddRange(Enumerable.Repeat(0, count));
                        }
                    }
                    else if (callee.Name == "input")
                    {
                        isInput = true;
                        foreach (var arg in call.Args)
                        {
                            if (arg is StringLiteral inputSl)
                                inputPrompt = inputSl.Value;
                            else if (arg is IntegerLiteral inputIl)
                                inputMaxLen = inputIl.Value;
                            else if (arg is KeywordArgExpr kw)
                            {
                                if (kw.Key == "prompt" && kw.Value is StringLiteral ksl) inputPrompt = ksl.Value;
                                else if (kw.Key == "maxlen" && kw.Value is IntegerLiteral kil) inputMaxLen = kil.Value;
                            }
                            else
                                throw UserError("input(): arguments must be compile-time string literal (prompt) and/or integer (maxlen)");
                        }
                        count = inputMaxLen;
                        initVals.AddRange(Enumerable.Repeat(0, count));
                    }
                }
            }

            if (count <= 0) throw UserError("bytearray: could not determine buffer size from initializer.");

            string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + stmt.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.Name : stmt.Name);

            arraySizes[qualified] = count;
            arrayElemTypes[qualified] = DataType.UINT8;
            variableTypes[qualified] = DataType.UINT8;
            arraysWithVariableIndex.Add(qualified);

            if (string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix))
                moduleSramArrays.Add(qualified);

            for (int k = 0; k < count; ++k)
                Emit(new ArrayStore(qualified, new Constant(k), new Constant(initVals[k]), DataType.UINT8, count));

            if (isInput)
            {
                // Emit prompt via print_str (console) or uart_write_str (fallback).
                if (!string.IsNullOrEmpty(inputPrompt))
                {
                    string writeStrFn = ResolveCallee("print_str");
                    if (writeStrFn == "print_str")
                    {
                        writeStrFn = ResolveCallee("uart_write_str");
                        if (writeStrFn == "uart_write_str")
                        {
                            foreach (var fnName in inlineFunctions.Keys)
                            {
                                if (fnName.EndsWith("_print_str") || fnName.EndsWith("_uart_write_str"))
                                { writeStrFn = fnName; break; }
                            }
                        }
                    }
                    VisitCall(new CallExpr(
                        new VariableExpr(writeStrFn),
                        new List<Expression> { new StringLiteral(inputPrompt) }));
                }

                // Emit uart_read_line(buf, maxlen) via VisitCall so that the inline
                // expansion runs, instead of emitting a bare Call IR node.
                string readLineFn = ResolveCallee("uart_read_line");
                if (readLineFn == "uart_read_line")
                {
                    foreach (var fnName in inlineFunctions.Keys)
                    {
                        if (fnName.EndsWith("_uart_read_line")) { readLineFn = fnName; break; }
                    }
                }
                VisitCall(new CallExpr(
                    new VariableExpr(readLineFn),
                    new List<Expression> { new VariableExpr(stmt.Name), new IntegerLiteral(inputMaxLen) }));
            }

            return;
        }

        DataType dt = declType;
        string q2 = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + stmt.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.Name : stmt.Name);
        variableTypes[q2] = dt;

        if (stmt.VarType == "str" && stmt.Init is StringLiteral sl)
        {
            strConstantVariables[q2] = sl.Value;
        }

        if (stmt.Init != null)
        {
            // Callable-typed variable: auto-wrap bare function name as FunctionRef
            if (dt == DataType.FUNCREF && stmt.Init is VariableExpr fnExpr)
            {
                string rhsKey = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + fnExpr.Name
                    : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + fnExpr.Name : fnExpr.Name);
                bool isAlreadyFuncref = variableTypes.TryGetValue(rhsKey, out DataType rhsDt) && rhsDt == DataType.FUNCREF;
                if (!isAlreadyFuncref)
                {
                    string fnName = ResolveCallee(fnExpr.Name);
                    Emit(new Copy(new FunctionRef(fnName), new Variable(q2, DataType.FUNCREF)));
                    // Remember the pointee's return width so an ICALL through q2 doesn't
                    // truncate a uint16/int16 return to the default uint8 result temp.
                    if (functionReturnTypes.TryGetValue(fnName, out var frt) && frt != null)
                        funcrefReturnTypes[q2] = DataTypeExtensions.StringToDataType(frt);
                    return;
                }
            }
            Val val = VisitExpression(stmt.Init);

            // A compile-time float result assigned to an integer variable (e.g.
            // `y: uint8 = 5 // 2.0`) is the same mistake as a bare float literal, but the
            // literal check above only sees a direct FloatLiteral — a folded FloatConstant
            // slipped through and the Copy was silently dropped. Require an explicit cast.
            if (val is FloatConstant
                && dt is DataType.UINT8 or DataType.INT8 or DataType.UINT16 or DataType.INT16
                      or DataType.UINT32 or DataType.INT32)
                throw new TypeError(
                    $"cannot assign a float value to integer variable '{stmt.Name}' of type " +
                    $"{stmt.VarType}; use {stmt.VarType}(...) to truncate",
                    stmt.Line > 0 ? stmt.Line : lastLine, 1);

            // Declarations bind the name itself -- never a stale value-tracking alias
            // (same invalidation-before-resolve as EmitScalarVarAssign).
            InvalidateAliasesForWrite(stmt.Name);
            Val target = ResolveBinding(stmt.Name);
            if (target is Variable v) target = v with { Type = dt };
            Emit(new Copy(val, target));

            if (string.IsNullOrEmpty(currentFunction))
            {
                if (val is Constant c && target is Variable tv && !mutableGlobals.ContainsKey(tv.Name))
                {
                    constantVariables[tv.Name] = c.Value;
                }
            }
        }
    }

    // Resolves a member access (self._buf) to the flattened SRAM array name it
    // was declared under via `self._buf: uint8[N]`, or null if it is not an
    // instance-member array. Visiting the object (self) only resolves an alias
    // and has no side effects.
    private string? ResolveMemberArrayName(MemberAccessExpr mem)
    {
        var objVal = VisitExpression(mem.Object);
        string? baseName = objVal is Variable v ? v.Name : (objVal is Temporary t ? t.Name : "");
        while (baseName != null && variableAliases.TryGetValue(baseName, out var alias)) baseName = alias;
        if (string.IsNullOrEmpty(baseName)) return null;
        string flat = baseName + "_" + mem.Member;
        return arraySizes.ContainsKey(flat) ? flat : null;
    }

    // Resolves an array-size annotation token to a constant: a literal ("24"),
    // a compile-time constant identifier ("n", e.g. a folded constructor param),
    // or a product of either ("n*3", "3*n").
    private int ResolveArraySizeExpr(string token)
    {
        int star = token.IndexOf('*');
        if (star >= 0)
            return ResolveArraySizeAtom(token[..star]) * ResolveArraySizeAtom(token[(star + 1)..]);
        return ResolveArraySizeAtom(token);
    }

    private int ResolveArraySizeAtom(string atom)
    {
        atom = atom.Trim();
        if (atom.Length > 0 && atom.All(char.IsDigit)) return int.Parse(atom);
        if (constantVariables.TryGetValue(currentInlinePrefix + atom, out int cv)) return cv;
        if (constantVariables.TryGetValue(atom, out int cv2)) return cv2;
        throw UserError("Array size '" + atom + "' is not a compile-time constant");
    }

    // The tracking key for a boxed (slot) ZCA instance. Normally the function-qualified
    // name -- but a MODULE-LEVEL instance in a top-level script is registered by
    // ScanGlobals as a module global, and every later reference resolves to that module
    // name. Registering the slot under the synthesized-main qualified name ("main.a")
    // while call sites resolve "a" made the slot lookup miss, so outlined methods fell
    // back to passing the flattened field VALUES -- and silently mutated copies.
    private string SlotInstanceKey(string name)
    {
        // A module-level `a = Acc()` executes inside the synthesized/explicit main (as
        // module init), but every later reference resolves the name as a MODULE global.
        // Track the instance under its module key so the slot lookup at method call
        // sites hits instead of falling back to flattened by-value fields.
        if (!string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix)
            && topLevelInstanceTargets.Contains(name))
            return currentModulePrefix + name;
        return !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);
    }

    // RFC 0001 Model B (SRAM slot): box a multi-field ZCA. Allocate a fixed SRAM byte slot
    // for the instance and store each field at its byte offset, mapping the field's source
    // __init__ parameter to the corresponding constructor argument. Tracks the instance so
    // its @outline (self-ptr) methods receive the slot base address as `self`.
    private void EmitSlotConstruction(VariableExpr targetVar, string cls, List<Expression> args)
    {
        string qn = SlotInstanceKey(targetVar.Name);
        string slot = qn + "__slot";

        var layout = classFieldLayout[cls];
        int total = layout.Sum(f => DataTypeExtensions.StringToDataType(f.Type).SizeOf());

        arraySizes[slot] = total;
        arrayElemTypes[slot] = DataType.UINT8;
        moduleSramArrays.Add(slot);

        functionParams.TryGetValue(cls + "___init__", out var initParams);
        int off = 0;
        foreach (var (field, type, srcParam) in layout)
        {
            int argIdx = 0;
            if (initParams != null && !string.IsNullOrEmpty(srcParam))
            {
                int pIdx = initParams.IndexOf(srcParam);
                if (pIdx >= 1) argIdx = pIdx - 1; // drop implicit self
            }

            Val v = argIdx < args.Count ? VisitExpression(args[argIdx]) : new Constant(0);
            EmitSlotFieldStore(slot, false, off, DataTypeExtensions.StringToDataType(type), v, total,
                byteWise: true);
            off += DataTypeExtensions.StringToDataType(type).SizeOf();
        }

        instanceClasses[qn] = cls;
        slotInstances[qn] = slot;
    }

    // RFC 0001 Model B (sret): `s = make(args)` for a multi-field (slot) ZCA factory. Allocate
    // the slot at the call site, pass its base address as the hidden __self pointer (first arg),
    // and track s as a slot instance. The factory stores the fields through __self; we discard
    // its returned pointer since we already hold the slot.
    private void EmitSlotFactoryCall(VariableExpr targetVar, string facFn, string cls,
        List<Expression> args)
    {
        string qn = SlotInstanceKey(targetVar.Name);
        string slot = qn + "__slot";

        var layout = classFieldLayout[cls];
        int total = layout.Sum(f => DataTypeExtensions.StringToDataType(f.Type).SizeOf());
        arraySizes[slot] = total;
        arrayElemTypes[slot] = DataType.UINT8;
        moduleSramArrays.Add(slot);

        var callArgs = new List<Val> { new ArrayBase(slot) };
        foreach (var a in args) callArgs.Add(VisitExpression(a));
        Emit(new Call(facFn, callArgs, new NoneVal()));

        instanceClasses[qn] = cls;
        slotInstances[qn] = slot;
    }

    // RFC 0001 Model B (Class[N]): construct element `index` of an instance array in place,
    // storing each field at index*stride + fieldOffset (flat byte offsets, since the array is a
    // contiguous UINT8 SRAM block). Constant index folds the offset; a runtime index computes it.
    private void EmitInstanceArrayStore(string arrQ, string cls, Expression indexExpr,
        List<Expression> args)
    {
        var layout = classFieldLayout[cls];
        int stride = instanceArrayStride[arrQ];
        int total = arraySizes[arrQ];
        functionParams.TryGetValue(cls + "___init__", out var init);

        Val idx = VisitExpression(indexExpr);
        var idxConst = idx as Constant;
        int off = 0;
        foreach (var (field, type, srcParam) in layout)
        {
            int argIdx = 0;
            if (init != null && !string.IsNullOrEmpty(srcParam))
            {
                int p = init.IndexOf(srcParam);
                if (p >= 1) argIdx = p - 1;
            }

            Val v = argIdx < args.Count ? VisitExpression(args[argIdx]) : new Constant(0);
            Val byteOff;
            if (idxConst != null)
            {
                byteOff = new Constant(idxConst.Value * stride + off);
            }
            else
            {
                Temporary scaled = MakeTemp(DataType.UINT16);
                Emit(new Binary(BinaryOp.Mul, idx, new Constant(stride), scaled));
                Temporary addr = MakeTemp(DataType.UINT16);
                Emit(new Binary(BinaryOp.Add, scaled, new Constant(off), addr));
                byteOff = addr;
            }

            // Store the field at its declared width, splitting a multi-byte value into
            // consecutive bytes (a uint16/uint32 element field was otherwise truncated to 1 byte).
            DataType fdt = DataTypeExtensions.StringToDataType(type);
            int fsz = fdt.SizeOf();
            for (int k = 0; k < fsz; ++k)
            {
                Temporary b = MakeTemp(DataType.UINT8);
                if (k == 0)
                {
                    Emit(new Copy(v, b));
                }
                else
                {
                    Temporary sh = MakeTemp(fdt);
                    Emit(new Binary(BinaryOp.RShift, v, new Constant(8 * k), sh));
                    Emit(new Copy(sh, b));
                }

                Val offK = byteOff;
                if (k > 0)
                {
                    if (byteOff is Constant bc) offK = new Constant(bc.Value + k);
                    else
                    {
                        Temporary a = MakeTemp(DataType.UINT16);
                        Emit(new Binary(BinaryOp.Add, byteOff, new Constant(k), a));
                        offK = a;
                    }
                }

                Emit(new ArrayStore(arrQ, offK, b, DataType.UINT8, total));
            }

            off += fdt.SizeOf();
        }
    }

    /// <summary>
    /// Reject an annotation that names no type this compiler knows. An unknown name used to
    /// fall back to uint8 without a word, so `x: unit8 = a * 300` truncated to 8 bits and
    /// printed 96 where the same line without an annotation printed 60000 -- a typo in one
    /// character silently changed the arithmetic. Only bare identifiers are checked: anything
    /// with brackets is a form (`uint8[4]`, `const[uint8]`, `list[uint8]`) whose own handling
    /// reports what it cannot make sense of.
    /// </summary>
    private void CheckAnnotationNames(string annotation)
    {
        if (string.IsNullOrEmpty(annotation) || annotation.IndexOf('[') >= 0) return;
        if (ScalarTypeNames.Contains(annotation)) return;
        if (annotation is "ptr" or "object" or "self") return;
        if (classNames.Contains(annotation) || classFieldLayout.ContainsKey(annotation)) return;
        if (importedAliases.ContainsKey(annotation) || aliasToOriginal.ContainsKey(annotation)) return;
        if (classNames.Any(c => c.EndsWith("." + annotation, StringComparison.Ordinal)
                                || c.EndsWith("_" + annotation, StringComparison.Ordinal))) return;
        if (ResolveCallee(annotation) is { } resolved
            && (classNames.Contains(resolved) || classFieldLayout.ContainsKey(resolved))) return;

        string? near = ScalarTypeNames.Concat(classNames)
            .Where(n => EditDistance(n, annotation) <= 2)
            .OrderBy(n => EditDistance(n, annotation))
            .FirstOrDefault();
        throw UserError($"unknown type '{annotation}' in the annotation"
            + (near != null ? $" (did you mean '{near}'?)" : "")
            + ". An unrecognized annotation used to be read as uint8, which changed the "
            + "arithmetic without saying so.");
    }

    private void VisitAnnAssign(AnnAssign stmt)
    {
        CheckAnnotationNames(stmt.Annotation);

        // A `const[...]` annotation marks the name immutable; record it so a later
        // assignment to it is rejected (see VisitAssign's reassignment guard).
        if (!stmt.Target.Contains('.') && IsConstType(stmt.Annotation))
            declaredConstants.Add(stmt.Target);

        // Instance-member array declaration (self._buf: uint8[N]): reserve a
        // per-instance SRAM framebuffer. The parser encodes the target as a
        // dotted name ("self._buf"); resolve the instance to its flattened
        // storage name (exactly like a normal `self.x = ...` member) and
        // register it as a variable-indexed (SRAM) array so pixels[i] works.
        if (stmt.Target.Contains('.'))
        {
            int dot = stmt.Target.IndexOf('.');
            string objName = stmt.Target.Substring(0, dot);
            string member = stmt.Target.Substring(dot + 1);

            int mb = stmt.Annotation.IndexOf('[');
            int mc = stmt.Annotation.LastIndexOf(']');
            if (mb == -1 || mc != stmt.Annotation.Length - 1 || mc <= mb + 1)
                throw UserError("Instance-member annotation must be an array type, e.g. uint8[N]");
            string memSz = stmt.Annotation.Substring(mb + 1, mc - mb - 1);
            int memCount = ResolveArraySizeExpr(memSz);
            DataType memElem = DataTypeExtensions.StringToDataType(stmt.Annotation.Substring(0, mb));

            var objVal = VisitExpression(new VariableExpr(objName));
            string? baseName = objVal is Variable v ? v.Name : (objVal is Temporary t ? t.Name : "");
            while (baseName != null && variableAliases.TryGetValue(baseName, out var alias)) baseName = alias;
            if (string.IsNullOrEmpty(baseName))
                throw UserError("Cannot resolve instance for member array '" + stmt.Target + "'");
            string flat = baseName + "_" + member;

            arraySizes[flat] = memCount;
            arrayElemTypes[flat] = memElem;
            variableTypes[flat] = memElem;
            arraysWithVariableIndex.Add(flat);

            // Zero-initialise (a NeoPixel strip starts all-off), or apply a
            // literal list initialiser when one is supplied.
            var memInit = new List<int>(Enumerable.Repeat(0, memCount));
            if (stmt.Value is ListExpr mle)
                for (int k = 0; k < Math.Min(memCount, mle.Elements.Count); k++)
                    if (TryEvalElemConst(mle.Elements[k], out int mv)) memInit[k] = mv;
            for (int k = 0; k < memCount; ++k)
                Emit(new ArrayStore(flat, new Constant(k), new Constant(memInit[k]), memElem, memCount));
            return;
        }

        // const[uint8[N]] annotation → flash (PROGMEM) array.
        if (stmt.Annotation.StartsWith("const[") && stmt.Annotation.EndsWith("]"))
        {
            string constInner = stmt.Annotation.Substring(6, stmt.Annotation.Length - 7);
            int ciB = constInner.IndexOf('[');
            int ciC = constInner.LastIndexOf(']');
            if (ciB != -1 && ciC == constInner.Length - 1 && ciC > ciB + 1)
            {
                string ciNum = constInner.Substring(ciB + 1, ciC - ciB - 1);
                if (!string.IsNullOrEmpty(ciNum) && ciNum.All(char.IsDigit))
                {
                    int count = int.Parse(ciNum);
                    DataType elemDt = DataTypeExtensions.StringToDataType(constInner.Substring(0, ciB));
                    if (elemDt == DataType.UINT8)
                    {
                        string qualified = string.IsNullOrEmpty(currentFunction)
                            ? stmt.Target
                            : currentFunction + "." + stmt.Target;
                        // Synthesized main: fall back to the module-level name registered by ScanGlobals.
                        if (!flashArrays.Contains(qualified) && flashArrays.Contains(stmt.Target))
                            qualified = stmt.Target;
                        arraySizes[qualified] = count;
                        arrayElemTypes[qualified] = elemDt;
                        variableTypes[qualified] = elemDt;
                        flashArrays.Add(qualified);

                        var bytes = new List<int>(Enumerable.Repeat(0, count));
                        if (stmt.Value is ListExpr le)
                        {
                            for (int k = 0; k < Math.Min(count, le.Elements.Count); k++)
                                if (TryEvalElemConst(le.Elements[k], out int v)) bytes[k] = v;
                        }
                        Emit(new FlashData(qualified, bytes));
                        return;
                    }
                }
            }
        }

        if (stmt.Annotation == "bytearray")
        {
            int count = 0;
            var initVals = new List<int>();

            if (stmt.Value != null && stmt.Value is CallExpr call && call.Callee is VariableExpr callee &&
                callee.Name == "bytearray" && call.Args.Count > 0)
            {
                var arg0 = call.Args[0];
                if (arg0 is ListExpr le)
                {
                    count = le.Elements.Count;
                    foreach (var e in le.Elements) initVals.Add(TryEvalElemConst(e, out int v) ? v : 0);
                }
                // Integer literal or any compile-time constant (bytearray(WINDOW)).
                else if (TryEvalElemConst(arg0, out int constN))
                {
                    count = constN;
                    initVals.AddRange(Enumerable.Repeat(0, count));
                }
            }

            if (count <= 0) throw UserError("bytearray: could not determine buffer size from initializer.");
            string qualified = string.IsNullOrEmpty(currentFunction)
                ? stmt.Target
                : currentFunction + "." + stmt.Target;
            // Synthesized main: fall back to the module-level name registered by ScanGlobals.
            if (!arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(stmt.Target))
                qualified = stmt.Target;
            arraySizes[qualified] = count;
            arrayElemTypes[qualified] = DataType.UINT8;
            variableTypes[qualified] = DataType.UINT8;
            arraysWithVariableIndex.Add(qualified);

            for (int k = 0; k < count; ++k)
                Emit(new ArrayStore(qualified, new Constant(k), new Constant(initVals[k]), DataType.UINT8, count));
            return;
        }

        // list[T] annotation → heap-allocated GC list
        if (stmt.Annotation.StartsWith("list[") && stmt.Annotation.EndsWith("]")) { EmitListAnnAssign(stmt); return; }

        int bracket = stmt.Annotation.IndexOf('[');
        int close = stmt.Annotation.LastIndexOf(']');
        if (bracket != -1 && close != -1 && close == stmt.Annotation.Length - 1 && close > bracket + 1
            && EmitFixedArrayAnnAssign(stmt, bracket, close)) return;

        DataType type = DataType.UINT8;
        bool isPtrAnnotation = stmt.Annotation.StartsWith("ptr[") && stmt.Annotation.EndsWith("]");
        DataType ptrElemType = DataType.UINT8;
        if (isPtrAnnotation)
        {
            string inner = stmt.Annotation.Substring(4, stmt.Annotation.Length - 5);
            ptrElemType = DataTypeExtensions.StringToDataType(inner);
        }

        if (stmt.Annotation.Contains("ptr[uint16]")) type = DataType.UINT16;
        else if (stmt.Annotation.Contains("ptr[uint32]")) type = DataType.UINT16; // ptr var holds a 16-bit address on AVR
        else if (stmt.Annotation.Contains("uint16")) type = DataType.UINT16;
        else if (stmt.Annotation.Contains("uint32")) type = DataType.UINT32;
        else if (stmt.Annotation == "Callable") type = DataType.FUNCREF;

        // A bare `const` (no explicit width) infers its width from the value's
        // magnitude, so a 16/32-bit compile-time constant (e.g. a PWM TOP =
        // clk/freq) is not silently truncated to uint8.
        if (stmt.Annotation == "const" && stmt.Value != null)
        {
            try
            {
                int cv = EvaluateConstantExpr(stmt.Value);
                if (cv < 0) type = cv < short.MinValue ? DataType.INT32 : DataType.INT16;
                else if (cv > 0xFFFF) type = DataType.UINT32;
                else if (cv > 0xFF) type = DataType.UINT16;
            }
            catch { /* non-constant initializer: keep the uint8 default */ }
        }

        // An unannotated module-level binding reaches here as an AnnAssign with an EMPTY
        // annotation (the module-init pass rewrites the VarDecl that way), and the uint8
        // default above is what truncated it: `b = 5` then `b = 300` stored 44. Take the width
        // the global scan computed from every literal assigned to the name.
        if (string.IsNullOrEmpty(stmt.Annotation)
            && mutableGlobals.TryGetValue(currentModulePrefix + stmt.Target, out var scannedWidth)
            && scannedWidth != DataType.UNKNOWN)
            type = scannedWidth;

        string qualified2 = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + stmt.Target
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.Target : stmt.Target);
        // When processing a top-level AnnAssign inside a synthesized (or explicit) main() body,
        // the variable may already be registered as a module-level mutable global by ScanGlobals.
        // Use the global name so we emit an initializer for the global rather than creating a
        // shadowing function-local variable.
        if (!string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix))
        {
            string mutableGlobalKey = currentModulePrefix + stmt.Target;
            if (mutableGlobals.ContainsKey(mutableGlobalKey))
                qualified2 = mutableGlobalKey;
        }
        variableTypes[qualified2] = type;

        if (stmt.Annotation == "str" && stmt.Value is StringLiteral sl2) strConstantVariables[qualified2] = sl2.Value;

        if (stmt.Value != null)
        {
            Val rhs = VisitExpression(stmt.Value);

            // For ptr[T] = ptr(constant), register the constant address and element type;
            // do not emit a Copy (the "variable" is a compile-time address constant).
            if (isPtrAnnotation && rhs is MemoryAddress ptrAddr)
            {
                constantAddressVariables[qualified2] = ptrAddr.Address;
                variableTypes[qualified2] = ptrElemType;
                return;
            }

            // ptr[T] = <runtime address> (e.g. ptr(BASE + x) with a non-constant offset):
            // the variable holds a runtime address whose width is the chip's native
            // pointer size (16-bit on AVR, 32-bit on Cortex-M / RISC-V). Using UINT16
            // unconditionally truncated 32-bit MMIO addresses on RP2040/RP2350. Record
            // it as a runtime pointer so a later `.value` read/write/aug-assign lowers
            // to Load/StoreIndirect.
            if (isPtrAnnotation)
            {
                DataType ptrAddrType = DataTypeExtensions.PointerWidth >= 4 ? DataType.UINT32 : DataType.UINT16;
                Emit(new Copy(rhs, new Variable(qualified2, ptrAddrType)));
                variableTypes[qualified2] = ptrAddrType;
                runtimePtrVars[qualified2] = ptrElemType;
                return;
            }

            if (rhs is MemoryAddress addr) rhs = addr with { Type = type };
            Emit(new Copy(rhs, new Variable(qualified2, type)));

            // Propagate string constant from rhs to the declared variable so that
            // downstream match/case DCE (e.g. select_port) can fold it.
            // Handles:  pin_name: str = _arduino_pin_name(13)
            if (stmt.Annotation == "str" && !strConstantVariables.ContainsKey(qualified2))
            {
                string? sv = rhs is Temporary tRhs ? ResolveStrConstant(tRhs.Name)
                           : rhs is Variable  vRhs ? ResolveStrConstant(vRhs.Name)
                           : null;
                if (sv == null && rhs is Constant cRhs && stringIdToStr.TryGetValue(cRhs.Value, out var cs))
                    sv = cs;
                if (sv != null) strConstantVariables[qualified2] = sv;
            }
        }
    }

    // list[T] annotation -> a heap-allocated GC list (bounded bump allocator).
    private void EmitListAnnAssign(AnnAssign stmt)
    {
        string elemTypeName = stmt.Annotation.Substring(5, stmt.Annotation.Length - 6);
        DataType elemDt = DataTypeExtensions.StringToDataType(elemTypeName);
        int elemSize = elemDt.SizeOf();

        // A growable list is heap-allocated and only the AVR backend has the collector. Said
        // at the declaration, with the program's own words: the phase that used to catch this
        // ran much later and answered in terms of GC_REF and gc_alloc, which mean nothing from
        // the program's side, without naming the variable or the line. A fixed array is the
        // shape that works everywhere, and it is what the reader wanted here anyway.
        string arch = deviceConfig?.Arch ?? "";
        if (arch != "avr" && arch != "")
        {
            int knownSize = stmt.Value is ListExpr initList ? initList.Elements.Count : 0;
            string sizeShown = knownSize > 0 ? knownSize.ToString() : "N";
            throw UserError(
                $"'{stmt.Target}: {stmt.Annotation}' needs a growable list, which is "
                + $"heap-allocated and only implemented on AVR (this target is {arch}). "
                + $"Use a fixed array instead: `{stmt.Target}: {elemTypeName}[{sizeShown}] = [...]`");
        }

        string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + stmt.Target
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.Target : stmt.Target);

        listVarElemTypes[qualified] = elemDt;
        variableTypes[qualified] = DataType.GC_REF;

        if (stmt.Value != null)
        {
            int capacity = 8;
            List<Val>? initElements = null;

            if (stmt.Value is CallExpr listCall && listCall.Callee is VariableExpr calleeV &&
                calleeV.Name == "list")
            {
                if (listCall.Args.Count == 1 && listCall.Args[0] is IntegerLiteral capLit)
                    capacity = capLit.Value;
            }
            else if (stmt.Value is ListExpr le)
            {
                initElements = new List<Val>();
                foreach (var e in le.Elements)
                    initElements.Add(VisitExpression(e));
                if (le.Elements.Count > capacity) capacity = le.Elements.Count;
            }

            int allocSize = 2 + capacity * elemSize;
            Temporary tmpPtr = MakeTemp(DataType.GC_REF);
            Emit(new GcAlloc(new Constant(allocSize), tmpPtr));

            int initCount = initElements?.Count ?? 0;
            EmitListStore(tmpPtr, 0, new Constant(initCount));
            EmitListStore(tmpPtr, 1, new Constant(capacity));

            if (initElements != null)
            {
                for (int k = 0; k < initElements.Count; k++)
                    EmitListStore(tmpPtr, 2 + k * elemSize, initElements[k], elemDt);
            }

            Emit(new Copy(tmpPtr, new Variable(qualified, DataType.GC_REF)));
        }

        return;
    }

    // Fixed-size array annotation `T[N]` (incl. Class[N] slot arrays and Callable[N]):
    // register sizes/types and emit element initializers. Returns true when handled;
    // false to fall through (e.g. a ptr[...] annotation handled by the scalar path).
    // Evaluate a list-literal initializer element to a constant int, handling negatives
    // (`-1` is UnaryExpr(Negate), not an IntegerLiteral) and constant expressions — not just a
    // bare IntegerLiteral. Returns false if it is not a compile-time constant (left as 0).
    private bool TryEvalElemConst(Expression e, out int value)
    {
        try { value = EvaluateConstantExpr(e); return true; }
        catch { value = 0; return false; }
    }

    private bool EmitFixedArrayAnnAssign(AnnAssign stmt, int bracket, int close)
    {
        string inner = stmt.Annotation.Substring(bracket + 1, close - bracket - 1);

        // RFC 0001 Model B (Class[N]): array of boxed ZCA instances. Lay out N contiguous
        // slots (count * stride bytes) as a flat SRAM byte array; record the element class
        // and stride so arr[i] = C(..) constructs into element i and arr[i].method() passes
        // the element address as self.
        string elemAnno = stmt.Annotation.Substring(0, bracket);
        // A Class[N] of a multi-field class is an instance (slot) array. The class qualifies via
        // slotClasses (multi-field with an outlined method) OR simply by having >= 2 fields -- a
        // pure-data struct with no methods is never added to slotClasses, but its Class[N] array
        // still needs the contiguous slot layout, else arr[i] falls back to a value-array and a
        // runtime index / field access fails.
        bool elemIsMultiField = slotClasses.Contains(elemAnno)
            || (classFieldLayout.TryGetValue(elemAnno, out var elemLay) && elemLay.Count >= 2);
        if (!string.IsNullOrEmpty(inner) && inner.All(char.IsDigit) && elemIsMultiField)
        {
            int n = int.Parse(inner);
            var layout = classFieldLayout[elemAnno];
            int stride = layout.Sum(f => DataTypeExtensions.StringToDataType(f.Type).SizeOf());
            string arrQ = string.IsNullOrEmpty(currentFunction)
                ? stmt.Target : currentFunction + "." + stmt.Target;
            arraySizes[arrQ] = n * stride;
            arrayElemTypes[arrQ] = DataType.UINT8;
            variableTypes[arrQ] = DataType.UINT8;
            arraysWithVariableIndex.Add(arrQ);
            moduleSramArrays.Add(arrQ);
            instanceArrayClass[arrQ] = elemAnno;
            instanceArrayStride[arrQ] = stride;
            return true;
        }

        if (!string.IsNullOrEmpty(inner) && inner.All(char.IsDigit))
        {
            int count = int.Parse(inner);
            DataType elemDt = DataTypeExtensions.StringToDataType(stmt.Annotation.Substring(0, bracket));
            string qualified = string.IsNullOrEmpty(currentFunction)
                ? stmt.Target
                : currentFunction + "." + stmt.Target;
            // Synthesized main: fall back to the module-level name registered by ScanGlobals.
            if (!arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(stmt.Target))
                qualified = stmt.Target;
            arraySizes[qualified] = count;
            arrayElemTypes[qualified] = elemDt;
            variableTypes[qualified] = elemDt;

            // Callable[N]: array of function references stored in SRAM.
            if (elemDt == DataType.FUNCREF)
            {
                bool isSramCallable = arraysWithVariableIndex.Contains(qualified) || moduleSramArrays.Contains(qualified);
                if (stmt.Value is ListExpr callableList)
                {
                    for (int k = 0; k < count; ++k)
                    {
                        Val fnVal;
                        if (k < callableList.Elements.Count)
                        {
                            var elem = callableList.Elements[k];
                            if (elem is VariableExpr fnVe)
                                fnVal = new FunctionRef(fnVe.Name);
                            else if (elem is CallExpr funcrefCall
                                     && funcrefCall.Callee is VariableExpr funcrefCallee
                                     && funcrefCallee.Name == "funcref"
                                     && funcrefCall.Args.Count == 1
                                     && funcrefCall.Args[0] is VariableExpr refVe)
                                fnVal = new FunctionRef(refVe.Name);
                            else
                                fnVal = new Constant(0);
                        }
                        else
                        {
                            fnVal = new Constant(0);
                        }

                        if (isSramCallable)
                            Emit(new ArrayStore(qualified, new Constant(k), fnVal, DataType.FUNCREF, count));
                        else
                        {
                            string elemName = qualified + "__" + k;
                            variableTypes[elemName] = DataType.FUNCREF;
                            Emit(new Copy(fnVal, new Variable(elemName, DataType.FUNCREF)));
                        }
                    }
                }
                return true;
            }

            var initVals = new List<int>(Enumerable.Repeat(0, count));
            var runtimeElems = new Dictionary<int, Expression>();
            if (stmt.Value != null)
            {
                if (stmt.Value is ListCompExpr lc)
                {
                    VisitListComp(lc, qualified, count, elemDt);
                    return true;
                }

                if (stmt.Value is IndexExpr idxRhs && idxRhs.Index is SliceExpr sl &&
                    idxRhs.Target is VariableExpr srcVe)
                {
                    string srcQ = string.IsNullOrEmpty(currentFunction)
                        ? srcVe.Name
                        : currentFunction + "." + srcVe.Name;
                    if (!arraySizes.ContainsKey(srcQ) && arraySizes.ContainsKey(srcVe.Name)) srcQ = srcVe.Name;
                    if (arraySizes.TryGetValue(srcQ, out int srcSize))
                    {
                        DataType srcEdt = arrayElemTypes[srcQ];
                        int start = sl.Start != null ? EvaluateConstantExpr(sl.Start) : 0;
                        int stop = sl.Stop != null ? EvaluateConstantExpr(sl.Stop) : srcSize;
                        int step = sl.Step != null ? EvaluateConstantExpr(sl.Step) : 1;
                        if (step == 0) throw UserError("Slice step cannot be zero");
                        if (start < 0) start += srcSize;
                        if (stop < 0) stop += srcSize;
                        start = Math.Max(0, Math.Min(start, srcSize));
                        stop = Math.Max(0, Math.Min(stop, srcSize));
                        bool srcSram = arraysWithVariableIndex.Contains(srcQ) || moduleSramArrays.Contains(srcQ);
                        int k = 0;
                        for (int i = start; (step > 0 ? i < stop : i > stop) && k < count; i += step, ++k)
                        {
                            string dstElem = qualified + "__" + k;
                            variableTypes[dstElem] = elemDt;
                            Val srcVal;
                            if (srcSram)
                            {
                                Temporary tmp = MakeTemp(srcEdt);
                                Emit(new ArrayLoad(srcQ, new Constant(i), tmp, srcEdt, srcSize));
                                srcVal = tmp;
                            }
                            else srcVal = new Variable(srcQ + "__" + i, srcEdt);

                            Emit(new Copy(srcVal, new Variable(dstElem, elemDt)));
                        }

                        for (; k < count; ++k)
                        {
                            string dstElem = qualified + "__" + k;
                            variableTypes[dstElem] = elemDt;
                            Emit(new Copy(new Constant(0), new Variable(dstElem, elemDt)));
                        }

                        return true;
                    }

                    throw UserError("Slice initializer target must be a named fixed-size array");
                }

                if (stmt.Value is ListExpr le)
                {
                    for (int k = 0; k < Math.Min(count, le.Elements.Count); ++k)
                        if (TryEvalElemConst(le.Elements[k], out int v)) initVals[k] = v;
                    arrayLiteralElements[qualified] = le.Elements;

                    // An element the folder cannot reduce still has a value at run time.
                    // Only the constants were being stored, so `data: uint8[2] = [a, b]` with
                    // a and b read from registers filled the array with zeros: data[0] and
                    // data[1] both read 0, and sum(data) added nothing.
                    for (int k = 0; k < Math.Min(count, le.Elements.Count); ++k)
                        if (!TryEvalElemConst(le.Elements[k], out _))
                            runtimeElems[k] = le.Elements[k];
                }

                if (stmt.Value is BinaryExpr be && be.Op == Frontend.BinaryOp.Mul && be.Left is ListExpr leRep &&
                    be.Right is IntegerLiteral repeatLit && repeatLit.Value > 0)
                {
                    for (int k = 0; k < count; ++k)
                    {
                        int srcIdx = k % leRep.Elements.Count;
                        if (srcIdx < leRep.Elements.Count && TryEvalElemConst(leRep.Elements[srcIdx], out int v))
                            initVals[k] = v;
                    }
                }
            }

            Val ElemInit(int k) => runtimeElems.TryGetValue(k, out var e)
                ? VisitExpression(e)
                : new Constant(initVals[k]);

            if (arraysWithVariableIndex.Contains(qualified) || moduleSramArrays.Contains(qualified))
            {
                for (int k = 0; k < count; ++k)
                    Emit(new ArrayStore(qualified, new Constant(k), ElemInit(k), elemDt, count));
            }
            else
            {
                for (int k = 0; k < count; ++k)
                {
                    string elemName = qualified + "__" + k;
                    var elemVar = new Variable(elemName, elemDt);
                    variableTypes[elemName] = elemDt;
                    Emit(new Copy(ElemInit(k), elemVar));
                }
            }

            return true;
        }
        return false;
    }

    private void VisitListComp(ListCompExpr lc, string qualifiedName, int count, DataType elemDt)
    {
        int? EvalConst(Expression e)
        {
            if (e is IntegerLiteral il) return il.Value;
            if (e is BooleanLiteral bl) return bl.Value ? 1 : 0;
            if (e is VariableExpr v &&
                constantVariables.TryGetValue(currentInlinePrefix + v.Name, out int cv)) return cv;
            if (e is BinaryExpr be)
            {
                var lv = EvalConst(be.Left);
                var rv = EvalConst(be.Right);
                if (lv == null || rv == null) return null;
                return be.Op switch
                {
                    Frontend.BinaryOp.Add => lv + rv,
                    Frontend.BinaryOp.Sub => lv - rv,
                    Frontend.BinaryOp.Mul => lv * rv,
                    Frontend.BinaryOp.Div => rv != 0 ? lv / rv : null,
                    Frontend.BinaryOp.FloorDiv => rv != 0 ? lv / rv : null,
                    Frontend.BinaryOp.Mod => rv != 0 ? lv % rv : null,
                    Frontend.BinaryOp.Equal => lv == rv ? 1 : 0,
                    Frontend.BinaryOp.NotEqual => lv != rv ? 1 : 0,
                    Frontend.BinaryOp.Less => lv < rv ? 1 : 0,
                    Frontend.BinaryOp.Greater => lv > rv ? 1 : 0,
                    Frontend.BinaryOp.LessEq => lv <= rv ? 1 : 0,
                    Frontend.BinaryOp.GreaterEq => lv >= rv ? 1 : 0,
                    Frontend.BinaryOp.And => (lv != 0 && rv != 0) ? 1 : 0,
                    Frontend.BinaryOp.Or => (lv != 0 || rv != 0) ? 1 : 0,
                    Frontend.BinaryOp.BitAnd => lv & rv,
                    Frontend.BinaryOp.BitOr => lv | rv,
                    Frontend.BinaryOp.BitXor => lv ^ rv,
                    Frontend.BinaryOp.LShift => lv << rv,
                    Frontend.BinaryOp.RShift => lv >> rv,
                    _ => null
                };
            }

            if (e is UnaryExpr ue)
            {
                var val = EvalConst(ue.Operand);
                if (val == null) return null;
                return ue.Op switch
                {
                    Frontend.UnaryOp.Negate => -val,
                    Frontend.UnaryOp.Not => val == 0 ? 1 : 0,
                    Frontend.UnaryOp.BitNot => ~val,
                    _ => null
                };
            }

            return null;
        }

        List<int> CollectIterable(Expression iterExpr)
        {
            var vals = new List<int>();
            if (iterExpr is CallExpr call && call.Callee is VariableExpr cv && cv.Name == "range")
            {
                int start = 0, stop = 0;
                if (call.Args.Count == 1)
                {
                    var sv = EvalConst(call.Args[0]);
                    if (sv == null) throw UserError("List comprehension const err");
                    stop = sv.Value;
                }
                else if (call.Args.Count >= 2)
                {
                    var sv = EvalConst(call.Args[0]);
                    var ev = EvalConst(call.Args[1]);
                    if (sv == null || ev == null) throw UserError("List comprehension const err");
                    start = sv.Value;
                    stop = ev.Value;
                }

                for (int i = start; i < stop; i++) vals.Add(i);
            }
            else if (iterExpr is ListExpr or TupleExpr)
            {
                var elems = iterExpr is ListExpr le ? le.Elements : ((TupleExpr)iterExpr).Elements;
                foreach (var e in elems)
                {
                    var v = EvalConst(e);
                    if (v == null) throw UserError("List comprehension const err");
                    vals.Add(v.Value);
                }
            }
            else if (iterExpr is VariableExpr iterName && ElementsOfNamedSequence(iterName.Name) is { } bound)
            {
                // `[x * 2 for x in base]` where base is a list this function already built.
                // Without this the iterable produced nothing and the comprehension was
                // reported as generating 0 elements for an array of 4.
                foreach (var e in bound)
                {
                    var v = EvalConst(e);
                    if (v == null) throw UserError("List comprehension const err");
                    vals.Add(v.Value);
                }
            }

            return vals;
        }

        var outerVals = CollectIterable(lc.Iterable);
        string outerKey = currentInlinePrefix + lc.VarName;
        string innerKey = string.IsNullOrEmpty(lc.Var2Name) ? "" : currentInlinePrefix + lc.Var2Name;
        bool hasInner = !string.IsNullOrEmpty(lc.Var2Name) && lc.Iterable2 != null;

        var entries = new List<Val>();
        foreach (int oval in outerVals)
        {
            constantVariables[outerKey] = oval;
            if (hasInner)
            {
                var innerVals = CollectIterable(lc.Iterable2!);
                foreach (int ival in innerVals)
                {
                    constantVariables[innerKey] = ival;
                    if (lc.Filter != null)
                    {
                        var fv = EvalConst(lc.Filter);
                        if (fv == null) throw UserError("filter error");
                        if (fv == 0) continue;
                    }

                    entries.Add(VisitExpression(lc.Element));
                }

                constantVariables.Remove(innerKey);
            }
            else
            {
                if (lc.Filter != null)
                {
                    var fv = EvalConst(lc.Filter);
                    if (fv == null) throw UserError("filter error");
                    if (fv == 0) continue;
                }

                entries.Add(VisitExpression(lc.Element));
            }
        }

        constantVariables.Remove(outerKey);

        if (entries.Count != count)
            throw UserError($"List comprehension generated {entries.Count} but array is {count}");
        bool useSram = arraysWithVariableIndex.Contains(qualifiedName) || moduleSramArrays.Contains(qualifiedName);

        for (int k = 0; k < count; ++k)
        {
            if (useSram) Emit(new ArrayStore(qualifiedName, new Constant(k), entries[k], elemDt, count));
            else
            {
                string elemName = qualifiedName + "__" + k;
                variableTypes[elemName] = elemDt;
                Emit(new Copy(entries[k], new Variable(elemName, elemDt)));
                if (entries[k] is Variable srcVar)
                    PropagateCtState(srcVar.Name, elemName);
            }
        }
    }

    /// <summary>
    /// Store a compile-time list of element expressions into an unrolled array bound to
    /// <paramref name="target"/> (slots name__0..name__N-1). ZCA-instance elements are
    /// constructed directly into their slot so instanceClasses[slot] is registered and
    /// for-in / enumerate over the array resolve the element type. Always handles the list.
    /// </summary>
    private bool TryVisitCtListAssign(VariableExpr target, List<Expression> elemExprs)
    {
        string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + target.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + target.Name : target.Name);
        if (!arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(target.Name)) qualified = target.Name;

        int count = elemExprs.Count;
        DataType elemDt = DataType.UINT8;   // ZCA slots use a placeholder; class travels in instanceClasses.

        for (int k = 0; k < count; ++k)
        {
            string elemName = qualified + "__" + k;
            variableTypes[elemName] = DataType.UINT8;

            // ZCA constructor element: build the instance directly into the slot (like a plain
            // `x = Cls(...)` assignment) so instanceClasses[slot] is registered. Constructing via
            // a temporary loses the class -- an `__init__` whose ReturnType is "" still allocates a
            // result temp, so VisitExpression would return that temp, not the instance.
            string? ctorClass = elemExprs[k] is CallExpr ce ? ResolveCtorClass(ce) : null;
            if (ctorClass != null)
            {
                instanceClasses[elemName] = ctorClass;
                virtualInstances.Add(elemName);
                pendingConstructorTarget = elemName;
                VisitExpression(elemExprs[k]);
                continue;
            }

            Val v = VisitExpression(elemExprs[k]);
            if (k == 0) elemDt = v switch { Temporary t => t.Type, Variable vv => vv.Type, _ => DataType.UINT8 };
            variableTypes[elemName] = elemDt;
            Emit(new Copy(v, new Variable(elemName, elemDt)));
            if (v is Variable srcVar) PropagateCtState(srcVar.Name, elemName);
        }

        arraySizes[qualified] = count;
        arrayElemTypes[qualified] = elemDt;
        variableTypes[qualified] = elemDt;
        return true;
    }

    /// <summary>
    /// If <paramref name="call"/> is a constructor call for a known class (Cls(...) or
    /// module.Cls(...)), return the resolved class prefix; otherwise null. Mirrors the
    /// constructor detection used for a plain `x = Cls(...)` assignment.
    /// </summary>
    private string? ResolveCtorClass(CallExpr call)
    {
        string resolvedClass = "";
        if (call.Callee is VariableExpr calleeVar)
            resolvedClass = ResolveCallee(calleeVar.Name);
        else if (call.Callee is MemberAccessExpr { Object: VariableExpr objVar } calleeMem && modules.ContainsKey(objVar.Name))
            resolvedClass = objVar.Name.Replace('.', '_') + "_" + calleeMem.Member;

        if (!string.IsNullOrEmpty(resolvedClass)
            && (inlineFunctions.ContainsKey(resolvedClass + "___init__")
                || overloadedFunctions.Contains(resolvedClass + "___init__")))
            return resolvedClass;
        return null;
    }

    /// <summary>
    /// Desugar a compile-time list comprehension into the concrete list of element
    /// expressions by substituting the loop variable with each iterable item. Supports the
    /// single-iterable, no-filter form over a tuple/list/range literal -- enough for
    /// CircuitPython idioms like [DigitalInOut(p) for p in (board.D5, board.D6)]. Returns
    /// null for anything it cannot expand so the caller falls back to the normal path.
    /// </summary>
    private List<Expression>? ExpandCtListComp(ListCompExpr lc)
    {
        if (lc.Filter != null) return null;

        // Two `for` clauses: the cross product, outer first, exactly as Python nests them.
        // Annotated, this form already compiled and held the right nine values; unannotated it
        // was reported as a comprehension with a filter it does not have.
        if (lc.Iterable2 != null && !string.IsNullOrEmpty(lc.Var2Name))
        {
            var outer = ExpandCtListComp(new ListCompExpr(
                new VariableExpr("__ctcomp_item"), lc.VarName, lc.Iterable));
            var inner = ExpandCtListComp(new ListCompExpr(
                new VariableExpr("__ctcomp_item"), lc.Var2Name, lc.Iterable2));
            if (outer == null || inner == null) return null;

            var pairs = new List<Expression>(outer.Count * inner.Count);
            foreach (var o in outer)
                foreach (var i in inner)
                    pairs.Add(SubstituteVar(SubstituteVar(lc.Element, lc.VarName, o), lc.Var2Name, i));
            return pairs;
        }

        if (!string.IsNullOrEmpty(lc.Var2Name) || lc.Iterable2 != null) return null;

        List<Expression> items;
        switch (lc.Iterable)
        {
            case TupleExpr te: items = te.Elements; break;
            case ListExpr le:  items = le.Elements; break;
            // `[x * 2 for x in base]` where base is a name bound to a list. The elements are
            // known -- that is what the binding records -- but only a literal iterable was
            // being read here, so the comprehension fell through to the value path and was
            // rejected for having a filter it does not have.
            case VariableExpr seqName when ResolveConstSequence(seqName.Name) is { } bound:
                items = bound;
                break;
            case CallExpr { Callee: VariableExpr { Name: "range" } } rangeCall:
                int start = 0, stop;
                if (rangeCall.Args.Count == 1) stop = EvaluateConstantExpr(rangeCall.Args[0]);
                else if (rangeCall.Args.Count >= 2)
                {
                    start = EvaluateConstantExpr(rangeCall.Args[0]);
                    stop = EvaluateConstantExpr(rangeCall.Args[1]);
                }
                else return null;
                items = new List<Expression>();
                for (int i = start; i < stop; ++i) items.Add(new IntegerLiteral(i));
                break;
            default: return null;
        }

        var result = new List<Expression>(items.Count);
        foreach (var item in items)
            result.Add(SubstituteVar(lc.Element, lc.VarName, item));
        return result;
    }

    /// <summary>
    /// Return a copy of <paramref name="expr"/> with every VariableExpr named
    /// <paramref name="varName"/> replaced by <paramref name="repl"/>. Only the expression
    /// shapes that appear in list-comp elements are rewritten; other nodes are returned as-is.
    /// </summary>
    private static Expression SubstituteVar(Expression expr, string varName, Expression repl)
    {
        Expression S(Expression e) => SubstituteVar(e, varName, repl);
        return expr switch
        {
            VariableExpr v when v.Name == varName => repl,
            CallExpr c => new CallExpr(S(c.Callee), c.Args.Select(S).ToList()),
            MemberAccessExpr m => new MemberAccessExpr(S(m.Object), m.Member),
            BinaryExpr b => new BinaryExpr(S(b.Left), b.Op, S(b.Right)),
            UnaryExpr u => new UnaryExpr(u.Op, S(u.Operand)),
            IndexExpr i => new IndexExpr(S(i.Target), S(i.Index)),
            KeywordArgExpr k => new KeywordArgExpr(k.Key, S(k.Value)),
            TupleExpr t => new TupleExpr(t.Elements.Select(S).ToList()),
            ListExpr l => new ListExpr(l.Elements.Select(S).ToList()),
            _ => expr
        };
    }

    private void VisitAugAssign(AugAssignStmt stmt)
    {
        // A const-declared name is immutable; an augmented assignment (`K += 1`) mutates it
        // just like a plain assignment, so reject it with the same located error.
        if (stmt.Target is VariableExpr augConstTgt && declaredConstants.Contains(augConstTgt.Name))
            throw UserError($"cannot assign to constant '{augConstTgt.Name}' (declared const)");

        // `obj OP= v` where obj is a ZCA instance: Python first tries the in-place dunder
        // (__iadd__ & co.), then falls back to the binary one via `obj = obj OP v`. Without
        // this routing the statement compiled as a scalar RMW on the instance handle --
        // silently mutating nothing.
        if (stmt.Target is VariableExpr zve)
        {
            string zq = string.IsNullOrEmpty(currentInlinePrefix)
                ? (string.IsNullOrEmpty(currentFunction) ? zve.Name : currentFunction + "." + zve.Name)
                : currentInlinePrefix + zve.Name;
            // A module-level instance in a top-level script is tracked under its module
            // key (see SlotInstanceKey), not the function-qualified name.
            if (!instanceClasses.ContainsKey(zq)
                && instanceClasses.ContainsKey(currentModulePrefix + zve.Name))
                zq = currentModulePrefix + zve.Name;
            if (instanceClasses.TryGetValue(zq, out var zcls) && !string.IsNullOrEmpty(zcls))
            {
                string idunder = stmt.Op switch
                {
                    AugOp.Add => "__iadd__",
                    AugOp.Sub => "__isub__",
                    AugOp.Mul => "__imul__",
                    AugOp.FloorDiv => "__ifloordiv__",
                    AugOp.Mod => "__imod__",
                    AugOp.BitAnd => "__iand__",
                    AugOp.BitOr => "__ior__",
                    AugOp.BitXor => "__ixor__",
                    AugOp.LShift => "__ilshift__",
                    AugOp.RShift => "__irshift__",
                    _ => "",
                };
                if (idunder.Length > 0 && inlineFunctions.ContainsKey(zcls + "_" + idunder))
                {
                    // Route through the regular method-call machinery (identical to a
                    // hand-written stats.add(v)): it binds self correctly for flattened
                    // AND slot instances. ZCA mutation is in place, so the Python rebind
                    // of the returned self is an identity and is dropped.
                    VisitExpression(new CallExpr(
                        new MemberAccessExpr(new VariableExpr(zve.Name), idunder),
                        new List<Expression> { stmt.Value }) { Line = stmt.Line });
                    return;
                }
                string bdunder = idunder.Length > 0 ? "__" + idunder.Substring(3) : "";
                if (bdunder.Length > 0 && inlineFunctions.ContainsKey(zcls + "_" + bdunder))
                {
                    Frontend.BinaryOp bop = stmt.Op switch
                    {
                        AugOp.Add => Frontend.BinaryOp.Add,
                        AugOp.Sub => Frontend.BinaryOp.Sub,
                        AugOp.Mul => Frontend.BinaryOp.Mul,
                        AugOp.FloorDiv => Frontend.BinaryOp.FloorDiv,
                        AugOp.Mod => Frontend.BinaryOp.Mod,
                        AugOp.BitAnd => Frontend.BinaryOp.BitAnd,
                        AugOp.BitOr => Frontend.BinaryOp.BitOr,
                        AugOp.BitXor => Frontend.BinaryOp.BitXor,
                        AugOp.LShift => Frontend.BinaryOp.LShift,
                        AugOp.RShift => Frontend.BinaryOp.RShift,
                        _ => throw UserError($"augmented operator {stmt.Op} has no dunder mapping"),
                    };
                    VisitAssign(new AssignStmt(zve, new BinaryExpr(zve, bop, stmt.Value)) { Line = stmt.Line });
                    return;
                }
                throw UserError(
                    $"'{zve.Name}' is a {zcls} instance: augmented assignment needs " +
                    $"{zcls}.{(idunder.Length > 0 ? idunder : "an in-place dunder")} (or the matching binary dunder) defined");
            }
        }

        Val operand = VisitExpression(stmt.Value);

        if (stmt.Target is VariableExpr ve)
        {
            Val target = ResolveBinding(ve.Name);
            if (target is Constant)
            {
                string q = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + ve.Name
                    : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + ve.Name : ve.Name);
                DataType dt = variableTypes.TryGetValue(q, out var dt2) ? dt2 : DataType.UINT8;
                target = new Variable(q, dt);
                constantVariables.Remove(q);
            }

            Emit(new AugAssign(IRGenerator.MapAugOp(stmt.Op), target, operand));
        }
        else if (stmt.Target is IndexExpr ie)
        {
            Val current = VisitIndex(ie);
            Temporary result = MakeTemp(DataType.UINT8);
            Emit(new Binary(IRGenerator.MapAugOp(stmt.Op), current, operand, result));

            if (ie.Target is VariableExpr ve2)
            {
                string qualified = string.IsNullOrEmpty(currentFunction) ? ve2.Name : currentFunction + "." + ve2.Name;
                if (!arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(ve2.Name)) qualified = ve2.Name;
                if (arraySizes.ContainsKey(qualified))
                {
                    if (arraysWithVariableIndex.Contains(qualified) || moduleSramArrays.Contains(qualified))
                    {
                        Val idxVal = VisitExpression(ie.Index);
                        Emit(new ArrayStore(qualified, idxVal, result, arrayElemTypes[qualified],
                            arraySizes[qualified]));
                    }
                    else
                    {
                        if (!(ie.Index is IntegerLiteral il)) throw UserError("Array subscript must be const");
                        string elemName = qualified + "__" + il.Value;
                        Emit(new Copy(result, new Variable(elemName, arrayElemTypes[qualified])));
                    }

                    return;
                }
            }

            var tgtVal = VisitExpression(ie.Target);
            var idxVal2 = VisitExpression(ie.Index);

            Val ResolveTargetAddr2(Val val)
            {
                var name = val is Temporary t ? t.Name : (val is Variable vv ? vv.Name : null);
                if (name == null || !constantAddressVariables.TryGetValue(name, out int addr)) return val;
                var dt = DataType.UINT8;
                if (variableTypes.TryGetValue(name, out var vt)) dt = vt;
                else if (!string.IsNullOrEmpty(currentInlinePrefix) &&
                         variableTypes.TryGetValue(currentInlinePrefix + name, out var vti)) dt = vti;

                return new MemoryAddress(addr, dt);

            }

            tgtVal = ResolveTargetAddr2(tgtVal);

            int bit = 0;
            if (idxVal2 is Constant c2) bit = c2.Value;
            else
            {
                bool TryConst(string name)
                {
                    if (constantVariables.TryGetValue(name, out int cv))
                    {
                        bit = cv;
                        return true;
                    }

                    return false;
                }

                bool resolved = false;
                if (idxVal2 is Temporary t) resolved = TryConst(t.Name);
                else if (idxVal2 is Variable v) resolved = TryConst(v.Name);
                if (!resolved) throw UserError("Bit index must be constant for augmented assignment");
            }

            Emit(new BitWrite(tgtVal, bit, result));
        }
        else if (stmt.Target is MemberAccessExpr mae && mae.Member == "value")
        {
            // `<ptr>.value OP= operand`: read-modify-write through the pointer. Without this
            // case a member-target augmented assignment was silently dropped.
            Val ptrObj = VisitExpression(mae.Object);
            string? pn = ptrObj switch { Variable pv => pv.Name, Temporary pt => pt.Name, _ => null };

            if (pn != null && runtimePtrVars.TryGetValue(pn, out var rElem))
            {
                // Runtime pointer (ptr(<runtime addr>)): LoadIndirect -> op -> StoreIndirect.
                // Elem rides on the instructions themselves: the optimizer may collapse the
                // typed temporaries into constants, and the access width must survive that.
                Temporary cur = MakeTemp(rElem);
                Emit(new LoadIndirect(ptrObj, cur, rElem));
                Temporary res = MakeTemp(rElem);
                Emit(new Binary(IRGenerator.MapAugOp(stmt.Op), cur, operand, res));
                Emit(new StoreIndirect(res, ptrObj, rElem));
                return;
            }

            // Compile-time address: a ptr[T] const-address variable or a register MemoryAddress.
            DataType elem = DataType.UINT8;
            if (pn != null && constantAddressVariables.TryGetValue(pn, out int caddr))
            {
                if (variableTypes.TryGetValue(pn, out var pet)) elem = pet;
                ptrObj = new MemoryAddress(caddr, elem);
            }
            else if (ptrObj is MemoryAddress mma) elem = mma.Type;

            if (ptrObj is MemoryAddress maddr)
            {
                // Reading a MemoryAddress operand dereferences it (IN/LDS); writing back
                // via Copy stores it (OUT/STS).
                Temporary res = MakeTemp(elem);
                Emit(new Binary(IRGenerator.MapAugOp(stmt.Op), maddr, operand, res));
                Emit(new Copy(res, new MemoryAddress(maddr.Address, elem)));
                return;
            }

            throw UserError("augmented assignment to .value requires a pointer or register target");
        }
        else if (stmt.Target is MemberAccessExpr mfield)
        {
            // `obj.field OP= v` for a ZCA field (slot, scalar, or flattened): read-modify-write.
            // Only `.value` had a case, so an augmented assignment to any other member was
            // silently dropped (e.g. `box.x += 3` left box.x unchanged). Reuse the field read and
            // write paths so the slot-aware load/store is applied for multi-field instances.
            Val cur = VisitMemberAccess(mfield);   // a @property read goes through the getter (A67)
            DataType dt = GetValType(cur);
            if (dt == DataType.UNKNOWN) dt = DataType.UINT8;
            Temporary res = MakeTemp(dt);
            Emit(new Binary(IRGenerator.MapAugOp(stmt.Op), cur, operand, res));
            // A @property must write back through its setter, not a phantom data field. The
            // getter read above already produced `cur`; route the new value through the setter.
            if (TryExpandPropertySetter(mfield, () => res)) return;
            EmitMemberAssign(new AssignStmt(mfield, mfield) { Line = stmt.Line }, mfield, res);
        }
    }

    // Stores `value` at `basePtr + offset`. For offset 0, stores directly via basePtr.
    // For offset > 0, emits a Binary ADD to compute the address then StoreIndirect.
    internal void EmitListStore(Val basePtr, int offset, Val value, DataType elemType = DataType.UINT8)
    {
        if (offset == 0)
        {
            Emit(new StoreIndirect(value, basePtr, elemType));
            return;
        }

        Val ptrUint16 = basePtr is Temporary t ? t with { Type = DataType.UINT16 }
                       : basePtr is Variable v ? v with { Type = DataType.UINT16 }
                       : basePtr;
        Temporary addrTmp = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, ptrUint16, new Constant(offset), addrTmp));
        Emit(new StoreIndirect(value, addrTmp, elemType));
    }

    // Loads a UINT8 value from `basePtr + offset` into a new Temporary.
    internal Temporary EmitListLoad(Val basePtr, int offset, DataType elemType = DataType.UINT8)
    {
        Temporary dst = MakeTemp(elemType);
        if (offset == 0)
        {
            Emit(new LoadIndirect(basePtr, dst, elemType));
            return dst;
        }

        Val ptrUint16 = basePtr is Temporary t ? t with { Type = DataType.UINT16 }
                       : basePtr is Variable v ? v with { Type = DataType.UINT16 }
                       : basePtr;
        Temporary addrTmp = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, ptrUint16, new Constant(offset), addrTmp));
        Emit(new LoadIndirect(addrTmp, dst, elemType));
        return dst;
    }

    private void VisitExprStmt(ExprStmt stmt) => VisitExpression(stmt.Expr);

    private void VisitGlobal(GlobalStmt stmt)
    {
        foreach (var n in stmt.Names) currentFunctionGlobals.Add(n);
    }

    private void VisitNonlocal(NonlocalStmt stmt)
    {
        if (string.IsNullOrEmpty(currentInlinePrefix)) return;
        foreach (var n in stmt.Names)
        {
            string innerKey = currentInlinePrefix + n;
            string outerName = currentFunction + "." + n;
            variableAliases[innerKey] = outerName;
            // A nonlocal alias is WRITE-THROUGH storage sharing (the inner name IS the outer
            // variable); it must survive writes, unlike the value-tracking aliases.
            writeThroughAliases.Add(innerKey);
        }
    }

    private void VisitTupleUnpack(TupleUnpackStmt stmt)
    {
        string QualifyTarget(string name)
        {
            if (!string.IsNullOrEmpty(currentInlinePrefix)) return currentInlinePrefix + name;
            if (!string.IsNullOrEmpty(currentFunction)) return currentFunction + "." + name;
            return name;
        }

        // Every target is bound here, whichever shape the right-hand side takes; the
        // undefined-name check reads this and nothing else about tuple targets.
        foreach (var t in stmt.Targets) boundNames.Add(QualifyTarget(t));

        if (stmt.Value is TupleExpr tup)
        {
            int nTup = tup.Elements.Count;
            int nTgt = stmt.Targets.Count;

            if (stmt.StarredIndex < 0)
            {
                if (nTup != nTgt) throw UserError($"Tuple size mismatch");
                // Python evaluates the whole RHS tuple before assigning, so snapshot
                // each runtime value first. Otherwise `a, b = b, a` would assign a = b and
                // then read the already-overwritten a. The snapshot must be a named Variable,
                // not a Temporary: the linear copy-propagation forwards a temp aliasing a
                // variable past that variable's reassignment (it would turn `b = snap` back
                // into `b = a` after `a = ...`), whereas a variable-to-variable copy is left to
                // the CFG-aware pass, whose dataflow correctly kills the alias when the source
                // is redefined. The name is globally unique (tempCounter), so it never collides.
                var snapshots = new List<Val>(nTup);
                foreach (var el in tup.Elements)
                {
                    Val v = VisitExpression(el);
                    if (v is Variable or Temporary)
                    {
                        DataType st = GetValType(v);
                        var snap = new Variable($"__unpack{tempCounter++}", st);
                        variableTypes[snap.Name] = st;
                        Emit(new Copy(v, snap));
                        snapshots.Add(snap);
                    }
                    else snapshots.Add(v);
                }
                for (int k = 0; k < nTgt; ++k)
                {
                    string qualified = QualifyTarget(stmt.Targets[k]);
                    DataType dt = variableTypes.TryGetValue(qualified, out var t) ? t : GetValType(snapshots[k]);
                    variableTypes[qualified] = dt;
                    Emit(new Copy(snapshots[k], new Variable(qualified, dt)));
                    if (snapshots[k] is Constant c) constantVariables[qualified] = c.Value;
                    else constantVariables.Remove(qualified);
                }
            }
            else
            {
                int nFixed = nTgt - 1;
                if (nTup < nFixed) throw UserError("Not enough values to unpack");
                int starIdx = stmt.StarredIndex;
                int starCount = nTup - nFixed;

                for (int k = 0; k < starIdx; ++k)
                {
                    Val v = VisitExpression(tup.Elements[k]);
                    string qualified = QualifyTarget(stmt.Targets[k]);
                    Emit(new Copy(v, new Variable(qualified, DataType.UINT8)));
                    if (v is Constant c) constantVariables[qualified] = c.Value;
                    variableTypes[qualified] = DataType.UINT8;
                }

                string starName = QualifyTarget(stmt.Targets[starIdx]);
                arraySizes[starName] = starCount;
                arrayElemTypes[starName] = DataType.UINT8;
                for (int k = 0; k < starCount; ++k)
                {
                    int srcIdx = starIdx + k;
                    Val v = VisitExpression(tup.Elements[srcIdx]);
                    string elemKey = starName + "__" + k;
                    Emit(new Copy(v, new Variable(elemKey, DataType.UINT8)));
                    if (v is Constant c) constantVariables[elemKey] = c.Value;
                    variableTypes[elemKey] = DataType.UINT8;
                }

                int nAfter = nTgt - starIdx - 1;
                for (int k = 0; k < nAfter; ++k)
                {
                    int srcIdx = starIdx + starCount + k;
                    Val v = VisitExpression(tup.Elements[srcIdx]);
                    string qualified = QualifyTarget(stmt.Targets[starIdx + 1 + k]);
                    Emit(new Copy(v, new Variable(qualified, DataType.UINT8)));
                    if (v is Constant c) constantVariables[qualified] = c.Value;
                    variableTypes[qualified] = DataType.UINT8;
                }
            }
        }
        else if (stmt.Value is CallExpr call)
        {
            pendingTupleCount = stmt.Targets.Count;
            if (stmt.StarredIndex >= 0)
                throw UserError("Starred expressions not supported with inline multi-return.");

            Val ignored = VisitExpression(call);
            pendingTupleCount = 0;

            if (lastTupleResults.Count != stmt.Targets.Count)
                throw UserError($"Expected {stmt.Targets.Count} tuple results, got {lastTupleResults.Count}");

            for (int k = 0; k < stmt.Targets.Count; ++k)
            {
                string srcName = lastTupleResults[k];
                string dstName = QualifyTarget(stmt.Targets[k]);
                // An undeclared target inherits the result slot's width, so a callee annotated
                // `-> (uint8, uint16)` does not get its second value truncated to 8 bits.
                DataType dt = variableTypes.TryGetValue(dstName, out var t) ? t
                    : variableTypes.TryGetValue(srcName, out var st) ? st : DataType.UINT8;
                Emit(new Copy(new Variable(srcName, dt), new Variable(dstName, dt)));
                if (constantVariables.TryGetValue(srcName, out int cVal)) constantVariables[dstName] = cVal;
            }
        }
        else
            throw UserError(
                "Tuple unpacking RHS must be a tuple literal or an inline function call returning a tuple.");
    }

    private void VisitClassDef(ClassDef classNode)
    {
    } // Only scanned


    /// <summary>
    /// The elements behind a name bound to a sequence: an unannotated `base = [1, 2, 3]`
    /// binding, or the literal an annotated `base: uint8[4] = [...]` was built from.
    /// </summary>
    private List<Expression>? ElementsOfNamedSequence(string name)
    {
        if (ResolveConstSequence(name) is { } bound) return bound;

        foreach (var key in new[]
                 {
                     !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name : null,
                     !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : null,
                     !string.IsNullOrEmpty(currentModulePrefix) ? currentModulePrefix + name : null,
                     name,
                 })
        {
            if (key != null && arrayLiteralElements.TryGetValue(key, out var elems)) return elems;
        }
        return null;
    }

}