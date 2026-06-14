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
    // Resolves the fixed-array size of `name` by following the variableAliases
    // chain from the current inline prefix. This lets a `for x in param` loop
    // find the array length when a (possibly uninitialised) runtime array is
    // passed into an @inline function -- the size is registered under the
    // caller's qualified name, not the parameter name.
    private int ResolveAliasedArraySize(string name, out string baseKey)
    {
        baseKey = "";
        string key = currentInlinePrefix + name;
        for (int d = 0; d < 20; d++)
        {
            if (variableAliases.TryGetValue(key, out var nxt)) key = nxt;
            else break;
            if (arraySizes.TryGetValue(key, out int s)) { baseKey = key; return s; }
        }
        return -1;
    }

    // `for c in <const[str]>` unrolls at or below this length (each char a compile-time
    // constant); longer strings emit a runtime loop over a flash table instead so a heavy
    // body is not duplicated per character.
    private const int StringForLoopUnrollLimit = 8;

    // True if `s` has a break/continue that targets the *enclosing* loop — i.e. one not nested
    // inside its own for/while (which owns its break/continue). A compile-time-unrolled loop
    // must, only when this holds, bracket each iteration with a continue label and share a break
    // label so those statements have somewhere to jump; when it does not, the plain unroll is
    // kept so per-iteration constant folding is not split across label boundaries.
    private static bool LoopBodyHasBreakOrContinue(Statement? s)
    {
        switch (s)
        {
            case null: return false;
            case BreakStmt:
            case ContinueStmt: return true;
            case ForStmt:
            case WhileStmt: return false;            // nested loop owns its break/continue
            case Block b: return b.Statements.Any(LoopBodyHasBreakOrContinue);
            case IfStmt i:
                return LoopBodyHasBreakOrContinue(i.ThenBranch)
                       || i.ElifBranches.Any(e => LoopBodyHasBreakOrContinue(e.Body))
                       || LoopBodyHasBreakOrContinue(i.ElseBranch);
            case MatchStmt m: return m.Branches.Any(br => LoopBodyHasBreakOrContinue(br.Body));
            case WithStmt w: return LoopBodyHasBreakOrContinue(w.Body);
            case TryStmt t:
                return t.Body.Any(LoopBodyHasBreakOrContinue)
                       || t.Handlers.Any(h => h.Handler.Any(LoopBodyHasBreakOrContinue))
                       || (t.Finally?.Any(LoopBodyHasBreakOrContinue) ?? false);
            default: return false;
        }
    }

    private void VisitFor(ForStmt stmt)
    {
        if (stmt.Iterable != null)
        {
            var iter = stmt.Iterable;
            // Qualify like the body resolves variable references (func-scoped names get the
            // `func.` prefix when not inline-expanded), so a constant the unrolled loop binds to
            // the loop variable is found when the body reads it. The inline-only prefix left a
            // top-level loop variable bare while the body read "func.<name>".
            string varKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + stmt.VarName
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);

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
                // Short strings unroll (each char a compile-time constant) — smallest code
                // and preserves bodies that need a constant loop variable. Longer strings
                // emit a RUNTIME loop that reads each byte from a flash table, so the body
                // is generated ONCE instead of N times. This keeps idiomatic `for c in s`
                // from exploding when the body is heavy (e.g. an I2C/SPI write per char).
                if (strOpt.Length > StringForLoopUnrollLimit)
                {
                    string sFlash = InternStringAsFlash(strOpt);
                    var sCharVar = new Variable(varKey, DataType.UINT8);
                    var sIdxVar = new Variable(varKey + "__si", DataType.UINT8);

                    constantVariables.Remove(varKey);
                    variableTypes[varKey] = DataType.UINT8;

                    Emit(new Copy(new Constant(0), sIdxVar));
                    string sStart = MakeLabel();
                    string sCont = MakeLabel();
                    string sEnd = MakeLabel();
                    // continue advances the index then re-tests (else the loop spins on one char).
                    loopStack.Add(new LoopLabels { ContinueLabel = sCont, BreakLabel = sEnd });

                    Emit(new Label(sStart));
                    Emit(new JumpIfGreaterOrEqual(sIdxVar, new Constant(strOpt.Length), sEnd));
                    Emit(new ArrayLoadFlash(sFlash, sIdxVar, sCharVar));

                    VisitStatement(stmt.Body);

                    Emit(new Label(sCont));
                    Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, sIdxVar, new Constant(1)));
                    Emit(new Jump(sStart));
                    Emit(new Label(sEnd));
                    loopStack.RemoveAt(loopStack.Count - 1);
                    return;
                }

                foreach (char c in strOpt)
                {
                    constantVariables[varKey] = (int)c;
                    VisitStatement(stmt.Body);
                }

                constantVariables.Remove(varKey);
                return;
            }

            // A parameter bound to a bytes/list literal argument (e.g. the `buf` of
            // uart.write(b"Hi")) iterates exactly like a direct list literal.
            ListExpr? GetListParam(Expression e)
            {
                if (e is not VariableExpr varE) return null;
                return ResolveListLiteralParam(varE.Name);
            }

            if (GetListParam(iter) is ListExpr boundList)
            {
                foreach (var elem in boundList.Elements)
                {
                    if (elem is IntegerLiteral il)
                    {
                        constantVariables[varKey] = il.Value;
                        VisitStatement(stmt.Body);
                    }
                    else throw UserError("for-in list iterable elements must be compile-time integer constants.");
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
                    else throw UserError("for-in list iterable elements must be compile-time integer constants.");
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
                            throw UserError("for-in range() argument must be a compile-time constant.");
                        stop = sv.Value;
                    }
                    else if (call.Args.Count >= 2)
                    {
                        var sv = EvalConst(call.Args[0]);
                        var ev = EvalConst(call.Args[1]);
                        if (!sv.HasValue || !ev.HasValue)
                            throw UserError("for-in range() arguments must be compile-time constants.");
                        start = sv.Value;
                        stop = ev.Value;
                        if (call.Args.Count >= 3)
                        {
                            var stv = EvalConst(call.Args[2]);
                            if (!stv.HasValue)
                                throw UserError("for-in range() step must be a compile-time constant.");
                            step = stv.Value;
                        }
                    }
                    else throw UserError("for-in range() requires at least one argument.");

                    if (step == 0) throw UserError("for-in range() step cannot be zero.");
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
                                throw UserError(
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
                                throw UserError("enumerate(range()) argument must be compile-time constant.");
                            rstop = sv.Value;
                        }
                        else if (rcall.Args.Count >= 2)
                        {
                            var sv = EvalC(rcall.Args[0]);
                            var ev = EvalC(rcall.Args[1]);
                            if (!sv.HasValue || !ev.HasValue)
                                throw UserError("enumerate(range()) arguments must be compile-time constants.");
                            rstart = sv.Value;
                            rstop = ev.Value;
                            if (rcall.Args.Count >= 3)
                            {
                                var stv = EvalC(rcall.Args[2]);
                                if (!stv.HasValue)
                                    throw UserError("enumerate(range()) step must be compile-time constant.");
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

                        if (arrSize < 0)
                        {
                            int s3a = ResolveAliasedArraySize(vE.Name, out var b3a);
                            if (s3a > 0) { arrSize = s3a; @base = b3a; }
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
                            bool enBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                            string enBreakLabel = enBrk ? MakeLabel() : "";
                            for (int k = 0; k < arrSize; ++k)
                            {
                                string enContLabel = enBrk ? MakeLabel() : "";
                                if (enBrk)
                                    loopStack.Add(new LoopLabels { ContinueLabel = enContLabel, BreakLabel = enBreakLabel });
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
                                        // Bind to the function-qualified value-var name the loop body
                                        // resolves to (qualifiedVal), not the inline-only valKey, so a
                                        // `pin.value = ...` setter inside a def sees the ZCA state.
                                        PropagateCtState(elemKey, qualifiedVal);
                                    }
                                    else if (constantVariables.TryGetValue(elemKey, out int cv))
                                    {
                                        constantVariables[qualifiedVal] = cv;
                                    }
                                    else
                                    {
                                        var srcVar = new Variable(elemKey, elemDt);
                                        var valVar = new Variable(qualifiedVal, elemDt);
                                        Emit(new Copy(srcVar, valVar));
                                    }
                                }

                                VisitStatement(stmt.Body);
                                if (enBrk) { loopStack.RemoveAt(loopStack.Count - 1); Emit(new Label(enContLabel)); }
                                CleanCtState(qualifiedVal);
                                constantVariables.Remove(qualifiedVal);
                            }
                            if (enBrk) Emit(new Label(enBreakLabel));

                            constantVariables.Remove(idxKey);
                            return;
                        }
                    }

                    throw UserError(
                        "enumerate() argument must be a constant list literal, range(N), or a fixed-size array.");
                }
                else if (calleeVar.Name == "zip" && !string.IsNullOrEmpty(stmt.Var2Name) && call.Args.Count == 2)
                {
                    string key1 = currentInlinePrefix + stmt.VarName;
                    string key2 = currentInlinePrefix + stmt.Var2Name;
                    Expression arg0 = call.Args[0];
                    Expression arg1 = call.Args[1];

                    // zip(a, b) over two fixed arrays whose elements may be runtime values:
                    // iterate element-wise, binding each loop variable to the element (Copy from
                    // arr__k, or ArrayLoad for SRAM arrays). The all-constant fast paths below
                    // still apply to list literals / function-reference lists.
                    (string Base, int Size, DataType Elem)? ResolveArr(Expression e)
                    {
                        if (e is not VariableExpr ve) return null;
                        foreach (var k in new[]
                        {
                            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + ve.Name,
                            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + ve.Name,
                            ve.Name,
                        })
                        {
                            if (k != null && arraySizes.TryGetValue(k, out int sz))
                                return (k, sz, arrayElemTypes.TryGetValue(k, out var dt) ? dt : DataType.UINT8);
                        }
                        int sz2 = ResolveAliasedArraySize(ve.Name, out var b2);
                        if (sz2 > 0) return (b2, sz2, arrayElemTypes.TryGetValue(b2, out var dt2) ? dt2 : DataType.UINT8);
                        return null;
                    }

                    if (ResolveArr(arg0) is var ra && ra is not null
                        && ResolveArr(arg1) is var rb && rb is not null)
                    {
                        string qk1 = !string.IsNullOrEmpty(currentInlinePrefix)
                            ? key1 : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);
                        string qk2 = !string.IsNullOrEmpty(currentInlinePrefix)
                            ? key2 : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.Var2Name : stmt.Var2Name);
                        variableTypes[qk1] = ra.Value.Elem;
                        variableTypes[qk2] = rb.Value.Elem;
                        bool sram0 = arraysWithVariableIndex.Contains(ra.Value.Base) || moduleSramArrays.Contains(ra.Value.Base);
                        bool sram1 = arraysWithVariableIndex.Contains(rb.Value.Base) || moduleSramArrays.Contains(rb.Value.Base);
                        int zlen = Math.Min(ra.Value.Size, rb.Value.Size);
                        bool zbrk = LoopBodyHasBreakOrContinue(stmt.Body);
                        string zBreak = zbrk ? MakeLabel() : "";

                        void Bind(string qk, (string Base, int Size, DataType Elem) arr, bool sram, int k)
                        {
                            string ek = arr.Base + "__" + k;
                            if (sram)
                            {
                                Temporary tmp = MakeTemp(arr.Elem);
                                Emit(new ArrayLoad(arr.Base, new Constant(k), tmp, arr.Elem, arr.Size));
                                Emit(new Copy(tmp, new Variable(qk, arr.Elem)));
                            }
                            else if (constantVariables.TryGetValue(ek, out int cv)) constantVariables[qk] = cv;
                            else Emit(new Copy(new Variable(ek, arr.Elem), new Variable(qk, arr.Elem)));
                        }

                        for (int k = 0; k < zlen; ++k)
                        {
                            string zCont = zbrk ? MakeLabel() : "";
                            if (zbrk) loopStack.Add(new LoopLabels { ContinueLabel = zCont, BreakLabel = zBreak });
                            Bind(qk1, ra.Value, sram0, k);
                            Bind(qk2, rb.Value, sram1, k);
                            VisitStatement(stmt.Body);
                            if (zbrk) { loopStack.RemoveAt(loopStack.Count - 1); Emit(new Label(zCont)); }
                            constantVariables.Remove(qk1);
                            constantVariables.Remove(qk2);
                        }
                        if (zbrk) Emit(new Label(zBreak));
                        return;
                    }

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
                                        throw UserError("zip() list elements must be compile-time integer constants.");
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
                                        throw UserError(
                                            "zip() array elements must be compile-time integer constants.");
                                }

                                return vals;
                            }
                        }

                        throw UserError("zip() arguments must be constant list literals or constant arrays.");
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
                                throw UserError("reversed() list elements must be compile-time integer constants.");
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

                        if (arrSize < 0)
                        {
                            int s3r = ResolveAliasedArraySize(v.Name, out var b3r);
                            if (s3r > 0) { arrSize = s3r; @base = b3r; }
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
                            bool rvBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                            string rvBreakLabel = rvBrk ? MakeLabel() : "";
                            for (int k = arrSize - 1; k >= 0; --k)
                            {
                                string rvContLabel = rvBrk ? MakeLabel() : "";
                                if (rvBrk)
                                    loopStack.Add(new LoopLabels { ContinueLabel = rvContLabel, BreakLabel = rvBreakLabel });
                                string elemKey = @base + "__" + k;
                                if (constantVariables.TryGetValue(elemKey, out int cv))
                                    constantVariables[valKey] = cv;
                                else if (instanceClasses.ContainsKey(elemKey) ||
                                         instanceClasses.Keys.Any(x => x.StartsWith(elemKey + ".")))
                                    PropagateCtState(elemKey, qValKey);
                                else
                                    Emit(new Copy(new Variable(elemKey, elemDt), new Variable(qValKey, elemDt)));
                                VisitStatement(stmt.Body);
                                if (rvBrk) { loopStack.RemoveAt(loopStack.Count - 1); Emit(new Label(rvContLabel)); }
                                CleanCtState(qValKey);
                                constantVariables.Remove(valKey);
                            }
                            if (rvBrk) Emit(new Label(rvBreakLabel));
                            return;
                        }
                    }

                    throw UserError("reversed() argument must be a constant list literal or a constant array.");
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
                    string loopCont = MakeLabel();
                    string loopEnd = MakeLabel();
                    // continue advances the index then re-tests (else the loop spins on one elem).
                    loopStack.Add(new LoopLabels { ContinueLabel = loopCont, BreakLabel = loopEnd });

                    Emit(new Label(loopStart));
                    Temporary cmpTmp = MakeTemp(DataType.UINT8);
                    Emit(new Binary(PyMCU.IR.BinaryOp.GreaterEqual, idxVar, listLen, cmpTmp));
                    Emit(new JumpIfNotZero(cmpTmp, loopEnd));

                    Temporary elemAddr = EmitElemAddr(listPtr, idxVar, elemDt.SizeOf());
                    Temporary elemTmp = MakeTemp(elemDt);
                    Emit(new LoadIndirect(elemAddr, elemTmp));
                    Emit(new Copy(elemTmp, elemVar));

                    VisitStatement(stmt.Body);

                    Emit(new Label(loopCont));
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
                if (forSize < 0)
                {
                    int fs3 = ResolveAliasedArraySize(forVarExpr2.Name, out var fb3);
                    if (fs3 > 0) { forSize = fs3; forBase = fb3; }
                }

                if (forSize > 0)
                {
                    // Qualify the loop variable the same way ResolveBinding does for a bare name,
                    // so the loop body's references (e.g. a `pin.direction = ...` property setter)
                    // resolve to the same key the loop binds -- including the currentFunction prefix
                    // when iterating inside a def. Without this, ZCA per-element state registered on
                    // the loop var is invisible to the body inside a function.
                    string forVarKey = !string.IsNullOrEmpty(currentInlinePrefix)
                        ? currentInlinePrefix + stmt.VarName
                        : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);
                    DataType elemDt2 = arrayElemTypes.TryGetValue(forBase, out var dt3) ? dt3 : DataType.UINT8;
                    variableTypes[forVarKey] = elemDt2;

                    // Only bracket iterations with labels when the body actually uses break/continue
                    // (else keep the plain unroll so constant folding is not split by labels).
                    bool forBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                    string forBreakLabel = forBrk ? MakeLabel() : "";

                    for (int fk = 0; fk < forSize; fk++)
                    {
                        string forContLabel = forBrk ? MakeLabel() : "";
                        if (forBrk)
                            loopStack.Add(new LoopLabels { ContinueLabel = forContLabel, BreakLabel = forBreakLabel });

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

                        if (forBrk)
                        {
                            loopStack.RemoveAt(loopStack.Count - 1);
                            Emit(new Label(forContLabel));   // continue lands here: end of this iteration
                        }
                        CleanCtState(forVarKey);
                        constantVariables.Remove(forVarKey);
                    }
                    if (forBrk) Emit(new Label(forBreakLabel));
                    return;
                }
            }

            // for v in arr[lo:hi:step]: — unroll over a fixed-array slice (constant bounds).
            if (iter is IndexExpr { Target: VariableExpr sliceVar, Index: SliceExpr slc })
            {
                string slBase = "";
                int slSize = -1;
                if (!string.IsNullOrEmpty(currentInlinePrefix)
                    && arraySizes.TryGetValue(currentInlinePrefix + sliceVar.Name, out int ss0))
                { slSize = ss0; slBase = currentInlinePrefix + sliceVar.Name; }
                if (slSize < 0 && !string.IsNullOrEmpty(currentFunction)
                    && arraySizes.TryGetValue(currentFunction + "." + sliceVar.Name, out int ss1))
                { slSize = ss1; slBase = currentFunction + "." + sliceVar.Name; }
                if (slSize < 0 && arraySizes.TryGetValue(sliceVar.Name, out int ss2))
                { slSize = ss2; slBase = sliceVar.Name; }
                if (slSize < 0)
                {
                    int ss3 = ResolveAliasedArraySize(sliceVar.Name, out var sb3);
                    if (ss3 > 0) { slSize = ss3; slBase = sb3; }
                }

                if (slSize > 0)
                {
                    int start = slc.Start != null ? EvaluateConstantExpr(slc.Start) : 0;
                    int stop = slc.Stop != null ? EvaluateConstantExpr(slc.Stop) : slSize;
                    int step = slc.Step != null ? EvaluateConstantExpr(slc.Step) : 1;
                    if (step == 0) throw UserError("for-in slice step cannot be zero.");
                    if (start < 0) start += slSize;
                    if (stop < 0) stop += slSize;
                    start = Math.Max(0, Math.Min(start, slSize));
                    stop = Math.Max(0, Math.Min(stop, slSize));

                    DataType slElem = arrayElemTypes.TryGetValue(slBase, out var sdt) ? sdt : DataType.UINT8;
                    bool slSram = arraysWithVariableIndex.Contains(slBase) || moduleSramArrays.Contains(slBase);
                    string slKey = !string.IsNullOrEmpty(currentInlinePrefix)
                        ? currentInlinePrefix + stmt.VarName
                        : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);
                    variableTypes[slKey] = slElem;

                    bool slBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                    string slBreakLabel = slBrk ? MakeLabel() : "";

                    for (int i = start; step > 0 ? i < stop : i > stop; i += step)
                    {
                        string slContLabel = slBrk ? MakeLabel() : "";
                        if (slBrk)
                            loopStack.Add(new LoopLabels { ContinueLabel = slContLabel, BreakLabel = slBreakLabel });

                        string elemKey = slBase + "__" + i;
                        if (slSram)
                        {
                            Temporary tmp = MakeTemp(slElem);
                            Emit(new ArrayLoad(slBase, new Constant(i), tmp, slElem, slSize));
                            Emit(new Copy(tmp, new Variable(slKey, slElem)));
                        }
                        else if (constantVariables.TryGetValue(elemKey, out int cv))
                            constantVariables[slKey] = cv;
                        else
                            Emit(new Copy(new Variable(elemKey, slElem), new Variable(slKey, slElem)));

                        VisitStatement(stmt.Body);

                        if (slBrk)
                        {
                            loopStack.RemoveAt(loopStack.Count - 1);
                            Emit(new Label(slContLabel));
                        }
                        constantVariables.Remove(slKey);
                    }
                    if (slBrk) Emit(new Label(slBreakLabel));

                    return;
                }
            }

            throw UserError(
                "for-in loop iterable must be a compile-time string constant, a constant list literal [v0, v1, ...], range(N), enumerate(list/range), zip(a, b), reversed(iterable), or a fixed-array slice arr[lo:hi]. Use 'const[str]' type annotation for string parameters.");
        }

        Val startVal = stmt.RangeStart != null ? VisitExpression(stmt.RangeStart) : new Constant(0);
        Val stopVal = VisitExpression(stmt.RangeStop!);
        Val stepVal = stmt.RangeStep != null ? VisitExpression(stmt.RangeStep) : new Constant(1);

        // A zero step never advances the loop variable (Python raises ValueError). The
        // compile-time-unrolled path above already rejects this; mirror it for the runtime
        // loop, where a literal-zero step would otherwise emit an infinite loop.
        if (stepVal is Constant stepZero && stepZero.Value == 0)
            throw UserError("for-in range() step cannot be zero.");

        // Qualify the loop variable the same way the body resolves a variable reference:
        // function-scoped names get the `func.` prefix when not inline-expanded. Using the
        // inline-only prefix left a top-level loop variable bare ("i") while the body read it
        // as "func.i", so the counter and the body's reads were different registers — using `i`
        // in the body read 0 (e.g. `for i in range(n): acc += i` produced 0).
        string varName = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + stmt.VarName
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);
        var loopVar = new Variable(varName, DataType.UINT8);
        Emit(new Copy(startVal, loopVar));

        string startLabel = MakeLabel();
        string contLabel = MakeLabel();
        string endLabel = MakeLabel();
        // `continue` must run the step before re-testing, otherwise the loop variable never
        // advances and the loop spins forever — so the continue target is the step, not the
        // condition check at the top.
        loopStack.Add(new LoopLabels { ContinueLabel = contLabel, BreakLabel = endLabel });

        Emit(new Label(startLabel));
        // A negative step counts down, so the loop ends when the variable drops to or below
        // stop (Python's range(hi, lo, -1)); a positive step ends at/above stop. The previous
        // unconditional `>= stop` test made any negative-step runtime range exit immediately.
        if (stepVal is Constant stepC && stepC.Value < 0)
            Emit(new JumpIfLessOrEqual(loopVar, stopVal, endLabel));
        else
            Emit(new JumpIfGreaterOrEqual(loopVar, stopVal, endLabel));

        VisitStatement(stmt.Body);

        Emit(new Label(contLabel));
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
                throw UserError("AssertionError" + (string.IsNullOrEmpty(stmt.Message) ? "" : ": " + stmt.Message));
            }
        }
        catch (Exception e)
        {
            if (e.Message.StartsWith("AssertionError")) throw;
        }
    }
}