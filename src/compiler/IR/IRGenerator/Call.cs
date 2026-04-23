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
                    if (instanceClasses.TryGetValue(vObj.Name, out string clsC))
                    {
                        callee = clsC + "_" + memC.Member;
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
                    if (argVal is Constant c) SetConst(paramKey, c.Value);
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
                    KillConst(pk);
                    variableTypes.Remove(pk);
                }

                return resultL;
            }
        }

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

            if (v is FloatConstant fc)
                return new Constant((int)fc.Value);

            Temporary dst = MakeTemp(dstType);
            Emit(new Copy(v, dst));
            return dst;
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

            // Resolve uart_write_str inline function for string output
            string writeStrFn = ResolveCallee("uart_write_str");
            if (writeStrFn == "uart_write_str")
            {
                foreach (var fnName in inlineFunctions.Keys)
                {
                    if (fnName.EndsWith("_uart_write_str"))
                    {
                        writeStrFn = fnName;
                        break;
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
                if (TryGetConst(key, out int cv) && cv == 0) return new NoneVal();

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

            foreach (var arg in expr.Args)
            {
                if (arg is KeywordArgExpr kw)
                {
                    string savedOuterPct = pendingConstructorTarget;
                    pendingConstructorTarget = "";
                    kwArgValues[kw.Key] = VisitExpression(kw.Value);
                    if (kw.Value is StringLiteral s) rawKwStrArgs[kw.Key] = s.Value;
                    if (string.IsNullOrEmpty(pendingConstructorTarget)) pendingConstructorTarget = savedOuterPct;
                }
                else
                {
                    rawStrArgs.Add(arg as StringLiteral);
                    string savedOuterPct = pendingConstructorTarget;
                    pendingConstructorTarget = "";
                    argValues.Add(VisitExpression(arg));
                    if (string.IsNullOrEmpty(pendingConstructorTarget)) pendingConstructorTarget = savedOuterPct;
                }
            }

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

                // PEP 3102: keyword-only params must not be bound positionally.
                if (func.Params[paramIdx].IsKeywordOnly)
                    throw new Exception(
                        $"TypeError: '{func.Params[paramIdx].Name}' is a keyword-only parameter and must be passed as a keyword argument");

                string paramName = currentInlinePrefix + func.Params[paramIdx].Name;
                boundParams.Add(paramIdx);

                if (argValues[i] is Variable vArg)
                {
                    if (func.Params[paramIdx].Type == "const[str]")
                    {
                        string? strVal = ResolveStrConstant(vArg.Name);
                        if (strVal != null)
                        {
                            strConstantVariables[paramName] = strVal;
                            KillConst(paramName);
                            variableAliases.Remove(paramName);
                            continue;
                        }
                    }

                    if (floatConstantVariables.TryGetValue(vArg.Name, out double fv))
                    {
                        floatConstantVariables[paramName] = fv;
                        KillConst(paramName);
                        strConstantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                        continue;
                    }

                    variableAliases[paramName] = vArg.Name;
                    KillConst(paramName);
                    strConstantVariables.Remove(paramName);
                    continue;
                }

                if (argValues[i] is Temporary tArg)
                {
                    // A Temporary can carry a compile-time string or numeric constant
                    // when it is the result of a DCE'd @inline function (e.g.,
                    // _arduino_pin_name(13) → "PB5").  Without this block the value
                    // would fall through to the runtime Copy, losing the constant.
                    string? tStr = ResolveStrConstant(tArg.Name);
                    if (tStr == null && TryGetConst(tArg.Name, out int tId))
                        stringIdToStr.TryGetValue(tId, out tStr);
                    if (tStr != null)
                    {
                        strConstantVariables[paramName] = tStr;
                        KillConst(paramName);
                        variableAliases.Remove(paramName);
                        continue;
                    }
                    if (TryGetConst(tArg.Name, out int tNum))
                    {
                        SetConst(paramName, tNum);
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
                    SetConst(paramName, cArg2.Value);
                    continue;
                }

                if (argValues[i] is FloatConstant fcArg)
                {
                    floatConstantVariables[paramName] = fcArg.Value;
                    KillConst(paramName);
                    strConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    continue;
                }

                if (argValues[i] is Constant cArg3)
                {
                    SetConst(paramName, cArg3.Value);
                    continue;
                }

                if (argValues[i] is MemoryAddress mArg)
                {
                    constantAddressVariables[paramName] = mArg.Address;
                    constantAddressVariables.Remove(paramName + "_type");
                    continue;
                }

                KillConst(paramName);
                strConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                DataType paramType = DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
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
                                SetConst(paramName, ckw.Value);
                            }
                        }
                        else if (kvp.Value is Constant ckw2) SetConst(paramName, ckw2.Value);
                        else
                        {
                            DataType paramType = DataTypeExtensions.StringToDataType(func.Params[pi].Type);
                            Emit(new Copy(kvp.Value, new Variable(paramName, paramType)));
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
                    if (func.Params[i].DefaultValue is NoneExpr)
                    {
                        DataType pdt = DataTypeExtensions.StringToDataType(func.Params[i].Type);
                        if (pdt != DataType.VOID && pdt != DataType.UNKNOWN)
                            throw new Exception(
                                $"TypeError: cannot use None as default for '{func.Params[i].Type}' parameter '{func.Params[i].Name}'; use a sentinel constant (e.g. 0xFF) instead");
                    }

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
                        SetConst(paramName, cdf.Value);
                        continue;
                    }

                    if (defaultVal is Constant cdf2) SetConst(paramName, cdf2.Value);
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

        var argValuesL = new List<Val>();
        foreach (var arg in expr.Args) argValuesL.Add(VisitExpression(arg));

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
                Emit(new Copy(argValuesL[i], new Variable(paramVarName, ptype)));
            }
        }

        if (callee == "pull") callee = "__pio_pull";
        else if (callee == "push") callee = "__pio_push";
        else if (callee == "out") callee = "__pio_out";
        else if (callee == "in_") callee = "__pio_in";
        else if (callee == "wait") callee = "__pio_wait";

        var isPioIntrinsic = callee.StartsWith("__pio_") || callee == "delay";

        if (isPioIntrinsic || (functionReturnTypes.TryGetValue(callee, out string? rType) &&
                               (rType == "void" || rType == "None")))
        {
            Emit(new Call(callee, argValuesL, new NoneVal()));
            return new NoneVal();
        }

        Temporary dstC = MakeTemp();
        Emit(new Call(callee, argValuesL, dstC));
        return dstC;
    }
}