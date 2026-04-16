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

public partial class IRGenerator
{
    private void VisitAssign(AssignStmt stmt)
    {
        if (stmt.Target is IndexExpr indexExpr)
        {
            if (indexExpr.Target is VariableExpr ve)
            {
                string qualified = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
                if (!arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(ve.Name))
                    qualified = ve.Name;

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
                        if (!(indexExpr.Index is IntegerLiteral c))
                            throw new Exception("Array subscript must be a compile-time constant");
                        string elemName = qualified + "__" + c.Value;
                        Val srcVal = VisitExpression(stmt.Value);
                        Emit(new Copy(srcVal, new Variable(elemName, arrayElemTypes[qualified])));
                    }

                    return;
                }
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
                        Val srcVal = VisitExpression(stmt.Value);
                        EmitDunderCall(selfName, cls, funcKey, new List<Val> { idxVal, srcVal });
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
                        string mangled = objVar.Name.Replace('.', '_');
                        resolvedClass = mangled + "_" + calleeMem.Member;
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
                    instanceClasses[qualifiedName] = resolvedClass;
                    pendingConstructorTarget = qualifiedName;
                    virtualInstances.Add(qualifiedName);
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
                            switch (argVal)
                            {
                                case Constant c:
                                    constantVariables[paramName] = c.Value;
                                    break;
                                case Variable vv:
                                    variableAliases[paramName] = vv.Name;
                                    break;
                                case Temporary tt:
                                    variableAliases[paramName] = tt.Name;
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
                        string qualifiedName = currentFunction + "." + varExpr.Name;
                        DataType type = DataType.UINT8;
                        if (variableTypes.TryGetValue(qualifiedName, out var t)) type = t;
                        else
                        {
                            if (value is Temporary tmp) type = tmp.Type;
                            else if (value is Variable vv) type = vv.Type;
                            variableTypes[qualifiedName] = type;
                        }

                        target = new Variable(qualifiedName, type);
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
                        if (value is Constant constVal)
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
                        if (value is Constant constVal32)
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
                        constantVariables[flattenedName] = c.Value;
                    }
                    else if (stringIdToStr.TryGetValue(c.Value, out var value1))
                    {
                        constantVariables[flattenedName] = c.Value;
                        strConstantVariables[flattenedName] = value1;
                        return;
                    }
                }

                var folded = value switch
                {
                    Temporary t4 => TryTempName(t4.Name),
                    Variable v4 => TryTempName(v4.Name),
                    _ => false
                };
                if (folded) return;

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

    private void VisitVarDecl(VarDecl stmt)
    {
        if (stmt.VarType == "bytearray")
        {
            int count = 0;
            var initVals = new List<int>();

            if (stmt.Init != null)
            {
                if (stmt.Init is CallExpr call && call.Callee is VariableExpr callee && callee.Name == "bytearray" &&
                    call.Args.Count > 0)
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

    private void VisitAnnAssign(AnnAssign stmt)
    {
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
            arraySizes[qualified] = count;
            arrayElemTypes[qualified] = DataType.UINT8;
            variableTypes[qualified] = DataType.UINT8;
            arraysWithVariableIndex.Add(qualified);

            for (int k = 0; k < count; ++k)
                Emit(new ArrayStore(qualified, new Constant(k), new Constant(initVals[k]), DataType.UINT8, count));
            return;
        }

        int bracket = stmt.Annotation.IndexOf('[');
        int close = stmt.Annotation.LastIndexOf(']');
        if (bracket != -1 && close != -1 && close == stmt.Annotation.Length - 1 && close > bracket + 1)
        {
            string inner = stmt.Annotation.Substring(bracket + 1, close - bracket - 1);
            if (!string.IsNullOrEmpty(inner) && inner.All(char.IsDigit))
            {
                int count = int.Parse(inner);
                DataType elemDt = DataTypeExtensions.StringToDataType(stmt.Annotation.Substring(0, bracket));
                string qualified = string.IsNullOrEmpty(currentFunction)
                    ? stmt.Target
                    : currentFunction + "." + stmt.Target;
                arraySizes[qualified] = count;
                arrayElemTypes[qualified] = elemDt;
                variableTypes[qualified] = elemDt;

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

        string qualified2 = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + stmt.Target
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.Target : stmt.Target);
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
            }
        }
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