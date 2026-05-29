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
    private void VisitFor(ForStmt stmt)
    {
        if (stmt.Iterable != null)
        {
            var iter = stmt.Iterable;
            string varKey = currentInlinePrefix + stmt.VarName;

            string? GetStr(Expression e)
            {
                if (e is StringLiteral lit) return lit.Value;
                if (e is not VariableExpr varE) return null;
                var key = currentInlinePrefix + varE.Name;
                for (var depth = 0; depth < 20; depth++)
                {
                    if (key != null && strConstantVariables.TryGetValue(key, out var s)) return s;
                    if (key != null && variableAliases.TryGetValue(key, out var alias)) key = alias;
                    else break;
                }

                return null;
            }

            if (GetStr(iter) is string strOpt)
            {
                foreach (char c in strOpt)
                {
                    constantVariables[varKey] = (int)c;
                    VisitStatement(stmt.Body);
                }

                constantVariables.Remove(varKey);
                return;
            }

            if (iter is ListExpr le)
            {
                foreach (var elem in le.Elements)
                {
                    if (elem is IntegerLiteral il)
                    {
                        constantVariables[varKey] = il.Value;
                        VisitStatement(stmt.Body);
                    }
                    else throw new Exception("for-in list iterable elements must be compile-time integer constants.");
                }

                constantVariables.Remove(varKey);
                return;
            }

            if (iter is CallExpr call && call.Callee is VariableExpr calleeVar)
            {
                if (calleeVar.Name == "range")
                {
                    int? EvalConst(Expression e)
                    {
                        if (e is IntegerLiteral il) return il.Value;
                        if (e is VariableExpr v)
                        {
                            string k = currentInlinePrefix + v.Name;
                            if (constantVariables.TryGetValue(k, out int cv)) return cv;
                        }

                        return null;
                    }

                    int start = 0, stop = 0, step = 1;
                    if (call.Args.Count == 1)
                    {
                        var sv = EvalConst(call.Args[0]);
                        if (!sv.HasValue)
                            throw new Exception("for-in range() argument must be a compile-time constant.");
                        stop = sv.Value;
                    }
                    else if (call.Args.Count >= 2)
                    {
                        var sv = EvalConst(call.Args[0]);
                        var ev = EvalConst(call.Args[1]);
                        if (!sv.HasValue || !ev.HasValue)
                            throw new Exception("for-in range() arguments must be compile-time constants.");
                        start = sv.Value;
                        stop = ev.Value;
                        if (call.Args.Count >= 3)
                        {
                            var stv = EvalConst(call.Args[2]);
                            if (!stv.HasValue)
                                throw new Exception("for-in range() step must be a compile-time constant.");
                            step = stv.Value;
                        }
                    }
                    else throw new Exception("for-in range() requires at least one argument.");

                    if (step == 0) throw new Exception("for-in range() step cannot be zero.");
                    for (int i = start; step > 0 ? i < stop : i > stop; i += step)
                    {
                        constantVariables[varKey] = i;
                        VisitStatement(stmt.Body);
                    }

                    constantVariables.Remove(varKey);
                    return;
                }
                else if (calleeVar.Name == "enumerate" && !string.IsNullOrEmpty(stmt.Var2Name) && call.Args.Count == 1)
                {
                    string idxKey = currentInlinePrefix + stmt.VarName;
                    string valKey = currentInlinePrefix + stmt.Var2Name;
                    Expression inner = call.Args[0];
                    int idx = 0;

                    if (inner is ListExpr lExpr)
                    {
                        foreach (var elem in lExpr.Elements)
                        {
                            if (elem is IntegerLiteral il)
                            {
                                constantVariables[idxKey] = idx++;
                                constantVariables[valKey] = il.Value;
                                VisitStatement(stmt.Body);
                            }
                            else
                                throw new Exception(
                                    "enumerate() list elements must be compile-time integer constants.");
                        }

                        constantVariables.Remove(idxKey);
                        constantVariables.Remove(valKey);
                        return;
                    }

                    if (inner is CallExpr rcall && rcall.Callee is VariableExpr rv && rv.Name == "range")
                    {
                        int? EvalC(Expression e)
                        {
                            if (e is IntegerLiteral il) return il.Value;
                            if (e is VariableExpr v)
                            {
                                string k = currentInlinePrefix + v.Name;
                                if (constantVariables.TryGetValue(k, out int cv)) return cv;
                            }

                            return null;
                        }

                        int rstart = 0, rstop = 0, rstep = 1;
                        if (rcall.Args.Count == 1)
                        {
                            var sv = EvalC(rcall.Args[0]);
                            if (!sv.HasValue)
                                throw new Exception("enumerate(range()) argument must be compile-time constant.");
                            rstop = sv.Value;
                        }
                        else if (rcall.Args.Count >= 2)
                        {
                            var sv = EvalC(rcall.Args[0]);
                            var ev = EvalC(rcall.Args[1]);
                            if (!sv.HasValue || !ev.HasValue)
                                throw new Exception("enumerate(range()) arguments must be compile-time constants.");
                            rstart = sv.Value;
                            rstop = ev.Value;
                            if (rcall.Args.Count >= 3)
                            {
                                var stv = EvalC(rcall.Args[2]);
                                if (!stv.HasValue)
                                    throw new Exception("enumerate(range()) step must be compile-time constant.");
                                rstep = stv.Value;
                            }
                        }

                        for (int rval = rstart; rstep > 0 ? rval < rstop : rval > rstop; rval += rstep)
                        {
                            constantVariables[idxKey] = idx++;
                            constantVariables[valKey] = rval;
                            VisitStatement(stmt.Body);
                        }

                        constantVariables.Remove(idxKey);
                        constantVariables.Remove(valKey);
                        return;
                    }

                    if (inner is VariableExpr vE)
                    {
                        string @base = "";
                        int arrSize = -1;
                        if (!string.IsNullOrEmpty(currentInlinePrefix))
                        {
                            string k = currentInlinePrefix + vE.Name;
                            if (arraySizes.TryGetValue(k, out int s))
                            {
                                arrSize = s;
                                @base = k;
                            }
                        }

                        if (arrSize < 0 && !string.IsNullOrEmpty(currentFunction))
                        {
                            string k = currentFunction + "." + vE.Name;
                            if (arraySizes.TryGetValue(k, out int s))
                            {
                                arrSize = s;
                                @base = k;
                            }
                        }

                        if (arrSize < 0 && arraySizes.TryGetValue(vE.Name, out int s2))
                        {
                            arrSize = s2;
                            @base = vE.Name;
                        }

                        if (arrSize > 0)
                        {
                            DataType elemDt = arrayElemTypes.TryGetValue(@base, out var dt) ? dt : DataType.UINT8;
                            bool useSram = arraysWithVariableIndex.Contains(@base) || moduleSramArrays.Contains(@base);

                            string qualifiedVal;
                            if (!string.IsNullOrEmpty(currentInlinePrefix))
                                qualifiedVal = currentInlinePrefix + stmt.Var2Name;
                            else if (!string.IsNullOrEmpty(currentFunction))
                                qualifiedVal = currentFunction + "." + stmt.Var2Name;
                            else qualifiedVal = stmt.Var2Name;

                            variableTypes[qualifiedVal] = elemDt;
                            for (int k = 0; k < arrSize; ++k)
                            {
                                constantVariables[idxKey] = k;
                                if (useSram)
                                {
                                    var synTarget = new VariableExpr(vE.Name);
                                    var synIndex = new IntegerLiteral(k);
                                    var synIdxExpr = new IndexExpr(synTarget, synIndex);
                                    Val elemVal = VisitIndex(synIdxExpr);
                                    var valVar = new Variable(qualifiedVal, elemDt);
                                    Emit(new Copy(elemVal, valVar));
                                }
                                else
                                {
                                    string elemKey = @base + "__" + k;
                                    bool elemIsZca = instanceClasses.ContainsKey(elemKey) ||
                                                     instanceClasses.Keys.Any(x => x.StartsWith(elemKey + "."));
                                    if (elemIsZca)
                                    {
                                        PropagateCtState(elemKey, valKey);
                                    }
                                    else if (constantVariables.TryGetValue(elemKey, out int cv))
                                    {
                                        constantVariables[valKey] = cv;
                                    }
                                    else
                                    {
                                        var srcVar = new Variable(elemKey, elemDt);
                                        var valVar = new Variable(qualifiedVal, elemDt);
                                        Emit(new Copy(srcVar, valVar));
                                    }
                                }

                                VisitStatement(stmt.Body);
                                CleanCtState(valKey);
                                constantVariables.Remove(valKey);
                            }

                            constantVariables.Remove(idxKey);
                            return;
                        }
                    }

                    throw new Exception(
                        "enumerate() argument must be a constant list literal, range(N), or a fixed-size array.");
                }
                else if (calleeVar.Name == "zip" && !string.IsNullOrEmpty(stmt.Var2Name) && call.Args.Count == 2)
                {
                    string key1 = currentInlinePrefix + stmt.VarName;
                    string key2 = currentInlinePrefix + stmt.Var2Name;
                    Expression arg0 = call.Args[0];
                    Expression arg1 = call.Args[1];

                    List<int> CollectInts(Expression e)
                    {
                        if (e is ListExpr le2)
                        {
                            var vals = new List<int>();
                            foreach (var elem in le2.Elements)
                            {
                                if (elem is IntegerLiteral il)
                                {
                                    vals.Add(il.Value);
                                }
                                else
                                {
                                    // Try resolving as a compile-time constant expression
                                    // (e.g. enum member like Priority.IDLE, or BooleanLiteral).
                                    Val resolved = VisitExpression(elem);
                                    if (resolved is Constant rc)
                                        vals.Add(rc.Value);
                                    else
                                        throw new Exception("zip() list elements must be compile-time integer constants.");
                                }
                            }

                            return vals;
                        }

                        if (e is VariableExpr v)
                        {
                            string @base = "";
                            int arrSize = -1;
                            if (!string.IsNullOrEmpty(currentInlinePrefix))
                            {
                                string k = currentInlinePrefix + v.Name;
                                if (arraySizes.TryGetValue(k, out int s))
                                {
                                    arrSize = s;
                                    @base = k;
                                }
                            }

                            if (arrSize < 0 && !string.IsNullOrEmpty(currentFunction))
                            {
                                string k = currentFunction + "." + v.Name;
                                if (arraySizes.TryGetValue(k, out int s))
                                {
                                    arrSize = s;
                                    @base = k;
                                }
                            }

                            if (arrSize < 0 && arraySizes.TryGetValue(v.Name, out int s2))
                            {
                                arrSize = s2;
                                @base = v.Name;
                            }

                            if (arrSize > 0)
                            {
                                var vals = new List<int>();
                                for (int k = 0; k < arrSize; ++k)
                                {
                                    string elemKey = @base + "__" + k;
                                    if (constantVariables.TryGetValue(elemKey, out int cv)) vals.Add(cv);
                                    else
                                        throw new Exception(
                                            "zip() array elements must be compile-time integer constants.");
                                }

                                return vals;
                            }
                        }

                        throw new Exception("zip() arguments must be constant list literals or constant arrays.");
                    }

                    // Try to interpret a list expression as a list of function references.
                    // Returns null if any element is not a known function name.
                    List<string>? TryCollectFuncRefs(Expression e)
                    {
                        if (e is not ListExpr le3) return null;
                        var names = new List<string>();
                        foreach (var elem in le3.Elements)
                        {
                            if (elem is VariableExpr ve)
                            {
                                string resolved = ResolveCallee(ve.Name);
                                if (functionParams.ContainsKey(resolved) || functionReturnTypes.ContainsKey(resolved)
                                    || inlineFunctions.ContainsKey(resolved))
                                {
                                    names.Add(resolved);
                                    continue;
                                }
                            }
                            return null;
                        }
                        return names;
                    }

                    List<string>? funcRefs0 = TryCollectFuncRefs(arg0);
                    List<string>? funcRefs1 = TryCollectFuncRefs(arg1);

                    if (funcRefs0 != null)
                    {
                        // First list is function references; second must be integer constants.
                        var vals1 = CollectInts(arg1);
                        int len = Math.Min(funcRefs0.Count, vals1.Count);
                        for (int k = 0; k < len; ++k)
                        {
                            loopFunctionAliases[key1] = funcRefs0[k];
                            constantVariables[key2] = vals1[k];
                            VisitStatement(stmt.Body);
                        }
                        loopFunctionAliases.Remove(key1);
                        constantVariables.Remove(key2);
                    }
                    else if (funcRefs1 != null)
                    {
                        // First list is integer constants; second is function references.
                        var vals0 = CollectInts(arg0);
                        int len = Math.Min(vals0.Count, funcRefs1.Count);
                        for (int k = 0; k < len; ++k)
                        {
                            constantVariables[key1] = vals0[k];
                            loopFunctionAliases[key2] = funcRefs1[k];
                            VisitStatement(stmt.Body);
                        }
                        constantVariables.Remove(key1);
                        loopFunctionAliases.Remove(key2);
                    }
                    else
                    {
                        // Both lists are integer constants (original behaviour).
                        var vals0 = CollectInts(arg0);
                        var vals1 = CollectInts(arg1);
                        int len = Math.Min(vals0.Count, vals1.Count);
                        for (int k = 0; k < len; ++k)
                        {
                            constantVariables[key1] = vals0[k];
                            constantVariables[key2] = vals1[k];
                            VisitStatement(stmt.Body);
                        }

                        constantVariables.Remove(key1);
                        constantVariables.Remove(key2);
                    }
                    return;
                }
                else if (calleeVar.Name == "reversed" && call.Args.Count == 1)
                {
                    string valKey = currentInlinePrefix + stmt.VarName;
                    Expression inner = call.Args[0];

                    if (inner is ListExpr le3)
                    {
                        for (int k = le3.Elements.Count - 1; k >= 0; --k)
                        {
                            if (le3.Elements[k] is IntegerLiteral il) constantVariables[valKey] = il.Value;
                            else
                                throw new Exception("reversed() list elements must be compile-time integer constants.");
                            VisitStatement(stmt.Body);
                        }

                        constantVariables.Remove(valKey);
                        return;
                    }

                    if (inner is VariableExpr v)
                    {
                        string @base = "";
                        int arrSize = -1;
                        if (!string.IsNullOrEmpty(currentInlinePrefix))
                        {
                            string k = currentInlinePrefix + v.Name;
                            if (arraySizes.TryGetValue(k, out int s))
                            {
                                arrSize = s;
                                @base = k;
                            }
                        }

                        if (arrSize < 0 && !string.IsNullOrEmpty(currentFunction))
                        {
                            string k = currentFunction + "." + v.Name;
                            if (arraySizes.TryGetValue(k, out int s))
                            {
                                arrSize = s;
                                @base = k;
                            }
                        }

                        if (arrSize < 0 && arraySizes.TryGetValue(v.Name, out int s2))
                        {
                            arrSize = s2;
                            @base = v.Name;
                        }

                        if (arrSize > 0)
                        {
                            DataType elemDt = arrayElemTypes.TryGetValue(@base, out var edt) ? edt : DataType.UINT8;
                            // Use the fully-qualified key so the optimizer's copy-propagation
                            // maps "main.v" correctly when the body resolves the loop variable.
                            string qValKey = !string.IsNullOrEmpty(currentInlinePrefix)
                                ? valKey
                                : (!string.IsNullOrEmpty(currentFunction)
                                    ? currentFunction + "." + stmt.VarName
                                    : valKey);
                            variableTypes[qValKey] = elemDt;
                            for (int k = arrSize - 1; k >= 0; --k)
                            {
                                string elemKey = @base + "__" + k;
                                if (constantVariables.TryGetValue(elemKey, out int cv))
                                    constantVariables[valKey] = cv;
                                else if (instanceClasses.ContainsKey(elemKey) ||
                                         instanceClasses.Keys.Any(x => x.StartsWith(elemKey + ".")))
                                    PropagateCtState(elemKey, qValKey);
                                else
                                    Emit(new Copy(new Variable(elemKey, elemDt), new Variable(qValKey, elemDt)));
                                VisitStatement(stmt.Body);
                                CleanCtState(qValKey);
                                constantVariables.Remove(valKey);
                            }
                            return;
                        }
                    }

                    throw new Exception("reversed() argument must be a constant list literal or a constant array.");
                }
            }

            // for v in x: where x is a list[T] variable → runtime loop with heap load per iteration
            if (iter is VariableExpr listVarExpr)
            {
                string listQ = ResolveListVarQualified(listVarExpr.Name);
                if (!string.IsNullOrEmpty(listQ))
                {
                    DataType elemDt = listVarElemTypes[listQ];
                    Variable listPtr = new Variable(listQ, DataType.GC_REF);

                    // load length
                    Temporary listLen = MakeTemp(DataType.UINT8);
                    Emit(new LoadIndirect(listPtr, listLen));

                    // loop index
                    string idxVarName = string.IsNullOrEmpty(currentInlinePrefix)
                        ? (string.IsNullOrEmpty(currentFunction)
                            ? "__list_i" + tempCounter
                            : currentFunction + ".__list_i" + tempCounter)
                        : currentInlinePrefix + "__list_i" + tempCounter;
                    tempCounter++;
                    Variable idxVar = new Variable(idxVarName, DataType.UINT8);
                    variableTypes[idxVarName] = DataType.UINT8;
                    Emit(new Copy(new Constant(0), idxVar));

                    // loop variable (the element)
                    string elemVarName = string.IsNullOrEmpty(currentInlinePrefix)
                        ? (string.IsNullOrEmpty(currentFunction) ? stmt.VarName : currentFunction + "." + stmt.VarName)
                        : currentInlinePrefix + stmt.VarName;
                    Variable elemVar = new Variable(elemVarName, elemDt);
                    variableTypes[elemVarName] = elemDt;

                    string loopStart = MakeLabel();
                    string loopEnd = MakeLabel();
                    loopStack.Add(new LoopLabels { ContinueLabel = loopStart, BreakLabel = loopEnd });

                    Emit(new Label(loopStart));
                    Temporary cmpTmp = MakeTemp(DataType.UINT8);
                    Emit(new Binary(PyMCU.IR.BinaryOp.GreaterEqual, idxVar, listLen, cmpTmp));
                    Emit(new JumpIfNotZero(cmpTmp, loopEnd));

                    Temporary elemAddr = EmitElemAddr(listPtr, idxVar, elemDt.SizeOf());
                    Temporary elemTmp = MakeTemp(elemDt);
                    Emit(new LoadIndirect(elemAddr, elemTmp));
                    Emit(new Copy(elemTmp, elemVar));

                    VisitStatement(stmt.Body);

                    Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, idxVar, new Constant(1)));
                    Emit(new Jump(loopStart));
                    Emit(new Label(loopEnd));
                    loopStack.RemoveAt(loopStack.Count - 1);
                    return;
                }
            }

            // for v in ct_array: — unroll over compile-time array (scalars or ZCA instances)
            if (iter is VariableExpr forVarExpr2)
            {
                string forBase = "";
                int forSize = -1;
                if (!string.IsNullOrEmpty(currentInlinePrefix))
                {
                    string fk = currentInlinePrefix + forVarExpr2.Name;
                    if (arraySizes.TryGetValue(fk, out int fs)) { forSize = fs; forBase = fk; }
                }
                if (forSize < 0 && !string.IsNullOrEmpty(currentFunction))
                {
                    string fk = currentFunction + "." + forVarExpr2.Name;
                    if (arraySizes.TryGetValue(fk, out int fs)) { forSize = fs; forBase = fk; }
                }
                if (forSize < 0 && arraySizes.TryGetValue(forVarExpr2.Name, out int fs2))
                { forSize = fs2; forBase = forVarExpr2.Name; }

                if (forSize > 0)
                {
                    string forVarKey = currentInlinePrefix + stmt.VarName;
                    DataType elemDt2 = arrayElemTypes.TryGetValue(forBase, out var dt3) ? dt3 : DataType.UINT8;
                    variableTypes[forVarKey] = elemDt2;

                    for (int fk = 0; fk < forSize; fk++)
                    {
                        string elemKey2 = forBase + "__" + fk;
                        bool isZca = instanceClasses.ContainsKey(elemKey2) ||
                                     instanceClasses.Keys.Any(x => x.StartsWith(elemKey2 + "."));
                        if (isZca)
                            PropagateCtState(elemKey2, forVarKey);
                        else if (constantVariables.TryGetValue(elemKey2, out int cv2))
                            constantVariables[forVarKey] = cv2;
                        else
                            Emit(new Copy(new Variable(elemKey2, elemDt2), new Variable(forVarKey, elemDt2)));

                        VisitStatement(stmt.Body);

                        CleanCtState(forVarKey);
                        constantVariables.Remove(forVarKey);
                    }
                    return;
                }
            }

            throw new Exception(
                "for-in loop iterable must be a compile-time string constant, a constant list literal [v0, v1, ...], range(N), enumerate(list/range), zip(a, b), or reversed(iterable). Use 'const[str]' type annotation for string parameters.");
        }

        Val startVal = stmt.RangeStart != null ? VisitExpression(stmt.RangeStart) : new Constant(0);
        Val stopVal = VisitExpression(stmt.RangeStop!);
        Val stepVal = stmt.RangeStep != null ? VisitExpression(stmt.RangeStep) : new Constant(1);

        string varName = string.IsNullOrEmpty(currentInlinePrefix) ? stmt.VarName : currentInlinePrefix + stmt.VarName;
        var loopVar = new Variable(varName, DataType.UINT8);
        Emit(new Copy(startVal, loopVar));

        string startLabel = MakeLabel();
        string endLabel = MakeLabel();
        loopStack.Add(new LoopLabels { ContinueLabel = startLabel, BreakLabel = endLabel });

        Emit(new Label(startLabel));
        Emit(new JumpIfGreaterOrEqual(loopVar, stopVal, endLabel));

        VisitStatement(stmt.Body);

        Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, loopVar, stepVal));
        Emit(new Jump(startLabel));
        Emit(new Label(endLabel));
        loopStack.RemoveAt(loopStack.Count - 1);
    }

    private void VisitWith(WithStmt stmt)
    {
        if (stmt.ContextExpr is VariableExpr varExpr)
        {
            string objName = varExpr.Name;

            if (!string.IsNullOrEmpty(stmt.AsName))
            {
                string qualified = string.IsNullOrEmpty(currentFunction)
                    ? stmt.AsName
                    : currentFunction + "." + stmt.AsName;
                string qualifiedObj = string.IsNullOrEmpty(currentFunction) ? objName : currentFunction + "." + objName;
                variableAliases[qualified] = qualifiedObj;
            }

            var enterCallee = new MemberAccessExpr(new VariableExpr(objName), "__enter__");
            var enterCall = new CallExpr(enterCallee, new List<Expression>());
            VisitExpression(enterCall);

            VisitStatement(stmt.Body);

            var exitCallee = new MemberAccessExpr(new VariableExpr(objName), "__exit__");
            var exitCall = new CallExpr(exitCallee, new List<Expression>());
            VisitExpression(exitCall);
        }
        else
        {
            VisitStatement(stmt.Body);
        }
    }

    private void VisitAssert(AssertStmt stmt)
    {
        try
        {
            int val = EvaluateConstantExpr(stmt.Condition);
            if (val == 0)
            {
                throw new Exception("AssertionError" + (string.IsNullOrEmpty(stmt.Message) ? "" : ": " + stmt.Message));
            }
        }
        catch (Exception e)
        {
            if (e.Message.StartsWith("AssertionError")) throw;
        }
    }
}