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
    // The keywords input() really has, named for the same reason PrintKeywords is: the loop
    // that accepts them and the refusal that lists them must not drift apart.
    private static readonly string[] InputKeywords = { "prompt", "maxlen" };

    private void VisitAssign(AssignStmt stmt)
    {
        // `self.v: T = x`. A BARE name annotation on an assignment never becomes an AnnAssign
        // -- both front ends build one only when the annotation contains a '[' -- so it
        // arrives here as AnnotatedType and was the position the check never saw (#278).
        CheckAnnotationNames(stmt.AnnotatedType ?? "", stmt);

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

        // Rebinding the name of a module-level `def`. The name is bound at compile time and
        // every call through it lowers to a direct CALL, so the assignment cannot change what
        // a call means -- and nothing refused it, so the program compiled with the name
        // meaning two different things at once. Measured on atmega328p:
        //
        //   def helper() -> uint8: return 1
        //   def main() -> None:
        //       global helper
        //       helper = 5
        //       GPIOR1.value = helper()   # CALL helper -> 1
        //       GPIOR2.value = helper     # LDI R24, 5  -> 5
        //
        // CPython raises TypeError on that call, so neither reading agrees with Python. The
        // function-valued form is worse than the value one: `helper = other` through `global`
        // silently redirects the call to `other`, which is the dispatch table the sibling
        // check below exists to refuse and which reached it only through an alias name.
        //
        // Only the MODULE-LEVEL binding is refused. `helper = 5` inside a function with no
        // `global` is an ordinary local shadowing the name, exactly as in Python, and keeps
        // compiling to the value.
        // Two ways to reach the module-level binding. `global name` inside a function is the
        // explicit one. The other is an assignment written at module level, which cannot be
        // recognised by scope here because those statements are lowered INTO main's body, so
        // it is recognised by its effect instead: the scan records a module-level assignment
        // in mutableGlobals, and a name that is both a module global and a function is the
        // signature of this bug. A function-local shadow is never recorded there.
        if (stmt.Target is VariableExpr fnRebindTgt
            && string.IsNullOrEmpty(currentInlinePrefix)
            && (currentFunctionGlobals.Contains(fnRebindTgt.Name)
                || mutableGlobals.ContainsKey(currentModulePrefix + fnRebindTgt.Name)))
        {
            string rebindResolved = ResolveCallee(fnRebindTgt.Name);
            if (functionParams.ContainsKey(rebindResolved) || inlineFunctions.ContainsKey(rebindResolved))
                throw UserError(
                    $"'{fnRebindTgt.Name}' is bound to a function at compile time, so it cannot be "
                    + "rebound. A call through the name is lowered to a direct call and never reads "
                    + "the assignment, so the name would mean the function where it is called and "
                    + "the new value everywhere else. Give the value a different name.",
                    fnRebindTgt);
        }

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
                            + "and pass the function, or take its address with funcref().", fnTgt);

                    loopFunctionAliases[tgtKey] = resolvedFn;
                    boundNames.Add(tgtKey);
                    return;
                }
            }
        }

        // A name declared with a `const[...]` annotation is immutable; reassigning it is a
        // user error (previously this was silently accepted, overwriting the constant).
        if (stmt.Target is VariableExpr constTgt && declaredConstants.Contains(constTgt.Name))
            throw UserError($"cannot assign to constant '{constTgt.Name}' (declared const)", constTgt);

        // The same refusal one level in: an enum member. It was already refused where the enum
        // is declared in the same file, but only by falling through to the ordinary member
        // store, which evaluated the enum class as a value and reported "name 'Color' is not
        // defined" about a name the file declares above. Imported, it was not refused at all:
        // `from cfg import Color` then `Color.RED = 9` built, dropped the write and read the
        // old value (#273). Here, before anything is lowered, and saying what is wrong.
        if (EnumMemberAssignTarget(stmt.Target) is { } enumTgt)
            throw UserError(EnumMemberAssignMessage(enumTgt.Cls, enumTgt.Member), enumTgt.At);

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
            // What the right-hand side IS, not whether it was spelled as a literal. `b = a`
            // and `c = BANNER` and `n = cfg.name` all hold a text the compiler knows, and
            // clearing it here is what made print stream the interned id as a decimal: a
            // two-line program printed 256 for "abc" (#209).
            string? boundText = StaticStringOf(stmt.Value);
            if (boundText != null)
                strConstantVariables[strKey] = boundText;
            else
                strConstantVariables.Remove(strKey);

            // DISPATCH is a different question from what the name holds, and only this half
            // was ever load-bearing. A name bound to anything but a literal must stop
            // selecting const[str] overloads (#144) and must keep refusing when it is rebound
            // to a different text on another path (#145). That stays exactly as it was: the
            // two removals below run for every non-literal right-hand side, including the
            // ones whose text is now kept.
            if (stmt.Value is not StringLiteral)
            {
                multiStrVariables.Remove(strKey);
                multiStrVariables.Remove(StrBindingKey(strTgt.Name));
            }
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

            // The one place the tuple-ness of the name exists (#299). Recorded for BOTH lengths,
            // before the unroll-limit test below splits them, because the two paths differ only
            // in how the elements are stored and not in what the name is.
            NoteSequenceMutability(seqKey, seqTgt.Name, isTuple: stmt.Value is TupleExpr);

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

        // `order = range(2, 0, -1)` and `order = reversed(order)`: a compile-time sequence under
        // a name (#363). A range has no run-time value, so the binding IS the whole meaning of
        // the statement -- the same shape as the tuple literal above, and the reason both emit
        // nothing. Refused before this, the two lines were reported as "range() is not a value"
        // on a program whose only use of the name is the `for` the message asks for.
        if (stmt.Target is VariableExpr rngTgt && ConstSequenceFromRange(stmt.Value) is { } rngElems)
        {
            string rngKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + rngTgt.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + rngTgt.Name : rngTgt.Name);

            // Past the unroll limit the elements would become that many loop iterations, which
            // is the reason the limit exists. Such a name falls through and keeps the refusal.
            if (rngElems.Count > 0 && rngElems.Count <= ConstSequenceUnrollLimit)
            {
                NoteSequenceMutability(rngKey, rngTgt.Name, isTuple: false);
                constSequenceBindings[rngKey] = rngElems;
                rangeBoundSequences.Add(rngKey);
                return;
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

        // Unannotated `name = array.array(typecode[, initializer])`: the same MicroPython
        // shape as the bytearray case above, mapped onto the heap-bounded list[T] the
        // compiler already has, with T decided by the typecode.
        if (stmt.Target is VariableExpr arrTgt
            && stmt.Value is CallExpr { Callee: MemberAccessExpr { Object: VariableExpr { Name: "array" }, Member: "array" } } arrCall)
        {
            string elemTypeName = ArrayTypecodeToTypeName(arrCall);
            Expression ctorArg = arrCall.Args.Count > 1 ? arrCall.Args[1] : new CallExpr(new VariableExpr("list"), new List<Expression>());
            VisitAnnAssign(new AnnAssign(arrTgt.Name, $"list[{elemTypeName}]", ctorArg) { Line = stmt.Line });
            return;
        }

        // Mutating a dict-literal binding (`d[k] = v`) has no runtime structure to write to.
        if (stmt.Target is IndexExpr { Target: VariableExpr mutVe }
            && TryGetDictBinding(mutVe.Name, out _))
            throw UserError(
                $"'{mutVe.Name}' is a compile-time dict literal (read-only lookup table). " +
                "For a mutable dict use pymcu.collections.FixedDict(capacity) -- fixed " +
                "footprint, no heap.", mutVe);

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
                            Frontend.BinaryOp.Div => "__truediv__",
                            Frontend.BinaryOp.Pow => "__pow__",
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
        //
        // A TUPLE right-hand side arrives here too. Up to ConstSequenceUnrollLimit constant
        // elements the binding above already claimed it (the `for` unrolls and nothing is
        // stored), but past that limit a LIST fell through to this path and became a real
        // array while a tuple fell through to the expression visitor and was refused as a
        // runtime value -- so `DUTIES = (256, 383, ...)` with ten elements did not compile
        // and `DUTIES = [256, 383, ...]` did. Immutability is a Python-level property of the
        // name, not of the storage: the elements go into the same array either way, which is
        // already what a short tuple's binding does one screen up.
        if (stmt.Target is VariableExpr listTarget)
        {
            List<Expression>? elemExprs = stmt.Value switch
            {
                ListExpr le => le.Elements,
                TupleExpr te => te.Elements,
                ListCompExpr lc => ExpandCtListComp(lc),
                _ => null
            };
            if (elemExprs != null && TryVisitCtListAssign(listTarget, elemExprs)) return;
        }

        // `pattern = self.digits[num]`: a row of a rectangular dict. A constant key folds to
        // the row's values; a run-time key binds a ROW VIEW, which is the flash table, the row
        // width and the run-time row index, because seven bytes have nothing to be held in.
        if (stmt.Target is VariableExpr rowTgt && stmt.Value is IndexExpr rowIdx
            && TryGetDictFor(rowIdx.Target, out var rowDict)
            && DictKeyedRows(rowDict) is { } keyedRows)
        {
            string rowKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + rowTgt.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + rowTgt.Name : rowTgt.Name);
            string rowSource = rowIdx.Target is MemberAccessExpr rm ? rm.Member
                             : (rowIdx.Target is VariableExpr rv ? rv.Name : rowTgt.Name);
            string rowCache = SequenceKeyOf(rowIdx.Target) ?? rowSource;
            Val rowKeyVal = VisitExpression(rowIdx.Index);
            rowViews.Remove(rowKey);
            constSequenceBindings.Remove(rowKey);

            if (rowKeyVal is Constant rowConst)
            {
                int at = keyedRows.FindIndex(r => r.Key == rowConst.Value);
                if (at < 0)
                    throw UserError(
                        $"KeyError: {DescribeDictKey(rowIdx.Index, rowConst)} is not a key of "
                        + "this dict literal (checked at compile time)", rowIdx.Index);
                constSequenceBindings[rowKey] =
                    keyedRows[at].Row.Select(v => (Expression)new IntegerLiteral(v)).ToList();
                return;
            }

            if (MaterialiseDictRows("dictrows:" + rowCache, rowSource,
                                    keyedRows.Select(r => r.Row).ToList()) is { } rowTable)
            {
                Val rowIndex = EmitDictRowIndex(keyedRows, rowCache, rowSource, rowKeyVal);
                rowViews[rowKey] = (rowTable, keyedRows[0].Row.Count, rowIndex);
                return;
            }
        }

        // `self._pins = pins` / `self._levels = levels`: a FIELD that holds a compile-time
        // sequence. The field is another name for the sequence, not a scalar: before this it
        // became one, and every `self._pins[0]` read the zero that nothing had written -- built
        // clean, ran wrong, which is the shape a driver taking a list of pins always has.
        if (stmt.Target is MemberAccessExpr { Object: VariableExpr } seqMem
            && MemberFlatKey(seqMem) is { } seqFieldKey)
        {
            string? seqSourceBase = null;
            if (stmt.Value is ListExpr seqFieldLit && IsInstanceSequenceLiteral(seqFieldLit))
                seqSourceBase = HoistInstanceSequence(seqFieldLit);
            else if (stmt.Value is ListCompExpr seqFieldComp && IsInstanceComprehension(seqFieldComp))
                seqSourceBase = HoistInstanceComprehension(seqFieldComp);
            else if (stmt.Value is not ListExpr
                     && TryResolveInstanceSequence(stmt.Value, out var seqBoundBase, out _))
                seqSourceBase = seqBoundBase;

            if (seqSourceBase != null)
            {
                BindSequenceAlias(seqFieldKey, seqSourceBase);
                return;
            }

            // `self.digits = {...}`: a dict or set literal kept in a FIELD. It is a
            // compile-time lookup table, and a driver keeps its table where it keeps
            // everything else. Bound to a name it already worked, read from a method
            // included; only the field turned it into a value position and refused it.
            if (stmt.Value is DictExpr fieldDict)
            {
                dictLiteralBindings[seqFieldKey] = fieldDict;
                setLiteralBindings.Remove(seqFieldKey);
                return;
            }
            if (stmt.Value is SetExpr fieldSet)
            {
                setLiteralBindings[seqFieldKey] = fieldSet;
                dictLiteralBindings.Remove(seqFieldKey);
                return;
            }

            // `self._data = data`: a field handed a bytearray or a fixed array. It keeps the
            // storage it was given -- before this the address went into a scalar field and
            // `self._data[i]` was read as a bit index into that scalar.
            if (stmt.Value is VariableExpr seqArrVe)
            {
                string seqArrSrc = ResolveNameKey(seqArrVe.Name);
                // The alias chain can end on a stale qualified name (`main.buf`) while the
                // scanner filed the storage under the bare module name (`buf`) -- the same
                // split #460 fixed at the declaration site. Only the qualified -> bare
                // direction is safe: the reverse (bare `levels` -> `main.levels`) can land on
                // a const-sequence size record that has no SRAM behind it, and binding the
                // field to that makes every subscript read zeros.
                if (!arraySizes.ContainsKey(seqArrSrc) && !bytearrayParams.Contains(seqArrSrc))
                {
                    int d = seqArrSrc.LastIndexOf('.');
                    if (d >= 0)
                    {
                        string seqArrBare = seqArrSrc[(d + 1)..];
                        if (arraySizes.ContainsKey(seqArrBare) || bytearrayParams.Contains(seqArrBare))
                            seqArrSrc = seqArrBare;
                    }
                }
                if (seqArrSrc != seqFieldKey
                    && (arraySizes.ContainsKey(seqArrSrc) || bytearrayParams.Contains(seqArrSrc))
                    && !instanceClasses.ContainsKey(seqArrSrc + "__0"))
                {
                    BindSequenceAlias(seqFieldKey, seqArrSrc);
                    if (bytearrayParams.Contains(seqArrSrc)) bytearrayParams.Add(seqFieldKey);
                    return;
                }
            }

            // A list of NUMBERS keeps its elements against the field, so a constant subscript
            // folds, `for v in self._levels` unrolls and `len()` answers -- the same three
            // things the name outside the class already answers.
            if (stmt.Value is not ListExpr && ResolveConstSequenceExpr(stmt.Value) is { } seqConstElems)
            {
                constSequenceBindings[seqFieldKey] = seqConstElems;
                variableAliases.Remove(seqFieldKey);
                return;
            }
        }

        // `self.data = bytearray(N)` / `= bytearray([...])` / `= bytearray(b"...")` inside
        // __init__ (#392): the same call bound to a local already routes through VisitVarDecl
        // (see the `baTgt` case above), which lays out a fixed SRAM buffer instead of trying to
        // evaluate bytearray() as a runtime call. A field target reached the generic expression
        // visitor instead, which has no lowering for the bytearray() builtin and fell into the
        // "unsupported Python builtin" fallback -- the same diagnostic #380 reports for a call
        // argument, fired here because Assign.cs never routes a MemberAccessExpr target through
        // the bytearray-recognizing path at all. Only a compile-time-sized buffer is handled
        // here; a runtime-sized `bytearray(n)` is a different (arena-allocated) shape.
        if (stmt.Target is MemberAccessExpr baFieldTgt
            && stmt.Value is CallExpr { Callee: VariableExpr { Name: "bytearray" or "bytes" } } baFieldCall)
        {
            Expression? baFieldSizeSource = null;
            var baFieldInit = new List<int>();
            int baFieldCount = 0;

            if (((VariableExpr)baFieldCall.Callee).Name == "bytes")
            {
                // Same immutable spelling as the local-variable case above: refuses a
                // run-time size itself, naming bytearray.
                var bytesElems = TryBytesLiteralElements(baFieldCall);
                if (bytesElems != null)
                {
                    baFieldSizeSource = baFieldCall.Args.Count > 0 ? baFieldCall.Args[0] : baFieldCall;
                    baFieldCount = bytesElems.Count;
                    foreach (var e in bytesElems)
                        baFieldInit.Add(TryEvalElemConst(e, out int ev) ? ev : 0);
                }
            }
            else if (baFieldCall.Args.Count > 0)
            {
                var baArg0 = baFieldCall.Args[0];
                baFieldSizeSource = baArg0;
                if (baArg0 is ListExpr baList)
                {
                    baFieldCount = baList.Elements.Count;
                    foreach (var e in baList.Elements)
                        baFieldInit.Add(TryEvalElemConst(e, out int ev) ? ev : 0);
                }
                // Integer literal or any compile-time constant (bytearray(WINDOW)).
                else if (TryEvalElemConst(baArg0, out int baConstN))
                {
                    baFieldCount = baConstN;
                    baFieldInit.AddRange(Enumerable.Repeat(0, baFieldCount));
                }
            }

            if (baFieldCount <= 0)
                throw UserError(
                    "bytearray: could not determine buffer size from initializer.",
                    baFieldSizeSource);

            EmitMemberArrayInit(baFieldTgt.Object, baFieldTgt.Member, DataType.UINT8,
                baFieldCount, baFieldInit, FormatMemberTarget(baFieldTgt));
            return;
        }

        // `self.buf = [0, 0, 0]`: a list FIELD with no annotation. It reached the generic
        // expression visitor, which has no lowering for a list literal, and answered "Unknown
        // Expression type: ListExpr", the name of a compiler class, about a field written
        // exactly the way a local list is. The literal carries the length, and the widest
        // element carries the type, so the field is the same fixed array that
        // `self.buf: list[uint8] = [...]` declares. Only all-constant literals qualify: a list
        // of instances (`self.pins = [Pin(1), Pin(2)]`) is a different shape with its own path,
        // and a literal whose size cannot be read still asks for the annotation by name.
        if (stmt.Target is MemberAccessExpr listMem && stmt.Value is ListExpr fieldList)
        {
            if (fieldList.Elements.Count > 0
                && fieldList.Elements.All(e => TryEvalElemConst(e, out _))
                && ResolveMemberArrayName(listMem) is null)
            {
                var fieldVals = new List<int>();
                foreach (var e in fieldList.Elements) { TryEvalElemConst(e, out int ev); fieldVals.Add(ev); }
                EmitMemberArrayInit(listMem.Object, listMem.Member,
                    WidestElemType(fieldVals), fieldVals.Count, fieldVals,
                    FormatMemberTarget(listMem));
                return;
            }

            throw UserError(
                $"a list literal assigned to '{FormatMemberTarget(listMem)}' needs a declared size. "
                + $"Write `{FormatMemberTarget(listMem)}: uint8[{fieldList.Elements.Count}] = [...]` "
                + "(or another element type), which reserves the storage in the instance and is "
                + "indexable at run time.", listMem);
        }

        Val value = VisitExpression(stmt.Value);

        // `x = f()` where f inlined `return <its local buffer>`: the call's value is a
        // Variable naming that fixed slot. Bind `x` as another NAME for the same bytes --
        // a scalar copy of the name would write nothing -- and the alias makes `x[i]`,
        // `x[i] = v`, `len(x)` and `for` all answer the callee's storage.
        if (value is Variable retBuf && arraySizes.ContainsKey(retBuf.Name)
            && stmt.Target is VariableExpr bufTgt)
        {
            string bufKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + bufTgt.Name
                : (!string.IsNullOrEmpty(currentFunction)
                    ? currentFunction + "." + bufTgt.Name : bufTgt.Name);
            BindSequenceAlias(bufKey, retBuf.Name);
            return;
        }

        if (stmt.Target is VariableExpr varExpr) { EmitScalarVarAssign(stmt, varExpr, value); }
        else if (stmt.Target is MemberAccessExpr memExpr2) { EmitMemberAssign(stmt, memExpr2, value); }
        else if (stmt.Target is UnaryExpr unExpr && unExpr.Op == Frontend.UnaryOp.Deref)
        {
            Val ptr = VisitExpression(unExpr.Operand);
            Emit(new StoreIndirect(value, ptr, RuntimePtrElem(ptr)));
        }
        else throw UserError("Invalid assignment target", stmt.Target);

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
    // True when the class (or one it inherits from) defines `member` as a method. Overloads are
    // registered under suffixed keys and the bare key is vacated, so both spellings count.
    private bool ClassDefinesMethod(string cls, string member)
    {
        string bare = cls + "_" + member;
        if (inlineFunctions.ContainsKey(bare) || functionParams.ContainsKey(bare)) return true;
        string overloadPfx = bare + "___";
        foreach (var key in inlineFunctions.Keys)
            if (key.StartsWith(overloadPfx, StringComparison.Ordinal)) return true;
        return classDirectMethods.TryGetValue(cls, out var direct) && direct.Contains(member);
    }

    // Assigning to a name the class defines as a METHOD wrote a phantom field that shadowed the
    // method, and the write went nowhere. `p.value = 1` on a HAL Pin is the CircuitPython
    // spelling and `Pin.value` is an overloaded method here (value() reads, value(x) writes), so
    // the program built clean and drove nothing: measured on the Uno, firmware.gas.asm held the
    // DDR bit from the constructor and no write to the port at all (#316).
    private void RejectAssignmentToAMethod(MemberAccessExpr memTarget)
    {
        if (memTarget.Object is not VariableExpr) return;
        string owner = ResolveNameKey(((VariableExpr)memTarget.Object).Name);
        if (!instanceClasses.TryGetValue(owner, out var cls) || string.IsNullOrEmpty(cls)) return;
        string concrete = ResolveConcreteClass(cls) ?? cls;
        if (!ClassDefinesMethod(concrete, memTarget.Member)) return;
        // A class that also declares the name as a FIELD is not this mistake; the layout is what
        // decides, and a name in it is storage whatever else shares the spelling.
        if (classFieldLayout.TryGetValue(concrete, out var layout)
            && layout.Any(f => f.Item1 == memTarget.Member)) return;

        int seg = concrete.LastIndexOf('_');
        string shownCls = seg >= 0 ? concrete[(seg + 1)..] : concrete;
        string target = FormatMemberTarget(memTarget);
        throw UserError(
            $"'{memTarget.Member}' is a method on '{shownCls}', not a field: assigning to it "
            + "would hide the method and the write would go nowhere. Call it instead, "
            + $"`{target}(...)` to set and `{target}()` to read.", memTarget);
    }

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
                    $"getter but no @{memTarget.Member}.setter", memTarget);
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
            // Cleared like the two above, and for the same reason: this key is the inline
            // prefix plus the parameter name, and it is reused by every setter expansion at
            // the same depth, so a None left by an earlier assignment would answer for this
            // one (#306).
            noneValuedNames.Remove(paramName);
            switch (argVal)
            {
                case Constant c:
                    constantVariables[paramName] = c.Value;
                    break;
                case NoneVal:
                    // `obj.prop = None`. None has no runtime representation, so there is
                    // nothing to copy: record the parameter as None-valued, which is what lets
                    // `p is None` fold inside the setter and what lets a `match p:` be decided
                    // instead of lowering every arm. Leaving it unbound is how
                    // `pin.pull = None` reached the Pull.DOWN arm and was refused with
                    // "Pull-down resistor not supported on AVR" (#306).
                    noneValuedNames.Add(paramName);
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
        var savedSourcePath = currentSourcePath;
        var savedSourceFile = currentSourceFile;
        currentInlinePrefix = newPrefix;
        currentModulePrefix = cls + "_";

        // The setter's body is text in the file the setter is DEFINED in, and every other
        // expansion says so while it lowers one. This one did not, so a call inside the body
        // looked to the rest of the generator like a call the user had written: the guard that
        // drops an argument's position when the call is inside a library saw an empty path and
        // kept it, and a refusal reached through the setter was reported at the LIBRARY's line
        // and column against the USER's file. `pin.pull = Pull.DOWN` on line 6 of a nine-line
        // program came out as main.py:109:32, the position of the `2` in digitalio.py (#306).
        if (setter != null && functionSourcePath.TryGetValue(setter, out var setterPath))
        {
            currentSourcePath = setterPath ?? "";
            currentSourceFile = SourceFileLabel(currentSourcePath);
        }

        inlineStack.Add(new InlineContext { ExitLabel = exitLabel,
            CallerSourcePath = savedSourcePath });
        if (setter?.Body != null) VisitBlock(setter.Body);
        Emit(new Label(exitLabel));
        inlineStack.RemoveAt(inlineStack.Count - 1);

        inlineDepth--;
        currentInlinePrefix = savedPrefix;
        currentModulePrefix = savedModulePrefix;
        currentSourcePath = savedSourcePath;
        currentSourceFile = savedSourceFile;

        return true;
    }

    // `x = value` to a plain (scalar) variable target: type/alias resolution, constant
    // tracking and the Copy. Receives the pre-computed rhs `value`. Terminal for the
    // chain (completes -> VisitAssign returns).
    private bool IsMemoryAddressGlobal(string name) =>
        globals.TryGetValue(name, out var sym) && sym.IsMemoryAddress;

    /// <summary>
    /// The width of a FRESH local inside an @inline expansion, taken from what is stored in it.
    ///
    /// Such a name has no recorded type, and the binding the fallback hands back is a BYTE, so
    /// the store truncated whatever was put there: a uint16 came back as its low byte and a
    /// float as 0. The same method compiled as a shared subroutine answers correctly, which is
    /// why this only ever showed up in a body that something forced inline -- `timestamp =
    /// time.monotonic()` in adafruit_hcsr04, whose method is expanded because it reaches
    /// through a field, read back 0 and made every measurement after the first time out (#385).
    ///
    /// Only when the name resolved to THIS expansion's own local. A name that resolved to a
    /// parameter, a constant, an enclosing frame or a module global already has a width, and
    /// that width is not ours to change.
    /// </summary>
    private Val WidenInlineLocalToValue(VariableExpr varExpr, Val target, Val value)
    {
        if (target is not Variable tv) return target;
        string key = currentInlinePrefix + varExpr.Name;

        // `pulses = self.get_pulses()` inside a force-inlined method, where get_pulses is
        // ALSO force-inlined and its `return pulses` hands back the callee's own list[T]
        // variable directly (no fresh temp for a plain `return <local>`): `value` here is
        // that Variable, already registered in listVarElemTypes under ITS OWN name. `key`
        // needs the same registration, and GC_REF, regardless of whatever scalar width an
        // earlier scan gave it -- a list is never the "prior scalar use" the width-only
        // check below exists for. Adafruit_dht's array.array-returning methods are exactly
        // this shape (PyMCU#433).
        string? valueListKey = value switch
        {
            Variable lv when listVarElemTypes.ContainsKey(lv.Name) => lv.Name,
            Temporary lt when listVarElemTypes.ContainsKey(lt.Name) => lt.Name,
            _ => null,
        };
        if (valueListKey != null)
        {
            listVarElemTypes[key] = listVarElemTypes[valueListKey];
            variableTypes[key] = DataType.GC_REF;
            return new Variable(key, DataType.GC_REF);
        }

        if (tv.Name != key || variableTypes.ContainsKey(key)) return target;

        DataType vt = value switch
        {
            Temporary t => t.Type,
            Variable v => v.Type,
            FloatConstant => DataType.FLOAT,
            _ => DataType.UNKNOWN,
        };
        if (vt == DataType.UNKNOWN || vt == tv.Type) return target;
        // Only ever WIDER. Narrowing a binding here would be a new truncation, and FLOAT is
        // not comparable by size to an integer: it is a different representation, and a store
        // into an integer slot loses the value rather than its high bytes.
        if (vt != DataType.FLOAT && vt.SizeOf() <= tv.Type.SizeOf()) return target;

        variableTypes[key] = vt;
        return new Variable(key, vt);
    }

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
                    $"use {varExpr.Name}.value = ... to write the whole register, or {varExpr.Name}[bit] = ... for one bit", varExpr);
        }

        // A write creates a NEW binding: kill the target's value-tracking alias BEFORE
        // resolving it (else the store itself is redirected through a stale alias), and
        // every alias that resolves TO it (their recorded value is about to change).
        // Without this, `free = i` inside an @inline loop left free -> i standing while i
        // kept changing, and the sibling expansion's `free = 255` even WROTE into i (the
        // FixedDict.__setitem__ corruption). Nonlocal write-through aliases are exempt.
        InvalidateAliasesForWrite(varExpr.Name);

        // `s = "running"` on a name that other paths bind to another text: the id goes to the
        // name's 16-bit slot, whatever scope the name lives in. Without this the store either
        // resolved to the id it was storing (a copy with no destination) or landed in a slot
        // one byte wide, which printed the id truncated.
        if (stmt.Value is StringLiteral && MultiStrStoreTarget(varExpr.Name) is Variable strSlot)
        {
            Emit(new Copy(value, strSlot));
            constantVariables.Remove(strSlot.Name);
            return;
        }

        // A name whose only binding so far is None has no WIDTH, because None is not a value:
        // `x = None` emits no store at all, and the byte the empty binding left behind is a
        // placeholder rather than a measurement. The store that finally gives the name a value
        // is the one that decides how wide it is. Without this, `pulselen = None` ahead of
        // `pulselen = self._echo[0]` -- how every CircuitPython driver declares an optional
        // result -- kept a byte, and a 570 us echo was read back as 58 (#385). Recorded before
        // the target is resolved, so every scope reads the same width: a local, a name inside
        // an @inline expansion, and a module global all resolve through variableTypes here.
        if (stmt.Value is not NoneLiteral && string.IsNullOrEmpty(stmt.AnnotatedType))
        {
            string noneKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + varExpr.Name
                : (!string.IsNullOrEmpty(currentFunction)
                    ? currentFunction + "." + varExpr.Name
                    : varExpr.Name);
            if (noneValuedNames.Contains(noneKey))
            {
                DataType noneWidth = value switch
                {
                    Temporary vt => vt.Type,
                    Variable vv => vv.Type,
                    FloatConstant => DataType.FLOAT,
                    _ => DataType.UNKNOWN,
                };
                if (noneWidth != DataType.UNKNOWN
                    && (!variableTypes.TryGetValue(noneKey, out var hadWidth)
                        || noneWidth.SizeOf() > hadWidth.SizeOf()))
                    variableTypes[noneKey] = noneWidth;
            }
        }

        Val target;
        if (!string.IsNullOrEmpty(currentFunction))
        {
            if (currentFunctionGlobals.Contains(varExpr.Name))
            {
                target = ResolveBinding(varExpr.Name, varExpr);
            }
            else
            {
                if (!string.IsNullOrEmpty(currentInlinePrefix)) target = ResolveBinding(varExpr.Name, varExpr);
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
                                stmt.Line > 0 ? stmt.Line : lastLine, stmt.Column);

                        // First top-level store into an unannotated global that ScanGlobals
                        // could not type: adopt the RHS's real width. As uint8, a uint16
                        // getter result wrapped at the store (pwm.freq() printed 232 for
                        // 1000 on a real Uno).
                        // `x = None` is not that first store: None has no width, so spending the
                        // one chance to widen on it left the name a byte and the assignment
                        // that DID carry a value truncated into it (#385).
                        if (stmt.Value is not NoneLiteral && widenableGlobals.Remove(moduleGlobalName))
                        {
                            DataType rhsT = value switch
                            {
                                Temporary wt => wt.Type,
                                Variable wv => wv.Type,
                                // A compile-time float. Without this case the switch answered
                                // with the global's own (1-byte) type, the store folded the
                                // float to an integer, and the name read back as a small int
                                // (#379).
                                FloatConstant => DataType.FLOAT,
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
                            // `x = f()` where f's declared return is `list[T]`: the result temp
                            // is already GC_REF (see EmitRegularFunctionCall/
                            // EmitInlineFunctionCall), but `x` also needs its OWN
                            // listVarElemTypes entry -- len(x)/x[i]/x.append() all resolve the
                            // qualified NAME, not the temp that flows through the Copy below.
                            // Left unregistered, x kept UNKNOWN-turned-uint8 and a subscript
                            // silently fell to the register-bit-index path (adafruit_dht's
                            // `pulses = self._get_pulses_pulseio()`, PyMCU#433).
                            if (stmt.Value is CallExpr && value is Temporary
                                && lastCallReturnTypeText is { } lcrt
                                && lcrt.StartsWith("list[") && lcrt.EndsWith("]"))
                                listVarElemTypes[qualifiedName] = DataTypeExtensions.StringToDataType(
                                    lcrt.Substring(5, lcrt.Length - 6));

                            if (listVarElemTypes.ContainsKey(qualifiedName)) type = DataType.GC_REF;
                            else if (value is Temporary tmp) type = tmp.Type;
                            else if (value is Variable vv) type = vv.Type;
                            // A float literal with no annotation. There was no case for it, so
                            // it fell past every branch below and kept the UINT8 default: the
                            // name was typed an integer and the store truncated, so `f = 1.5`
                            // printed 1 with no error (#216). The annotated form is refused
                            // outright (`f: uint8 = 1.5` is a TypeError), and float arithmetic
                            // types its result correctly, which is why this survived -- it needs
                            // a bare literal, no annotation, and no arithmetic before the read.
                            else if (value is FloatConstant) type = DataType.FLOAT;
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
            target = ResolveBinding(varExpr.Name, varExpr);
        }

        if (!string.IsNullOrEmpty(currentInlinePrefix))
            target = WidenInlineLocalToValue(varExpr, target, value);

        // A Variable that names array STORAGE is a returned buffer, not a scalar to copy:
        // `x = f()` where f inlined `return result` hands back the callee's slot name, and
        // the alias binding below makes `x` another name for those same bytes.
        if (!(value is NoneVal)
            && !(value is Variable arrVal && arraySizes.ContainsKey(arrVal.Name)))
            Emit(new Copy(value, target));

        // RFC 0001 Model B (register handle): `x = make()` where make is a non-@inline
        // factory returning a single-field ZCA (VisitReturn already lowers the callee's
        // body to hand back the field as a scalar; see Statements.cs). `x` IS that scalar,
        // tracked as a factory-handle instance so a call site that finds the method already
        // outlined passes `x` itself as the field (Call.cs). But a method call site that
        // reaches this same instance through the ordinary force-inline/virtual-construction
        // path instead resolves `self.<field>` to the flattened `<inst>_<field>` convention
        // every DIRECTLY-constructed instance uses -- a name this assignment never wrote, so
        // it read as zero. Mirror the handle into that name too, so either dispatch finds the
        // right value (#429).
        if (target is Variable handleTgt && factoryHandleInstances.Contains(handleTgt.Name)
            && instanceClasses.TryGetValue(handleTgt.Name, out var handleCls) && handleCls != null
            && classFieldLayout.TryGetValue(handleCls, out var handleLayout) && handleLayout.Count == 1
            && !(value is NoneVal))
        {
            var (hField, hType, _) = handleLayout[0];
            Emit(new Copy(value, new Variable(handleTgt.Name + "_" + hField,
                DataTypeExtensions.StringToDataType(hType))));
        }

        // `x = None` on an UNANNOTATED local, and its mirror.
        //
        // None-ness is a compile-time property here: a parameter defaulting to None, a field
        // assigned None and an annotated local all record it, and `x is None` and `if x:` read
        // that record. The bare `x = None` was the one binding that did not, so the record
        // said nothing, `if x is None:` answered FALSE, and the branch that would have given
        // `x` a value was dropped -- leaving a read of a name nothing ever wrote. That is the
        // shape `if end is None: end = len(buf)` has, which is how a bus driver spells an
        // optional bound.
        //
        // The Remove is the half that matters just as much: `x = None` then `x = 5` has to stop
        // being None, or the assignment that fixed it is the one the compiler ignores.
        // The SOURCE has to be the None literal, not a NoneVal result. A constructor whose
        // `__init__` returns void also hands back NoneVal, and marking the instance as None
        // made `match p:` report that the subject's class was not known -- for a program that
        // had just built it one line above.
        if (target is Variable noneTgt)
        {
            if (stmt.Value is NoneLiteral) noneValuedNames.Add(noneTgt.Name);
            else if (value is not NoneVal) noneValuedNames.Remove(noneTgt.Name);
        }

        if (value is Variable vv2 && target is Variable tv2)
        {
            variableAliases[tv2.Name] = vv2.Name;
            // An alias to an INSTANCE is structural, not value-tracking: WHICH OBJECT the name
            // stands for does not depend on which path ran, so it has to survive a label. Filed
            // as value-tracking, it was dropped by the first label a loop emits, and
            // `wd = microcontroller.watchdog` followed by `wd.feed()` inside a `while` became a
            // method call on an integer -- while the same two lines with no loop between them
            // compiled (#259). That binding is how every compat-layer namespace is spelled:
            // `alarm.pin`, `alarm.time`, `microcontroller.cpu`, `microcontroller.watchdog`.
            //
            // A write to either name still clears it, through InvalidateAliasesForWrite.
            if (ReceiverClassThroughAliases(vv2.Name) is null)
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
        else if (target is Variable tv4)
        {
            constantVariables.Remove(tv4.Name);
            // Not folded into constantVariables -- see localConstantValues -- but remembered,
            // so a call that passes this name can bind the callee's parameter as the constant
            // it is (PyMCU#327).
            RecordLocalConstant(tv4.Name, value, stmt.Value, stmt.AnnotatedType, tv4.Type);
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
            // A base-class call whose declared return type is a ZCA class, in either spelling:
            // `super().split(raw)` and `Base.split(self, raw)`. Without this the target is never
            // registered as that class, so the constructor inside the base body builds into an
            // anonymous `__cN` and the caller reads `p_a` / `p_b`, names nothing ever writes. It
            // read two unwritten slots as zero, silently (PyMCU#157). The module branch below
            // covers `m.Pin(...)`; a CLASS receiver is a different thing and had no branch.
            if (string.IsNullOrEmpty(resolvedClass)
                && TryResolveBaseCallClass(call) is { } baseRt)
            {
                resolvedClass = baseRt;
            }

            if (call.Callee is MemberAccessExpr calleeMem && calleeMem.Object is VariableExpr objVar)
            {
                if (modules.ContainsKey(objVar.Name))
                {
                    // Resolve a module alias (import machine as m) to the real module
                    // name so `m.Pin(...)` resolves the machine_Pin class.
                    string realMod = TryImportedAlias(objVar.Name, out var rm) && rm != null
                        ? rm : objVar.Name;
                    string mangled = realMod.Replace('.', '_');
                    resolvedClass = mangled + "_" + calleeMem.Member;
                }
                else if (string.IsNullOrEmpty(resolvedClass)
                         && TryResolveInstanceMethodAst(objVar.Name, calleeMem.Member) is { } factoryMethod
                         && factoryMethod.Body?.Statements != null)
                {
                    // `led = pcf.get_pin(7)`: an ordinary METHOD -- not a constructor -- whose
                    // body constructs and returns a class instance. adafruit_pcf8574.py's
                    // `get_pin` is exactly this shape: it validates the pin number and hands
                    // back `DigitalInOut(pin, self)`. Nothing tagged the assignment target
                    // with a class before, so the first method called on it (`led
                    // .switch_to_output(...)`) mangled to the undefined `led_switch_to_output`
                    // -- the free-function factory case just above resolves the same shape
                    // for a bare function name; a method call had no equivalent.
                    //
                    // Read statically, the same way the free-function factory case does:
                    // find a `return ClassName(...)` in the method's own body. The call
                    // itself still runs normally (the method is force-inlined at its call
                    // site like any other instance method, and validation code such as the
                    // assert above executes); this only tells the ASSIGNMENT TARGET what
                    // class the value it receives is.
                    //
                    // Resolved under the DEFINING module's prefix, not the caller's: a
                    // cross-module `pcf.get_pin(...)` (adafruit_pcf8574's own shape --
                    // PCF8574 and its DigitalInOut return type are both defined in
                    // adafruit_pcf8574.py, used from main.py) has `Pin`/`DigitalInOut`
                    // written unqualified inside a method that belongs to the OTHER module,
                    // and ResolveCallee reads a name against `currentModulePrefix`, which at
                    // this assignment is the CALLER's module, not the one that wrote it.
                    string savedFactoryPrefix = currentModulePrefix;
                    // classModuleMap is keyed by the BARE class name (however InstanceClassOfName
                    // answers qualified, e.g. "pinlib_Owner" for a cross-module receiver) -- find
                    // the entry whose prefix + bare name reconstructs it, rather than assuming
                    // either spelling.
                    if (InstanceClassOfName(objVar.Name) is { } recvCls)
                    {
                        foreach (var (bareName, modPrefix) in classModuleMap)
                        {
                            if (modPrefix + bareName == recvCls || bareName == recvCls)
                            {
                                currentModulePrefix = modPrefix;
                                break;
                            }
                        }
                    }
                    try
                    {
                        foreach (var fbs in factoryMethod.Body.Statements)
                            if (fbs is ReturnStmt fr && fr.Value is CallExpr frcall
                                && frcall.Callee is VariableExpr frcv)
                            {
                                string frc = ResolveCallee(frcv.Name);
                                if (inlineFunctions.ContainsKey(frc + "___init__")
                                    || overloadedFunctions.Contains(frc + "___init__")
                                    || classFieldLayout.ContainsKey(frc))
                                    resolvedClass = frc;
                            }
                    }
                    finally
                    {
                        currentModulePrefix = savedFactoryPrefix;
                    }
                }
            }

            // `mod.singleton.Nested(...)` -- a nested class reached through a module-level
            // singleton, which is how a compat layer spells a submodule (alarm.time.TimeAlarm,
            // alarm.pin.PinAlarm). The branch above resolves `m.Pin(...)`, ONE member access
            // deep; this is two, and nothing covered it.
            //
            // The consequence was not a diagnostic about the constructor. With resolvedClass
            // empty, the assignment never set pendingConstructorTarget, so the constructor
            // minted an anonymous `__cN`, tagged THAT with the class, and left the named
            // variable untagged. A later `o.field` then resolved against a name carrying no
            // class and reported "'field' is not a member of a numeric value" -- pointing at
            // the field read, one function away from the assignment that lost the type.
            //
            // Ordering made it look like something else entirely: whether the read reached the
            // untagged name or the tagged `__cN` depended on what had been expanded first, so
            // an @inline call before the read failed and the same call after it passed
            // (PyMCU#271).
            if (string.IsNullOrEmpty(resolvedClass)
                && call.Callee is MemberAccessExpr nestMem
                && nestMem.Object is MemberAccessExpr ownerMem
                && ownerMem.Object is VariableExpr ownerModVar
                && modules.ContainsKey(ownerModVar.Name))
            {
                string ownerMod = TryImportedAlias(ownerModVar.Name, out var orm) && orm != null
                    ? orm : ownerModVar.Name;
                string ownerKey = ownerMod.Replace('.', '_') + "_" + ownerMem.Member;
                if (instanceClasses.TryGetValue(ownerKey, out var ownerCls) && ownerCls != null)
                    resolvedClass = ownerCls + "_" + nestMem.Member;
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
                //
                // SlotInstanceKey, not a bare currentFunction/currentInlinePrefix
                // qualification: a module-level `led = get_pin(7)` runs inside the
                // synthesized/explicit main as module init, and every LATER reference to
                // `led` resolves it as a module global under its bare name -- the same
                // reason SlotInstanceKey exists for a direct constructor target a few
                // lines up. Tracking the handle under the function-qualified "main.led"
                // pointed the class tag at a name the call site never looked up, so
                // `led.value(1)` found no class and mangled to the undefined `led_value`
                // (adafruit_pcf8574's `pcf.get_pin(7)` is exactly this shape).
                string qn = SlotInstanceKey(varExprCtor.Name);
                instanceClasses[qn] = facRt;
                factoryHandleInstances.Add(qn);
            }
        }
    }

    // The class a base-class method call returns, for the two spellings of one construct:
    // `super().m(...)` and `Base.m(self, ...)`. Returns null when the call is not one of those,
    // or when its declared return type is not a class this compiler builds with a constructor.
    private string? TryResolveBaseCallClass(CallExpr call)
    {
        if (call.Callee is not MemberAccessExpr mem) return null;

        string callee;
        if (mem.Object is CallExpr { Callee: VariableExpr { Name: "super" } })
        {
            string childClass = methodInstanceTypes.TryGetValue(currentFunction, out var mit)
                ? mit
                : (string.IsNullOrEmpty(currentModulePrefix)
                    ? ""
                    : currentModulePrefix[..^1]);
            if (!classBasePrefixes.TryGetValue(childClass, out var basePrefix)) return null;
            callee = basePrefix + mem.Member;
        }
        else if (mem.Object is VariableExpr clsVe && classNames.Contains(clsVe.Name))
        {
            callee = ResolveCallee(clsVe.Name) + "_" + mem.Member;
        }
        else return null;

        if (!functionReturnTypes.TryGetValue(callee, out var rt) || string.IsNullOrEmpty(rt)) return null;
        if (rt is "None" or "void") return null;

        foreach (var key in new[] { ResolveCallee(rt), currentModulePrefix + rt, rt })
            if (inlineFunctions.ContainsKey(key + "___init__")
                || overloadedFunctions.Contains(key + "___init__"))
                return key;

        return null;
    }

    // `obj.member = v`: assign through a member-access target — ZCA field stores
    // (slot/Model A), property-less member writes, and the related dispatch.
    // `self.buf` as the program spells it, for a diagnostic. Falls back to the member name
    // alone when the receiver is not a plain name.
    private static string FormatMemberTarget(MemberAccessExpr mem)
        => mem.Object is VariableExpr mv ? mv.Name + "." + mem.Member : mem.Member;

    // True while lowering a constructor body, at any nesting depth inside it. A method is
    // named `<Class>___init__` when compiled on its own and carries an `...__init__` inline
    // prefix when force-inlined at a construction site, so both spellings are checked.
    private bool IsInsideInit()
    {
        static bool IsInit(string s) =>
            s.EndsWith("___init__", StringComparison.Ordinal)
            || s.EndsWith(".__init__", StringComparison.Ordinal)
            || s == "__init__";

        if (!string.IsNullOrEmpty(currentFunction) && IsInit(currentFunction)) return true;
        if (string.IsNullOrEmpty(currentInlinePrefix)) return false;
        // The prefix is a dotted chain of expansions (`inline1.__init__.`); any __init__ link
        // in it means this statement came from a constructor body.
        foreach (var part in currentInlinePrefix.Split('.'))
            if (part == "__init__" || IsInit(part)) return true;
        return false;
    }

    /// <summary>
    /// The enum member an assignment target names, or null when it names anything else.
    ///
    /// BOTH spellings, because they were three different programs. `Color.RED = 9` with the
    /// enum in the same file reported a name-resolution failure; imported by name it built and
    /// dropped the write; and reached through the module -- `import cfg` then
    /// `cfg.Color.RED = 9` -- the write LANDED and the member read 9, which is the one answer
    /// CPython never gives. One statement, three outcomes, chosen by how the enum was
    /// imported. The caret goes on the first token of the target, as the const-reassignment
    /// guard above puts it.
    /// </summary>
    private (string Cls, string Member, ASTNode At)? EnumMemberAssignTarget(Expression target)
    {
        if (target is not MemberAccessExpr mem) return null;
        return mem.Object switch
        {
            // `Color.RED = ...`
            VariableExpr cv when IsEnumMemberTarget(cv.Name, mem.Member)
                => (cv.Name, mem.Member, (ASTNode)cv),
            // `cfg.Color.RED = ...`
            MemberAccessExpr { Object: VariableExpr mv } dotted
                when IsEnumMemberTarget(dotted.Member, mem.Member)
                => (dotted.Member, mem.Member, (ASTNode)mv),
            _ => null,
        };
    }

    /// <summary>
    /// True when `cls.member` names an EXISTING member of an enum.
    ///
    /// Existing is the whole test, and it is CPython's: `Color.RED = 9` raises
    /// `AttributeError: cannot reassign member 'RED'`, while `Color.NOPE = 9` on the same enum
    /// is allowed, because only a member is protected. Measured against CPython 3.14 rather
    /// than assumed, so a name that is not a member keeps whatever this compiler already does
    /// with it instead of being refused by a rule Python does not have.
    /// </summary>
    private bool IsEnumMemberTarget(string cls, string member)
    {
        if (!enumClassNames.Contains(cls)) return false;
        string pfx = classModuleMap.TryGetValue(cls, out var p) ? p : currentModulePrefix;
        return globals.ContainsKey(pfx + cls + "_" + member)
            || globals.ContainsKey(cls + "_" + member);
    }

    /// <summary>CPython's sentence, plus the thing to do instead.</summary>
    private static string EnumMemberAssignMessage(string cls, string member) =>
        $"cannot reassign enum member '{cls}.{member}' -- a member IS its value, and the name " +
        "is fixed to it. Bind a variable to the member if you need something that changes " +
        $"(`limit = {cls}.{member}`), or use a plain class attribute for a value meant to be " +
        "written.";

    private void EmitMemberAssign(AssignStmt stmt, MemberAccessExpr memExpr2, Val value)
    {
        RejectAssignmentToAMethod(memExpr2);

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

        // A field literally named "value" (Base/Sub's `self.value` in #430, but any class is
        // exposed to this) collides with the MMIO/pointer `.value` write below, which has no
        // guard at all here (unlike its read-side counterpart in Expr.cs): every write to
        // `self.value` took the register path regardless of whether the receiver was an
        // actual ZCA instance. For an instance whose fields folded away as compile-time
        // constants (no runtime slot to fail the earlier slot-write check either), the
        // register path's own fallback silently dropped the store: the field never received
        // its value AND was never registered as a constant, so a later read landed on an
        // uninitialized flattened variable. Same guard as the read side.
        if (memExpr2.Member == "value" && !IsKnownInstanceField(memExpr2.Object, "value"))
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
                    throw UserError("Cannot assign to .value of this expression type", memExpr2);
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
                    throw UserError("16-bit .value assignment requires constant address", memExpr2);
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
                    throw UserError("32-bit .value assignment requires constant address", memExpr2);
                default:
                    throw UserError("Unsupported type size for .value assignment", memExpr2);
            }
        }
        else
        {
            var objVal = VisitExpression(memExpr2.Object);
            var baseName = objVal is Variable v3 ? v3.Name : (objVal is Temporary t3 ? t3.Name : "");
            if (string.IsNullOrEmpty(baseName))
                throw UserError("Unknown member access in assignment: " + memExpr2.Member, memExpr2);
            while (baseName != null && variableAliases.TryGetValue(baseName, out var alias)) baseName = alias;
            var flattenedName = baseName + "_" + memExpr2.Member;

            // Reaching here on a known class means the member is not one of its fields: every real
            // field write (slot store, write-back, instance array) returned above. The write lands
            // in a flattened `<obj>_<member>` of its own, disjoint from the object, so a misspelled
            // name compiles to a new variable and the field the author meant keeps its old value.
            //
            // Scoped by WHERE the assignment is, not by what the field set says. Inside __init__ an
            // assignment DEFINES a field, so a typo and a new field are the same shape there and
            // nothing can be said; outside it, a `self.<name>` that no __init__ introduced is either
            // a typo or a field that should have been declared, and the fix is the same for both.
            //
            // That scoping is also what makes this safe. classFieldLayout only collects TOP-LEVEL
            // assignments in __init__, so a class assigning its fields inside a `match` (the HAL's
            // _PinRegs does) has fields the layout never learned. Asking the layout about those
            // rejected the stdlib outright. Skipping __init__ entirely puts every such assignment
            // out of scope, so the gap in the map cannot be reached from here. The gap itself is
            // still real and is recorded in #170.
            // A class attribute whose class defines __set__ is a DESCRIPTOR, and writing it is
            // calling that method rather than creating a field (#360). Asked here, where the
            // receiver's name is resolved and both it and the value are already lowered, so the
            // rewrite can hand them over without emitting either a second time.
            if (TryDescriptorWrite(baseName, objVal, memExpr2, value))
                return;

            if (deviceConfig.Arch.Length > 0 && !deviceConfig.Arch.Contains("pio")
                && !IsInsideInit()
                && baseName != null
                && instanceClasses.TryGetValue(baseName, out var fieldCls)
                && fieldCls != null
                && classFieldLayout.TryGetValue(fieldCls, out var fieldLay)
                && fieldLay.Count > 0
                && !fieldLay.Any(f => f.Field == memExpr2.Member)
                && !IsKnownMethodName(memExpr2.Member))
                throw UserError(
                    $"'{fieldCls}' has no field '{memExpr2.Member}' -- assigning it here creates a "
                    + "name of its own rather than reaching the object, because PyMCU lays instances "
                    + "out at compile time. Assign it in some method to make it a field, or correct the "
                    + $"spelling. Declared fields: {string.Join(", ", fieldLay.Select(f => f.Field))}",
                    memExpr2);

            // A field assigned None has no runtime value; record the flattened name so
            // `obj.field is None` folds to True (IsNoneValued checks this set). A later non-None
            // write clears the mark (the field now holds a real value). Without this the field
            // read 0 and `is None` silently returned False (broke optional/sentinel fields).
            //
            // `self._pin = pin`, where `pin` is a parameter that arrived None, counts as one.
            // That is the whole shape `Optional[X] = None` has once a driver stores it, and the
            // value resolves to the parameter's BINDING rather than to a NoneVal, so testing the
            // Val alone missed it: a later `if self._pin is not None:` in a method then lowered
            // both sides, and the instance that has no pin ran the branch that uses one.
            if (value is NoneVal || SourceIsNoneInThisScope(stmt.Value))
            {
                noneValuedNames.Add(flattenedName);
                constantVariables.Remove(flattenedName);
                return;
            }
            noneValuedNames.Remove(flattenedName);

            // This write supersedes whatever the field was tracked as, so drop its compile-time
            // STRING identity up front; the paths below that do have a string set it again.
            //
            // Clearing has to happen here rather than in any one of those paths, because the key
            // is reused across call sites: a field of an instance built inside an @inline
            // flattens under that expansion's own prefix (`inline1.build.r__x`), so a later call
            // to the same @inline lands on exactly the same name. Whichever path this site takes,
            // the previous site's string must not survive into it -- a numeric field wearing the
            // earlier call's string selected the const[str] overload, which then rejected the
            // call outright. Same hazard the binder documents for the other maps (#144).
            strConstantVariables.Remove(flattenedName);

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

            // A ONE-CHARACTER string never reaches the id table: VisitExpression turns it into
            // the character's code, which is what makes `c == 'x'` and uart.write('A') work. So
            // the stringIdToStr lookup below has nothing to give back for it and the field kept
            // only the number: a class holding `self.sep: str = ","` printed 44 where "," was
            // meant, and sep.join([...]) on that field was refused for a separator the compiler
            // was holding all along. Ask the AST what the value IS -- it can tell a character
            // from a string -- before the id table, which cannot.
            //
            // The numeric identity is untouched: the branches below still record the code, so
            // `o.sep == ","` and passing the field where a character is wanted keep folding.
            if (StaticStringOf(stmt.Value) is { } fieldText)
                strConstantVariables[flattenedName] = fieldText;

            // Where the value came from, filed under the same key the value itself is. A driver
            // stores its pin at construction and validates it at first use, so by the time the
            // refusal fires the argument is several expansions away; without this the origin is
            // dropped at the field store and the diagnostic lands on the `read()` line, which
            // holds nothing the reader can change (#193).
            if (stmt.Value is VariableExpr srcVe)
            {
                if (argumentOrigin.TryGetValue(currentInlinePrefix + srcVe.Name, out var fo)
                    || argumentOrigin.TryGetValue(srcVe.Name, out fo))
                    argumentOrigin[flattenedName] = fo;
            }
            else if (stmt.Value.Column > 0)
            {
                // Written at the construction site itself (`self.pin = "PA0"` is rare, but a
                // literal default in the class body is not), and that position is as good as an
                // argument's.
                argumentOrigin[flattenedName] = stmt.Value;
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
            // A field of a module-level instance that some function assigns needs real storage:
            // without it the write is a dead store to a name nothing else in that function
            // reads, and the reader in another function folds the constructor's value instead.
            if (moduleInstanceMutableFields.Contains(flattenedName))
                mutableGlobals[flattenedName] = fdt;

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
                // A compile-time STRING travels as its interned id, so the id alone lands on the
                // field and the string itself is lost. Every use site that asks what the field
                // holds -- overload selection above all -- then types it numerically:
                // `self._name = name_for(n)` followed by `Low(self._name, k)` picked the numeric
                // overload with nothing reported. A string LITERAL assigned to the field already
                // took the Constant path above, which does carry it.
                string? tstr = ResolveStrConstant(tname)
                    ?? (stringIdToStr.TryGetValue(cv2, out var sidStr) ? sidStr : null);
                if (tstr != null) strConstantVariables[flattenedName] = tstr;
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

        var (bits, bitsTy) = AsStorableBits(value, fieldTy);

        for (int i = 0; i < sz; ++i)
        {
            Temporary b = MakeTemp(DataType.UINT8);
            if (i == 0)
            {
                Emit(new Copy(bits, b));   // low byte (truncating copy)
            }
            else
            {
                // Byte i = (bits >> 8*i) & 0xFF. The truncating Copy to UINT8 keeps the low byte,
                // which equals byte i of bits regardless of the shift's sign behaviour.
                Temporary sh = MakeTemp(bitsTy);
                Emit(new Binary(BinaryOp.RShift, bits, new Constant(8 * i), sh));
                Emit(new Copy(sh, b));
            }

            StoreByte(off + i, b);
        }
    }

    /// <summary>
    /// The value to split into bytes for a field of <paramref name="fieldTy"/>, and the type to
    /// shift it as.
    ///
    /// For an integer field that is the value itself. For a FLOAT it is not: a float's bytes are
    /// its IEEE-754 representation and not an arithmetic quantity, so `value >> 8` is not an
    /// operation that exists on one. The AVR backend said so -- "no float lowering for RShift...
    /// an earlier pass rewrote a float operation into one, which is a bug in that pass" -- and
    /// this was that pass. It named the constructor's line and a pass rather than a cause, and it
    /// refused an ordinary class: a float field alongside ANY second field is enough, because one
    /// field alone collapses to a scalar and never reaches a slot at all (#404).
    ///
    /// A Bitcast is the reinterpretation the store wanted in the first place. It costs nothing at
    /// run time on a target whose float is already four little-endian bytes, and it is what the
    /// READ side has been doing all along: a multi-byte field is loaded back through ONE typed
    /// LoadIndirect over the same four bytes.
    /// </summary>
    private (Val Bits, DataType Ty) AsStorableBits(Val value, DataType fieldTy)
    {
        if (fieldTy != DataType.FLOAT) return (value, fieldTy);
        Temporary bits = MakeTemp(DataType.UINT32);
        Emit(new Bitcast(value, bits));
        return (bits, DataType.UINT32);
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
                + $"it needs an explicit fixed-size annotation: '{target.Name}: <type>[N] = ...'", target);

        DataType srcEdt = arrayElemTypes[srcQ];
        int start = sl.Start != null ? EvaluateConstantExpr(sl.Start) : 0;
        int stop = sl.Stop != null ? EvaluateConstantExpr(sl.Stop) : srcSize;
        int step = sl.Step != null ? EvaluateConstantExpr(sl.Step) : 1;
        if (step == 0) throw UserError("Slice step cannot be zero", sl);
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
        // A `join` whose receiver is not a string is somebody's own method and belongs to the
        // ordinary call lowering. Asking that FIRST is what lets everything below speak about
        // str.join with no hedging: past this line the receiver is a string, so the refusals
        // can name the part that is unsupported instead of the shape of the statement.
        string? sep = StaticStringOf(jm.Object);
        if (sep == null) return false;
        if (jc.Args.Count != 1)
            throw UserError(
                "str.join takes one argument, the sequence to join; this call passes "
                + $"{jc.Args.Count}.", jm);

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
                        $"buffer was sized {existing.Capacity} by an earlier assignment", value);
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

        // The separator is a compile-time string, so this IS str.join in assignment form and
        // the SEQUENCE is what cannot be laid out. The refusal used to be left to the bare
        // expression path, which answers "assign the result to a variable before using it":
        // advice describing a property `s = ",".join([a, "b"])` already has, so a reader who
        // followed it rewrote the assignment they had and got the same error back.
        throw UserError(JoinSequenceRefusal(jc.Args[0]), jc.Args[0]);
    }

    // A PyMCU string has no heap to be built in, so str.join is a compile-time operation and
    // its result has to be spelled out while compiling. Both refusals share this, so the
    // reader gets one account of what str.join is rather than two half-descriptions.
    private const string JoinIsCompileTime =
        "str.join lays its result out at compile time (a PyMCU string lives in flash, and "
        + "there is no heap to build a new one in), so it needs a separator that is a "
        + "compile-time string and a list whose elements are all compile-time strings. The "
        + "one run-time form is ''.join([chr(b) for b in buf]) over a fixed-size buffer.";

    // Name the element that cannot be laid out, and what to write instead. Naming the FIRST
    // one is deliberate: a message that says "some element" leaves the reader checking each
    // in turn, which is the work the compiler already did.
    private string JoinSequenceRefusal(Expression seq)
    {
        if (seq is not ListExpr le)
            return "str.join needs a list written out at the call ([a, b, c]), or "
                   + "[chr(b) for b in buf] over a fixed-size buffer. " + JoinIsCompileTime;

        for (int i = 0; i < le.Elements.Count; i++)
        {
            if (StaticStringOf(le.Elements[i]) != null) continue;
            string what = le.Elements[i] switch
            {
                VariableExpr v => $"element {i} is '{v.Name}', which does not hold a "
                                  + "compile-time string",
                FStringExpr => $"element {i} is an f-string, which is built at run time",
                CallExpr => $"element {i} is a call result, which is only known at run time",
                _ => $"element {i} is only known at run time",
            };
            return $"str.join cannot build this text: {what}. {JoinIsCompileTime} Build text "
                   + "that depends on a run-time value with an f-string instead, which does "
                   + "have a run-time form: s = f\"{a},b\".";
        }

        return "str.join cannot build this text from the list given. " + JoinIsCompileTime;
    }

    // Compile-time __len__ of a class, when its body is a single constant return
    // (e.g. _NVM.__len__ -> 1024). Null when absent or not statically known.
    private int? DunderConstLen(string cls) => ConstReturnOfMethod(cls, "__len__", 0);

    /// <summary>
    /// The compile-time value a single-return method hands back, or null (#329).
    ///
    /// This used to be one AST pattern -- `[ReturnStmt { Value: IntegerLiteral }]` -- so the
    /// number had to be a literal REACHED WITHOUT LEAVING THE METHOD. A compile-time `if` chain
    /// worked only because the frontend prunes it before this runs, leaving one literal behind;
    /// a name, a class attribute or one method hop did not, and a part's EEPROM size is a chip
    /// fact that lives in the HAL, one hop away from the layer that has to ask for it.
    ///
    /// Three readings now, in the order they cost:
    ///   * a literal, unchanged;
    ///   * a NAME -- a module constant, a class attribute, an imported constant -- through the
    ///     constant evaluator the rest of the compiler uses, which already resolves all three;
    ///   * ONE HOP, `self._size()` or `Eeprom().size()`, by asking the callee the same question.
    ///
    /// The hop limit is one, and it is deliberate. This runs SPECULATIVELY from three call
    /// sites, one of them a probe that falls back on false, so it must stay pure: it reads the
    /// AST and the constant tables and emits nothing. Following an arbitrary chain would turn a
    /// cheap probe into a search, and a second hop has never been what any of these programs
    /// needed. Anything deeper keeps the sentence it has, which says the length is not a
    /// compile-time constant -- true of that program, and the honest answer.
    /// </summary>
    private int? ConstReturnOfMethod(string cls, string method, int depth)
    {
        if (depth > 1) return null;
        if (string.IsNullOrEmpty(cls)) return null;
        if (!inlineFunctions.TryGetValue(cls + "_" + method, out var fn)) return null;
        if (fn.Body.Statements is not [ReturnStmt { Value: { } ret }]) return null;
        return ConstValueOfReturn(ret, cls, depth);
    }

    /// <summary>
    /// The compile-time value of `Owner.ATTR`, or null. A class attribute is stored under the
    /// UNDERSCORE-JOINED name, optionally under the defining module's prefix, which is the same
    /// pair of keys the dotted-constant reader in the expression path tries.
    /// </summary>
    private int? ConstClassAttribute(string owner, string member)
    {
        foreach (string key in new[] { owner + "_" + member, currentModulePrefix + owner + "_" + member })
        {
            if (key.Length == 0) continue;
            if (globals.TryGetValue(key, out var sym) && !sym.IsMemoryAddress) return sym.Value;
            if (constantVariables.TryGetValue(key, out int cv)) return cv;
        }
        return null;
    }

    /// The compile-time value of a `return` expression inside <paramref name="cls"/>, or null.
    private int? ConstValueOfReturn(Expression ret, string cls, int depth)
    {
        switch (ret)
        {
            case IntegerLiteral il:
                return il.Value;

            // A NAME: a module-level constant, or a constant imported from another module.
            // EvaluateConstantExpr resolves both, and it THROWS on anything it cannot read,
            // which here means "not a compile-time constant" rather than "this program is
            // wrong" -- the caller decides what to say about that.
            case VariableExpr:
                try { return EvaluateConstantExpr(ret); }
                catch (Exception) { return null; }

            // A CLASS ATTRIBUTE, `Table.SIZE` or `self.SIZE`. Through ProbeBinding, which is
            // the null-returning half of the name resolver: the constant evaluator has no
            // reading for a dotted name at all, so it would throw on every one of these.
            case MemberAccessExpr { Object: VariableExpr attrObj } attr:
                string owner = attrObj.Name == "self" ? cls : attrObj.Name;
                return ConstClassAttribute(owner, attr.Member);

            // One hop. `self._size()` asks this class; `Eeprom().size()` asks the class the
            // call constructs, which is the shape a layer uses to reach a HAL fact.
            case CallExpr { Callee: MemberAccessExpr hop, Args.Count: 0 }:
                string? hopClass = hop.Object switch
                {
                    VariableExpr { Name: "self" } => cls,
                    VariableExpr hv => ResolveCtorClass(new CallExpr(hv, new List<Expression>())),
                    CallExpr ctor => ResolveCtorClass(ctor),
                    _ => null,
                };
                return hopClass == null ? null : ConstReturnOfMethod(hopClass, hop.Member, depth + 1);

            default:
                return null;
        }
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
                "(its __len__ is not a compile-time constant)", sl);
        List<int> idx;
        try { idx = SliceIndices(sl, len ?? int.MaxValue); }
        catch (Exception) { return false; }

        if (stmt.Value is not ListExpr le)
            throw UserError(
                "slice assignment to an object with __setitem__ needs a bytes or list " +
                "literal source of the same length", sl);
        if (le.Elements.Count != idx.Count)
            throw UserError(
                $"slice assignment length mismatch: target selects {idx.Count} " +
                $"element(s), source has {le.Elements.Count}", sl);

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
        if (step == 0) throw UserError("Slice step cannot be zero", sl);
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
        })
            if (k != null && arraySizes.TryGetValue(k, out int sz)) return (k, sz);

        // The bare name is the MODULE-level slot. A local binding of the same name shadows
        // it for every question this lookup answers: `return data` inside a function whose
        // `data` is a scalar local must not see the user's `data = bytearray(2)` (#458), and
        // `data[i] = v` must not write the global's storage through the local's name either.
        if (LocalScopeBinds(name)) return null;

        if (arraySizes.TryGetValue(name, out int bareSz)) return (name, bareSz);
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
                        $"element(s), source list has {le.Elements.Count}", sl);
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
            // The source, not the target: the target's length comes from a declaration that
            // may be anywhere, and the source slice is written here.
            throw UserError(
                $"slice assignment length mismatch: target selects {dstIdx.Count} " +
                $"element(s), source selects {srcIdx.Count}", srcArr);

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
    /// <summary>
    /// Record, or clear, that a name is bound to a tuple (#299).
    ///
    /// Both the scope-qualified key and the bare name, because the binding and the write do not
    /// always compute the same key: a module-level tuple is bound while the module's top level
    /// is being lowered and written from inside a function, and the array tables the write path
    /// normalizes against hold nothing for the short form. Clearing does both for the same
    /// reason, and clearing is what makes the wrong answer here an ACCEPTED write rather than a
    /// refused one: a name this misses is exactly where the compiler stands today.
    /// </summary>
    /// <summary>
    /// Whether the right-hand side is a NAME that is None IN THIS SCOPE.
    ///
    /// Deliberately NOT `IsNoneValued`, whose last resort is the BARE name: any scope that ever
    /// binds `value` to None would then make every `self.x = value` anywhere in the program a
    /// None field, and a ZCA bit index recorded that way stops being a constant. Measured on
    /// fixtures/compat-cp-bitbangio-i2c, which went from 272 bytes to "runtime bit index is
    /// only supported on a chip register".
    ///
    /// The scoped key is the one the parameter binding writes, which is the shape this exists
    /// for: `self._pin = pin` inside a constructor whose `pin` arrived None.
    /// </summary>
    private bool SourceIsNoneInThisScope(Expression? value)
    {
        if (value is not VariableExpr ve) return false;
        foreach (string? key in new[]
                 {
                     string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + ve.Name,
                     string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + ve.Name,
                 })
        {
            if (key != null && noneValuedNames.Contains(key)) return true;
        }
        return false;
    }

    /// Whether a write through this name is a write through a tuple, under any of the keys a
    /// binding may have recorded it as.
    private bool IsTupleBound(string name)
        => tupleBoundNames.Contains(name)
           || (!string.IsNullOrEmpty(currentFunction) && tupleBoundNames.Contains(currentFunction + "." + name))
           || (!string.IsNullOrEmpty(currentInlinePrefix) && tupleBoundNames.Contains(currentInlinePrefix + name));

    private void NoteSequenceMutability(string qualifiedKey, string bareName, bool isTuple)
    {
        if (isTuple)
        {
            tupleBoundNames.Add(qualifiedKey);
            tupleBoundNames.Add(bareName);
        }
        else
        {
            tupleBoundNames.Remove(qualifiedKey);
            tupleBoundNames.Remove(bareName);
        }
    }

    private void EmitIndexAssign(AssignStmt stmt, IndexExpr indexExpr)
    {
        // A tuple does not support item assignment, here or in CPython (#299). Refused before
        // the slice and array paths below, which cannot tell a tuple from a list: the two share
        // their storage, and the name is the only place the difference is recorded.
        //
        // The refusal names the tuple and says what to write instead. The elements go into the
        // same array either way, so a list is not a workaround for a missing capability -- it is
        // the spelling that means "this is written to".
        // `m[x, y] = v` on a target whose class cannot take the pair (#352), refused before the
        // index is visited so the generic tuple sentence never claims it. Same site and same
        // text as the read path, so the two spellings of one mistake get one answer.
        if (indexExpr.Index is TupleExpr && !SubscriptTakesAPair(indexExpr.Target, "__setitem__"))
            throw UserError(TwoIndexSubscriptRefusal, indexExpr.Index);

        if (indexExpr.Target is VariableExpr tupTgt && IsTupleBound(tupTgt.Name))
            throw UserError(
                $"'{tupTgt.Name}' is a tuple, and a tuple does not support item assignment. "
                + $"Write the same elements as a list (`{tupTgt.Name} = [...]`): the storage is "
                + "identical, and a list is the spelling that says the name is written to.",
                indexExpr);

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
                "supported — restructure with explicit element assignments", indexExpr);
        }

        if (indexExpr.Target is VariableExpr ve)
        {
            string qualified = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
            // A name no function scope claims is module scope: `poke.cfg` resolves to the
            // module array's canonical spelling (the bare `cfg` ScanGlobals filed), not a
            // per-function slot that would swallow the store (PyMCU#460).
            if (!arraySizes.ContainsKey(qualified))
                qualified = ModuleScopeArrayName(qualified);
            // `x[i] = v` where `x` was bound to a buffer a callee returned: the alias, not
            // a per-function slot, is the storage. Adopted only when the endpoint is real
            // array storage, same as the read path.
            if (!arraySizes.ContainsKey(qualified) && !bytearrayParams.Contains(qualified)
                && variableAliases.ContainsKey(qualified)
                && TryResolveArrayStorageKey(FollowAliases(qualified), out var aliasedStore))
                qualified = aliasedStore;
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

            // The alias for an inline parameter bound to a MODULE-level array resolves to the
            // function-qualified name ("main.gbuf") while a module array is registered bare
            // ("gbuf"), so the lookup missed and the subscript fell through to the register
            // bit path. With a run-time index that failed as "Bit index must be constant";
            // with a constant index it compiled SILENTLY into a bit test of the array's
            // ADDRESS. Same normalization the qualified/bare fallback above does, and the
            // mirror of the one on the read path in Expr.cs.
            if (!arraySizes.ContainsKey(qualified) && !bytearrayParams.Contains(qualified))
            {
                int lastDot = qualified.LastIndexOf('.');
                if (lastDot >= 0)
                {
                    string bare = qualified[(lastDot + 1)..];
                    if (arraySizes.ContainsKey(bare) || bytearrayParams.Contains(bare))
                        qualified = bare;
                }
            }

            // An alias can land on a class attribute's canonical name (`Sensor__BUFFER`)
            // while the storage is filed under the module init (`main.Sensor__BUFFER`) --
            // the same normalization the read path in Expr.cs applies.
            if (!arraySizes.ContainsKey(qualified) && !bytearrayParams.Contains(qualified)
                && TryResolveArrayStorageKey(qualified, out var storeKey))
                qualified = storeKey;

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
                        throw UnrolledArrayIndexError(qualified, indexExpr.Target);
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
                    // `m[x, y] = v` (#352). The KEY is bound as a compile-time sequence, the
                    // same way the VALUE already is below, so `x, y = key` in the dunder
                    // unpacks and no tuple exists at run time. Both may be sequences at once,
                    // which is `m[x, y] = (r, g, b)`.
                    if (indexExpr.Index is TupleExpr keyTup)
                    {
                        var seq = new Dictionary<int, ListExpr>
                        {
                            { 0, new ListExpr(keyTup.Elements) },
                        };
                        var args = new List<Val> { new NoneVal(), new NoneVal() };
                        if (stmt.Value is ListExpr vle) seq[1] = vle;
                        else if (stmt.Value is TupleExpr vte) seq[1] = new ListExpr(vte.Elements);
                        else args[1] = VisitExpression(stmt.Value);
                        EmitDunderCall(selfName, cls, funcKey, args, seq);
                        return;
                    }
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
                        stmt.Line > 0 ? stmt.Line : lastLine, stmt.Column);
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
        // A minus sign is always a Negate around a POSITIVE literal: the parser never builds
        // a negative IntegerLiteral. So a negative value on the node can only be the 32-bit
        // bit pattern the parser stored for a literal in 2^31..2^32-1, and the magnitude the
        // program wrote is the unsigned reading of it. Negating the wrapped pattern instead
        // turned `-2147483648` into +2147483648, which was then rejected as out of range for
        // int32, the very type whose minimum it is.
        UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral lit } => -(long)(uint)lit.Value,
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

        // `init` IS the literal the message quotes, and a literal is stamped, so this is the
        // one direct throw in the file that had a line and no column with a node in hand.
        if (shown < r.Min || shown > r.Max)
            throw new ValueError(
                $"integer literal {shown} is out of range for {r.Name} (valid range {r.Min}..{r.Max})",
                init.Line > 0 ? init.Line : line, init.Column, init.Length > 0 ? init.Length : 1);
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
                "add `import pymcu.strfmt as _pymcu_strfmt` to the entry file.", value);

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
                    "the longest f-string first (buffer size is fixed at the first assignment).", value);
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
    /// <summary>
    /// Remembers what a function-local now holds, when that is a compile-time value.
    ///
    /// The VISITED value answers for `x = 2`. It does not answer for `ms = total_us // 1000`,
    /// where total_us is itself a local: reads of a local are deliberately not folded, so the
    /// division arrives as a run-time Temporary even though every input is known. The
    /// initializer is therefore folded as WRITTEN, with the locals in scope, which is the same
    /// question the range unroller asks (PyMCU#327).
    ///
    /// A value the declared width would truncate is NOT recorded: the storage would hold one
    /// number and a later call would be handed another.
    /// </summary>
    private void RecordLocalConstant(string key, Val value, Expression? init,
                                     string? declaredType, DataType storedType)
    {
        // A FLOAT name is never recorded here, and the map is never asked about one. This map
        // holds INTEGERS; a float local reaching it answers a read with the integer part, and
        // `x: float = 1.0` then a read of x came back as the integer 1, whose bytes are not
        // 1.0's. Measured: the compat-cp float probes read OCR0A as 0x00 where 1.0's MSB is
        // 0x3F. The float constants live in floatConstantVariables and are asked for there.
        if (storedType == DataType.FLOAT
            || (declaredType != null && declaredType.Contains("float", StringComparison.Ordinal)))
        {
            localConstantValues.Remove(key);
            return;
        }

        string width = !string.IsNullOrEmpty(declaredType) ? declaredType! : storedType switch
        {
            DataType.INT8 => "int8",
            DataType.UINT16 => "uint16",
            DataType.INT16 => "int16",
            DataType.UINT32 => "uint32",
            DataType.INT32 => "int32",
            _ => "uint8",
        };

        if (value is Constant c)
        {
            if (FitsInScalar(c.Value, width)) localConstantValues[key] = c.Value;
            else localConstantValues.Remove(key);
            return;
        }

        if (init != null && TryFoldWithLocals(init, out int folded) && FitsInScalar(folded, width))
        {
            localConstantValues[key] = folded;
            return;
        }

        localConstantValues.Remove(key);
    }

    /// <summary>The value of an expression with the caller's locals in scope, or false.</summary>
    private bool TryFoldWithLocals(Expression e, out int value)
    {
        bool saved = foldLocalConstants;
        foldLocalConstants = true;
        try { return TryFoldThroughConversions(e, out value); }
        finally { foldLocalConstants = saved; }
    }

    /// <summary>Drops every spelling of a name from the locals map: it has just been written.</summary>
    private void ForgetLocalConstant(string bareName)
    {
        foreach (var k in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + bareName,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + bareName,
            bareName,
        })
            if (k != null) localConstantValues.Remove(k);
    }

    private void InvalidateAliasesForWrite(string name)
    {
        foreach (var k in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + name,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + name,
            name,
        })
        {
            if (k == null) continue;
            // Every write to the name, whatever spelling reaches here, clears what it was
            // known to hold. The two assignment sites put it back when the value is constant.
            localConstantValues.Remove(k);
            if (!writeThroughAliases.Contains(k))
                variableAliases.Remove(k);
        }

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
        CheckAnnotationNames(stmt.VarType, stmt);

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
                    stmt.Line > 0 ? stmt.Line : lastLine, stmt.Column);
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
                    stmt.Line > 0 ? stmt.Line : lastLine, stmt.Column);
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
            // The element EXPRESSIONS of a list form, kept so a run-time element is stored
            // by evaluating it rather than as the zero the constant-only initVals carries.
            List<Expression>? initElems = null;
            bool isInput = false;
            string inputPrompt = "";
            int inputMaxLen = 64;

            // The expression the size was supposed to come from, kept so the refusal below
            // can point at it. Not the `bytearray(...)` call and not the declared name: the
            // message says the size could not be determined FROM THE INITIALIZER, and the
            // initializer's argument is the part that failed to supply one. An argument the
            // compiler never got to look at leaves this null and the caret is withheld,
            // because then there is no such part.
            Expression? sizeSource = null;

            if (stmt.Init != null)
            {
                // A bytes literal carries its own size: b"ab" is two bytes.
                if (stmt.Init is ListExpr bytesLit)
                {
                    sizeSource = bytesLit;
                    count = bytesLit.Elements.Count;
                    initElems = bytesLit.Elements;
                    foreach (var e in bytesLit.Elements)
                        initVals.Add(TryEvalElemConst(e, out int bv) ? bv : 0);
                }
                else if (stmt.Init is CallExpr call && call.Callee is VariableExpr callee)
                {
                    if (callee.Name == "bytearray" && call.Args.Count > 0)
                    {
                        Expression arg0 = call.Args[0];
                        sizeSource = arg0;
                        if (arg0 is ListExpr le)
                        {
                            count = le.Elements.Count;
                            initElems = le.Elements;
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
                    // `bytes([...])` / `bytes(N)`: the immutable spelling of the same
                    // constructor (#365 already reads a `bytes` PARAMETER as this same
                    // buffer). `TryBytesLiteralElements` refuses a run-time N itself, naming
                    // bytearray as the type that takes one.
                    else if (callee.Name == "bytes" && TryBytesLiteralElements(call) is { } bytesElems)
                    {
                        sizeSource = call.Args.Count > 0 ? call.Args[0] : call;
                        count = bytesElems.Count;
                        if (call.Args.Count > 0 && call.Args[0] is ListExpr) initElems = bytesElems;
                        foreach (var e in bytesElems)
                            initVals.Add(TryEvalElemConst(e, out int v) ? v : 0);
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
                                // Two ways to fall out of those, and they are different
                                // questions. A key input() does not have is the same refusal
                                // every other call gives; a key it DOES have, carrying
                                // something that is not a literal, is about the value. Both
                                // were silently dropped: `maxlenn=8` took the default 64-byte
                                // buffer instead of 8, which is 56 bytes of SRAM nobody asked
                                // for on a part that has two thousand of them.
                                else if (kw.Key is "prompt" or "maxlen")
                                    // The VALUE, not the key. The key is spelled correctly and
                                    // is one input() accepts; what is wrong is what it carries,
                                    // and that is the half the reader has to rewrite. The
                                    // neighbouring refusal seven lines down is about the key
                                    // instead, and marks the call.
                                    throw UserError(
                                        $"input() '{kw.Key}' must be a compile-time "
                                        + (kw.Key == "prompt" ? "string literal" : "integer literal"),
                                        kw.Value);
                                else
                                    RefuseUnknownKeyword("input", kw.Key, InputKeywords, call);
                            }
                            else
                                // The argument that is none of the three accepted shapes. A
                                // call can pass several and only one of them be wrong, so
                                // marking the call would leave the reader to find out which.
                                throw UserError("input(): arguments must be compile-time string literal (prompt) and/or integer (maxlen)",
                                                arg);
                        }
                        count = inputMaxLen;
                        initVals.AddRange(Enumerable.Repeat(0, count));
                    }
                }
            }

            if (count <= 0)
                throw UserError("bytearray: could not determine buffer size from initializer.",
                                sizeSource);

            string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + stmt.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.Name : stmt.Name);

            // Module scope replayed inside the synthesized init: the scan already filed the
            // array under the BARE name, so `main.cfg` is the same storage spelled another
            // way -- and registering it again split the array in two, with writes through a
            // `global` landing on whichever spelling was not the one that was initialised
            // (PyMCU#460). The annotated spelling (`cfg: bytearray = ...`) already falls
            // back this way; the unannotated `cfg = bytearray(N)` arrives here.
            bool replayingModuleLevel = string.IsNullOrEmpty(currentInlinePrefix)
                && (currentFunction == "main"
                    || currentFunction.EndsWith("___module_init", StringComparison.Ordinal));
            if (replayingModuleLevel
                && !arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(stmt.Name))
                qualified = stmt.Name;

            arraySizes[qualified] = count;
            arrayElemTypes[qualified] = DataType.UINT8;
            variableTypes[qualified] = DataType.UINT8;
            arraysWithVariableIndex.Add(qualified);

            if ((string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix))
                || replayingModuleLevel)
                moduleSramArrays.Add(qualified);

            for (int k = 0; k < count; ++k)
            {
                // An element that is not a compile-time constant is evaluated HERE, at
                // construction, and stored -- `bytearray([reg & 0xFF])` wrote a zero before
                // (initVals carries 0 for a non-constant), silently sending the wrong byte.
                Val initVal = initElems != null && !TryEvalElemConst(initElems[k], out _)
                    ? VisitExpression(initElems[k])
                    : new Constant(initVals[k]);
                Emit(new ArrayStore(qualified, new Constant(k), initVal, DataType.UINT8, count));
            }

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
                    stmt.Line > 0 ? stmt.Line : lastLine, stmt.Column);

            // Declarations bind the name itself -- never a stale value-tracking alias
            // (same invalidation-before-resolve as EmitScalarVarAssign).
            InvalidateAliasesForWrite(stmt.Name);
            // A name bound to several string literals keeps its id in a 16-bit slot: resolving
            // it would answer with THIS binding's id, and the copy would have no destination
            // (`copy const 256 -> const 256` is what the declaration used to emit).
            Val? strSlot = stmt.VarType == "str" && stmt.Init is StringLiteral
                ? MultiStrStoreTarget(stmt.Name) : null;
            Val target = strSlot ?? ResolveBinding(stmt.Name);
            if (strSlot == null && target is Variable v) target = v with { Type = dt };
            Emit(new Copy(val, target));

            if (string.IsNullOrEmpty(currentFunction))
            {
                if (val is Constant c && target is Variable tv && !mutableGlobals.ContainsKey(tv.Name))
                {
                    constantVariables[tv.Name] = c.Value;
                }
            }
            else if (target is Variable ltv)
            {
                // The declared local. Same reasoning as EmitScalarVarAssign: remembered for the
                // call sites that pass it, not folded into every read of it (PyMCU#327).
                RecordLocalConstant(ltv.Name, val, stmt.Init, stmt.VarType, dt);
            }
        }
    }

    // Reserves the per-instance SRAM array behind a member (self._buf) and emits its
    // initialisers. Shared by the three spellings that declare one: `self._buf: uint8[N]`,
    // `self.buf: list[uint8] = [...]` and the bare `self.buf = [...]`.
    private void EmitMemberArrayInit(Expression objExpr, string member, DataType elem,
                                     int count, List<int> init, string targetShown)
    {
        var objVal = VisitExpression(objExpr);
        string? baseName = objVal is Variable v ? v.Name : (objVal is Temporary t ? t.Name : "");
        while (baseName != null && variableAliases.TryGetValue(baseName, out var alias)) baseName = alias;
        if (string.IsNullOrEmpty(baseName))
            throw UserError("Cannot resolve instance for member array '" + targetShown + "'", objExpr);
        string flat = baseName + "_" + member;

        arraySizes[flat] = count;
        arrayElemTypes[flat] = elem;
        variableTypes[flat] = elem;
        arraysWithVariableIndex.Add(flat);

        for (int k = 0; k < count; ++k)
            Emit(new ArrayStore(flat, new Constant(k), new Constant(init[k]), elem, count));
    }

    // The narrowest type that holds every element of an unannotated integer list literal.
    // A list field written without an annotation has no declared element type, and picking
    // uint8 outright would silently truncate `self.levels = [0, 300]`.
    private static DataType WidestElemType(List<int> values)
    {
        bool negative = values.Any(x => x < 0);
        int magnitude = values.Count == 0 ? 0 : values.Max(x => x < 0 ? -x : x);
        if (negative)
            return magnitude <= 0x7F ? DataType.INT8 : magnitude <= 0x7FFF ? DataType.INT16 : DataType.INT32;
        return magnitude <= 0xFF ? DataType.UINT8 : magnitude <= 0xFFFF ? DataType.UINT16 : DataType.UINT32;
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
        if (TryResolveArrayStorageKey(flat, out var flatStored)) return flatStored;

        // A field that was HANDED an array (`self._data = data`, the shape every buffer-taking
        // driver has) is another NAME for that storage, not a copy of its address into a scalar
        // field. Follow the alias to the array itself so the indexed load and store find it.
        // The alias can land on the class-canonical name (`Sensor__BUFFER`) while the storage
        // is filed under the module init (`main.Sensor__BUFFER`), so normalize that endpoint
        // too -- without it a `self._BUFFER[i]` store fell through to a bit write on the bare
        // global while enumerate() read the real array.
        string resolved = FollowAliases(flat);
        if (resolved != flat)
        {
            if (TryResolveArrayStorageKey(resolved, out var resStored)) resolved = resStored;
            if (!arraySizes.ContainsKey(resolved)) return null;
            // A compile-time sequence of instances also lives behind such an alias, and it has no
            // SRAM to index: that shape is answered by the element paths, not by an array load.
            return instanceClasses.ContainsKey(resolved + "__0") ? null : resolved;
        }

        // A CLASS attribute (`_BUFFER = bytearray(8)` declared on the class, not the
        // instance) has no per-instance flat name and no field alias -- the attribute is
        // registered under its class-canonical name (`Sensor__BUFFER`) while its array
        // storage is filed under the module init (`main.Sensor__BUFFER`). Without this
        // probe, `self._BUFFER[i]` in a method fell through to a bit op on the bare
        // global while `enumerate(buffer)` read the real array -- the same program
        // reading and writing two different objects.
        if (TryFindClassAttribute(baseName, mem.Member, out _, out var attrName)
            && TryResolveArrayStorageKey(attrName, out var attrStored))
            return attrStored;
        return null;
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
            // Same reinterpretation as the single-instance slot: a float's bytes are its
            // IEEE-754 representation, so they are split as an integer and not shifted as a
            // float. This is the second site of the one rule, and both call one helper.
            var (vBits, vBitsTy) = AsStorableBits(v, fdt);
            for (int k = 0; k < fsz; ++k)
            {
                Temporary b = MakeTemp(DataType.UINT8);
                if (k == 0)
                {
                    Emit(new Copy(vBits, b));
                }
                else
                {
                    Temporary sh = MakeTemp(vBitsTy);
                    Emit(new Binary(BinaryOp.RShift, vBits, new Constant(8 * k), sh));
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
    /// The heads a bracketed annotation may have that are NOT already scalar type names:
    /// `ptr[uint8]`, `list[uint8]`, `tuple[uint8, uint8]`, `Callable[...]`. `tuple` is the
    /// multi-value return form and is the one this list was missing when it was first written:
    /// the corpus said so, with one integration fixture and seven unit tests, rather than with
    /// an argument.
    ///
    /// Asked alongside ScalarTypeNames rather than seeded from it. The two live in different
    /// files of this partial class, and a static field that reads another file's static field
    /// initialised to null here and turned every bracketed annotation into an
    /// InternalCompilerError -- which compiles clean and only appears when a program is run
    /// through it.
    /// </summary>
    private static readonly HashSet<string> BracketedFormHeads =
        new() { "ptr", "list", "tuple", "Callable", "PIORegister" };

    private static bool IsKnownBracketedHead(string head) =>
        ScalarTypeNames.Contains(head) || BracketedFormHeads.Contains(head);

    // Word for word Parser.cs's UnionAnnotationRefusal and the CPython bridge's copy of it in
    // pymcu_translate.py, which already carry "change one, change both" notes to each other.
    // Change one, change all THREE. Duplicated rather than shared because the parser's copy is
    // private to it, and consolidating them means editing Parser.cs.
    //
    // The two spellings report in different PHASES -- `a | b` at parse, `Union[a, b]` here --
    // so a reader sees SyntaxError for one and CompileError for the other. The sentence they
    // act on is identical.
    private const string UnionAnnotationRefusal =
        "a union type annotation is not supported. PyMCU needs one concrete type, because the " +
        "storage for a value is decided at compile time and two types do not share a size";

    /// The 64-bit integer names this compiler has no lowering for, mapped to the widest name it
    /// does have. `DataType` has no 64-bit member on any backend.
    private static readonly Dictionary<string, string> SixtyFourBitNames = new()
    {
        ["uint64"] = "uint32",
        ["int64"] = "int32",
    };

    /// <summary>
    /// Refuses `uint64` and `int64`, which were accepted and compiled as ONE BYTE.
    ///
    /// Both names are in this compiler's own list of built-in type names, so the annotation was
    /// legal, and `StringToDataType` has no branch for either, so it returned `UNKNOWN` -- whose
    /// `SizeOf()` is 1. Measured across all five annotation positions, both front ends and both
    /// targets: 40 of 40 accepted, exit 0, and the name carried `UNKNOWN` in the IR wherever a
    /// width was observable (#410).
    ///
    /// Removing the two names from the known set instead would produce "unknown type 'uint64'",
    /// which is worse: the name IS a type, it is just one this target has no storage for, and a
    /// reader told it is unknown goes looking for a typo. So the refusal names the widest type
    /// that does exist, which is the edit the reader has to make.
    ///
    /// Judged here rather than in the two readers, for the reason the union refusal above gives:
    /// this is the one site both front ends reach, so they cannot diverge by a comment going
    /// unread. `pymcu.types` exports neither name, so a program using one does not compile under
    /// CPython either -- which is the second reason it cannot be made to work by widening a case
    /// somewhere.
    /// </summary>
    private void RefuseSixtyFourBit(string annotation, ASTNode? at)
    {
        foreach (var (name, widest) in SixtyFourBitNames)
        {
            if (annotation != name && !annotation.Contains(name, StringComparison.Ordinal)) continue;
            // Substring, so `myuint64` and `uint64_t` are somebody else's names, not this one.
            if (annotation != name && !IsWholeTypeName(annotation, name)) continue;
            throw UserError(
                $"'{name}' is not a width PyMCU can store: no target it compiles for has 64-bit "
                + $"integers, and the name was being given ONE byte. The widest integer type is "
                + $"'{widest}'.", at);
        }
    }

    /// True when <paramref name="name"/> appears in <paramref name="annotation"/> as a complete
    /// type name rather than as part of a longer identifier.
    private static bool IsWholeTypeName(string annotation, string name)
    {
        int i = 0;
        while ((i = annotation.IndexOf(name, i, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = i == 0 || !IsNameChar(annotation[i - 1]);
            int end = i + name.Length;
            bool rightOk = end == annotation.Length || !IsNameChar(annotation[end]);
            if (leftOk && rightOk) return true;
            i = end;
        }
        return false;

        static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    /// <summary>
    /// Reject an annotation that names no type this compiler knows. An unknown name used to
    /// fall back to uint8 without a word, so `x: unit8 = a * 300` truncated to 8 bits and
    /// printed 96 where the same line without an annotation printed 60000 -- a typo in one
    /// character silently changed the arithmetic. Only bare identifiers are checked: anything
    /// with brackets is a form (`uint8[4]`, `const[uint8]`, `list[uint8]`) whose own handling
    /// reports what it cannot make sense of.
    ///
    /// EVERY POSITION AN ANNOTATION CAN APPEAR IN, which was the bug (#278). This was written
    /// for a local and a module-level global and applied to those two only, so the same typo
    /// in a parameter, a return type or an instance field was accepted in silence. There is
    /// one check and one sentence for all five, deliberately: the reader's mistake is the
    /// same mistake wherever they made it.
    ///
    /// `at` is the node to point at. Without one the error lands on whatever `lastLine` held,
    /// which reported a seven-line file at line 206.
    /// </summary>
    private void CheckAnnotationNames(string annotation, ASTNode? at = null, bool allowUnion = false)
    {
        if (string.IsNullOrEmpty(annotation)) return;

        // `...` on its own. Both readers now carry it here as text rather than refusing it
        // themselves (#357), so this is the one site that answers for it and the two front ends
        // produce the same sentence by construction rather than by a comment asking for it.
        if (annotation == "...")
            throw UserError("'...' is not a type annotation PyMCU can read. Write one type "
                            + "name, optionally with a size or element type in brackets, "
                            + "e.g. uint8 or uint8[4]", at);

        // A BRACKETED annotation is checked by its HEAD name. This used to return here, on the
        // grounds that a bracketed form's own handling reports what it cannot make sense of.
        // That is true for the forms that mean something and false for every other head, so
        // `Optional[uint8]`, `Tuple[uint8, uint8]`, `List[uint8]`, `Dict[uint8, uint8]` and
        // `Bogus[uint8, bool]` were all accepted in silence -- and those are the spellings a
        // typing-annotated library writes, so the hole was closed for the rare spelling and
        // left open for the common one.
        // A union that is STILL a union after the normaliser has read it: two real types, and
        // no width they share. `X | None` never reaches here, because None-ness is a
        // compile-time property and the annotation means X; what is left is the case the
        // refusal was always about.
        //
        // Judged here rather than in the two readers, which is where it used to be refused
        // twice, word for word. Neither reader can tell `uint8 | None` from `uint8 | bool`
        // without the rule, and a rule kept in two files by a comment is the divergence this
        // one site exists to close.
        if (annotation.Contains('|'))
        {
            if (allowUnion) return;
            throw UserError(UnionAnnotationRefusal, at);
        }

        int lb = annotation.IndexOf('[');
        if (lb >= 0)
        {
            string head = annotation[..lb];
            // `Optional[X]` IS `Union[X, None]`, and `Union[a, b]` IS `a | b`. One idea, so one
            // answer: the sentence the `|` spelling already gets, rather than a true and
            // useless "unknown type 'Union'".
            //
            // `allowUnion`: a parameter of an @inline-expanded function or method (an ordinary
            // constructor included -- every ZCA instance is built at its call site) reads a
            // union as "the type of the argument AT THIS SITE, which must be one of the
            // members" -- exactly how an @inline overload already dispatches on an argument's
            // type, just without a second FunctionDef to pick between. `Optional[X]` was
            // already `X`, by the time this runs, everywhere; only a union of two REAL types
            // reaches here, and only on a parameter is a call site available to resolve it.
            if (head is "Union" or "typing.Union")
            {
                if (allowUnion) return;
                throw UserError(UnionAnnotationRefusal, at);
            }
            if (head is "Optional" or "typing.Optional")
                throw UserError(UnionAnnotationRefusal, at);
            // The 64-bit names before the known-head return, and inside the brackets as well
            // as at the head: `const[uint64]` and `uint64[4]` both put a width this compiler
            // does not have where storage is decided.
            RefuseSixtyFourBit(annotation, at);
            if (head.Length == 0 || IsKnownBracketedHead(head)) return;
            annotation = head;   // fall through and report the head as the unknown type
        }

        RefuseSixtyFourBit(annotation, at);
        if (ScalarTypeNames.Contains(annotation)) return;
        if (annotation is "ptr" or "object" or "self") return;
        if (classNames.Contains(annotation) || classFieldLayout.ContainsKey(annotation)) return;

        // `busio.I2C`, the module-qualified spelling of a class (#342). The bare name the
        // dotted one ends in is what every check below is written against, and the two reach
        // the same class: `from busio import I2C` with `p: I2C` compiles today, and the
        // dotted form is what a CircuitPython library writes because a module-level import is
        // what it has.
        //
        // The head must NAME A MODULE, so `Direction.OUTPUT` -- a class attribute, not a type
        // -- keeps the refusal it has rather than being read as the type `OUTPUT`. Only then
        // is the tail retried through the whole check, which is why this is a rewrite of
        // `annotation` and not a `return`: a dotted name ending in a typo must still be
        // reported, by the same sentence, against the name the reader wrote.
        int lastDot = annotation.LastIndexOf('.');
        if (lastDot > 0)
        {
            string head = annotation[..annotation.IndexOf('.')];
            if (modules.ContainsKey(head) || IsImportedAlias(head) || aliasToOriginal.ContainsKey(head))
            {
                string tail = annotation[(lastDot + 1)..];
                if (ScalarTypeNames.Contains(tail)
                    || classNames.Contains(tail) || classFieldLayout.ContainsKey(tail)
                    || IsImportedAlias(tail) || aliasToOriginal.ContainsKey(tail)
                    || classNames.Any(c => c.EndsWith("." + tail, StringComparison.Ordinal)
                                           || c.EndsWith("_" + tail, StringComparison.Ordinal))
                    || (ResolveCallee(tail) is { } dotted
                        && (classNames.Contains(dotted) || classFieldLayout.ContainsKey(dotted))))
                    return;
            }
        }
        if (IsImportedAlias(annotation) || aliasToOriginal.ContainsKey(annotation)) return;
        if (classNames.Any(c => c.EndsWith("." + annotation, StringComparison.Ordinal)
                                || c.EndsWith("_" + annotation, StringComparison.Ordinal))) return;
        if (ResolveCallee(annotation) is { } resolved
            && (classNames.Contains(resolved) || classFieldLayout.ContainsKey(resolved))) return;

        // #376: an annotation naming an ENUM MEMBER, not the enum type -- `digitalio.Direction
        // .OUTPUT` or the bare `Direction.OUTPUT`. CircuitPython's digitalio exposes
        // Direction.OUTPUT and Pull.UP as the values a property can take, and a driver
        // annotates the property with the value it actually returns, which is unusual Python
        // and legal. `Direction` and `Pull` are compile-time constants in the layer already
        // (ALL-CAPS class attributes, folded exactly like a module-level constant), so a member
        // of one is a value whose type is known: the annotation is read as that value's own
        // type, which is what CheckAnnotationNames answers "" (this target's default numeric
        // width) for everywhere else a folded constant is used without one of its own.
        //
        // Judged on the class the annotation names, not on whether the LAST segment is really
        // one of its members: the busio.I2C block above accepts a dotted class name the same
        // loose way, by the class alone, and a member spelled wrong is still inside a class the
        // compiler knows -- refusing it here would report the class as unknown, which is false.
        if (lastDot > 0 && IsKnownClassPath(annotation[..lastDot])) return;

        // NEVER the name being rejected. The suggestion pool and the known set are different
        // sets, so a name can be in the pool and out of the known set -- `list`, `tuple` and
        // `PIORegister` are, since they are legal only as the HEAD of a bracketed form -- and
        // `x: list` answered "unknown type 'list' (did you mean 'list'?)". A suggestion
        // identical to the input cannot work by construction, and it is worse than none: it
        // tells the reader the compiler cannot see a difference it is acting on.
        //
        // Filtered here rather than by pruning the pool, because the pool is right: `List`
        // SHOULD be told about `list`. It is only the distance-zero case that is nonsense.
        // A name that is legal only as the HEAD of a bracketed form is not a near-miss for
        // anything: the reader has the right word and the wrong shape. `x: list` used to be
        // told "did you mean 'int'?", which is true, unhelpful, and teaches that `int` exists
        // rather than the spelling that works. Same slot, same sentence, a hint that fits the
        // mistake instead of a suggestion that does not.
        // A TYPING-ONLY name (#367): `Type`, `Sequence`, `TracebackType`, a PIL `Image` -- a name
        // that comes from `typing` or from an import the module itself wrote inside a `try`
        // because a board does not have it. It names nothing this target has a representation
        // for, which is exactly why it is not a typo: the library is telling a type checker
        // something, and the value it annotates is usually one the body never touches.
        //
        // Accepted here and REFUSED AT THE FIRST READ, in ResolveBinding, so no computation is
        // deleted and nothing the program touches has an unknown width. The sentence moves to
        // the line that uses the value instead of a signature whose parameters the body ignores.
        if (IsTypingOnlyName(annotation)) return;

        string hint = BracketedFormHeads.Contains(annotation)
            ? $" ('{annotation}' is the head of a bracketed type, not a type on its own: "
              + $"write '{annotation}[...]' with the element type, e.g. {ExampleForm(annotation)})"
            : NearMissHint(annotation);

        throw UserError($"unknown type '{annotation}' in the annotation" + hint
            + ". An unrecognized annotation used to be read as uint8, which changed the "
            + "arithmetic without saying so.", at);
    }

    /// Whether <paramref name="path"/> -- the text of an annotation up to (not including) its
    /// last dot -- names a class this compiler knows, in either spelling CheckAnnotationNames
    /// already accepts for a class annotation: module-qualified (`busio.I2C`) or bare
    /// (`I2C`). Used by #376 to accept `Module.Class.MEMBER` and `Class.MEMBER` annotations
    /// once the class half is confirmed real, independent of whether the class was reached
    /// through one dot or several (a nested class, `alarm.time.TimeAlarm`, has two).
    private bool IsKnownClassPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        int dot = path.IndexOf('.');
        if (dot < 0)
            return classNames.Contains(path) || classFieldLayout.ContainsKey(path)
                || classNames.Any(c => c.EndsWith("." + path, StringComparison.Ordinal)
                                       || c.EndsWith("_" + path, StringComparison.Ordinal))
                || (ResolveCallee(path) is { } resolved
                    && (classNames.Contains(resolved) || classFieldLayout.ContainsKey(resolved)));

        string head = path[..dot];
        if (!modules.ContainsKey(head) && !IsImportedAlias(head) && !aliasToOriginal.ContainsKey(head))
            return false;

        string tail = path[(path.LastIndexOf('.') + 1)..];
        return classNames.Contains(tail) || classFieldLayout.ContainsKey(tail)
            || classNames.Any(c => c.EndsWith("." + tail, StringComparison.Ordinal)
                                   || c.EndsWith("_" + tail, StringComparison.Ordinal))
            || (ResolveCallee(tail) is { } dotted
                && (classNames.Contains(dotted) || classFieldLayout.ContainsKey(dotted)));
    }

    /// The `typing` spellings a library writes whether or not it imports them. Kept alongside
    /// the names collected from folded imports rather than instead of them: a module may write
    /// `Type[...]` under `if TYPE_CHECKING:` with no import this compiler ever sees.
    private static readonly HashSet<string> TypingModuleNames = new()
    {
        "Type", "Sequence", "Iterable", "Iterator", "Mapping", "MutableMapping", "MutableSequence",
        "Any", "AnyStr", "Text", "NoReturn", "ClassVar", "Final", "Annotated", "Generic",
        "TypeVar", "Protocol", "Hashable", "Sized", "Container", "Collection", "Reversible",
        "Awaitable", "Coroutine", "AsyncIterable", "AsyncIterator", "ContextManager", "IO",
        "TracebackType", "ModuleType", "FunctionType",
    };

    /// <summary>
    /// Whether an annotation is built from a name that stands for nothing at run time here.
    ///
    /// The HEAD decides, because `Type[type]` and `Sequence[int]` are the shapes these appear
    /// in, and the bracket holds another such name as often as not. A dotted spelling
    /// (`typing.Type`) answers on its tail, the way every other dotted annotation does.
    /// </summary>
    private bool IsTypingOnlyName(string annotation)
    {
        if (string.IsNullOrEmpty(annotation)) return false;
        int lb = annotation.IndexOf('[');
        string head = lb >= 0 ? annotation[..lb] : annotation;
        head = head[(head.LastIndexOf('.') + 1)..];
        if (head.Length == 0) return false;
        return TypingModuleNames.Contains(head)
               || typingOnlyNames.Contains(head)
               // A builtin exception NAME in an annotation position. There is no exception
               // object on this target -- a raise carries only which exception was raised --
               // so `exc_val: BaseException` is a promise about a value that cannot exist,
               // which is the same situation as the two above.
               || PyMCU.Common.BuiltinExceptionNames.Codes.ContainsKey(head)
               || head is "BaseException" or "Exception";
    }

    /// <summary>The closest known type name to <paramref name="annotation"/>, as a parenthesised
    /// hint, or "" when nothing is close. NEVER the name itself: the suggestion pool and the
    /// known set are different sets, so a name can be in the pool and out of the known set, and
    /// a suggestion identical to the input cannot work by construction (#280).</summary>
    private string NearMissHint(string annotation)
    {
        string? near = ScalarTypeNames.Concat(BracketedFormHeads).Concat(classNames)
            .Where(n => n != annotation)
            .Where(n => EditDistance(n, annotation) <= 2)
            .OrderBy(n => EditDistance(n, annotation))
            .FirstOrDefault();
        return near != null ? $" (did you mean '{near}'?)" : "";
    }

    /// <summary>A worked example of the bracketed form, so the hint shows the shape rather than
    /// describing it.</summary>
    private static string ExampleForm(string head) => head switch
    {
        "tuple" => "tuple[uint8, uint8]",
        // Unreachable while `Callable` is also a scalar type name, so a bare one returns as
        // known before it gets here. Kept because the fallback below would be wrong for it,
        // and because that membership is the kind of thing that moves.
        "Callable" => "Callable[[], None]",
        _ => head + "[uint8]",
    };

    /// <summary>
    /// Check the annotations in every function SIGNATURE the program defines.
    ///
    /// Not in VisitFunction, which is where it belongs by shape and does not work: a function
    /// that is force-inlined at its call sites never reaches it. Measured -- for a program
    /// with `def take(v: Bogus)` called once, VisitFunction runs for `main` alone, so a check
    /// there sees no parameter at all. Every DEFINITION passes through here instead.
    ///
    /// Runs after every module is scanned, because the check asks whether a name is a class
    /// and the class tables are not complete until then.
    /// </summary>
    private void CheckSignatureAnnotations(ProgramNode ast)
    {
        void Fn(FunctionDef f)
        {
            // The FILE the signature is written in, for as long as it is being checked (#347).
            // Every module's functions reach this one sweep, and it used to run with
            // `currentSourcePath` at whatever the last module left it -- empty by the time the
            // sweep runs -- so `LocatedFile` meant "the entry file" while the line came from
            // the definition, in its own module. The pair named a location that does not
            // exist: a union inside adafruit_motor/servo.py was reported as main.py:52, in a
            // main.py fifteen lines long.
            //
            // The scan already recorded the path per definition, which is the same answer the
            // inline-expansion path takes from the same map, so there is one source of truth
            // for where a function was written rather than two.
            string savedPath = currentSourcePath;
            if (functionSourcePath.TryGetValue(f, out var defPath))
            {
                currentSourcePath = defPath;
                currentSourceFile = defPath.Length > 0 ? SourceFileLabel(defPath) : "";
            }

            try
            {
                // A union on a PARAMETER is resolvable at the call site -- the argument's
                // actual type is known there -- only for a function or method that IS expanded
                // at its call site: an @inline decorator, or __init__, which every ZCA instance
                // already builds that way. A real subroutine has one ABI for every caller, and
                // a union parameter there keeps its refusal.
                bool paramUnionAllowed = f.IsInline || f.Name == "__init__";
                foreach (var prm in f.Params) CheckAnnotationNames(prm.Type ?? "", f, paramUnionAllowed);
                CheckAnnotationNames(f.ReturnType ?? "", f);

                // A RETURN annotation is the one position where a tuple's LENGTH is what the
                // compiler needs: the count is what the caller unpacks, and there is no
                // run-time tuple to ask. `-> Tuple[int, ...]` says the length is open, which
                // is the one thing this position cannot read (#357). Everywhere else -- a
                // parameter, a global -- only the element type matters and the `...` costs
                // nothing.
                if (PyMCU.Common.AnnotationText.IsVariadicTuple(f.ReturnType))
                    throw UserError(
                        $"'{f.ReturnType}' does not say how many values '{f.Name}' returns, and "
                        + "a return position needs that number: the caller unpacks it at "
                        + "compile time and there is no run-time tuple to count. Write the "
                        + "elements out (e.g. `-> tuple[uint8, uint8]`).", f);
            }
            finally
            {
                currentSourcePath = savedPath;
            }
        }

        foreach (var f in ast.Functions) Fn(f);
        foreach (var st in ast.GlobalStatements)
            if (st is ClassDef { Body: Block cb })
                foreach (var m in cb.Statements)
                    if (m is FunctionDef mf) Fn(mf);
    }

    /// <summary>
    /// Check the annotation on every MODULE-LEVEL GLOBAL the program declares (#348).
    ///
    /// This was the one annotation position `CheckAnnotationNames` never swept. A local, a
    /// parameter, a return type and an instance field all reached it; `X: Bogus = 1` written at
    /// a module's top level did not, so it took the silent uint8 fallback that #278 exists to
    /// stop -- and the same name one line lower, inside a function, was refused. One position
    /// answering differently from the other four is the mistake being reported inconsistently,
    /// not a different mistake.
    ///
    /// A module-level declaration never reaches `VisitAnnAssign` or `VisitVarDecl`, which is
    /// where the other spellings are checked: ScanGlobals consumes it, and ScanGlobals runs
    /// before the class tables are complete, so the check cannot live there either. It runs
    /// here, beside the signature sweep, for the same reason and at the same moment.
    /// </summary>
    private void CheckGlobalAnnotations(ProgramNode ast)
    {
        int savedStmtLine = currentStmtLine;
        try
        {
            foreach (var st in ast.GlobalStatements)
            {
                // The LINE the declaration is written on. A module-level declaration is never
                // lowered through a statement visitor, so nothing has set `currentStmtLine`
                // when this sweep runs and the refusal fell back to line 1 on a file whose
                // third line held the typo. The C# front end records no column on either node,
                // so the line is what the located overload has to work from.
                currentStmtLine = st.Line;
                switch (st)
                {
                    // `X: Bogus = 1`. The parser splits an annotated binding on one character:
                    // an annotation containing '[' becomes an AnnAssign and anything else a
                    // VarDecl, so both spellings have to be named here or the check depends on
                    // the shape of the type rather than on the name in it.
                    case VarDecl vd:
                        CheckAnnotationNames(vd.VarType ?? "", vd);
                        break;
                    case AnnAssign an:
                        CheckAnnotationNames(an.Annotation, an);
                        break;
                }
            }
        }
        finally
        {
            currentStmtLine = savedStmtLine;
        }
    }

    private void VisitAnnAssign(AnnAssign stmt)
    {
        CheckAnnotationNames(stmt.Annotation, stmt);

        // A `const[...]` annotation marks the name immutable; record it so a later
        // assignment to it is rejected (see VisitAssign's reassignment guard).
        if (!stmt.Target.Contains('.') && IsConstType(stmt.Annotation))
            declaredConstants.Add(stmt.Target);

        // `T: tuple[...] = (a, b, c)` IS `T = (a, b, c)` with the type written down (#357).
        //
        // The two spellings took different paths: the bare one binds the elements against the
        // name, and the annotated one fell through to the expression visitor, which answered
        // "tuples are not supported as runtime values" -- true of a tuple used as a value, and
        // not what this statement does. The annotation adds nothing the binding does not
        // already know, so it is the same statement and takes the same path.
        if (!stmt.Target.Contains('.') && PyMCU.Common.TupleType.IsTupleType(stmt.Annotation)
            && stmt.Value is TupleExpr or ListExpr)
        {
            VisitStatement(new AssignStmt(new VariableExpr(stmt.Target), stmt.Value)
                { Line = stmt.Line, Column = stmt.Column, Length = stmt.Length });
            return;
        }

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
            // Three conditions shared one message, and it described the arm the author had in
            // mind rather than the one that fired (#240). Split, so each says what it found.
            //
            // ALL THREE ARE NOW UNREACHABLE, and the same commit is what made that true, so
            // read this before assuming a program can get here. An AnnAssign exists only when
            // the annotation contains '[' (Parser.cs, and s_annassign), and both front ends now
            // accept only NAME or NAME[...] as an annotation. A NAME has no bracket, so it is
            // not an AnnAssign; a NAME[...] always ends at its ']'. That leaves no input for
            // any of the three.
            //
            // Kept rather than deleted, and split rather than left as one, because the arms are
            // the invariant written down: if the annotation grammar is ever widened, each says
            // which assumption broke instead of all three blaming the first.
            if (mb == -1)
            {
                // UNREACHABLE, and kept as an invariant rather than deleted. Both front ends
                // build an AnnAssign ONLY when the annotation contains '[' (Parser.cs, and
                // s_annassign in the translator, whose comment says the three shapes are kept
                // exactly), so an AnnAssign whose annotation has no bracket does not exist.
                // If this ever fires, that pairing has been broken somewhere upstream.
                throw UserError("Instance-member annotation must be an array type, e.g. uint8[N]");
            }
            if (mc != stmt.Annotation.Length - 1)
            {
                // The arm that USED to be reachable, and the one this issue was written from:
                // `self.x: uint8[2] | None` arrived here through the CPython bridge, which
                // rendered any expression into an annotation string. The old text told a reader
                // who had written `uint8[2] | None` that the annotation "must be an array
                // type", which it is: what is wrong is what follows the ']'. Both front ends
                // now refuse that annotation while reading it, so this no longer fires.
                string tail = mc == -1
                    ? stmt.Annotation.Substring(mb)
                    : stmt.Annotation.Substring(mc + 1).Trim();
                throw UserError(
                    $"Instance-member annotation must end at the ']', and this one continues "
                    + $"with '{tail}'. Write the array type on its own, e.g. uint8[N]");
            }
            if (mc <= mb + 1)
                throw UserError(
                    "Instance-member annotation needs a size between the brackets, e.g. uint8[4]");
            string memHead = stmt.Annotation.Substring(0, mb);
            string memSz = stmt.Annotation.Substring(mb + 1, mc - mb - 1);
            int memCount;
            DataType memElem;
            if (memHead == "list")
            {
                // `self.buf: list[uint8] = [0, 0, 0]` -- a list FIELD. Under the T[N] reading
                // the bracket looks like a SIZE, and the size resolver reported the ELEMENT
                // TYPE as not being a compile-time constant, for a program with no size
                // expression in it. The field cannot hold the growable heap list a local gets,
                // but the literal states how many elements there are, so the field is the fixed
                // array of that element type: exactly what `self.buf: uint8[3] = [...]` gives,
                // down to the same ROM.
                memElem = DataTypeExtensions.StringToDataType(memSz);
                if (stmt.Value is not ListExpr memLit || memLit.Elements.Count == 0)
                    throw UserError(
                        $"'{stmt.Target}: {stmt.Annotation}' has no size. A list field is a "
                        + $"fixed array, so its length comes from the literal: write "
                        + $"`{stmt.Target}: {stmt.Annotation} = [0, 0, 0]`, or give the length "
                        + $"outright with `{stmt.Target}: {memSz}[N] = [...]`.",
                        stmt.Value);
                memCount = memLit.Elements.Count;
            }
            else
            {
                memCount = ResolveArraySizeExpr(memSz);
                memElem = DataTypeExtensions.StringToDataType(memHead);
            }

            // Zero-initialise (a NeoPixel strip starts all-off), or apply a
            // literal list initialiser when one is supplied.
            var memInit = new List<int>(Enumerable.Repeat(0, memCount));
            if (stmt.Value is ListExpr mle)
                for (int k = 0; k < Math.Min(memCount, mle.Elements.Count); k++)
                    if (TryEvalElemConst(mle.Elements[k], out int mv)) memInit[k] = mv;

            EmitMemberArrayInit(new VariableExpr(objName), member, memElem, memCount, memInit, stmt.Target);
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
            // The same as the VarDecl path above: the argument that failed to give a size.
            Expression? annSizeSource = null;
            var initVals = new List<int>();
            // The element EXPRESSIONS of a list form, for the same reason as the VarDecl
            // path above: a run-time element is stored by evaluating it, not as a zero.
            List<Expression>? initElems = null;

            if (stmt.Value != null && stmt.Value is CallExpr call && call.Callee is VariableExpr callee &&
                callee.Name == "bytearray" && call.Args.Count > 0)
            {
                var arg0 = call.Args[0];
                annSizeSource = arg0;
                if (arg0 is ListExpr le)
                {
                    count = le.Elements.Count;
                    initElems = le.Elements;
                    foreach (var e in le.Elements) initVals.Add(TryEvalElemConst(e, out int v) ? v : 0);
                }
                // Integer literal or any compile-time constant (bytearray(WINDOW)).
                else if (TryEvalElemConst(arg0, out int constN))
                {
                    count = constN;
                    initVals.AddRange(Enumerable.Repeat(0, count));
                }
            }
            // `x: bytes = bytes([...])` / `bytes(N)`: the annotation already normalizes to
            // "bytearray" (AnnotationText), so this is the same shape one call spelling later.
            else if (stmt.Value is CallExpr bCall && bCall.Callee is VariableExpr bCallee &&
                bCallee.Name == "bytes" && TryBytesLiteralElements(bCall) is { } bElems)
            {
                annSizeSource = bCall.Args.Count > 0 ? bCall.Args[0] : bCall;
                count = bElems.Count;
                if (bCall.Args.Count > 0 && bCall.Args[0] is ListExpr) initElems = bElems;
                foreach (var e in bElems) initVals.Add(TryEvalElemConst(e, out int v) ? v : 0);
            }

            if (count <= 0)
                throw UserError("bytearray: could not determine buffer size from initializer.",
                                annSizeSource);
            string qualified = string.IsNullOrEmpty(currentFunction)
                ? stmt.Target
                : currentFunction + "." + stmt.Target;
            // Synthesized main: fall back to the module-level name registered by ScanGlobals.
            // Only where the module level is being REPLAYED, which is the entry point and a
            // module's synthesized __module_init. It used to fire in ANY function, so a
            // function's own local array overwrote a module-level array of the same name under
            // the shared bare key. A local declaration is a NEW binding that shadows; it is not
            // the module-level one being lowered again.
            //
            // What it cost, and the name makes it likely rather than exotic: the AVR UART HAL
            // declares `buf: uint8[32]` inside uart_write_fmt, so a user's module-level
            // `buf: uint8[300]` had its size replaced by 32. A store from a third function then
            // carried count 32, took the narrow 8-bit index path, and every write past index 255
            // wrapped into the low bytes. Measured: writing 99 at index 257 and reading it back
            // printed 0, on a clean build with no diagnostic. Renaming the array to a name the
            // stdlib does not use made it correct, which is what pinned it to the collision.
            bool replayingModuleLevel = currentFunction == "main"
                || currentFunction.EndsWith("___module_init", StringComparison.Ordinal);
            if (replayingModuleLevel
                && !arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(stmt.Target))
                qualified = stmt.Target;
            arraySizes[qualified] = count;
            arrayElemTypes[qualified] = DataType.UINT8;
            variableTypes[qualified] = DataType.UINT8;
            arraysWithVariableIndex.Add(qualified);

            for (int k = 0; k < count; ++k)
            {
                Val initVal = initElems != null && !TryEvalElemConst(initElems[k], out _)
                    ? VisitExpression(initElems[k])
                    : new Constant(initVals[k]);
                Emit(new ArrayStore(qualified, new Constant(k), initVal, DataType.UINT8, count));
            }
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
        // `x: float = 0.25` at module level. The chain above has no float arm, so the name kept
        // the uint8 default, the store folded the literal to an integer and the read came back
        // 0.0 (#379). The unannotated spelling is answered by the scanned width below; the
        // annotated one names its width here and was the one nothing read.
        else if (stmt.Annotation is "float" or "const[float]") type = DataType.FLOAT;

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
        // A str global another function rebinds keeps its interned id at run time. As a uint8
        // the id (>= 256) was truncated at the store, so the module init wrote a flat zero and
        // the declared text was not anywhere in the firmware.
        if (stmt.Annotation == "str" && stmt.Value is StringLiteral
            && multiStrCandidates.ContainsKey(qualified2))
            type = DataType.UINT16;

        variableTypes[qualified2] = type;

        if (stmt.Annotation == "str" && stmt.Value is StringLiteral sl2
            && !multiStrVariables.ContainsKey(qualified2))
            strConstantVariables[qualified2] = sl2.Value;

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
            if (stmt.Annotation == "str" && !strConstantVariables.ContainsKey(qualified2)
                && !multiStrVariables.ContainsKey(qualified2))
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

    /// <summary>
    /// `bytes([a, b, ...])` and `bytes(N)` with a compile-time N as a fixed sequence of byte
    /// values -- the same shape `bytearray(...)` already produces, since a `bytes` PARAMETER
    /// is already read as that same buffer (AnnotationText, #365, #431). Returns null for anything
    /// that is not a one-argument `bytes(...)` call, or is `bytes(other_buffer)` copying an
    /// existing buffer (out of scope here; left untouched for whatever the callee does with a
    /// call it does not itself recognise).
    ///
    /// A `bytes(n)` with a run-time `n` is refused HERE rather than returned as null: every
    /// caller of this method has already committed to treating the argument as a fixed-size
    /// buffer, so the one useful answer is which type DOES take a run-time size.
    /// </summary>
    private List<Expression>? TryBytesLiteralElements(Expression arg)
    {
        if (arg is not CallExpr { Callee: VariableExpr { Name: "bytes" } } call || call.Args.Count != 1)
            return null;
        Expression a0 = call.Args[0];
        if (a0 is ListExpr le) return le.Elements;
        if (a0 is VariableExpr copyVe && ResolveBufferKey(copyVe) != null) return null;
        if (TryEvalElemConst(a0, out int n))
        {
            if (n < 0) throw UserError("bytes(n): n must not be negative", a0);
            return Enumerable.Repeat((Expression)new IntegerLiteral(0), n).ToList();
        }
        throw UserError(
            "bytes(n) needs a compile-time size for n: a buffer's size is fixed while "
            + "compiling, and there is no allocator to size one at run time. bytearray(n) "
            + "takes the same run-time n and grows it through the arena allocator.",
            a0);
    }

    /// <summary>
    /// array.array's typecode argument, read as the pymcu.types name of the list[T] element
    /// it decides -- B/b, H/h and I/L/i/l are the widths this compiler already has a storage
    /// class for; f (float), d (double) and q/Q (64-bit) are not, and are refused by name
    /// rather than silently narrowed to something the typecode did not ask for.
    /// </summary>
    private string ArrayTypecodeToTypeName(CallExpr call)
    {
        if (call.Args.Count == 0 || call.Args[0] is not StringLiteral tc)
            throw UserError(
                "array.array(typecode, ...): the typecode must be a string literal ('B', 'H', "
                + "'I', 'b', 'h' or 'i'), decided while compiling -- there is no way to size the "
                + "list's elements from one that is only known at run time.",
                call.Args.Count > 0 ? call.Args[0] : call);
        string typeName = tc.Value switch
        {
            "B" => "uint8",
            "b" => "int8",
            "H" => "uint16",
            "h" => "int16",
            "I" or "L" => "uint32",
            "i" or "l" => "int32",
            "f" or "d" or "q" or "Q" =>
                throw UserError(
                    $"array.array(\"{tc.Value}\", ...): PyMCU has no {(tc.Value is "f" or "d" ? "float" : "64-bit integer")} "
                    + "list element -- list[T] stores uint8/int8/uint16/int16/uint32/int32. "
                    + (tc.Value is "f" or "d"
                        ? "Scale the samples into a fixed-point integer width instead."
                        : "Split the value across two uint32 elements instead."),
                    tc),
            _ => throw UserError(
                $"array.array(\"{tc.Value}\", ...): not a typecode array.array understands "
                + "(B, b, H, h, I, L, i or l)",
                tc),
        };
        return typeName;
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
            // Only where the module level is being REPLAYED, which is the entry point and a
            // module's synthesized __module_init. It used to fire in ANY function, so a
            // function's own local array overwrote a module-level array of the same name under
            // the shared bare key. A local declaration is a NEW binding that shadows; it is not
            // the module-level one being lowered again.
            //
            // What it cost, and the name makes it likely rather than exotic: the AVR UART HAL
            // declares `buf: uint8[32]` inside uart_write_fmt, so a user's module-level
            // `buf: uint8[300]` had its size replaced by 32. A store from a third function then
            // carried count 32, took the narrow 8-bit index path, and every write past index 255
            // wrapped into the low bytes. Measured: writing 99 at index 257 and reading it back
            // printed 0, on a clean build with no diagnostic. Renaming the array to a name the
            // stdlib does not use made it correct, which is what pinned it to the collision.
            bool replayingModuleLevel = currentFunction == "main"
                || currentFunction.EndsWith("___module_init", StringComparison.Ordinal);
            if (replayingModuleLevel
                && !arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(stmt.Target))
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
                        // The step expression the user wrote, which is the `0` the message is
                        // about. Non-null whenever this fires: an absent step defaults to 1.
                        if (step == 0) throw UserError("Slice step cannot be zero", sl.Step);
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

                    // srcVe, the name on the right, is what the message is about: it is the
                    // thing that had to be a named fixed-size array and is not. Safe by the
                    // three-clause rule -- it is a syntactic child of the statement being
                    // lowered (`stmt.Value`), not a name looked up in a resolution table and
                    // not a caller's node read during argument binding.
                    //
                    // Reached by seven programs, the shortest being a scalar local:
                    //     ys: uint8 = 5
                    //     xs: uint8[2] = ys[0:2]
                    // The branch above requires `idxRhs.Target is VariableExpr`, so a slice of
                    // a call result, a bytes literal or a list literal never arrives here at
                    // all. #177 listed those three as the things to try; the guarding condition
                    // rules out all three, and what reaches it is any NAME that is not a known
                    // fixed-size array.
                    throw UserError("Slice initializer target must be a named fixed-size array", srcVe);
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
                int start = 0, stop = 0, step = 1;
                if (call.Args.Count == 1)
                {
                    var sv = EvalConst(call.Args[0]);
                    if (sv == null) throw UserError("List comprehension const err", lc);
                    stop = sv.Value;
                }
                else if (call.Args.Count >= 2)
                {
                    var sv = EvalConst(call.Args[0]);
                    var ev = EvalConst(call.Args[1]);
                    if (sv == null || ev == null) throw UserError("List comprehension const err", lc);
                    start = sv.Value;
                    stop = ev.Value;
                    // The third argument was never read here, so `range(0, 10, 2)` yielded
                    // every value in [0, 10) (PyMCU#287).
                    if (call.Args.Count >= 3)
                    {
                        var stv = EvalConst(call.Args[2]);
                        if (stv == null) throw UserError("List comprehension const err", lc);
                        if (stv.Value == 0) throw UserError("range() step cannot be zero.", call.Args[2]);
                        step = stv.Value;
                    }
                }

                long trips = IRGenerator.RangeTripCount(start, stop, step);
                for (long k = 0; k < trips; k++) vals.Add((int)(start + k * step));
            }
            else if (iterExpr is ListExpr or TupleExpr)
            {
                var elems = iterExpr is ListExpr le ? le.Elements : ((TupleExpr)iterExpr).Elements;
                foreach (var e in elems)
                {
                    var v = EvalConst(e);
                    if (v == null) throw UserError("List comprehension const err", lc);
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
                    if (v == null) throw UserError("List comprehension const err", lc);
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
                        if (fv == null) throw UserError("filter error", lc);
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
                    if (fv == null) throw UserError("filter error", lc);
                    if (fv == 0) continue;
                }

                entries.Add(VisitExpression(lc.Element));
            }
        }

        constantVariables.Remove(outerKey);

        if (entries.Count != count)
            throw UserError($"List comprehension generated {entries.Count} but array is {count}", lc);
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

        // An all-constant literal carries its own element width, and the widest element is what
        // the whole table has to hold. The type was read from element 0 alone, and a constant
        // answers neither Temporary nor Variable, so it fell to the uint8 default and every wide
        // table was stored on its low byte: `DUTIES = [256, 383, 512, ...]` printed 0, 127, 0
        // with no diagnostic, and a PWM duty cycle is a 16-bit number on any part that has one.
        var constElems = new List<int>(count);
        bool allConst = count > 0 && elemExprs.All(e => TryEvalElemConst(e, out _));
        if (allConst)
        {
            foreach (var e in elemExprs) { TryEvalElemConst(e, out int cv); constElems.Add(cv); }
            elemDt = WidestElemType(constElems);
        }

        for (int k = 0; k < count; ++k)
        {
            string elemName = qualified + "__" + k;
            variableTypes[elemName] = elemDt;

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
            if (!allConst && k == 0)
                elemDt = v switch { Temporary t => t.Type, Variable vv => vv.Type, _ => DataType.UINT8 };
            variableTypes[elemName] = elemDt;
            Emit(new Copy(v, new Variable(elemName, elemDt)));
            if (v is Variable srcVar) PropagateCtState(srcVar.Name, elemName);
        }

        arraySizes[qualified] = count;
        arrayElemTypes[qualified] = elemDt;
        variableTypes[qualified] = elemDt;
        // Keep the values: a run-time subscript reaching this array later can turn them into a
        // flash table, which is the storage a lookup table written as a plain list wants.
        // A name rebound to another list denotes the new values from here on, so any layout
        // built from the old ones must not be reused for it.
        materialisedConstTables.Remove(qualified);
        materialisedConstTables.Remove("dictrows:" + qualified);
        materialisedConstTables.Remove("dictkeys:dictrows:" + qualified);
        if (allConst) ctArrayConstElements[qualified] = constElems;
        else ctArrayConstElements.Remove(qualified);
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
                int start = 0, stop, step = 1;
                if (rangeCall.Args.Count == 1) stop = EvaluateConstantExpr(rangeCall.Args[0]);
                else if (rangeCall.Args.Count >= 2)
                {
                    start = EvaluateConstantExpr(rangeCall.Args[0]);
                    stop = EvaluateConstantExpr(rangeCall.Args[1]);
                    // The step was dropped here too (PyMCU#287).
                    if (rangeCall.Args.Count >= 3)
                    {
                        step = EvaluateConstantExpr(rangeCall.Args[2]);
                        if (step == 0) throw UserError("range() step cannot be zero.", rangeCall.Args[2]);
                    }
                }
                else return null;
                items = new List<Expression>();
                long rtrips = IRGenerator.RangeTripCount(start, stop, step);
                for (long k = 0; k < rtrips; ++k) items.Add(new IntegerLiteral((int)(start + k * step)));
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
            throw UserError($"cannot assign to constant '{augConstTgt.Name}' (declared const)", augConstTgt);

        // `Color.RED += 1` mutates an enum member exactly as a plain assignment does.
        if (EnumMemberAssignTarget(stmt.Target) is { } augEnumTgt)
            throw UserError(EnumMemberAssignMessage(augEnumTgt.Cls, augEnumTgt.Member), augEnumTgt.At);

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
                    AugOp.Div => "__itruediv__",
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
                        AugOp.Div => Frontend.BinaryOp.Div,
                        AugOp.FloorDiv => Frontend.BinaryOp.FloorDiv,
                        AugOp.Mod => Frontend.BinaryOp.Mod,
                        AugOp.BitAnd => Frontend.BinaryOp.BitAnd,
                        AugOp.BitOr => Frontend.BinaryOp.BitOr,
                        AugOp.BitXor => Frontend.BinaryOp.BitXor,
                        AugOp.LShift => Frontend.BinaryOp.LShift,
                        AugOp.RShift => Frontend.BinaryOp.RShift,
                        // UNREACHABLE, and left standing on purpose. `AugOp` is a closed enum
                        // of eleven members and the eleven arms above name all of them, so no
                        // value of stmt.Op arrives here. That is a proof rather than an
                        // estimate: compare the arms against the declaration in Ast.cs.
                        //
                        // THIS ARM HAS A READER AND IT IS NOT THE USER. It fires the day someone
                        // adds a twelfth member and forgets the row, and what it owes that
                        // person is the name of the member, which it already gives. It is not a
                        // user-facing diagnostic and is counted as none: giving it a caret would
                        // dress an internal invariant up as something a program can provoke.
                        _ => throw UserError($"augmented operator {stmt.Op} has no dunder mapping"),
                    };
                    VisitAssign(new AssignStmt(zve, new BinaryExpr(zve, bop, stmt.Value)) { Line = stmt.Line });
                    return;
                }
                // Rendered in two shapes on purpose. With a known dunder name the reader gets
                // it qualified (`Acc.__iadd__`); without one, naming the class before the
                // prose produced "Acc.an in-place dunder", which is not a sentence and reads
                // as a placeholder that lost its argument (#168).
                throw UserError(
                    $"'{zve.Name}' is a {zcls} instance: augmented assignment needs "
                    + (idunder.Length > 0
                        ? $"{zcls}.{idunder}"
                        : $"an in-place dunder on {zcls}")
                    + " (or the matching binary dunder) defined", zve);
            }
        }

        Val operand = VisitExpression(stmt.Value);

        if (stmt.Target is VariableExpr ve)
        {
            Val target = ResolveBinding(ve.Name, ve);
            // An augmented assignment is a WRITE, so whatever the name was known to hold stops
            // being true here. Only the Constant case cleared constantVariables, which is all
            // that was needed while locals were not tracked; `total = 0` followed by
            // `total += v` left the locals map saying 0, and a call after the loop was handed
            // that (PyMCU#327).
            ForgetLocalConstant(ve.Name);
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
                if (!arraySizes.ContainsKey(qualified))
                    qualified = ModuleScopeArrayName(qualified);
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

            throw UserError("augmented assignment to .value requires a pointer or register target",
                stmt.Target);
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

    /// <summary>
    /// `self.column, self.row = 0, 0` (#344). An ATTRIBUTE target cannot take the Copy-into-a
    /// -Variable path below -- an instance field is stored through the assignment path, which
    /// knows the layout -- so a tuple unpack with one is rewritten into the statements the
    /// author would otherwise have written: the whole right-hand side into snapshot names
    /// first, because Python evaluates it before any store, then one plain assignment per
    /// target. Both halves go through VisitAssign, so a field keeps exactly the lowering it
    /// has when it is written on its own line.
    ///
    /// Returns false when no target is an attribute, leaving the all-names case untouched.
    /// </summary>
    private bool TryUnpackIntoAttributes(TupleUnpackStmt stmt)
    {
        if (!stmt.Targets.Any(t => t.Contains('.'))) return false;

        if (stmt.StarredIndex >= 0)
            throw UserError("a starred target collects into an array, which an attribute "
                            + "cannot be. Unpack into names and assign the attributes from "
                            + "them", stmt);

        var values = stmt.Value is TupleExpr tup
            ? tup.Elements
            : throw UserError("unpacking into an attribute needs the values written out, "
                              + $"as in `{stmt.Targets[0]}, ... = a, b`", stmt);

        if (values.Count != stmt.Targets.Count)
            throw UserError(
                $"tuple unpacking size mismatch: {stmt.Targets.Count} target"
                + $"{(stmt.Targets.Count == 1 ? "" : "s")} on the left "
                + $"({string.Join(", ", stmt.Targets)}), {values.Count} "
                + $"value{(values.Count == 1 ? "" : "s")} on the right",
                values.Count > stmt.Targets.Count ? values[stmt.Targets.Count] : null);

        // The snapshots, so `self.a, self.b = self.b, self.a` still swaps.
        var snapNames = new List<string>(values.Count);
        for (int k = 0; k < values.Count; ++k)
        {
            string snap = $"__unpack{tempCounter++}";
            snapNames.Add(snap);
            VisitAssign(new AssignStmt(new VariableExpr(snap), values[k]) { Line = stmt.Line });
        }

        for (int k = 0; k < stmt.Targets.Count; ++k)
        {
            Expression target;
            string t = stmt.Targets[k];
            int dot = t.IndexOf('.');
            if (dot < 0) target = new VariableExpr(t);
            else
            {
                Expression obj = new VariableExpr(t[..dot]);
                foreach (var member in t[(dot + 1)..].Split('.'))
                    obj = new MemberAccessExpr(obj, member);
                target = obj;
            }
            VisitAssign(new AssignStmt(target, new VariableExpr(snapNames[k])) { Line = stmt.Line });
        }

        return true;
    }

    private void VisitTupleUnpack(TupleUnpackStmt stmt)
    {
        // `x, y = key`, where `key` is a NAME standing for a compile-time sequence (#352).
        //
        // This is what a two-index subscript's dunder writes: the pair arrives bound to the
        // parameter, and the body unpacks it. It used to reach the refusal at the bottom of
        // this method, which says the right-hand side has to be a tuple literal or an inline
        // call -- true of the shapes that were handled, and no help to someone whose name IS a
        // tuple literal one binding away.
        //
        // Rewritten into the literal form rather than given a fourth branch, so the size
        // mismatch, the starred target and the evaluate-before-assign snapshot are the same
        // code and cannot drift from it.
        if (stmt.Value is not TupleExpr && stmt.Value is not CallExpr
            && ResolveConstSequenceExpr(stmt.Value) is { } seqElements)
        {
            VisitTupleUnpack(new TupleUnpackStmt(
                stmt.Targets, new TupleExpr(seqElements), stmt.StarredIndex)
                { Line = stmt.Line, Column = stmt.Column, Length = stmt.Length });
            return;
        }

        if (TryUnpackIntoAttributes(stmt)) return;

        // Every target is written here, so none of them still holds what it held.
        foreach (var unpacked in stmt.Targets) ForgetLocalConstant(unpacked);

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
                // "Tuple size mismatch" states that the two sides differ without saying what
                // either side is, so the reader has to count the program back. Print both
                // counts and which side is which.
                if (nTup != nTgt)
                    // With more values than targets the first surplus one is the value with
                    // nowhere to go, and it is a node. With fewer there is no surplus element,
                    // and a TupleExpr carries no position of its own, so that direction stays
                    // caretless rather than marking an innocent element.
                    throw UserError(
                        $"tuple unpacking size mismatch: {nTgt} target{(nTgt == 1 ? "" : "s")} on the "
                        + $"left ({string.Join(", ", stmt.Targets)}), {nTup} "
                        + $"value{(nTup == 1 ? "" : "s")} on the right",
                        nTup > nTgt ? tup.Elements[nTgt] : null);
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
                // The right-hand side, which is the side that is short. The targets are the
                // count the reader declared and the RHS is what failed to match it.
                if (nTup < nFixed) throw UserError("Not enough values to unpack", stmt.Value);
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
        else if (stmt.Value is CallExpr or MemberAccessExpr)
        {
            // A call (`a, b = f()`) or a property read (`a, b = self.color_raw` -- a
            // MemberAccessExpr whose getter is expanded inline) both deliver their tuple
            // through lastTupleResults; the expansion binds one result slot per element.
            pendingTupleCount = stmt.Targets.Count;
            if (stmt.StarredIndex >= 0)
                throw UserError("Starred expressions not supported with inline multi-return.",
                    stmt.Value is CallExpr ce ? ce.Callee : stmt.Value);

            // A RHS that never expands a tuple return must not see the list a previous
            // unpack left behind -- its count could coincidentally match the targets.
            lastTupleResults = new List<string>();
            Val ignored = VisitExpression(stmt.Value);
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
        {
            // A starred target is the construct that is missing here, and it is on the LEFT.
            // Blaming the right-hand side sent the reader to change a value that was fine
            // (`first, *rest = data`, with data a declared fixed-size array). Name the star.
            if (stmt.StarredIndex >= 0)
            {
                string plain = stmt.Targets.Count > 1
                    ? stmt.Targets[stmt.StarredIndex == 0 ? 1 : 0]
                    : "first";
                throw UserError(
                    $"starred unpacking target '*{stmt.Targets[stmt.StarredIndex]}' is only supported "
                    + "when the right-hand side is a tuple literal, because '*' collects a "
                    + "variable number of values and there is no run-time list to collect them "
                    + $"into. Take the elements you need by index instead (`{plain} = <sequence>[0]`, "
                    + "and so on).",
                    stmt.Value);
            }

            // The RHS in both of the two above: each message says what the right-hand side has
            // to be, so the caret marks the right-hand side that is not it. The target list is
            // legal in every one of these and marking it would send the reader to rewrite the
            // half that is correct.
            throw UserError(
                "Tuple unpacking RHS must be a tuple literal or an inline function call returning "
                + "a tuple; unpacking an array or another sequence by name is not supported. "
                + "Assign each target from its index instead.",
                stmt.Value);
        }
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