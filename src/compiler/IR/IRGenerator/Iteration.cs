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

    // A named list/tuple unrolls up to this length; past it the loop stays a loop.
    internal const int ConstSequenceUnrollLimit = 8;



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

    // Emits one unrolled-iteration body. When `breakLabel` is non-empty (the body uses
    // break/continue), a fresh continue label brackets the iteration and a shared break label
    // is active, so continue lands at the end of this iteration and break exits the loop.
    // Evaluate a list/tuple element to a compile-time integer constant. Accepts
    // integer / boolean literals and a unary-minus on an integer literal.
    private static bool TryEvalConstElement(Expression e, out int value)
    {
        switch (e)
        {
            case IntegerLiteral il: value = il.Value; return true;
            case BooleanLiteral bl: value = bl.Value ? 1 : 0; return true;
            case UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral n }: value = -n.Value; return true;
            default: value = 0; return false;
        }
    }

    /// <summary>
    /// The elements of a name bound to a short all-constant list/tuple, following aliases the
    /// way the parameter lookup does. Null when the name is not such a binding.
    /// </summary>
    private List<Expression>? ResolveConstSequence(string name)
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
            string? key = candidate;
            for (int depth = 0; depth < 20 && key != null; depth++)
            {
                if (constSequenceBindings.TryGetValue(key, out var elements)) return elements;
                if (!variableAliases.TryGetValue(key, out key)) break;
            }
        }

        return null;
    }

    // The (start, stop, step) of a plain `for x in range(...)` whose bounds are compile-time
    // constants and whose trip count is at most <see cref="ConstSequenceUnrollLimit"/>, or null
    // when the loop has to stay a loop. Only literals and names already folded to a constant
    // count -- the same rule the range-as-iterable path uses -- so nothing is evaluated here
    // that a run-time bound could make wrong.
    private (int Start, int Stop, int Step)? RangeUnrollBounds(ForStmt stmt)
    {
        int? Bound(Expression? e, int whenAbsent)
        {
            if (e == null) return whenAbsent;
            if (e is IntegerLiteral il) return il.Value;
            if (e is UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral n }) return -n.Value;
            // A NAME goes through the general constant evaluator, which knows the unrolled
            // loop variables, the inline-expansion bindings and the module's own constants --
            // so `WIDTH = 4` then `range(WIDTH)` unrolls exactly like `range(4)`. It throws on
            // anything whose value is not fixed at compile time, which is the answer wanted:
            // a run-time bound leaves the loop a loop.
            if (e is VariableExpr)
            {
                try { return EvaluateConstantExpr(e); }
                catch { return null; }
            }
            return null;
        }

        if (Bound(stmt.RangeStart, 0) is not { } start) return null;
        if (Bound(stmt.RangeStop, 0) is not { } stop) return null;
        if (Bound(stmt.RangeStep, 1) is not { } step || step == 0) return null;

        long trips = step > 0
            ? (stop > start ? ((long)stop - start + step - 1) / step : 0)
            : (stop < start ? ((long)start - stop - step - 1) / -step : 0);
        if (trips <= 0 || trips > ConstSequenceUnrollLimit) return null;

        return (start, stop, step);
    }

    private void EmitUnrolledIteration(Statement body, string breakLabel)
    {
        if (breakLabel.Length == 0) { VisitStatement(body); return; }
        string cont = MakeLabel();
        loopStack.Add(new LoopLabels { ContinueLabel = cont, BreakLabel = breakLabel, FinallyDepth = finallyStack.Count });
        VisitStatement(body);
        loopStack.RemoveAt(loopStack.Count - 1);
        Emit(new Label(cont));
    }

    private void VisitFor(ForStmt stmt)
    {
        // The loop variable (and enumerate's index) is a binding even when no type is filed for
        // it -- a range loop and a runtime-bounded slice both bind a name that never enters
        // variableTypes. Recorded before any lowering decision, because `for i in range(...)`
        // carries no Iterable at all and would otherwise miss the shape below.
        {
            string loopKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + stmt.VarName
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);
            boundNames.Add(loopKey);
            if (!string.IsNullOrEmpty(stmt.Var2Name))
                boundNames.Add(loopKey[..^stmt.VarName.Length] + stmt.Var2Name);
        }

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
                    loopStack.Add(new LoopLabels { ContinueLabel = sCont, BreakLabel = sEnd, FinallyDepth = finallyStack.Count });

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

                string strBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (char c in strOpt)
                {
                    constantVariables[varKey] = (int)c;
                    EmitUnrolledIteration(stmt.Body, strBrk);
                }
                if (strBrk.Length > 0) Emit(new Label(strBrk));

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
                string lpBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (var elem in boundList.Elements)
                {
                    if (elem is IntegerLiteral il)
                    {
                        constantVariables[varKey] = il.Value;
                        EmitUnrolledIteration(stmt.Body, lpBrk);
                    }
                    else throw UserError("for-in list iterable elements must be compile-time integer constants.");
                }
                if (lpBrk.Length > 0) Emit(new Label(lpBrk));

                constantVariables.Remove(varKey);
                return;
            }

            // The same unrolling when the sequence was bound to a name first. `for p in pins:`
            // is the shape every "declare the pins, then walk them" program has, and without
            // this it fell through to a run-time loop whose variable is not a constant -- so
            // Pin(p) rejected it while `for p in (11, 12, 13):` compiled.
            if (iter is VariableExpr seqVar && ResolveConstSequence(seqVar.Name) is { } boundSeq)
            {
                string sqBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (var elem in boundSeq)
                {
                    if (!TryEvalConstElement(elem, out int sv))
                        throw UserError("for-in over a named sequence needs compile-time integer elements.");
                    constantVariables[varKey] = sv;
                    EmitUnrolledIteration(stmt.Body, sqBrk);
                }
                if (sqBrk.Length > 0) Emit(new Label(sqBrk));

                constantVariables.Remove(varKey);
                return;
            }

            if (iter is ListExpr or TupleExpr)
            {
                var elems = iter is ListExpr le ? le.Elements : ((TupleExpr)iter).Elements;
                string llBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";

                // `for a, b in [(1, 2), (3, 4)]`. The unrolling is the same one the single-target
                // form does; what the two-name form needs is the second key bound alongside the
                // first, from the element's second component. Qualified the same way varKey is,
                // so the body finds it under whatever name it reads.
                string? varKey2 = string.IsNullOrEmpty(stmt.Var2Name) ? null
                    : (!string.IsNullOrEmpty(currentInlinePrefix)
                        ? currentInlinePrefix + stmt.Var2Name
                        : (!string.IsNullOrEmpty(currentFunction)
                            ? currentFunction + "." + stmt.Var2Name : stmt.Var2Name));

                foreach (var elem in elems)
                {
                    if (varKey2 != null)
                    {
                        // Refuse by naming what the element IS and how many names it carries,
                        // rather than repeating "must be constants" at a program whose elements
                        // are all constants.
                        if (elem is not (ListExpr or TupleExpr))
                            throw UserError(
                                $"'for {stmt.VarName}, {stmt.Var2Name} in ...' unpacks two names from each " +
                                "element, so every element has to be a pair like (1, 2). This one is not a " +
                                $"pair, so there is nothing to give {stmt.Var2Name}.");

                        var parts = elem is ListExpr pl ? pl.Elements : ((TupleExpr)elem).Elements;
                        if (parts.Count != 2)
                            throw UserError(
                                $"'for {stmt.VarName}, {stmt.Var2Name} in ...' unpacks two names, and this " +
                                $"element has {parts.Count}. Every element has to carry exactly two values.");

                        if (!TryEvalConstElement(parts[0], out int pv0)
                            || !TryEvalConstElement(parts[1], out int pv1))
                            throw UserError(
                                "for-in over a list of pairs unrolls at compile time, so both values in " +
                                "each pair have to be integer constants. Read the run-time value inside " +
                                "the body instead.");

                        constantVariables[varKey] = pv0;
                        constantVariables[varKey2] = pv1;
                        EmitUnrolledIteration(stmt.Body, llBrk);
                        continue;
                    }

                    if (TryEvalConstElement(elem, out int ev))
                    {
                        constantVariables[varKey] = ev;
                        EmitUnrolledIteration(stmt.Body, llBrk);
                    }
                    // A tuple element with a single loop name is the shape that used to be
                    // reported as a non-constant element while every element was a constant.
                    // What is missing is a name to unpack it into, which is what it says now.
                    else if (elem is ListExpr or TupleExpr)
                        throw UserError(
                            $"each element here is a pair, and '{stmt.VarName}' is one name, so there is " +
                            $"nowhere to put the second value. Write 'for {stmt.VarName}, second in ...' to " +
                            "unpack both.");
                    else throw UserError("for-in list/tuple iterable elements must be compile-time integer constants.");
                }
                if (llBrk.Length > 0) Emit(new Label(llBrk));

                constantVariables.Remove(varKey);
                if (varKey2 != null) constantVariables.Remove(varKey2);
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

                    // enumerate() over a list [..] / tuple (..) literal, or an inline
                    // parameter bound to such a literal, of compile-time constants.
                    Expression enumInner = inner;
                    if (enumInner is VariableExpr epv && ResolveListLiteralParam(epv.Name) is ListExpr eBound)
                        enumInner = eBound;
                    var seqElems = enumInner switch
                    {
                        ListExpr le  => le.Elements,
                        TupleExpr te => te.Elements,
                        // A name bound to a short constant sequence enumerates like the literal
                        // it stands for; `for i, p in enumerate(pins)` is the same program as
                        // enumerating the list written at the call.
                        VariableExpr ev2 => ResolveConstSequence(ev2.Name),
                        _ => null,
                    };
                    if (seqElems != null)
                    {
                        foreach (var elem in seqElems)
                        {
                            if (TryEvalConstElement(elem, out int ev))
                            {
                                constantVariables[idxKey] = idx++;
                                constantVariables[valKey] = ev;
                                VisitStatement(stmt.Body);
                            }
                            else
                                throw UserError(
                                    "enumerate() list/tuple elements must be compile-time integer constants.");
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
                                    loopStack.Add(new LoopLabels { ContinueLabel = enContLabel, BreakLabel = enBreakLabel, FinallyDepth = finallyStack.Count });
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
                                        BindInstanceForIteration(elemKey, qualifiedVal);
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
                            if (zbrk) loopStack.Add(new LoopLabels { ContinueLabel = zCont, BreakLabel = zBreak, FinallyDepth = finallyStack.Count });
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
                                    loopStack.Add(new LoopLabels { ContinueLabel = rvContLabel, BreakLabel = rvBreakLabel, FinallyDepth = finallyStack.Count });
                                string elemKey = @base + "__" + k;
                                if (constantVariables.TryGetValue(elemKey, out int cv))
                                    constantVariables[valKey] = cv;
                                else if (instanceClasses.ContainsKey(elemKey) ||
                                         instanceClasses.Keys.Any(x => x.StartsWith(elemKey + ".")))
                                    BindInstanceForIteration(elemKey, qValKey);
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
                    loopStack.Add(new LoopLabels { ContinueLabel = loopCont, BreakLabel = loopEnd, FinallyDepth = finallyStack.Count });

                    Emit(new Label(loopStart));
                    Temporary cmpTmp = MakeTemp(DataType.UINT8);
                    Emit(new Binary(PyMCU.IR.BinaryOp.GreaterEqual, idxVar, listLen, cmpTmp));
                    Emit(new JumpIfNotZero(cmpTmp, loopEnd));

                    Temporary elemAddr = EmitElemAddr(listPtr, idxVar, elemDt.SizeOf());
                    Temporary elemTmp = MakeTemp(elemDt);
                    Emit(new LoadIndirect(elemAddr, elemTmp, elemDt));
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
                    // An SRAM-resident array (runtime-indexed or module-level) has no per-element
                    // arr__k vars — its elements live in memory and must be read with an indexed
                    // load, exactly as the enumerate path does. Without this a `for v in arr` over
                    // such an array read 0 from the missing element vars.
                    bool forSram = arraysWithVariableIndex.Contains(forBase) || moduleSramArrays.Contains(forBase);

                    // Only bracket iterations with labels when the body actually uses break/continue
                    // (else keep the plain unroll so constant folding is not split by labels).
                    bool forBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                    string forBreakLabel = forBrk ? MakeLabel() : "";

                    for (int fk = 0; fk < forSize; fk++)
                    {
                        string forContLabel = forBrk ? MakeLabel() : "";
                        if (forBrk)
                            loopStack.Add(new LoopLabels { ContinueLabel = forContLabel, BreakLabel = forBreakLabel, FinallyDepth = finallyStack.Count });

                        string elemKey2 = forBase + "__" + fk;
                        bool isZca = instanceClasses.ContainsKey(elemKey2) ||
                                     instanceClasses.Keys.Any(x => x.StartsWith(elemKey2 + "."));
                        if (forSram)
                        {
                            Temporary tmp = MakeTemp(elemDt2);
                            Emit(new ArrayLoad(forBase, new Constant(fk), tmp, elemDt2, forSize));
                            Emit(new Copy(tmp, new Variable(forVarKey, elemDt2)));
                        }
                        else if (isZca)
                            BindInstanceForIteration(elemKey2, forVarKey);
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
                    // Runtime bounds (`for b in buf[0:n]` with n known only at runtime): no
                    // allocation is needed to ITERATE a slice, so rewrite to the range loop
                    // the bounds describe -- `for __i in range(lo, hi): v = arr[__i]; <body>`.
                    // The range machinery provides break/continue/else; ScanStmt already
                    // marked the array variable-indexed (any slice subscript does), so the
                    // per-iteration read is a runtime ArrayLoad. Step must still be constant:
                    // a runtime stride has no clear termination against a fixed array.
                    bool slConstBounds = true;
                    try
                    {
                        if (slc.Start != null) EvaluateConstantExpr(slc.Start);
                        if (slc.Stop != null) EvaluateConstantExpr(slc.Stop);
                    }
                    catch (Exception) { slConstBounds = false; }

                    if (!slConstBounds)
                    {
                        if (slc.Step != null)
                            throw UserError(
                                "for-in over a slice with runtime bounds does not take a step; " +
                                "iterate range() with the stride explicitly");
                        string slIdx = "__slci" + (++sliceLoopId);
                        var slBody = new Block();
                        slBody.Statements.Add(new AssignStmt(
                            new VariableExpr(stmt.VarName),
                            new IndexExpr(new VariableExpr(sliceVar.Name), new VariableExpr(slIdx))));
                        if (stmt.Body is Block slOb) slBody.Statements.AddRange(slOb.Statements);
                        else slBody.Statements.Add(stmt.Body);
                        VisitStatement(new ForStmt(slIdx,
                            slc.Start ?? new IntegerLiteral(0),
                            slc.Stop ?? new IntegerLiteral(slSize),
                            null, slBody));
                        return;
                    }

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
                            loopStack.Add(new LoopLabels { ContinueLabel = slContLabel, BreakLabel = slBreakLabel, FinallyDepth = finallyStack.Count });

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

            // CPython's OLD iteration protocol is __getitem__(0), __getitem__(1), ... until
            // IndexError. PyMCU cannot stop on the exception, for the same reason it cannot run
            // __iter__/__next__ below, but it does not need to: when __len__ is a compile-time
            // constant the trip count is known, so this rewrites to `for __sqi in range(0, N):
            // v = obj[__sqi]` and the range machinery unrolls it into the code the author would
            // have written by hand. Each obj[__sqi] dispatches __getitem__ the way a direct
            // subscript already does.
            if (stmt.Iterable is VariableExpr sqVe
                && TryResolveInstanceMethodAst(sqVe.Name, "__getitem__") != null
                && InstanceClassOfName(sqVe.Name) is { } sqCls
                && DunderConstLen(sqCls) is { } sqLen)
            {
                if (sqLen < 0)
                    throw UserError(
                        $"'{sqVe.Name}' has a negative __len__ ({sqLen}), so the loop has no trip count.");
                if (sqLen > 0)
                {
                    string sqIdx = "__sqi" + (++sliceLoopId);
                    var sqBody = new Block();
                    sqBody.Statements.Add(new AssignStmt(
                        new VariableExpr(stmt.VarName),
                        new IndexExpr(new VariableExpr(sqVe.Name), new VariableExpr(sqIdx))));
                    if (stmt.Body is Block sqOb) sqBody.Statements.AddRange(sqOb.Statements);
                    else sqBody.Statements.Add(stmt.Body);
                    VisitStatement(new ForStmt(sqIdx,
                        new IntegerLiteral(0), new IntegerLiteral(sqLen), null, sqBody));
                }
                return;
            }

            // An instance of a class defining __iter__/__next__ is the one shape worth naming
            // separately: it looks like it should work, and the generic list would leave the
            // author guessing why their iterator protocol is ignored.
            if (stmt.Iterable is VariableExpr itVe
                && TryResolveInstanceMethodAst(itVe.Name, "__next__") != null)
                throw UserError(
                    $"'{itVe.Name}' defines __iter__/__next__, but PyMCU does not run the iterator " +
                    "protocol: there is no exception to stop on, so the loop could never end. " +
                    "Write the loop explicitly (`while <cond>: v = obj.next()`), or iterate a " +
                    "range/fixed array instead. A `yield` generator function IS supported.");

            // __getitem__ without a compile-time __len__: the sequence protocol is the right
            // shape, but the trip count is only known at run time and there is no IndexError to
            // stop on, so unrolling is not available. Name that rather than listing the forms
            // this object is not.
            if (stmt.Iterable is VariableExpr sqBadVe
                && TryResolveInstanceMethodAst(sqBadVe.Name, "__getitem__") != null)
                throw UserError(
                    $"'{sqBadVe.Name}' defines __getitem__, but " +
                    (TryResolveInstanceMethodAst(sqBadVe.Name, "__len__") == null
                        ? "no __len__, so the loop has no trip count"
                        : "its __len__ is not a compile-time constant, so the trip count is only " +
                          "known at run time") +
                    " and PyMCU has no IndexError to stop on. Give the class a __len__ with a " +
                    "constant return (`def __len__(self) -> uint8: return 4`) to iterate it " +
                    $"directly, or write the loop over the length you have (`for i in range(n): " +
                    $"v = {sqBadVe.Name}[i]`).");

            throw UserError(
                "for-in loop iterable must be a compile-time string constant, a constant list literal [v0, v1, ...], range(N), enumerate(list/range), zip(a, b), reversed(iterable), or a fixed-array slice arr[lo:hi]. Use 'const[str]' type annotation for string parameters.");
        }

        // `for p in range(11, 14)` over CONSTANT bounds and a short trip count unrolls, the way
        // a short constant list already does. The parser files the plain form's bounds in
        // RangeStart/Stop/Step and leaves Iterable null, so the unrolling above -- which only
        // ever sees range() as an ITERABLE expression, the shape enumerate() and zip() build --
        // never ran for it. The loop variable therefore never qualified where a compile-time
        // constant is required, and `Pin(p, Pin.OUT)` rejected the range spelling while
        // `pins = [11, 12, 13]` then `for p in pins:` compiled to the same three pins.
        //
        // The trip-count cap is the sequence limit: unrolling copies the body no more times
        // than writing the same values as a list literal already copies it. Past it the loop
        // stays a loop, and a body that needs a constant loop variable says so as before.
        //
        // Emitted before the run-time lowering starts, because the checks below evaluate the
        // bound expressions and that is not free of side effects.
        if (RangeUnrollBounds(stmt) is { } unroll)
        {
            (int unrollStart, int unrollStop, int unrollStep) = unroll;
            string unrollKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + stmt.VarName
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);
            string unrollBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";

            for (int i = unrollStart; unrollStep > 0 ? i < unrollStop : i > unrollStop; i += unrollStep)
            {
                constantVariables[unrollKey] = i;
                EmitUnrolledIteration(stmt.Body, unrollBrk);
            }
            if (unrollBrk.Length > 0) Emit(new Label(unrollBrk));

            constantVariables.Remove(unrollKey);
            return;
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
        loopStack.Add(new LoopLabels { ContinueLabel = contLabel, BreakLabel = endLabel, FinallyDepth = finallyStack.Count });

        // Same rule as a `while`: the body is emitted once and runs many times, so nothing it
        // can write may be folded from the value it holds on the way in. Without this an
        // accumulator read its starting value on every pass, and `for i in range(4): total =
        // total + i` came out 3 instead of 6.
        var strBeforeLoop = new Dictionary<string, string?>(strConstantVariables);
        InvalidateConstantsAssignedIn(stmt.Body);

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

        // A range whose bounds are decided at run time can run zero times: a str the body
        // rebinds holds either value at the exit (see MarkStrReboundBy).
        MarkStrReboundBy(strBeforeLoop);
    }

    private int _withManagerCounter;

    private void VisitWith(WithStmt stmt)
    {
        // `with C(x) as v:` -- give the manager a name of its own and carry on as if the
        // program had written `m = C(x)` first. Without this the statement fell through to
        // "just run the body": nothing constructed the manager, nothing bound v, and the
        // program was rejected for using a name that is assigned right there in its header.
        if (stmt.ContextExpr is not VariableExpr && !string.IsNullOrEmpty(stmt.AsName))
        {
            string managerName = "__with_manager_" + (_withManagerCounter++);
            VisitStatement(new AssignStmt(new VariableExpr(managerName), stmt.ContextExpr));
            stmt = new WithStmt(new VariableExpr(managerName), stmt.AsName, stmt.Body);
        }

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
            Val entered = VisitExpression(enterCall);

            // `with obj as v`: v is what __enter__ RETURNED. The alias above already covers the
            // usual `return self` (the value IS the instance, and aliasing keeps its class), but
            // a context manager handing back something else -- a value, a different object --
            // would otherwise leave v bound to the manager itself.
            if (!string.IsNullOrEmpty(stmt.AsName))
            {
                string qualified = string.IsNullOrEmpty(currentFunction)
                    ? stmt.AsName
                    : currentFunction + "." + stmt.AsName;
                string qualifiedObj = string.IsNullOrEmpty(currentFunction) ? objName : currentFunction + "." + objName;
                // A ZCA `__enter__` whose body is `return self` hands back the instance, but the
                // instance has no single runtime value to hand back: the expansion yields a
                // temporary that stands for nothing. Reading the AST settles it -- when the
                // method returns bare self, v IS obj, and the alias must stay. Without this the
                // alias was dropped and v read whatever the temporary happened to hold, which
                // was every field zero.
                bool entersSelf = entered is NoneVal
                                  || (entered is Variable ev && (ev.Name == qualifiedObj || ev.Name == objName))
                                  || (TryResolveInstanceMethodAst(objName, "__enter__") is { } enterDef
                                      && MethodReturnsBareSelf(enterDef));
                if (!entersSelf)
                {
                    variableAliases.Remove(qualified);
                    DataType et = entered switch
                    {
                        Variable v3 => v3.Type,
                        Temporary t3 => t3.Type,
                        _ => DataType.UINT8,
                    };
                    variableTypes[qualified] = et;
                    Emit(new Copy(entered, new Variable(qualified, et)));
                }
            }

            VisitStatement(stmt.Body);

            // CPython always calls __exit__(exc_type, exc_value, traceback); PyMCU's own HAL
            // declares it as __exit__(self) alone. Pass exactly as many placeholders as the
            // resolved method declares, so both spellings work instead of the CPython one
            // reporting a missing argument.
            var exitArgs = new List<Expression>();
            if (TryResolveInstanceMethodAst(objName, "__exit__") is { } exitDef)
                for (int i = 1; i < exitDef.Params.Count; i++) exitArgs.Add(new IntegerLiteral(0));
            var exitCallee = new MemberAccessExpr(new VariableExpr(objName), "__exit__");
            var exitCall = new CallExpr(exitCallee, exitArgs);
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

    /// <summary>True when every return in the method hands back bare `self`.</summary>
    private static bool MethodReturnsBareSelf(FunctionDef method)
    {
        bool sawReturn = false, allSelf = true;
        void S(Statement? st)
        {
            switch (st)
            {
                case null: return;
                case Block b: foreach (var cs in b.Statements) S(cs); return;
                case ReturnStmt r:
                    sawReturn = true;
                    if (r.Value is not VariableExpr { Name: "self" }) allSelf = false;
                    return;
                case IfStmt i:
                    S(i.ThenBranch);
                    foreach (var (_, br) in i.ElifBranches) S(br);
                    S(i.ElseBranch);
                    return;
                case WhileStmt w: S(w.Body); return;
                case ForStmt f: S(f.Body); return;
                case WithStmt wi: S(wi.Body); return;
                case TryStmt t:
                    foreach (var cs in t.Body) S(cs);
                    foreach (var (_, h) in t.Handlers) foreach (var cs in h) S(cs);
                    if (t.ElseBody != null) foreach (var cs in t.ElseBody) S(cs);
                    if (t.Finally != null) foreach (var cs in t.Finally) S(cs);
                    return;
            }
        }
        S(method.Body);
        return sawReturn && allSelf;
    }

}