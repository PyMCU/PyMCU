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
    private Val VisitCall(CallExpr expr)
    {
        if (expr.Callee is MemberAccessExpr mem)
        {
            if (mem.Object is CallExpr superCall)
            {
                if (superCall.Callee is VariableExpr superVar)
                {
                    if (superVar.Name == "super")
                    {
                        string childClass = string.IsNullOrEmpty(currentModulePrefix)
                            ? ""
                            : currentModulePrefix.Substring(0, currentModulePrefix.Length - 1);
                        if (classBasePrefixes.TryGetValue(childClass, out var basePrefix))
                        {
                            var baseMethod = basePrefix + mem.Member;
                            var calleeSuper = baseMethod;

                            if (inlineFunctions.TryGetValue(calleeSuper, out var funcSuper))
                            {
                                var exitLabel = MakeLabel();
                                var newDepth = inlineDepth + 1;
                                var newPrefix = $"inline{newDepth}_{funcSuper.Name}_";

                                var selfAlias = currentInlinePrefix + "self";
                                if (variableAliases.TryGetValue(selfAlias, out var vAlias))
                                    variableAliases[newPrefix + "self"] = vAlias;
                                else if (!string.IsNullOrEmpty(pendingConstructorTarget))
                                    variableAliases[newPrefix + "self"] = pendingConstructorTarget;

                                var paramIdx = 0;
                                foreach (var p in funcSuper.Params)
                                {
                                    if (p.Name == "self") continue;
                                    if (paramIdx >= expr.Args.Count) continue;
                                    var argVal = VisitExpression(expr.Args[paramIdx]);
                                    var paramKey = newPrefix + p.Name;
                                    if (argVal is Variable vArg)
                                    {
                                        variableAliases[paramKey] = vArg.Name;
                                    }
                                    else
                                    {
                                        var paramVar = new Variable(paramKey, DataType.UINT8);
                                        Emit(new Copy(argVal, paramVar));
                                    }

                                    paramIdx++;
                                }

                                var savedPrefix = currentInlinePrefix;
                                var savedMod = currentModulePrefix;
                                var savedDepth = inlineDepth;

                                currentInlinePrefix = newPrefix;
                                currentModulePrefix = basePrefix;
                                inlineDepth = newDepth;
                                inlineStack.Add(new InlineContext { ExitLabel = exitLabel });

                                VisitBlock(funcSuper.Body);
                                Emit(new Label(exitLabel));
                                inlineStack.RemoveAt(inlineStack.Count - 1);

                                currentInlinePrefix = savedPrefix;
                                currentModulePrefix = savedMod;
                                inlineDepth = savedDepth;
                                return new NoneVal();
                            }
                        }
                    }
                }
            }
        }

        string callee = "";
        if (expr.Callee is VariableExpr varE)
        {
            callee = ResolveCallee(varE.Name);
        }
        else if (expr.Callee is MemberAccessExpr memC)
        {
            bool resolvedAsModule = false;
            if (memC.Object is VariableExpr ve)
            {
                if (modules.ContainsKey(ve.Name))
                {
                    string mangledMod = ve.Name.Replace('.', '_');
                    callee = mangledMod + "_" + memC.Member;
                    resolvedAsModule = true;
                }
                else if (classNames.Contains(ve.Name))
                {
                    callee = currentModulePrefix + ve.Name + "_" + memC.Member;
                    resolvedAsModule = true;
                }
                else if (ve.Name == "int")
                {
                    callee = "int_" + memC.Member;
                    resolvedAsModule = true;
                }
            }

            if (!resolvedAsModule)
            {
                Val objVal = VisitExpression(memC.Object);
                if (objVal is Variable vObj)
                {
                    // list[T] method dispatch
                    if (listVarElemTypes.ContainsKey(vObj.Name))
                    {
                        switch (memC.Member)
                        {
                            case "append" when expr.Args.Count == 1:
                                return EmitListAppend(vObj, expr.Args[0]);
                            default:
                                throw new Exception($"list.{memC.Member}(): method not supported");
                        }
                    }

                    if (instanceClasses.TryGetValue(vObj.Name, out string clsC))
                    {
                        // Walk MRO: find the class that actually defines the method so that
                        // inherited non-inline methods (e.g. DHTBase._read_byte called on a
                        // DHT11 instance) resolve to the correct label instead of the
                        // non-existent <ConcreteClass>_<method> symbol.
                        string definingClass = ResolveMROMethod(clsC!, memC.Member);
                        callee = definingClass + "_" + memC.Member;

                        // ZCA force-inline: if the resolved callee is a non-inline instance
                        // method, add its AST to inlineFunctions on-demand so the standard
                        // inline expansion runs.  ZCA field aliasing requires inline expansion;
                        // without it, `self._field` accesses inside the method would reference
                        // the wrong stack frame.
                        if (!inlineFunctions.ContainsKey(callee)
                            && instanceMethodDefs.TryGetValue(callee, out var implDef))
                        {
                            inlineFunctions[callee] = implDef;
                        }

                        // Phase 3 gate: emit VirtualCall only when static dispatch cannot be
                        // proven safe.  In the current ZCA model instanceClasses always holds
                        // the exact concrete type (Rule 2), so IsVirtualDispatch always returns
                        // false and we always take the direct-call path above.
                        // (IsVirtualDispatch kept here for future polymorphic-variable support.)
                    }
                    else
                    {
                        callee = vObj.Name + "_" + memC.Member;
                    }
                }
                else if (objVal is MemoryAddress addr)
                {
                    callee = $"MemoryAddress_{addr.Address}_{memC.Member}";
                }
                else
                {
                    throw new Exception("Complex member access in call not yet supported");
                }
            }
        }
        else if (expr.Callee is IndexExpr idxCallee0 && idxCallee0.Target is VariableExpr idxArrVe0)
        {
            // Callable[N] array call: _tasks[i]() — load function address from SRAM, then ICALL.
            string arrKey0 = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + idxArrVe0.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + idxArrVe0.Name : idxArrVe0.Name);
            if (!arraySizes.ContainsKey(arrKey0) && arraySizes.ContainsKey(idxArrVe0.Name))
                arrKey0 = idxArrVe0.Name;
            if (arraySizes.TryGetValue(arrKey0, out int arrSz0)
                && arrayElemTypes.TryGetValue(arrKey0, out DataType arrElemDt0)
                && arrElemDt0 == DataType.FUNCREF)
            {
                Val idxVal0 = VisitExpression(idxCallee0.Index);
                Temporary tmpFn0 = MakeTemp(DataType.FUNCREF);
                Emit(new ArrayLoad(arrKey0, idxVal0, tmpFn0, DataType.FUNCREF, arrSz0));
                var indArgs0 = new List<Val>();
                foreach (var a in expr.Args)
                    indArgs0.Add(VisitExpression(a));
                Val indDst0 = new NoneVal();
                Emit(new IndirectCall(tmpFn0, indArgs0, indDst0));
                return indDst0;
            }
            throw new Exception($"Callable array '{idxArrVe0.Name}' not found or element type is not Callable");
        }
        else
        {
            throw new Exception("Indirect calls not yet supported");
        }

        {
            string qcallee = "";
            if (expr.Callee is VariableExpr ve2)
            {
                if (!string.IsNullOrEmpty(currentInlinePrefix)) qcallee = currentInlinePrefix + ve2.Name;
                else if (!string.IsNullOrEmpty(currentFunction)) qcallee = currentFunction + "." + ve2.Name;
                else qcallee = ve2.Name;
            }

            string lambdaKey = "";
            if (!string.IsNullOrEmpty(qcallee) && lambdaVariableNames.TryGetValue(qcallee, out string lk1))
                lambdaKey = lk1;
            else if (lambdaVariableNames.TryGetValue(callee, out string lk2)) lambdaKey = lk2;

            if (!string.IsNullOrEmpty(lambdaKey) && lambdaFunctionsMap.TryGetValue(lambdaKey, out var lam))
            {
                string pfx = "__lam" + lambdaCounter++ + "_";
                for (int i = 0; i < lam.Params.Count && i < expr.Args.Count; ++i)
                {
                    string paramKey = pfx + lam.Params[i].Name;
                    Val argVal = VisitExpression(expr.Args[i]);
                    DataType dt = DataTypeExtensions.StringToDataType(lam.Params[i].Type);
                    if (argVal is Constant c) constantVariables[paramKey] = c.Value;
                    else
                    {
                        Emit(new Copy(argVal, new Variable(paramKey, dt)));
                        variableTypes[paramKey] = dt;
                    }
                }

                string savedInline = currentInlinePrefix;
                currentInlinePrefix = pfx;
                Val resultL = VisitExpression(lam.Body);
                currentInlinePrefix = savedInline;

                foreach (var p in lam.Params)
                {
                    string pk = pfx + p.Name;
                    constantVariables.Remove(pk);
                    variableTypes.Remove(pk);
                }

                return resultL;
            }
        }

        // Indirect call via FUNCREF-typed variable (function pointer via funcref() intrinsic).
        // After the lambda check so lambdas take priority; before all intrinsics/inline expansion.
        if (expr.Callee is VariableExpr fvExpr)
        {
            // Build qualified key matching Assign.cs (currentFunction + "." + name when not inline)
            string fvKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + fvExpr.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + fvExpr.Name : fvExpr.Name);
            for (int d = 0; d < 20; ++d)
                if (variableAliases.TryGetValue(fvKey, out string nx)) fvKey = nx;
                else break;
            if (variableTypes.TryGetValue(fvKey, out DataType fvType) && fvType == DataType.FUNCREF)
            {
                var indArgs = new List<Val>();
                foreach (var a in expr.Args)
                    indArgs.Add(VisitExpression(a));
                Temporary indDst = MakeTemp();
                Emit(new IndirectCall(new Variable(fvKey, DataType.FUNCREF), indArgs, indDst));
                return indDst;
            }
        }

        // Indirect call via Callable[N] array: _tasks[i]()
        // Note: this path is unreachable now since the IndexExpr case above handles
        // it and returns early. Kept here as dead code guard in case of future refactoring.

        if (inlineFunctions.ContainsKey(callee + "___init__") || overloadedFunctions.Contains(callee + "___init__"))
        {
            callee += "___init__";
        }

        if (overloadedFunctions.Contains(callee))
        {
            string ShortClassName(string fullKey)
            {
                foreach (var cn in classNames)
                {
                    if (fullKey == cn) return cn;
                    if (fullKey.Length > cn.Length && fullKey[fullKey.Length - cn.Length - 1] == '_' &&
                        fullKey.EndsWith((string)cn)) return cn;
                }

                return fullKey;
            }

            string ArgTypeSuffix(Expression arg)
            {
                if (arg is StringLiteral) return "str";
                if (arg is VariableExpr v)
                {
                    string key = currentInlinePrefix + v.Name;
                    for (int depth = 0; depth < 20; depth++)
                    {
                        if (instanceClasses.TryGetValue(key, out string ic)) return ShortClassName(ic);
                        if (strConstantVariables.ContainsKey(key)) return "str";
                        if (variableAliases.TryGetValue(key, out string ak)) key = ak;
                        else break;
                    }

                    // SRAM arrays (variable-indexed) are passed as buffer pointers — use
                    // "bytearray" so overloads that accept bytearray parameters are selected.
                    // Try all three qualified forms since the set may use different prefixes.
                    string qKey = !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + v.Name : v.Name;
                    if (arraysWithVariableIndex.Contains(key) || arraysWithVariableIndex.Contains(qKey) ||
                        arraysWithVariableIndex.Contains(v.Name) ||
                        moduleSramArrays.Contains(key) || moduleSramArrays.Contains(qKey) ||
                        bytearrayParams.Contains(key) || bytearrayParams.Contains(qKey))
                        return "bytearray";
                }

                return IRGenerator.DataTypeToSuffixStr(InferExprType(arg));
            }

            string suffix = "";
            bool first = true;
            foreach (var arg in expr.Args)
            {
                if (arg is KeywordArgExpr) continue;
                if (!first) suffix += "_";
                first = false;
                suffix += ArgTypeSuffix(arg);
            }

            if (string.IsNullOrEmpty(suffix)) suffix = "void";

            var mangled = callee + "___" + suffix;
            if (inlineFunctions.ContainsKey(mangled)) callee = mangled;
            else
            {
                var argCount = expr.Args.Count(a => a is not KeywordArgExpr);
                foreach (var kvp in from kvp in inlineFunctions where kvp.Key.StartsWith(callee + "___") let candParams = kvp.Value.Params.Count<Param>(p => p.Name != "self") where candParams == argCount select kvp)
                {
                    callee = kvp.Key;
                    break;
                }
            }
        }

        {
            bool isSleepMs = callee == "sleep_ms" || callee == "time_sleep_ms" || callee == "pymcu_time_sleep_ms" || callee == "delay_ms" || callee == "time_delay_ms" || callee == "pymcu_time_delay_ms";
            bool isSleepUs = callee == "sleep_us" || callee == "time_sleep_us" || callee == "pymcu_time_sleep_us" || callee == "delay_us" || callee == "time_delay_us" || callee == "pymcu_time_delay_us";
            if (isSleepMs || isSleepUs)
            {
                string targetSuffix = isSleepMs ? "delay_ms" : "delay_us";
                string candidate = "pymcu_time_" + targetSuffix;
                if (!inlineFunctions.ContainsKey(candidate))
                {
                    candidate = "";
                    foreach (var fnName in inlineFunctions.Keys)
                    {
                        if (fnName.EndsWith(targetSuffix))
                        {
                            candidate = fnName;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(candidate)) callee = candidate;
            }
        }

        if (callee == "len")
        {
            if (expr.Args.Count != 1) throw new Exception("len() expects exactly one argument");
            if (expr.Args[0] is ListExpr le2) return new Constant(le2.Elements.Count);
            if (expr.Args[0] is VariableExpr vLen)
            {
                if (!string.IsNullOrEmpty(currentInlinePrefix) &&
                    arraySizes.TryGetValue(currentInlinePrefix + vLen.Name, out int s1)) return new Constant(s1);
                if (!string.IsNullOrEmpty(currentFunction) &&
                    arraySizes.TryGetValue(currentFunction + "." + vLen.Name, out int s2)) return new Constant(s2);
                if (arraySizes.TryGetValue(vLen.Name, out int s3)) return new Constant(s3);

                // Follow variableAliases to resolve through @inline parameter bindings
                // (e.g. len(buf) inside write(buf: bytearray) where buf aliases main.out_buf).
                string lenKey = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + vLen.Name
                    : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + vLen.Name : vLen.Name);
                string lenResolved = lenKey;
                for (int depth = 0; depth < 20; depth++)
                {
                    if (!variableAliases.TryGetValue(lenResolved, out string lenNext)) break;
                    lenResolved = lenNext;
                    if (arraySizes.TryGetValue(lenResolved, out int sAlias)) return new Constant(sAlias);
                }
            }

            Val argVal = VisitExpression(expr.Args[0]);
            string cls = GetValClass(argVal);
            if (!string.IsNullOrEmpty(cls))
            {
                string funcKey = cls + "_" + "__len__";
                if (inlineFunctions.ContainsKey(funcKey))
                {
                    string selfName = argVal is Variable v ? v.Name : (argVal is Temporary t ? t.Name : "");
                    return EmitDunderCall(selfName, cls, funcKey, new List<Val>());
                }
            }

            // Handle list[T] variable: len(x) → load length from offset 0 of heap header
            if (expr.Args[0] is VariableExpr vListLen)
            {
                string listQual = ResolveListVarQualified(vListLen.Name);
                if (!string.IsNullOrEmpty(listQual))
                {
                    Val listPtr = new Variable(listQual, DataType.GC_REF);
                    return EmitListLoad(listPtr, 0, DataType.UINT8);
                }
            }

            throw new Exception("len() argument must be a fixed-size array or list literal");
        }

        if (callee == "int_from_bytes")
        {
            if (expr.Args.Count != 2)
                throw new Exception("int.from_bytes() expects exactly two arguments (bytes, endian)");
            bool littleEndian = true;
            if (expr.Args[1] is StringLiteral estr)
            {
                if (estr.Value == "big") littleEndian = false;
                else if (estr.Value != "little")
                    throw new Exception("int.from_bytes() endian must be 'little' or 'big'");
            }
            else throw new Exception("int.from_bytes() endian argument must be a string literal");

            if (expr.Args[0] is ListExpr le)
            {
                if (le.Elements.Count < 2) throw new Exception("int.from_bytes() requires at least 2 bytes");
                Val b0 = VisitExpression(le.Elements[0]);
                Val b1 = VisitExpression(le.Elements[1]);

                if (b0 is Constant c0 && b1 is Constant c1)
                {
                    int val = littleEndian
                        ? ((c1.Value & 0xFF) << 8) | (c0.Value & 0xFF)
                        : ((c0.Value & 0xFF) << 8) | (c1.Value & 0xFF);
                    return new Constant(val);
                }

                Val loVal = littleEndian ? b0 : b1;
                Val hiVal = littleEndian ? b1 : b0;
                Temporary hiShifted = MakeTemp(DataType.UINT16);
                Temporary resT = MakeTemp(DataType.UINT16);
                Emit(new Binary(BinaryOp.LShift, hiVal, new Constant(8), hiShifted));
                Emit(new Binary(BinaryOp.BitOr, hiShifted, loVal, resT));
                return resT;
            }

            throw new Exception("int.from_bytes() first argument must be a bytes literal b\"...\" or list [lo, hi]");
        }

        if (callee == "abs")
        {
            if (expr.Args.Count != 1) throw new Exception("abs() expects exactly one argument");
            var v = VisitExpression(expr.Args[0]);
            if (v is Constant c) return new Constant(c.Value < 0 ? -c.Value : c.Value);
            var negLabel = MakeLabel();
            var endLabel = MakeLabel();
            var result = MakeTemp();
            var negv = MakeTemp();
            Emit(new Binary(BinaryOp.LessThan, v, new Constant(0), negv));
            Emit(new JumpIfNotZero(negv, negLabel));
            Emit(new Copy(v, result));
            Emit(new Jump(endLabel));
            Emit(new Label(negLabel));
            Temporary negResult = MakeTemp();
            Emit(new Binary(BinaryOp.Sub, new Constant(0), v, negResult));
            Emit(new Copy(negResult, result));
            Emit(new Label(endLabel));
            return result;
        }

        if (callee == "min")
        {
            if (expr.Args.Count != 2) throw new Exception("min() expects exactly two arguments");
            Val a = VisitExpression(expr.Args[0]);
            Val b = VisitExpression(expr.Args[1]);
            if (a is Constant ca && b is Constant cb) return new Constant(ca.Value < cb.Value ? ca.Value : cb.Value);
            string elseLabel = MakeLabel();
            string endLabel = MakeLabel();
            Temporary result = MakeTemp();
            Temporary cmp = MakeTemp();
            Emit(new Binary(BinaryOp.LessThan, a, b, cmp));
            Emit(new JumpIfZero(cmp, elseLabel));
            Emit(new Copy(a, result));
            Emit(new Jump(endLabel));
            Emit(new Label(elseLabel));
            Emit(new Copy(b, result));
            Emit(new Label(endLabel));
            return result;
        }

        if (callee == "max")
        {
            if (expr.Args.Count != 2) throw new Exception("max() expects exactly two arguments");
            var a = VisitExpression(expr.Args[0]);
            var b = VisitExpression(expr.Args[1]);
            if (a is Constant ca && b is Constant cb) return new Constant(ca.Value > cb.Value ? ca.Value : cb.Value);
            var elseLabel = MakeLabel();
            var endLabel = MakeLabel();
            var result = MakeTemp();
            var cmp = MakeTemp();
            Emit(new Binary(BinaryOp.GreaterThan, a, b, cmp));
            Emit(new JumpIfZero(cmp, elseLabel));
            Emit(new Copy(a, result));
            Emit(new Jump(endLabel));
            Emit(new Label(elseLabel));
            Emit(new Copy(b, result));
            Emit(new Label(endLabel));
            return result;
        }

        if (callee == "ord")
        {
            if (expr.Args.Count != 1) throw new Exception("ord() expects exactly one argument");
            if (expr.Args[0] is StringLiteral sl)
            {
                if (sl.Value.Length != 1) throw new Exception("ord() argument must be a single character");
                return new Constant((int)sl.Value[0]);
            }

            return VisitExpression(expr.Args[0]);
        }

        if (callee == "chr")
        {
            if (expr.Args.Count != 1) throw new Exception("chr() expects exactly one argument");
            return VisitExpression(expr.Args[0]);
        }

        if (callee == "sum")
        {
            if (expr.Args.Count != 1) throw new Exception("sum() expects exactly one argument");
            switch (expr.Args[0])
            {
                case ListExpr { Elements.Count: 0 }:
                    return new Constant(0);
                case ListExpr le:
                {
                    var acc = VisitExpression(le.Elements[0]);
                    for (var i = 1; i < le.Elements.Count; ++i)
                    {
                        var v = VisitExpression(le.Elements[i]);
                        if (acc is Constant ca && v is Constant cv)
                        {
                            acc = new Constant(ca.Value + cv.Value);
                            continue;
                        }

                        var t = MakeTemp();
                        Emit(new Binary(BinaryOp.Add, acc, v, t));
                        acc = t;
                    }

                    return acc;
                }
                case VariableExpr sumVar:
                {
                    int arrSize = -1;
                    string arrBase = "";
                    if (!string.IsNullOrEmpty(currentInlinePrefix))
                    {
                        string key = currentInlinePrefix + sumVar.Name;
                        if (arraySizes.TryGetValue(key, out int s))
                        {
                            arrSize = s;
                            arrBase = key;
                        }
                    }

                    if (arrSize < 0 && !string.IsNullOrEmpty(currentFunction))
                    {
                        string key = currentFunction + "." + sumVar.Name;
                        if (arraySizes.TryGetValue(key, out int s))
                        {
                            arrSize = s;
                            arrBase = key;
                        }
                    }

                    if (arrSize < 0 && arraySizes.TryGetValue(sumVar.Name, out int s2))
                    {
                        arrSize = s2;
                        arrBase = sumVar.Name;
                    }

                    if (arrSize <= 0) throw new Exception("sum() requires a list literal or fixed-size array");

                    Val acc = new Variable(arrBase + "__0", DataType.UINT8);
                    for (int i = 1; i < arrSize; ++i)
                    {
                        Val vi = new Variable(arrBase + "__" + i, DataType.UINT8);
                        Temporary t = MakeTemp();
                        Emit(new Binary(BinaryOp.Add, acc, vi, t));
                        acc = t;
                    }

                    return acc;
                }
                default:
                    throw new Exception("sum() requires a list literal or fixed-size array");
            }
        }

        if (callee == "any")
        {
            if (expr.Args.Count != 1) throw new Exception("any() expects exactly one argument");
            if (!(expr.Args[0] is ListExpr le)) throw new Exception("any() requires a list literal argument");
            if (le.Elements.Count == 0) return new Constant(0);
            bool allConst = true;
            foreach (var e in le.Elements)
            {
                Val v = VisitExpression(e);
                if (v is Constant c)
                {
                    if (c.Value != 0) return new Constant(1);
                }
                else allConst = false;
            }

            if (allConst) return new Constant(0);
            Temporary result = MakeTemp();
            Emit(new Copy(new Constant(0), result));
            foreach (var e in le.Elements)
            {
                Val v = VisitExpression(e);
                Temporary cmp = MakeTemp();
                Emit(new Binary(BinaryOp.NotEqual, v, new Constant(0), cmp));
                string endLbl = MakeLabel();
                Emit(new JumpIfNotZero(result, endLbl));
                Emit(new Copy(cmp, result));
                Emit(new Label(endLbl));
            }

            return result;
        }

        if (callee == "all")
        {
            if (expr.Args.Count != 1) throw new Exception("all() expects exactly one argument");
            if (!(expr.Args[0] is ListExpr le)) throw new Exception("all() requires a list literal argument");
            if (le.Elements.Count == 0) return new Constant(1);
            bool allConst = true;
            foreach (var e in le.Elements)
            {
                Val v = VisitExpression(e);
                if (v is Constant c)
                {
                    if (c.Value == 0) return new Constant(0);
                }
                else allConst = false;
            }

            if (allConst) return new Constant(1);
            Temporary result = MakeTemp();
            Emit(new Copy(new Constant(1), result));
            foreach (var e in le.Elements)
            {
                Val v = VisitExpression(e);
                Temporary cmp = MakeTemp();
                Emit(new Binary(BinaryOp.NotEqual, v, new Constant(0), cmp));
                string endLbl = MakeLabel();
                Emit(new JumpIfZero(result, endLbl));
                Emit(new Copy(cmp, result));
                Emit(new Label(endLbl));
            }

            return result;
        }

        if (callee == "hex")
        {
            if (expr.Args.Count != 1) throw new Exception("hex() expects exactly one argument");
            Val v = VisitExpression(expr.Args[0]);
            if (!(v is Constant c)) throw new Exception("hex() argument must be a compile-time constant integer");
            string hexstr = "0x" + c.Value.ToString("x");
            if (!stringLiteralIds.ContainsKey(hexstr))
            {
                stringLiteralIds[hexstr] = nextStringId;
                stringIdToStr[nextStringId] = hexstr;
                nextStringId++;
            }

            return new Constant(stringLiteralIds[hexstr]);
        }

        if (callee == "bin")
        {
            if (expr.Args.Count != 1) throw new Exception("bin() expects exactly one argument");
            Val v = VisitExpression(expr.Args[0]);
            if (!(v is Constant c)) throw new Exception("bin() argument must be a compile-time constant integer");
            string binstr = "0b" + Convert.ToString(c.Value, 2);
            if (!stringLiteralIds.ContainsKey(binstr))
            {
                stringLiteralIds[binstr] = nextStringId;
                stringIdToStr[nextStringId] = binstr;
                nextStringId++;
            }

            return new Constant(stringLiteralIds[binstr]);
        }

        if (callee == "str")
        {
            if (expr.Args.Count != 1) throw new Exception("str() expects exactly one argument");
            Val v = VisitExpression(expr.Args[0]);
            if (!(v is Constant c)) throw new Exception("str() argument must be a compile-time constant integer");
            string decstr = c.Value.ToString();
            if (!stringLiteralIds.ContainsKey(decstr))
            {
                stringLiteralIds[decstr] = nextStringId;
                stringIdToStr[nextStringId] = decstr;
                nextStringId++;
            }

            return new Constant(stringLiteralIds[decstr]);
        }

        if (callee == "pow")
        {
            if (expr.Args.Count != 2) throw new Exception("pow() expects exactly two arguments");
            Val bv = VisitExpression(expr.Args[0]);
            Val ev = VisitExpression(expr.Args[1]);
            if (!(bv is Constant cb) || !(ev is Constant ce))
                throw new Exception("pow() arguments must be compile-time constant integers");
            int @base = cb.Value;
            int exp = ce.Value;
            if (exp < 0) throw new Exception("pow() negative exponent not supported");
            int res = 1;
            for (int k = 0; k < exp; ++k) res *= @base;
            return new Constant(res);
        }

        if (callee == "divmod")
        {
            if (expr.Args.Count != 2) throw new Exception("divmod() expects exactly two arguments");
            Val aVal = VisitExpression(expr.Args[0]);
            Val bVal = VisitExpression(expr.Args[1]);
            if (aVal is Constant ca && bVal is Constant cb)
            {
                if (cb.Value == 0) throw new Exception("divmod(): division by zero");
                int q = ca.Value / cb.Value;
                int r = ca.Value % cb.Value;
                if (pendingTupleCount == 2)
                {
                    string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
                    string qn = bBase + ".divmod_q" + tempCounter;
                    string rn = bBase + ".divmod_r" + (tempCounter + 1);
                    tempCounter += 2;
                    Emit(new Copy(new Constant(q), new Variable(qn, DataType.UINT8)));
                    Emit(new Copy(new Constant(r), new Variable(rn, DataType.UINT8)));
                    lastTupleResults = new List<string> { qn, rn };
                    return new NoneVal();
                }

                return new Constant(q);
            }

            if (pendingTupleCount == 2)
            {
                string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
                string qn = bBase + ".divmod_q" + tempCounter;
                string rn = bBase + ".divmod_r" + (tempCounter + 1);
                tempCounter += 2;
                var qvar = new Variable(qn, DataType.UINT8);
                var rvar = new Variable(rn, DataType.UINT8);

                Emit(new Call("__div8", new List<Val> { aVal, bVal }, qvar));
                Emit(new Call("__mod8", new List<Val> { aVal, bVal }, rvar));
                lastTupleResults = new List<string> { qn, rn };
                return new NoneVal();
            }

            Temporary qTmp = MakeTemp();
            Emit(new Call("__div8", new List<Val> { aVal, bVal }, qTmp));
            return qTmp;
        }

        var castTypes = new Dictionary<string, DataType>
        {
            { "uint8", DataType.UINT8 }, { "uint16", DataType.UINT16 }, { "uint32", DataType.UINT32 },
            { "int8", DataType.INT8 }, { "int16", DataType.INT16 }, { "int32", DataType.INT32 },
            { "int", DataType.INT16 }
        };
        if (castTypes.TryGetValue(callee, out DataType dstType))
        {
            if (expr.Args.Count != 1) throw new Exception(callee + "() expects exactly one argument");
            Val v = VisitExpression(expr.Args[0]);
            if (v is Constant c)
            {
                int val = c.Value;
                switch (dstType)
                {
                    case DataType.UINT8: val = (byte)val; break;
                    case DataType.UINT16: val = (ushort)val; break;
                    case DataType.INT8: val = (sbyte)val; break;
                    case DataType.INT16: val = (short)val; break;
                }

                return new Constant(val);
            }

            // Float constant to integer cast: fold at compile time (e.g. uint16(0.5 * 1000) -> 500).
            if (v is FloatConstant fc && dstType != DataType.FLOAT)
            {
                int val = (int)fc.Value;
                switch (dstType)
                {
                    case DataType.UINT8: val = (byte)val; break;
                    case DataType.UINT16: val = (ushort)val; break;
                    case DataType.INT8: val = (sbyte)val; break;
                    case DataType.INT16: val = (short)val; break;
                }
                return new Constant(val);
            }

            Temporary dst = MakeTemp(dstType);
            Emit(new Copy(v, dst));
            return dst;
        }

        if (callee == "bitcast")
        {
            if (expr.Args.Count != 2) throw new Exception("bitcast() expects exactly two arguments: bitcast(type, value)");
            string typeName = (expr.Args[0] as VariableExpr)?.Name
                ?? throw new Exception("bitcast() first argument must be a type name");
            DataType bcDstType;
            if (typeName == "float")
                bcDstType = DataType.FLOAT;
            else if (!castTypes.TryGetValue(typeName, out bcDstType))
                throw new Exception($"bitcast(): unknown type '{typeName}'");

            Val srcVal = VisitExpression(expr.Args[1]);

            // Compile-time constant folding
            if (bcDstType == DataType.UINT32 && srcVal is FloatConstant fcBc)
            {
                uint bits = BitConverter.SingleToUInt32Bits((float)fcBc.Value);
                return new Constant((int)bits);
            }
            if (bcDstType == DataType.FLOAT && srcVal is Constant cBc)
                return new FloatConstant(BitConverter.Int32BitsToSingle(cBc.Value));

            Temporary bcDst = MakeTemp(bcDstType);
            Emit(new Bitcast(srcVal, bcDst));
            return bcDst;
        }

        if (callee == "gc_alloc")
        {
            if (expr.Args.Count != 1) throw new Exception("gc_alloc() expects exactly one argument: gc_alloc(size)");
            Val sizeVal = VisitExpression(expr.Args[0]);
            Temporary gcDst = MakeTemp(DataType.GC_REF);
            Emit(new GcAlloc(sizeVal, gcDst));
            return gcDst;
        }

        if (callee == "asm")
        {
            // asm("code")                  — bare inline assembly (no constraints)
            // asm("code", op0, op1, ...)   — assembly with %N register constraints
            if (expr.Args.Count < 1) throw new Exception("asm() requires at least one string argument");

            string? code = null;
            if (expr.Args[0] is StringLiteral str2)
                code = str2.Value;
            else if (expr.Args[0] is FStringExpr fstr2)
            {
                var resolved = VisitFStringExpr(fstr2);
                if (resolved is Constant c2 && stringIdToStr.TryGetValue(c2.Value, out var s2))
                    code = s2;
                else
                    throw new Exception("asm() f-string did not resolve to a string constant");
            }
            else if (expr.Args[0] is VariableExpr ve2)
                throw new Exception($"asm() argument must be a string literal, got variable '{ve2.Name}'");
            else
                throw new Exception("asm() argument must be a compile-time string literal");

            if (code == null) return new NoneVal();

            if (expr.Args.Count == 1)
            {
                Emit(new InlineAsm(code));
            }
            else
            {
                // Collect constraint operands (%0, %1, …).
                // Operands must resolve to Variables (not Constants) so that
                // the backend can both load the current value and store back
                // the modified result after the inline assembly executes.
                var operands = new List<Val>();
                for (int i = 1; i < expr.Args.Count; i++)
                {
                    if (expr.Args[i] is VariableExpr ve)
                    {
                        operands.Add(ResolveAsmOperand(ve.Name));
                    }
                    else
                    {
                        operands.Add(VisitExpression(expr.Args[i]));
                    }
                }
                Emit(new InlineAsm(code, operands));
            }
            return new NoneVal();
        }

        if (callee == "print")
        {
            string endStr = "\n";
            string sepStr = " ";
            var posArgs = new List<Expression>();
            foreach (var arg in expr.Args)
            {
                if (arg is KeywordArgExpr kw)
                {
                    if (kw.Key == "end" || kw.Key == "sep")
                    {
                        if (kw.Value is StringLiteral lit)
                        {
                            if (kw.Key == "end") endStr = lit.Value;
                            else sepStr = lit.Value;
                        }
                        else throw new Exception($"print() '{kw.Key}' must be a compile-time string literal");
                    }
                }
                else posArgs.Add(arg);
            }

            // Resolve the string-output function.  Prefer the arch-dispatched
            // console.print_str injected by the build driver; fall back to
            // uart_write_str for projects that initialise UART manually.
            string writeStrFn = ResolveCallee("print_str");
            if (writeStrFn == "print_str")
            {
                writeStrFn = ResolveCallee("uart_write_str");
                if (writeStrFn == "uart_write_str")
                {
                    foreach (var fnName in inlineFunctions.Keys)
                    {
                        if (fnName.EndsWith("_print_str") || fnName.EndsWith("_uart_write_str"))
                        {
                            writeStrFn = fnName;
                            break;
                        }
                    }
                }
            }

            void EmitStr(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                var synthCall = new CallExpr(
                    new VariableExpr(writeStrFn),
                    new List<Expression> { new StringLiteral(s) });
                VisitCall(synthCall);
            }

            // Integer output: uart_write_decimal_u8 is non-inline and works with direct Emit.
            // print_u8 from console.py is @inline and requires VisitCall; deferred to a
            // future refactor of EmitPrintArg to use VisitCall for all write functions.
            string decimalWriteFn = ResolveCallee("uart_write_decimal_u8");
            if (decimalWriteFn == "uart_write_decimal_u8")
            {
                string decSuffix = "uart_write_decimal_u8";
                foreach (var fnName in functionReturnTypes.Keys)
                {
                    if (fnName.EndsWith(decSuffix))
                    {
                        decimalWriteFn = fnName;
                        break;
                    }
                }
            }

            // Float output: same rationale — use the non-inline uart function.
            string floatWriteFn = ResolveCallee("uart_write_float");
            if (floatWriteFn == "uart_write_float")
            {
                foreach (var fnName in functionReturnTypes.Keys)
                {
                    if (fnName.EndsWith("uart_write_float"))
                    {
                        floatWriteFn = fnName;
                        break;
                    }
                }
            }

            void EmitPrintArg(Expression arg)
            {
                if (arg is StringLiteral lit)
                {
                    var synthCall = new CallExpr(
                        new VariableExpr(writeStrFn),
                        new List<Expression> { lit });
                    VisitCall(synthCall);
                    return;
                }

                if (arg is VariableExpr v)
                {
                    string key = currentInlinePrefix + v.Name;
                    string? strVal = ResolveStrConstant(key);
                    if (strVal != null)
                    {
                        var synthCall = new CallExpr(
                            new VariableExpr(writeStrFn),
                            new List<Expression> { new StringLiteral(strVal) });
                        VisitCall(synthCall);
                        return;
                    }
                }

                Val val = VisitExpression(arg);
                bool isFloat = val is FloatConstant ||
                               (val is Variable vf && vf.Type == DataType.FLOAT) ||
                               (val is Temporary tf && tf.Type == DataType.FLOAT);
                if (isFloat)
                {
                    Temporary ftmp = MakeTemp(DataType.FLOAT);
                    Emit(new Copy(val, ftmp));
                    Emit(new Call(floatWriteFn, new List<Val> { ftmp }, ftmp));
                    return;
                }
                Temporary tmp = MakeTemp();
                Emit(new Copy(val, tmp));
                Emit(new Call(decimalWriteFn, new List<Val> { tmp }, tmp));
            }

            if (posArgs.Count == 0)
            {
                EmitStr(endStr);
                return new NoneVal();
            }

            for (int i = 0; i < posArgs.Count; ++i)
            {
                if (i > 0) EmitStr(sepStr);
                EmitPrintArg(posArgs[i]);
            }

            EmitStr(endStr);
            return new NoneVal();
        }

        if (callee == "ptr" && intrinsicNames.Contains("ptr"))
        {
            if (expr.Args.Count != 1) throw new Exception("ptr() expects exactly one argument");
            Val argVal = VisitExpression(expr.Args[0]);
            if (argVal is Constant c) return new MemoryAddress(c.Value, DataType.UINT8);
            throw new Exception("ptr() argument must be a constant expression");
        }

        if (callee == "ptr" && !intrinsicNames.Contains("ptr"))
        {
            Console.Error.WriteLine(
                "[Warning] 'ptr' is not recognized as an intrinsic. Did you forget to import from pymcu.types?");
            return new Constant(0);
        }

        if (callee == "const" && intrinsicNames.Contains("const"))
        {
            if (expr.Args.Count != 1) throw new Exception("const() expects exactly one argument");
            Val argVal = VisitExpression(expr.Args[0]);
            if (argVal is Constant) return argVal;
            throw new Exception("const() argument must be a compile-time constant expression");
        }

        if ((callee == "funcref" || callee == "pymcu_types_funcref") && intrinsicNames.Contains("funcref"))
        {
            if (expr.Args.Count != 1)
                throw new Exception("funcref() expects exactly one argument: a function name");
            if (expr.Args[0] is not VariableExpr fnRefExpr)
                throw new Exception("funcref() argument must be a function name identifier");

            // Resolve alias chain (same as compile_isr) to find the canonical function name.
            string key = currentInlinePrefix + fnRefExpr.Name;
            for (int d = 0; d < 20; ++d)
                if (variableAliases.TryGetValue(key, out string nx)) key = nx;
                else break;

            string resolvedName = key;
            int lastDot = resolvedName.LastIndexOf('.');
            if (lastDot >= 0) resolvedName = resolvedName.Substring(lastDot + 1);
            string fnName = ResolveCallee(resolvedName);

            return new FunctionRef(fnName);
        }

        if (callee == "_set_irq_zca_arg" && intrinsicNames.Contains("_set_irq_zca_arg"))
        {
            // _set_irq_zca_arg(handler, zca_instance): records the ZCA variable that
            // should be bound to handler's first parameter when the ISR wrapper is synthesized.
            if (expr.Args.Count == 2)
            {
                // Resolve handler name (same alias-chase as compile_isr arg0)
                string hKey = "";
                if (expr.Args[0] is VariableExpr v0)
                {
                    hKey = currentInlinePrefix + v0.Name;
                    for (int d = 0; d < 20; d++)
                        if (variableAliases.TryGetValue(hKey, out string? nk) && nk != null) hKey = nk; else break;
                    int ld = hKey.LastIndexOf('.');
                    if (ld >= 0) hKey = hKey[(ld + 1)..];
                    hKey = ResolveCallee(hKey);
                }
                // Resolve ZCA instance to its root variable key (follow alias chain)
                Val zcaVal = VisitExpression(expr.Args[1]);
                string zcaKey = "";
                if (zcaVal is Variable vz) zcaKey = vz.Name;
                else if (zcaVal is Temporary tz) zcaKey = tz.Name;
                for (int d = 0; d < 20; d++)
                    if (variableAliases.TryGetValue(zcaKey, out string? nz) && nz != null) zcaKey = nz; else break;

                if (!string.IsNullOrEmpty(hKey) && !string.IsNullOrEmpty(zcaKey))
                    pendingZcaIsrBindings[hKey] = zcaKey;
            }
            return new NoneVal();
        }

        if (callee == "compile_isr" && intrinsicNames.Contains("compile_isr"))
        {
            if (expr.Args.Count != 2)
                throw new Exception("compile_isr() expects exactly 2 arguments: compile_isr(handler, vector)");
            Val vecVal = VisitExpression(expr.Args[1]);
            int vector = 0;
            if (vecVal is Constant c) vector = c.Value;
            else throw new Exception("compile_isr() second argument (vector) must be a compile-time constant");

            string handlerFuncName = "";
            bool handlerProvided = false;

            if (expr.Args[0] is VariableExpr v)
            {
                string key = currentInlinePrefix + v.Name;
                if (constantVariables.TryGetValue(key, out int cv) && cv == 0) return new NoneVal();

                for (int depth = 0; depth < 20; ++depth)
                {
                    if (variableAliases.TryGetValue(key, out string next)) key = next;
                    else break;
                }

                // When compile_isr() is called inside an inlined function, the
                // handler parameter is an alias chain (e.g. handler -> main.int0_isr).
                // After alias resolution above, `key` holds the resolved name which
                // may be scope-qualified (e.g. "main.int0_isr" for a function defined
                // at top-level in main.py).  Extract the bare function name (after
                // the last dot) and resolve it via ResolveCallee so it gets the
                // correct module-qualified IR name.
                string resolvedName = key;
                int lastDot = resolvedName.LastIndexOf('.');
                if (lastDot >= 0)
                    resolvedName = resolvedName.Substring(lastDot + 1);
                handlerFuncName = ResolveCallee(resolvedName);
                handlerProvided = !string.IsNullOrEmpty(handlerFuncName);
            }
            else
            {
                Val arg0 = VisitExpression(expr.Args[0]);
                if (arg0 is Constant c0 && c0.Value == 0) return new NoneVal();
                throw new Exception("compile_isr() first argument must be a function reference or 0");
            }

            if (!handlerProvided) return new NoneVal();

            // ZCA ISR synthesis: if a ZCA binding was registered via _set_irq_zca_arg,
            // synthesize a parameterless wrapper that inline-expands handler with the
            // ZCA constants bound. The wrapper is what gets registered at the vector.
            if (pendingZcaIsrBindings.TryGetValue(handlerFuncName, out string? zcaRootKey) &&
                !string.IsNullOrEmpty(zcaRootKey))
            {
                pendingZcaIsrBindings.Remove(handlerFuncName);
                string synthName = SynthesizeZcaIsrWrapper(handlerFuncName, zcaRootKey);
                if (!string.IsNullOrEmpty(synthName))
                {
                    pendingIsrRegistrations[synthName] = vector;
                    return new NoneVal();
                }
                // Synthesis returned empty -- fall through to original name (will fail if ZCA param)
            }

            pendingIsrRegistrations[handlerFuncName] = vector;
            return new NoneVal();
        }

        string cSym;
        if (externFunctionMap.TryGetValue(callee, out cSym))
        {
            var extArgs = new List<Val>();
            foreach (var arg in expr.Args)
            {
                Val av = VisitExpression(arg);
                if (av is FloatConstant avFc)
                    av = new Constant((int)Math.Round(avFc.Value));
                else if (av is Variable v && floatConstantVariables.TryGetValue(v.Name, out double fv))
                    av = new Constant((int)Math.Round(fv));
                extArgs.Add(av);
            }

            bool returnsVoid = !functionReturnTypes.ContainsKey(callee) || functionReturnTypes[callee] == "void" ||
                               functionReturnTypes[callee] == "None";
            if (returnsVoid)
            {
                Emit(new Call(cSym, extArgs, new NoneVal()));
                return new NoneVal();
            }

            Temporary extDst = MakeTemp(DataTypeExtensions.StringToDataType(functionReturnTypes[callee]));
            Emit(new Call(cSym, extArgs, extDst));
            return extDst;
        }

        if (inlineFunctions.TryGetValue(callee, out var func))
        {
            // @warning("..."): print the author-supplied note (once per function)
            // when a call to this function is expanded. Informational only -- it
            // does NOT abort compilation, so flagged-but-usable features (soft-float,
            // reduced bare-metal behaviour) still build.
            if (func != null && !string.IsNullOrEmpty(func.WarningMessage) && warningNoticed.Add(func.Name))
            {
                Console.Error.WriteLine($"[pymcuc] warning: {func.WarningMessage}");
            }

            var exitLabel = MakeLabel();
            var newDepth = inlineDepth + 1;
            var newPrefix = $"inline{newDepth}.{func?.Name}.";

            Temporary? result = null;
            var tupleResultNames = new List<string>();

            if (pendingTupleCount > 0)
            {
                string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
                for (int k = 0; k < pendingTupleCount; ++k)
                {
                    tupleResultNames.Add($"{bBase}.iret_{newDepth}_{k}");
                }
            }
            else if (func.ReturnType != "void" && func.ReturnType != "None")
            {
                result = MakeTemp(DataTypeExtensions.StringToDataType(func.ReturnType));
            }

            var argValues = new List<Val>();

            bool isConstructor = callee.EndsWith("___init__") || callee.Contains("___init____");
            int paramOffset = 0;

            if (!isConstructor)
            {
                if (expr.Callee is MemberAccessExpr mem2)
                {
                    Val objVal = VisitExpression(mem2.Object);
                    if (objVal is Variable v2 && instanceClasses.ContainsKey(v2.Name))
                    {
                        string selfName = newPrefix + "self";
                        variableAliases[selfName] = v2.Name;
                        instanceClasses[selfName] = instanceClasses[v2.Name];
                        paramOffset = 1;
                    }
                }
            }
            else paramOffset = 1;

            var kwArgValues = new Dictionary<string, Val>();
            var rawKwStrArgs = new Dictionary<string, string?>();
            var rawStrArgs = new List<StringLiteral?>();
            var rawListArgs = new List<ListExpr?>();

            foreach (var arg in expr.Args)
            {
                if (arg is KeywordArgExpr kw)
                {
                    string savedOuterPct = pendingConstructorTarget;
                    pendingConstructorTarget = "";
                    kwArgValues[kw.Key] = VisitExpression(kw.Value);
                    if (kw.Value is StringLiteral s) rawKwStrArgs[kw.Key] = s.Value;
                    // Always restore: inner ctor targets (anonymous __cN) must not
                    // overwrite the outer assignment target (e.g. "main.spi").
                    pendingConstructorTarget = savedOuterPct;
                }
                else
                {
                    rawStrArgs.Add(arg as StringLiteral);
                    // A bytes/list literal (b"Hi", [1,2,3]) or a tuple literal
                    // ((r,g,b)) is a fixed sequence: normalise both to a ListExpr
                    // and bind it to the inline parameter so the callee can consume
                    // it via `for x in param` (unrolled) or `param[const]` indexing.
                    ListExpr? seqLit = arg as ListExpr
                        ?? (arg is TupleExpr tple ? new ListExpr(tple.Elements) : null);
                    rawListArgs.Add(seqLit);
                    if (seqLit != null)
                    {
                        // Visiting the literal as an expression is unsupported; the raw
                        // AST is bound below. Push a placeholder to keep argValues
                        // index-aligned with the parameter list.
                        argValues.Add(new NoneVal());
                    }
                    else
                    {
                        string savedOuterPct = pendingConstructorTarget;
                        pendingConstructorTarget = "";
                        argValues.Add(VisitExpression(arg));
                        // Always restore: same reason as kwarg case above.
                        pendingConstructorTarget = savedOuterPct;
                    }
                }
            }

            bool isForceInlined = func != null && !func.IsInline;
            if (isForceInlined)
                Emit(new InlineExpansionMarker(callee, false));

            inlineDepth++;
            string savedPrefix = currentInlinePrefix;
            currentInlinePrefix = newPrefix;

            var savedModulePrefix = currentModulePrefix;
            if (func.Name.Length < callee.Length)
            {
                currentModulePrefix = callee.Substring(0, callee.Length - func.Name.Length);
            }

            if (methodInstanceTypes.TryGetValue(callee, out var mit))
            {
                instanceClasses[newPrefix + "self"] = mit;
            }

            string? ctorSubexprSynth = null;
            if (isConstructor)
            {
                var selfName = newPrefix + "self";
                var initPos = callee.IndexOf("___init____", StringComparison.Ordinal);
                var classPrefix =
                    initPos != -1 ? callee[..initPos] : callee[..^9];
                string target;
                if (!string.IsNullOrEmpty(pendingConstructorTarget))
                {
                    target = pendingConstructorTarget;
                    pendingConstructorTarget = "";
                }
                else
                {
                    string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
                    target = bBase + ".__c" + (++ctorAnonId);
                    ctorSubexprSynth = target;
                }

                variableAliases[selfName] = target;
                instanceClasses[selfName] = classPrefix;
                instanceClasses[target] = classPrefix;
                virtualInstances.Add(target);
            }

            inlineStack.Add(new InlineContext
                { ExitLabel = exitLabel, ResultTemp = result, ResultVars = tupleResultNames, CalleeName = callee });

            var boundParams = new HashSet<int>();

            for (int i = 0; i < argValues.Count; ++i)
            {
                int paramIdx = i + paramOffset;
                if (paramIdx >= func.Params.Count) break;
                string paramName = currentInlinePrefix + func.Params[paramIdx].Name;
                boundParams.Add(paramIdx);

                if (i < rawListArgs.Count && rawListArgs[i] != null)
                {
                    // Bytes/list/tuple literal bound to this parameter: record the raw AST
                    // so `for x in param` unrolls it and `param[const]` folds. Clear any
                    // stale scalar bindings.
                    listLiteralParams[paramName] = rawListArgs[i]!;
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    continue;
                }
                listLiteralParams.Remove(paramName);

                if (argValues[i] is FloatConstant fcArg)
                {
                    var fcPType = func.Params[paramIdx].Type;
                    bool fcIsInt = fcPType is "uint8" or "uint16" or "uint32" or "int8" or "int16" or "int32" or "int";
                    if (fcIsInt)
                    {
                        constantVariables[paramName] = (int)fcArg.Value;
                        floatConstantVariables.Remove(paramName);
                    }
                    else
                    {
                        floatConstantVariables[paramName] = fcArg.Value;
                        constantVariables.Remove(paramName);
                    }
                    strConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    variableTypes[paramName] = DataTypeExtensions.StringToDataType(fcPType);
                    continue;
                }

                if (argValues[i] is Variable vArg)
                {
                    if (func.Params[paramIdx].Type == "const[str]")
                    {
                        string? strVal = ResolveStrConstant(vArg.Name);
                        if (strVal != null)
                        {
                            strConstantVariables[paramName] = strVal;
                            constantVariables.Remove(paramName);
                            variableAliases.Remove(paramName);
                            continue;
                        }
                    }

                    if (floatConstantVariables.TryGetValue(vArg.Name, out double fv))
                    {
                        var fvPType = func.Params[paramIdx].Type;
                        bool fvIsInt = fvPType is "uint8" or "uint16" or "uint32" or "int8" or "int16" or "int32" or "int";
                        if (fvIsInt)
                        {
                            constantVariables[paramName] = (int)fv;
                            floatConstantVariables.Remove(paramName);
                        }
                        else
                        {
                            floatConstantVariables[paramName] = fv;
                            constantVariables.Remove(paramName);
                        }
                        strConstantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                        variableTypes[paramName] = DataTypeExtensions.StringToDataType(fvPType);
                        continue;
                    }

                    variableAliases[paramName] = vArg.Name;
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    variableTypes[paramName] = DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
                    continue;
                }

                if (argValues[i] is Temporary tArg)
                {
                    // A Temporary can carry a compile-time string or numeric constant
                    // when it is the result of a DCE'd @inline function (e.g.,
                    // _arduino_pin_name(13) → "PB5").  Without this block the value
                    // would fall through to the runtime Copy, losing the constant.
                    string? tStr = ResolveStrConstant(tArg.Name);
                    if (tStr == null && constantVariables.TryGetValue(tArg.Name, out int tId))
                        stringIdToStr.TryGetValue(tId, out tStr);
                    if (tStr != null)
                    {
                        strConstantVariables[paramName] = tStr;
                        constantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                        continue;
                    }
                    if (constantVariables.TryGetValue(tArg.Name, out int tNum))
                    {
                        constantVariables[paramName] = tNum;
                        strConstantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                        continue;
                    }
                    // Non-constant Temporary: fall through to runtime Copy
                }

                if (IsConstType(func.Params[paramIdx].Type))
                {
                    if (func.Params[paramIdx].Type == "const[str]")
                    {
                        if (i < rawStrArgs.Count && rawStrArgs[i] != null)
                        {
                            strConstantVariables[paramName] = rawStrArgs[i]!.Value;
                            continue;
                        }

                        if (argValues[i] is Variable vArg2 && ResolveStrConstant(vArg2.Name) is string sv2)
                        {
                            strConstantVariables[paramName] = sv2;
                            continue;
                        }

                        if (argValues[i] is Constant cArg && stringIdToStr.TryGetValue(cArg.Value, out string sv3))
                        {
                            strConstantVariables[paramName] = sv3;
                            continue;
                        }

                        throw new Exception(
                            $"Parameter '{func.Params[paramIdx].Name}' is declared as const[str] and requires a compile-time string constant value");
                    }

                    if (!(argValues[i] is Constant cArg2))
                        throw new Exception(
                            $"Parameter '{func.Params[paramIdx].Name}' is declared as const and requires a compile-time constant value");
                    constantVariables[paramName] = cArg2.Value;
                    continue;
                }
                if (argValues[i] is Constant cArg3)
                {
                    constantVariables[paramName] = cArg3.Value;
                    continue;
                }
                if (argValues[i] is MemoryAddress mArg)
                {
                    constantAddressVariables[paramName] = mArg.Address;
                    constantAddressVariables.Remove(paramName + "_type");
                    continue;
                }

                constantVariables.Remove(paramName);
                strConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                DataType paramType = DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
                variableTypes[paramName] = paramType;
                Emit(new Copy(argValues[i], new Variable(paramName, paramType)));
            }

            foreach (var kvp in kwArgValues)
            {
                bool found = false;
                for (int pi = paramOffset; pi < func.Params.Count; ++pi)
                {
                    if (func.Params[pi].Name == kvp.Key)
                    {
                        string paramName = currentInlinePrefix + func.Params[pi].Name;
                        boundParams.Add(pi);
                        found = true;

                        if (kvp.Value is Variable vkw) variableAliases[paramName] = vkw.Name;

                        if (IsConstType(func.Params[pi].Type))
                        {
                            if (func.Params[pi].Type == "const[str]")
                            {
                                if (rawKwStrArgs.TryGetValue(kvp.Key, out var skw))
                                    strConstantVariables[paramName] = skw;
                                else if (kvp.Value is Variable vkw2 && ResolveStrConstant(vkw2.Name) is { } svkw)
                                    strConstantVariables[paramName] = svkw;
                                else
                                    throw new Exception(
                                        $"Parameter '{func.Params[pi].Name}' is declared as const[str] and requires a compile-time string constant value");
                            }
                            else
                            {
                                if (!(kvp.Value is Constant ckw))
                                    throw new Exception(
                                        $"Parameter '{func.Params[pi].Name}' is declared as const and requires a compile-time constant value");
                                constantVariables[paramName] = ckw.Value;
                            }
                        }
                        else if (kvp.Value is Constant ckw2)
                        {
                            constantVariables[paramName] = ckw2.Value;
                        }
                        else
                        {
                            constantVariables.Remove(paramName);
                            strConstantVariables.Remove(paramName);
                            DataType paramType = DataTypeExtensions.StringToDataType(func.Params[pi].Type);
                            variableTypes[paramName] = paramType;
                            if (kvp.Value is Variable)
                            {
                                // Variable arg (including ZCA instances): preserve the alias
                                // set above and skip the Copy, same as positional arg handling.
                            }
                            else
                            {
                                variableAliases.Remove(paramName);
                                Emit(new Copy(kvp.Value, new Variable(paramName, paramType)));
                            }
                        }

                        break;
                    }
                }

                if (!found) throw new Exception($"Unknown keyword argument '{kvp.Key}' in call to {callee}");
            }

            for (int i = paramOffset; i < func.Params.Count; ++i)
            {
                if (boundParams.Contains(i)) continue;
                if (func.Params[i].DefaultValue != null)
                {
                    string paramName = currentInlinePrefix + func.Params[i].Name;
                    Val defaultVal = VisitExpression(func.Params[i].DefaultValue!);

                    if (IsConstType(func.Params[i].Type))
                    {
                        if (func.Params[i].Type == "const[str]")
                        {
                            if (defaultVal is Variable vdf && ResolveStrConstant(vdf.Name) is string svdf)
                            {
                                strConstantVariables[paramName] = svdf;
                                continue;
                            }
                        }

                        if (!(defaultVal is Constant cdf))
                            throw new Exception(
                                $"Default value for const parameter '{func.Params[i].Name}' must be a compile-time constant");
                        constantVariables[paramName] = cdf.Value;
                        continue;
                    }

                    if (defaultVal is Constant cdf2) constantVariables[paramName] = cdf2.Value;
                    else
                    {
                        DataType paramType = DataTypeExtensions.StringToDataType(func.Params[i].Type);
                        Emit(new Copy(defaultVal, new Variable(paramName, paramType)));
                    }
                }
            }

            int savedLastLine = lastLine;
            lastLine = -1;
            try
            {
                VisitBlock(func.Body);
            }
            catch (CompilerError)
            {
                throw;
            }
            catch (Exception ex)
            {
                int callLine = currentStmtLine > 0 ? currentStmtLine : 1;
                throw new CompilerError("CompileError", ex.Message, callLine, 1);
            }

            lastLine = savedLastLine;

            Emit(new Label(exitLabel));

            if (isForceInlined)
                Emit(new InlineExpansionMarker(callee, true));

            if (Enumerable.Last<InlineContext>(inlineStack).ResultVars.Count > 0)
                lastTupleResults = new List<string>(Enumerable.Last<InlineContext>(inlineStack).ResultVars);
            inlineStack.RemoveAt(inlineStack.Count - 1);

            currentInlinePrefix = savedPrefix;
            currentModulePrefix = savedModulePrefix;
            inlineDepth--;

            if (result != null) return result;
            if (ctorSubexprSynth != null) return new Variable(ctorSubexprSynth);
            return new NoneVal();
        }

        bool calleeIsKnownFunc = functionParams.ContainsKey(callee);
        var argValuesL = new List<Val>();
        foreach (var arg in expr.Args)
        {
            // const[str] argument to a non-@inline function: intern the string and pass its
            // flash address by reference (FlashStrAddr). The callee walks it with FlashLoadPtr,
            // so the byte-loop lives in a single shared subroutine instead of being inlined at
            // every call site. (Inline callees bind the literal via strConstantVariables and
            // never reach this path.)
            if (calleeIsKnownFunc)
            {
                string? argStr = arg switch
                {
                    StringLiteral sl => sl.Value,
                    VariableExpr ve => ResolveStrConstant((!string.IsNullOrEmpty(currentInlinePrefix)
                        ? currentInlinePrefix
                        : currentFunction + ".") + ve.Name),
                    _ => null
                };
                if (argStr != null)
                {
                    argValuesL.Add(new FlashStrAddr(InternStringAsFlash(argStr)));
                    continue;
                }
            }

            // If the argument is a bare variable name that refers to a local array,
            // pass its base address rather than trying to load it as a scalar.
            if (arg is VariableExpr argVe)
            {
                string argQualified = (!string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix
                    : currentFunction + ".") + argVe.Name;
                if (!arraySizes.ContainsKey(argQualified))
                {
                    // Fall back to unqualified / module-level name
                    string altQ = currentModulePrefix + argVe.Name;
                    if (arraySizes.ContainsKey(altQ)) argQualified = altQ;
                    else if (arraySizes.ContainsKey(argVe.Name)) argQualified = argVe.Name;
                }
                if (arraySizes.ContainsKey(argQualified))
                {
                    argValuesL.Add(new ArrayBase(argQualified));
                    continue;
                }
            }
            argValuesL.Add(VisitExpression(arg));
        }

        int dotPos2 = callee.IndexOf('.');
        if (dotPos2 != -1)
        {
            string mod = callee.Substring(0, dotPos2);
            if (modules.ContainsKey(mod))
            {
                callee = callee.Substring(0, dotPos2) + "_" + callee.Substring(dotPos2 + 1);
            }
        }

        if (functionParams.TryGetValue(callee, out var paramNames))
        {
            if (expr.Args.Count != paramNames.Count)
                throw new Exception(
                    $"Function '{callee}' expects {paramNames.Count} arguments, but {expr.Args.Count} were provided");
            var paramTypes = functionParamTypes.TryGetValue(callee, out var pt) ? pt : new List<DataType>();
            for (int i = 0; i < expr.Args.Count; ++i)
            {
                string paramVarName = callee + "." + paramNames[i];
                DataType ptype = i < paramTypes.Count ? paramTypes[i] : DataType.UINT8;
                Val argVal = argValuesL[i];

                // A flash-string-by-reference argument is a 16-bit flash address, regardless
                // of how the const[str] param's nominal type folds.
                if (argVal is FlashStrAddr) ptype = DataType.UINT16;

                // Auto-wrap: if a Callable (FUNCREF) parameter receives a bare function name
                // (which resolves as a UINT8 Variable rather than a FunctionRef), create the
                // FunctionRef so DCE treats the function as reachable and the backend emits
                // the correct lo8/hi8 address load rather than a SRAM load.
                if (ptype == DataType.FUNCREF && argVal is Variable argVar && argVar.Type != DataType.FUNCREF)
                {
                    string rawName = argVar.Name.Contains('.')
                        ? argVar.Name.Substring(argVar.Name.LastIndexOf('.') + 1)
                        : argVar.Name;
                    string resolvedFn = ResolveCallee(rawName);
                    if (functionParams.ContainsKey(resolvedFn) || functionReturnTypes.ContainsKey(resolvedFn))
                    {
                        argVal = new FunctionRef(resolvedFn);
                        // Update the arg list so the CALL instruction also passes the FunctionRef.
                        // Without this the backend would emit a 1-byte UINT8 load into R24 and
                        // leave R25 (the hi byte of the word address) undefined.
                        argValuesL[i] = argVal;
                    }
                }

                Emit(new Copy(argVal, new Variable(paramVarName, ptype)));
            }
        }

        bool returnsVoidEnd = functionReturnTypes.TryGetValue(callee, out string? rType) && (rType == "void" || rType == "None");
        
        if (returnsVoidEnd)
        {
            Emit(new Call(callee, argValuesL, new NoneVal()));
            return new NoneVal();
        }

        Temporary dstC = MakeTemp();
        Emit(new Call(callee, argValuesL, dstC));
        return dstC;
    }

    // Resolves a short variable name to its fully qualified list variable name,
    // or returns "" if the variable is not a list[T].
    private string ResolveListVarQualified(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix))
        {
            string k = currentInlinePrefix + name;
            if (listVarElemTypes.ContainsKey(k)) return k;
        }
        if (!string.IsNullOrEmpty(currentFunction))
        {
            string k = currentFunction + "." + name;
            if (listVarElemTypes.ContainsKey(k)) return k;
        }
        if (listVarElemTypes.ContainsKey(name)) return name;
        return "";
    }

    // Computes basePtr + 2 + index * elemSize as a UINT16 address Temporary.
    private Temporary EmitElemAddr(Val basePtr, Val index, int elemSize)
    {
        Val ptrU16 = basePtr is Temporary t ? t with { Type = DataType.UINT16 }
                   : basePtr is Variable v ? v with { Type = DataType.UINT16 }
                   : basePtr;

        Temporary finalAddr = MakeTemp(DataType.UINT16);
        if (elemSize == 1)
        {
            Temporary idxPlusTwo = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Add, index, new Constant(2), idxPlusTwo));
            Emit(new Binary(BinaryOp.Add, ptrU16, idxPlusTwo, finalAddr));
        }
        else
        {
            Temporary scaled = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Mul, index, new Constant(elemSize), scaled));
            Temporary scaledPlusTwo = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Add, scaled, new Constant(2), scaledPlusTwo));
            Emit(new Binary(BinaryOp.Add, ptrU16, scaledPlusTwo, finalAddr));
        }
        return finalAddr;
    }

    // Emits IR for list.append(val). Handles fast path (len < cap) and slow path (realloc).
    private Val EmitListAppend(Variable listVar, Expression valExpr)
    {
        DataType elemDt = listVarElemTypes[listVar.Name];
        int elemSize = elemDt.SizeOf();

        // Load current length (offset 0) and capacity (offset 1)
        Temporary tmpLen = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(listVar, tmpLen));
        Temporary tmpCap = EmitListLoad(listVar, 1, DataType.UINT8);

        string fastLabel = MakeLabel();

        // if len < cap: skip realloc
        Temporary ltCap = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.LessThan, tmpLen, tmpCap, ltCap));
        Emit(new JumpIfNotZero(ltCap, fastLabel));

        // === SLOW PATH: realloc to double capacity ===

        // new_cap = cap * 2
        Temporary newCap = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.Mul, tmpCap, new Constant(2), newCap));

        // new_alloc_size = 2 + new_cap * elemSize
        Temporary newCapScaled = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, newCap, new Constant(elemSize), newCapScaled));
        Temporary newAllocSize = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, newCapScaled, new Constant(2), newAllocSize));

        // Save old pointer, allocate new buffer
        Temporary oldPtr = MakeTemp(DataType.GC_REF);
        Emit(new Copy(listVar, oldPtr));
        Temporary newPtr = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(newAllocSize, newPtr));

        // Write new header
        EmitListStore(newPtr, 0, tmpLen);
        EmitListStore(newPtr, 1, newCap);

        // Copy existing elements byte-by-byte
        // Compute base pointers outside the loop
        Val oldPtrU16 = oldPtr with { Type = DataType.UINT16 };
        Val newPtrU16 = newPtr with { Type = DataType.UINT16 };
        Temporary totalBytes = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, tmpLen, new Constant(elemSize), totalBytes));
        Temporary oldBase = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, oldPtrU16, new Constant(2), oldBase));
        Temporary newBase = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, newPtrU16, new Constant(2), newBase));

        Temporary byteOff = MakeTemp(DataType.UINT16);
        Emit(new Copy(new Constant(0), byteOff));
        string copyLoopLabel = MakeLabel();
        string copyLoopEnd = MakeLabel();
        Emit(new Label(copyLoopLabel));
        Temporary cmpDone = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.GreaterEqual, byteOff, totalBytes, cmpDone));
        Emit(new JumpIfNotZero(cmpDone, copyLoopEnd));
        Temporary srcAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, oldBase, byteOff, srcAddr));
        Temporary dstAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, newBase, byteOff, dstAddr));
        Temporary byteTmp = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(srcAddr, byteTmp));
        Emit(new StoreIndirect(byteTmp, dstAddr));
        Emit(new AugAssign(BinaryOp.Add, byteOff, new Constant(1)));
        Emit(new Jump(copyLoopLabel));
        Emit(new Label(copyLoopEnd));

        // Update listVar → newPtr; shadow stack slot already tracks SRAM addr of listVar
        Emit(new Copy(newPtr, listVar));

        // === FAST PATH: write element at offset 2 + len * elemSize ===
        Emit(new Label(fastLabel));

        Val elemVal = VisitExpression(valExpr);
        Temporary appendAddr = EmitElemAddr(listVar, tmpLen, elemSize);
        Emit(new StoreIndirect(elemVal, appendAddr));

        // length += 1
        Temporary newLen = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.Add, tmpLen, new Constant(1), newLen));
        Emit(new StoreIndirect(newLen, listVar));

        return new NoneVal();
    }

    // -------------------------------------------------------------------------
    // Class hierarchy helpers — MRO resolution and virtual-dispatch gate
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walk the MRO chain starting at <paramref name="cls"/> (no trailing underscore)
    /// and return the first class that directly defines <paramref name="methodName"/>.
    /// Falls back to <paramref name="cls"/> if no defining class is found (safe: linker
    /// will catch a truly-missing symbol).
    /// </summary>
    private string ResolveMROMethod(string cls, string methodName)
    {
        string? current = cls;
        while (current != null)
        {
            if (classDirectMethods.TryGetValue(current, out var dm) && dm.Contains(methodName))
                return current;

            if (classBasePrefixes.TryGetValue(current, out var parentPrefix)
                && !string.IsNullOrEmpty(parentPrefix))
            {
                // parentPrefix has a trailing underscore — strip it for the next lookup.
                current = parentPrefix!.EndsWith("_") ? parentPrefix[..^1] : parentPrefix;
            }
            else
                break;
        }
        return cls;
    }

    /// <summary>
    /// Returns <c>true</c> when a call to <paramref name="methodName"/> on an object of
    /// declared type <paramref name="cls"/> cannot be devirtualized statically.
    ///
    /// Devirtualization is safe (returns false) when ANY of:
    ///   Rule 1 — <paramref name="cls"/> is a leaf class (no known subclasses).
    ///   Rule 3 — no subclass in the entire subtree overrides the method.
    ///
    /// Rule 2 (exact type from instanceClasses) is enforced by the call site: we only
    /// reach this helper when instanceClasses already holds the concrete type, so Rule 2
    /// always applies and this method always returns false for the current ZCA model.
    /// The implementation is kept general for future polymorphic variable support.
    /// </summary>
    private bool IsVirtualDispatch(string cls, string methodName)
    {
        // Rule 1: leaf class.
        if (!classChildren.TryGetValue(cls, out var children) || children.Count == 0)
            return false;

        // Rule 3: no subclass overrides the method.
        if (IsMethodNeverOverridden(cls, methodName))
            return false;

        return true;
    }

    private bool IsMethodNeverOverridden(string cls, string methodName)
    {
        if (!classChildren.TryGetValue(cls, out var children)) return true;
        foreach (var child in children)
        {
            if (classDirectMethods.TryGetValue(child, out var dm) && dm.Contains(methodName))
                return false;
            if (!IsMethodNeverOverridden(child, methodName))
                return false;
        }
        return true;
    }

    // Synthesizes a parameterless ISR wrapper that inline-expands handlerFuncName
    // with the ZCA variable zcaRootKey bound to the handler's first parameter.
    // The synthesized Function is added to pendingZcaSynthFunctions and its name returned.
    private string SynthesizeZcaIsrWrapper(string handlerFuncName, string zcaRootKey)
    {
        if (!zcaHandlerAstNodes.TryGetValue(handlerFuncName, out var entry)) return "";

        var funcDef = entry.Func;
        if (funcDef.Params.Count == 0) return "";

        // Build a collision-free synthesis name
        string baseName = handlerFuncName.Replace('.', '_');
        string zcaSuffix = zcaRootKey.Replace('.', '_');
        string synthName = "_irq_synth_" + baseName + "_" + zcaSuffix;

        // Save compilation state
        var savedInstructions  = currentInstructions;
        var savedFunction      = currentFunction;
        var savedModulePrefix  = currentModulePrefix;
        var savedInlinePrefix  = currentInlinePrefix;
        int savedInlineDepth   = inlineDepth;
        var savedLoopStack     = loopStack;
        var savedInlineStack   = inlineStack;
        int savedLastLine      = lastLine;
        var savedFunctionGlobals = currentFunctionGlobals;

        // Set up fresh compilation context for the wrapper
        currentInstructions   = new List<Instruction>();
        currentFunction       = synthName;
        currentModulePrefix   = entry.Prefix;
        currentInlinePrefix   = synthName + "_s_";
        inlineDepth           = 0;
        loopStack             = new List<LoopLabels>();
        inlineStack           = new List<InlineContext>();
        lastLine              = -1;
        currentFunctionGlobals = new HashSet<string>();

        // Bind handler's first parameter to the ZCA root variable
        string paramName = currentInlinePrefix + funcDef.Params[0].Name;
        variableAliases[paramName] = zcaRootKey;
        if (instanceClasses.TryGetValue(zcaRootKey, out string? cls) && cls != null)
            instanceClasses[paramName] = cls;

        // Propagate ZCA sub-fields (constantVariables, strConstantVariables, instanceClasses)
        string zcaFieldPfx = zcaRootKey + ".";
        foreach (var kv in constantVariables
            .Where(kv => kv.Key.StartsWith(zcaFieldPfx)).ToList())
            constantVariables[paramName + "." + kv.Key[zcaFieldPfx.Length..]] = kv.Value;
        foreach (var kv in strConstantVariables
            .Where(kv => kv.Key.StartsWith(zcaFieldPfx)).ToList())
            strConstantVariables[paramName + "." + kv.Key[zcaFieldPfx.Length..]] = kv.Value;
        foreach (var kv in instanceClasses
            .Where(kv => kv.Key.StartsWith(zcaFieldPfx)).ToList())
            instanceClasses[paramName + "." + kv.Key[zcaFieldPfx.Length..]] = kv.Value;

        // Compile the handler body with ZCA constants in scope
        VisitBlock(funcDef.Body);
        if (currentInstructions.Count == 0 || currentInstructions[^1] is not Return)
            Emit(new Return(new NoneVal()));

        // Build the IR function object
        var wrapperFunc = new Function
        {
            Name         = synthName,
            OriginalName = funcDef.Name,
            ReturnType   = DataType.VOID,
            Body         = new List<Instruction>(currentInstructions),
        };
        pendingZcaSynthFunctions.Add(wrapperFunc);

        // Restore compilation state
        currentInstructions    = savedInstructions;
        currentFunction        = savedFunction;
        currentModulePrefix    = savedModulePrefix;
        currentInlinePrefix    = savedInlinePrefix;
        inlineDepth            = savedInlineDepth;
        loopStack              = savedLoopStack;
        inlineStack            = savedInlineStack;
        lastLine               = savedLastLine;
        currentFunctionGlobals = savedFunctionGlobals;

        return synthName;
    }
}