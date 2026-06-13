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
        // RFC 0001 Model B (SRAM slot): `s = MultiFieldZCA(a, b)`. Box the instance into a
        // fixed SRAM slot and store each field at its offset. Handled as a self-contained
        // path (early return) so it never touches the virtual-constructor machinery.
        if (stmt.Target is VariableExpr slotTgt && stmt.Value is CallExpr slotCall
            && slotCall.Callee is VariableExpr slotCallee
            && slotClasses.Contains(ResolveCallee(slotCallee.Name)))
        {
            EmitSlotConstruction(slotTgt, ResolveCallee(slotCallee.Name), slotCall.Args);
            return;
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

        if (stmt.Target is IndexExpr indexExpr)
        {
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
                        Emit(new StoreIndirect(srcVal, elemAddr));
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
                            throw new Exception("Array subscript must be a compile-time constant");
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
                if (!resolved) throw new Exception("Bit index must be constant");
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

        if (stmt.Target is VariableExpr varExprCtor)
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

        if (stmt.Target is MemberAccessExpr memTarget && propertySetters.Count > 0)
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
            {
                var objVal = VisitExpression(memTarget.Object);
                var @base = objVal is Variable v ? v.Name : (objVal is Temporary t ? t.Name : "");
                while (!string.IsNullOrEmpty(@base) && variableAliases.TryGetValue(@base, out var alias))
                    @base = alias;
                if (!string.IsNullOrEmpty(@base) && instanceClasses.TryGetValue(@base, out var cls))
                {
                    var setterKey = cls + "." + memTarget.Member;
                    string? inlineKey;
                    if (propertySetters.TryGetValue(setterKey, out inlineKey))
                    {
                        var argVal = VisitExpression(stmt.Value);
                        if (inlineKey == null) return;
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

                        return;
                    }
                }
            }
        }

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

        if (stmt.Target is VariableExpr varExpr)
        {
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
                                else if (stmt.Value is IntegerLiteral) type = DataType.INT32;
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

            if (value is Variable vv2 && target is Variable tv2) variableAliases[tv2.Name] = vv2.Name;
            else if (value is Temporary tSrc && target is Variable tDst) variableAliases[tDst.Name] = tSrc.Name;

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
        else if (stmt.Target is MemberAccessExpr memExpr2)
        {
            if (memExpr2.Member == "value")
            {
                var target = VisitExpression(memExpr2.Object);
                var varType = DataType.UINT8;
                var originalName = memExpr2.Object is VariableExpr veObj ? veObj.Name : null;

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
                        throw new Exception("Cannot assign to .value of this expression type");
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
                        throw new Exception("16-bit .value assignment requires constant address");
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
                        throw new Exception("32-bit .value assignment requires constant address");
                    default:
                        throw new Exception("Unsupported type size for .value assignment");
                }
            }
            else
            {
                var objVal = VisitExpression(memExpr2.Object);
                var baseName = objVal is Variable v3 ? v3.Name : (objVal is Temporary t3 ? t3.Name : "");
                if (string.IsNullOrEmpty(baseName))
                    throw new Exception("Unknown member access in assignment: " + memExpr2.Member);
                while (baseName != null && variableAliases.TryGetValue(baseName, out var alias)) baseName = alias;
                var flattenedName = baseName + "_" + memExpr2.Member;

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

                Emit(new Copy(value, new Variable(flattenedName, DataType.UINT8)));
                return;

                bool TryTempName(string tname)
                {
                    if (constantAddressVariables.TryGetValue(tname, out int cv))
                    {
                        constantAddressVariables[flattenedName] = cv;
                        return true;
                    }

                    if (!constantVariables.TryGetValue(tname, out int cv2)) return false;
                    constantVariables[flattenedName] = cv2;
                    return true;
                }
            }
        }
        else if (stmt.Target is UnaryExpr unExpr && unExpr.Op == Frontend.UnaryOp.Deref)
        {
            Val ptr = VisitExpression(unExpr.Operand);
            Emit(new StoreIndirect(value, ptr));
        }
        else throw new Exception("Invalid assignment target");
    }

    // Folds a literal-only integer expression (decimal/hex/bool, optionally negated)
    // to its value. Returns null for anything that is not a direct literal — we only
    // range-check literals the user typed, never folded mask/shift expressions, to
    // avoid false positives on idioms like `~0` or `0xFFFF & 0xFF`.
    private static long? TryLiteralInt(Expression e) => e switch
    {
        IntegerLiteral il                                  => il.Value,
        BooleanLiteral b                                   => b.Value ? 1 : 0,
        UnaryExpr { Op: Frontend.UnaryOp.Negate } u when TryLiteralInt(u.Operand) is { } v => -v,
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

        if (v < r.Min || v > r.Max)
            throw new ValueError(
                $"integer literal {v} is out of range for {r.Name} (valid range {r.Min}..{r.Max})",
                line, 1);
    }

    private void VisitVarDecl(VarDecl stmt)
    {
        CheckIntLiteralRange(stmt.Init, DataTypeExtensions.StringToDataType(stmt.VarType), stmt.Line);

        if (stmt.VarType == "bytearray")
        {
            int count = 0;
            var initVals = new List<int>();
            bool isInput = false;
            string inputPrompt = "";
            int inputMaxLen = 64;

            if (stmt.Init != null)
            {
                if (stmt.Init is CallExpr call && call.Callee is VariableExpr callee)
                {
                    if (callee.Name == "bytearray" && call.Args.Count > 0)
                    {
                        Expression arg0 = call.Args[0];
                        if (arg0 is IntegerLiteral il)
                        {
                            count = il.Value;
                            initVals.AddRange(Enumerable.Repeat(0, count));
                        }
                        else if (arg0 is ListExpr le)
                        {
                            count = le.Elements.Count;
                            foreach (var e in le.Elements)
                            {
                                if (e is IntegerLiteral il2) initVals.Add(il2.Value);
                                else initVals.Add(0);
                            }
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
                                throw new Exception("input(): arguments must be compile-time string literal (prompt) and/or integer (maxlen)");
                        }
                        count = inputMaxLen;
                        initVals.AddRange(Enumerable.Repeat(0, count));
                    }
                }
            }

            if (count <= 0) throw new Exception("bytearray: could not determine buffer size from initializer.");

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

        DataType dt = DataTypeExtensions.StringToDataType(stmt.VarType);
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
                    return;
                }
            }
            Val val = VisitExpression(stmt.Init);
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
        throw new Exception("Array size '" + atom + "' is not a compile-time constant");
    }

    // RFC 0001 Model B (SRAM slot): box a multi-field ZCA. Allocate a fixed SRAM byte slot
    // for the instance and store each field at its byte offset, mapping the field's source
    // __init__ parameter to the corresponding constructor argument. Tracks the instance so
    // its @outline (self-ptr) methods receive the slot base address as `self`.
    private void EmitSlotConstruction(VariableExpr targetVar, string cls, List<Expression> args)
    {
        string qn = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + targetVar.Name
            : (!string.IsNullOrEmpty(currentFunction)
                ? currentFunction + "." + targetVar.Name
                : targetVar.Name);
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
            Emit(new ArrayStore(slot, new Constant(off), v, DataType.UINT8, total));
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
        string qn = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + targetVar.Name
            : (!string.IsNullOrEmpty(currentFunction)
                ? currentFunction + "." + targetVar.Name
                : targetVar.Name);
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

            Emit(new ArrayStore(arrQ, byteOff, v, DataType.UINT8, total));
            off += DataTypeExtensions.StringToDataType(type).SizeOf();
        }
    }

    private void VisitAnnAssign(AnnAssign stmt)
    {
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
                throw new Exception("Instance-member annotation must be an array type, e.g. uint8[N]");
            string memSz = stmt.Annotation.Substring(mb + 1, mc - mb - 1);
            int memCount = ResolveArraySizeExpr(memSz);
            DataType memElem = DataTypeExtensions.StringToDataType(stmt.Annotation.Substring(0, mb));

            var objVal = VisitExpression(new VariableExpr(objName));
            string? baseName = objVal is Variable v ? v.Name : (objVal is Temporary t ? t.Name : "");
            while (baseName != null && variableAliases.TryGetValue(baseName, out var alias)) baseName = alias;
            if (string.IsNullOrEmpty(baseName))
                throw new Exception("Cannot resolve instance for member array '" + stmt.Target + "'");
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
                    if (mle.Elements[k] is IntegerLiteral mil) memInit[k] = mil.Value;
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
                                if (le.Elements[k] is IntegerLiteral il) bytes[k] = il.Value;
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
                if (arg0 is IntegerLiteral il)
                {
                    count = il.Value;
                    initVals.AddRange(Enumerable.Repeat(0, count));
                }
                else if (arg0 is ListExpr le)
                {
                    count = le.Elements.Count;
                    foreach (var e in le.Elements) initVals.Add(e is IntegerLiteral il2 ? il2.Value : 0);
                }
            }

            if (count <= 0) throw new Exception("bytearray: could not determine buffer size from initializer.");
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
        if (stmt.Annotation.StartsWith("list[") && stmt.Annotation.EndsWith("]"))
        {
            string elemTypeName = stmt.Annotation.Substring(5, stmt.Annotation.Length - 6);
            DataType elemDt = DataTypeExtensions.StringToDataType(elemTypeName);
            int elemSize = elemDt.SizeOf();

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
                        EmitListStore(tmpPtr, 2 + k * elemSize, initElements[k]);
                }

                Emit(new Copy(tmpPtr, new Variable(qualified, DataType.GC_REF)));
            }

            return;
        }

        int bracket = stmt.Annotation.IndexOf('[');
        int close = stmt.Annotation.LastIndexOf(']');
        if (bracket != -1 && close != -1 && close == stmt.Annotation.Length - 1 && close > bracket + 1)
        {
            string inner = stmt.Annotation.Substring(bracket + 1, close - bracket - 1);

            // RFC 0001 Model B (Class[N]): array of boxed ZCA instances. Lay out N contiguous
            // slots (count * stride bytes) as a flat SRAM byte array; record the element class
            // and stride so arr[i] = C(..) constructs into element i and arr[i].method() passes
            // the element address as self.
            string elemAnno = stmt.Annotation.Substring(0, bracket);
            if (!string.IsNullOrEmpty(inner) && inner.All(char.IsDigit)
                && slotClasses.Contains(elemAnno))
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
                return;
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
                    return;
                }

                var initVals = new List<int>(Enumerable.Repeat(0, count));
                if (stmt.Value != null)
                {
                    if (stmt.Value is ListCompExpr lc)
                    {
                        VisitListComp(lc, qualified, count, elemDt);
                        return;
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
                            if (step == 0) throw new Exception("Slice step cannot be zero");
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

                            return;
                        }

                        throw new Exception("Slice initializer target must be a named fixed-size array");
                    }

                    if (stmt.Value is ListExpr le)
                    {
                        for (int k = 0; k < Math.Min(count, le.Elements.Count); ++k)
                        {
                            if (le.Elements[k] is IntegerLiteral il) initVals[k] = il.Value;
                        }
                    }

                    if (stmt.Value is BinaryExpr be && be.Op == Frontend.BinaryOp.Mul && be.Left is ListExpr leRep &&
                        be.Right is IntegerLiteral repeatLit && repeatLit.Value > 0)
                    {
                        for (int k = 0; k < count; ++k)
                        {
                            int srcIdx = k % leRep.Elements.Count;
                            if (srcIdx < leRep.Elements.Count && leRep.Elements[srcIdx] is IntegerLiteral il)
                                initVals[k] = il.Value;
                        }
                    }
                }

                if (arraysWithVariableIndex.Contains(qualified) || moduleSramArrays.Contains(qualified))
                {
                    for (int k = 0; k < count; ++k)
                        Emit(new ArrayStore(qualified, new Constant(k), new Constant(initVals[k]), elemDt, count));
                }
                else
                {
                    for (int k = 0; k < count; ++k)
                    {
                        string elemName = qualified + "__" + k;
                        var elemVar = new Variable(elemName, elemDt);
                        variableTypes[elemName] = elemDt;
                        Emit(new Copy(new Constant(initVals[k]), elemVar));
                    }
                }

                return;
            }
        }

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
                    if (sv == null) throw new Exception("List comprehension const err");
                    stop = sv.Value;
                }
                else if (call.Args.Count >= 2)
                {
                    var sv = EvalConst(call.Args[0]);
                    var ev = EvalConst(call.Args[1]);
                    if (sv == null || ev == null) throw new Exception("List comprehension const err");
                    start = sv.Value;
                    stop = ev.Value;
                }

                for (int i = start; i < stop; i++) vals.Add(i);
            }
            else if (iterExpr is ListExpr le)
            {
                foreach (var e in le.Elements)
                {
                    var v = EvalConst(e);
                    if (v == null) throw new Exception("List comprehension const err");
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
                        if (fv == null) throw new Exception("filter error");
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
                    if (fv == null) throw new Exception("filter error");
                    if (fv == 0) continue;
                }

                entries.Add(VisitExpression(lc.Element));
            }
        }

        constantVariables.Remove(outerKey);

        if (entries.Count != count)
            throw new Exception($"List comprehension generated {entries.Count} but array is {count}");
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
        if (!string.IsNullOrEmpty(lc.Var2Name) || lc.Iterable2 != null || lc.Filter != null)
            return null;

        List<Expression> items;
        switch (lc.Iterable)
        {
            case TupleExpr te: items = te.Elements; break;
            case ListExpr le:  items = le.Elements; break;
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
                        if (!(ie.Index is IntegerLiteral il)) throw new Exception("Array subscript must be const");
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
                if (!resolved) throw new Exception("Bit index must be constant for augmented assignment");
            }

            Emit(new BitWrite(tgtVal, bit, result));
        }
    }

    // Stores `value` at `basePtr + offset`. For offset 0, stores directly via basePtr.
    // For offset > 0, emits a Binary ADD to compute the address then StoreIndirect.
    internal void EmitListStore(Val basePtr, int offset, Val value)
    {
        if (offset == 0)
        {
            Emit(new StoreIndirect(value, basePtr));
            return;
        }

        Val ptrUint16 = basePtr is Temporary t ? t with { Type = DataType.UINT16 }
                       : basePtr is Variable v ? v with { Type = DataType.UINT16 }
                       : basePtr;
        Temporary addrTmp = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, ptrUint16, new Constant(offset), addrTmp));
        Emit(new StoreIndirect(value, addrTmp));
    }

    // Loads a UINT8 value from `basePtr + offset` into a new Temporary.
    internal Temporary EmitListLoad(Val basePtr, int offset, DataType elemType = DataType.UINT8)
    {
        Temporary dst = MakeTemp(elemType);
        if (offset == 0)
        {
            Emit(new LoadIndirect(basePtr, dst));
            return dst;
        }

        Val ptrUint16 = basePtr is Temporary t ? t with { Type = DataType.UINT16 }
                       : basePtr is Variable v ? v with { Type = DataType.UINT16 }
                       : basePtr;
        Temporary addrTmp = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, ptrUint16, new Constant(offset), addrTmp));
        Emit(new LoadIndirect(addrTmp, dst));
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

        if (stmt.Value is TupleExpr tup)
        {
            int nTup = tup.Elements.Count;
            int nTgt = stmt.Targets.Count;

            if (stmt.StarredIndex < 0)
            {
                if (nTup != nTgt) throw new Exception($"Tuple size mismatch");
                for (int k = 0; k < nTgt; ++k)
                {
                    Val v = VisitExpression(tup.Elements[k]);
                    string qualified = QualifyTarget(stmt.Targets[k]);
                    DataType dt = variableTypes.TryGetValue(qualified, out var t) ? t : DataType.UINT8;
                    Emit(new Copy(v, new Variable(qualified, dt)));
                    if (v is Constant c) constantVariables[qualified] = c.Value;
                }
            }
            else
            {
                int nFixed = nTgt - 1;
                if (nTup < nFixed) throw new Exception("Not enough values to unpack");
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
                throw new Exception("Starred expressions not supported with inline multi-return.");

            Val ignored = VisitExpression(call);
            pendingTupleCount = 0;

            if (lastTupleResults.Count != stmt.Targets.Count)
                throw new Exception($"Expected {stmt.Targets.Count} tuple results, got {lastTupleResults.Count}");

            for (int k = 0; k < stmt.Targets.Count; ++k)
            {
                string srcName = lastTupleResults[k];
                string dstName = QualifyTarget(stmt.Targets[k]);
                DataType dt = variableTypes.TryGetValue(dstName, out var t) ? t : DataType.UINT8;
                Emit(new Copy(new Variable(srcName, dt), new Variable(dstName, dt)));
                if (constantVariables.TryGetValue(srcName, out int cVal)) constantVariables[dstName] = cVal;
            }
        }
        else
            throw new Exception(
                "Tuple unpacking RHS must be a tuple literal or an inline function call returning a tuple.");
    }

    private void VisitClassDef(ClassDef classNode)
    {
    } // Only scanned
}