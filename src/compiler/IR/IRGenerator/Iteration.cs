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
                                    if (constantVariables.TryGetValue(elemKey, out int cv))
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
                                if (elem is IntegerLiteral il) vals.Add(il.Value);
                                else throw new Exception("zip() list elements must be compile-time integer constants.");
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
                            for (int k = arrSize - 1; k >= 0; --k)
                            {
                                string elemKey = @base + "__" + k;
                                if (constantVariables.TryGetValue(elemKey, out int cv)) constantVariables[valKey] = cv;
                                else
                                    throw new Exception(
                                        "reversed() array elements must be compile-time integer constants.");
                                VisitStatement(stmt.Body);
                            }

                            constantVariables.Remove(valKey);
                            return;
                        }
                    }

                    throw new Exception("reversed() argument must be a constant list literal or a constant array.");
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