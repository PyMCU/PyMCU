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
using AstUnOp = PyMCU.Frontend.UnaryOp;
using PyMCU.IR;
using PyMCU.Common;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    private int EmitOptimizedConditionalJump(Expression cond, string targetLabel, bool jumpIfTrue = false)
    {
        int? ResolveInt(Expression expr)
        {
            if (expr is IntegerLiteral num) return num.Value;
            if (expr is VariableExpr v && globals.TryGetValue(v.Name, out var sym) && !sym.IsMemoryAddress)
                return sym.Value;
            return null;
        }

        if (cond is BinaryExpr binExpr)
        {
            if (binExpr.Op == Frontend.BinaryOp.And || binExpr.Op == Frontend.BinaryOp.Or)
            {
                bool isAnd = binExpr.Op == Frontend.BinaryOp.And;

                void EmitSub(Expression sub, string label, bool ifTrue)
                {
                    int r = EmitOptimizedConditionalJump(sub, label, ifTrue);
                    if (r != 0) return;
                    Val v = VisitExpression(sub);
                    if (v is Constant c)
                    {
                        bool cval = c.Value != 0;
                        if (cval == ifTrue) Emit(new Jump(label));
                        return;
                    }

                    if (ifTrue) Emit(new JumpIfNotZero(v, label));
                    else Emit(new JumpIfZero(v, label));
                }

                if ((!jumpIfTrue && isAnd) || (jumpIfTrue && !isAnd))
                {
                    EmitSub(binExpr.Left, targetLabel, jumpIfTrue);
                    EmitSub(binExpr.Right, targetLabel, jumpIfTrue);
                }
                else
                {
                    string skipLabel = MakeLabel();
                    EmitSub(binExpr.Left, skipLabel, !jumpIfTrue);
                    EmitSub(binExpr.Right, targetLabel, jumpIfTrue);
                    Emit(new Label(skipLabel));
                }

                return 1;
            }

            if (binExpr.Op == Frontend.BinaryOp.In || binExpr.Op == Frontend.BinaryOp.NotIn ||
                binExpr.Op == Frontend.BinaryOp.Is || binExpr.Op == Frontend.BinaryOp.IsNot)
                return 0;

            Val v1 = VisitExpression(binExpr.Left);
            Val v2 = VisitExpression(binExpr.Right);

            if (v1 is Constant c1 && v2 is Constant c2)
            {
                bool condResult = false;
                switch (binExpr.Op)
                {
                    case Frontend.BinaryOp.Equal: condResult = c1.Value == c2.Value; break;
                    case Frontend.BinaryOp.NotEqual: condResult = c1.Value != c2.Value; break;
                    case Frontend.BinaryOp.Less: condResult = c1.Value < c2.Value; break;
                    case Frontend.BinaryOp.LessEq: condResult = c1.Value <= c2.Value; break;
                    case Frontend.BinaryOp.Greater: condResult = c1.Value > c2.Value; break;
                    case Frontend.BinaryOp.GreaterEq: condResult = c1.Value >= c2.Value; break;
                }

                if (jumpIfTrue)
                {
                    if (condResult) Emit(new Jump(targetLabel));
                }
                else
                {
                    if (!condResult) Emit(new Jump(targetLabel));
                }

                // 2 = CT-true (only then branch needed), -1 = CT-false (only else needed)
                return condResult ? 2 : -1;
            }

            // Fold compile-time ptr-register comparisons: `if pin_reg == PIND:` where
            // pin_reg is a ptr parameter propagated through constantAddressVariables.
            if (v1 is MemoryAddress ma1 && v2 is MemoryAddress ma2)
            {
                bool condResult = false;
                switch (binExpr.Op)
                {
                    case Frontend.BinaryOp.Equal:    condResult = ma1.Address == ma2.Address; break;
                    case Frontend.BinaryOp.NotEqual: condResult = ma1.Address != ma2.Address; break;
                    case Frontend.BinaryOp.Less:     condResult = ma1.Address <  ma2.Address; break;
                    case Frontend.BinaryOp.LessEq:   condResult = ma1.Address <= ma2.Address; break;
                    case Frontend.BinaryOp.Greater:  condResult = ma1.Address >  ma2.Address; break;
                    case Frontend.BinaryOp.GreaterEq:condResult = ma1.Address >= ma2.Address; break;
                }

                if (jumpIfTrue) { if (condResult) Emit(new Jump(targetLabel)); }
                else            { if (!condResult) Emit(new Jump(targetLabel)); }
                return condResult ? 2 : -1;
            }

            switch (binExpr.Op)
            {
                case Frontend.BinaryOp.Equal:
                    if (jumpIfTrue) Emit(new JumpIfEqual(v1, v2, targetLabel));
                    else Emit(new JumpIfNotEqual(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.NotEqual:
                    if (jumpIfTrue) Emit(new JumpIfNotEqual(v1, v2, targetLabel));
                    else Emit(new JumpIfEqual(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.Less:
                    if (jumpIfTrue) Emit(new JumpIfLessThan(v1, v2, targetLabel));
                    else Emit(new JumpIfGreaterOrEqual(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.LessEq:
                    if (jumpIfTrue) Emit(new JumpIfLessOrEqual(v1, v2, targetLabel));
                    else Emit(new JumpIfGreaterThan(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.Greater:
                    if (jumpIfTrue) Emit(new JumpIfGreaterThan(v1, v2, targetLabel));
                    else Emit(new JumpIfLessOrEqual(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.GreaterEq:
                    if (jumpIfTrue) Emit(new JumpIfGreaterOrEqual(v1, v2, targetLabel));
                    else Emit(new JumpIfLessThan(v1, v2, targetLabel));
                    return 1;
            }
        }

        if (cond is BinaryExpr binExpr2 &&
            (binExpr2.Op == Frontend.BinaryOp.Equal || binExpr2.Op == Frontend.BinaryOp.NotEqual))
        {
            var indexExpr = binExpr2.Left as IndexExpr;
            var rhsExpr = binExpr2.Right;
            if (indexExpr == null)
            {
                indexExpr = binExpr2.Right as IndexExpr;
                rhsExpr = binExpr2.Left;
            }

            if (indexExpr != null)
            {
                bool targetIsArray = false;
                if (indexExpr.Target is VariableExpr ve)
                {
                    string q = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
                    targetIsArray = arraySizes.ContainsKey(q);
                }

                if (!targetIsArray)
                {
                    var bitVal = ResolveInt(indexExpr.Index);
                    var targetVal = ResolveInt(rhsExpr);

                    if (bitVal.HasValue && targetVal.HasValue)
                    {
                        Val addr = VisitExpression(indexExpr.Target);
                        int bit = bitVal.Value;
                        int target = targetVal.Value;

                        bool invert = binExpr2.Op == Frontend.BinaryOp.NotEqual;
                        if (invert) target = target == 0 ? 1 : 0;

                        if (target == 0)
                        {
                            if (jumpIfTrue) Emit(new JumpIfBitClear(addr, bit, targetLabel));
                            else Emit(new JumpIfBitSet(addr, bit, targetLabel));
                            return 1;
                        }
                        else if (target == 1)
                        {
                            if (jumpIfTrue) Emit(new JumpIfBitSet(addr, bit, targetLabel));
                            else Emit(new JumpIfBitClear(addr, bit, targetLabel));
                            return 1;
                        }
                    }
                }
            }
        }

        if (cond is UnaryExpr unExpr && unExpr.Op == AstUnOp.Not)
        {
            if (unExpr.Operand is IndexExpr idx)
            {
                bool targetIsArray = false;
                if (idx.Target is VariableExpr ve)
                {
                    string q = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
                    targetIsArray = arraySizes.ContainsKey(q);
                }

                if (!targetIsArray)
                {
                    var bitVal = ResolveInt(idx.Index);
                    if (bitVal.HasValue)
                    {
                        Val addr = VisitExpression(idx.Target);
                        int bit = bitVal.Value;

                        if (jumpIfTrue) Emit(new JumpIfBitClear(addr, bit, targetLabel));
                        else Emit(new JumpIfBitSet(addr, bit, targetLabel));
                        return 1;
                    }
                }
            }
        }

        if (cond is IndexExpr idx2)
        {
            bool targetIsArray = false;
            if (idx2.Target is VariableExpr ve)
            {
                string q = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
                targetIsArray = arraySizes.ContainsKey(q);
            }

            if (!targetIsArray)
            {
                var bitVal = ResolveInt(idx2.Index);
                if (bitVal.HasValue)
                {
                    Val addr = VisitExpression(idx2.Target);
                    int bit = bitVal.Value;

                    if (jumpIfTrue) Emit(new JumpIfBitSet(addr, bit, targetLabel));
                    else Emit(new JumpIfBitClear(addr, bit, targetLabel));
                    return 1;
                }
            }
        }

        return 0;
    }

    private void VisitIf(IfStmt stmt)
    {
        string endLabel = MakeLabel();
        string nextLabel = (stmt.ElifBranches.Count == 0 && stmt.ElseBranch == null) ? endLabel : MakeLabel();

        int optResult = EmitOptimizedConditionalJump(stmt.Condition, nextLabel, false);
        bool skipThen = false;
        bool isRuntimeBranch = false;

        if (optResult == -1) skipThen = true;
        else if (optResult == 2)
        {
            // CT-true: only visit then branch, skip else entirely (prevents CT
            // side-effects like compile_isr from the else branch being processed).
            VisitStatement(stmt.ThenBranch);
            Emit(new Label(endLabel));
            return;
        }
        else if (optResult == 0)
        {
            Val condVal = VisitExpression(stmt.Condition);
            if (condVal is Constant c)
            {
                if (c.Value == 0)
                {
                    skipThen = true;
                    if (stmt.ElifBranches.Count == 0 && stmt.ElseBranch == null)
                    {
                        Emit(new Label(endLabel));
                        return;
                    }

                    Emit(new Jump(nextLabel));
                }
                else
                {
                    VisitStatement(stmt.ThenBranch);
                    Emit(new Label(endLabel));
                    return;
                }
            }
            else
            {
                Emit(new JumpIfZero(condVal, nextLabel));
                isRuntimeBranch = true;
            }
        }

        var snapBefore = new Dictionary<string, string>(strConstantVariables);
        var branchSnaps = new List<Dictionary<string, string>>();
        bool hasElse = stmt.ElseBranch != null;

        if (!skipThen)
        {
            if (isRuntimeBranch) _runtimeBranchDepth++;
            VisitStatement(stmt.ThenBranch);
            if (isRuntimeBranch) _runtimeBranchDepth--;
            if (stmt.ElifBranches.Count > 0 || stmt.ElseBranch != null)
                Emit(new Jump(endLabel));
            branchSnaps.Add(new Dictionary<string, string>(strConstantVariables));
            strConstantVariables = new Dictionary<string, string>(snapBefore);
        }

        for (int i = 0; i < stmt.ElifBranches.Count; ++i)
        {
            Emit(new Label(nextLabel));
            bool isLastElif = i == stmt.ElifBranches.Count - 1;
            nextLabel = (isLastElif && stmt.ElseBranch == null) ? endLabel : MakeLabel();

            var elifCond = stmt.ElifBranches[i].Condition;
            var elifBlock = stmt.ElifBranches[i].Body;

            int elifOpt = EmitOptimizedConditionalJump(elifCond, nextLabel, false);
            bool skipElif = false;
            bool elifIsRuntime = false;

            if (elifOpt == -1) skipElif = true;
            else if (elifOpt == 2)
            {
                // CT-true elif: only visit this block, skip remaining branches.
                VisitStatement(elifBlock);
                Emit(new Label(endLabel));
                return;
            }
            else if (elifOpt == 0)
            {
                Val elifVal = VisitExpression(elifCond);
                if (elifVal is Constant c)
                {
                    if (c.Value == 0)
                    {
                        skipElif = true;
                        Emit(new Jump(nextLabel));
                    }
                }
                else
                {
                    Emit(new JumpIfZero(elifVal, nextLabel));
                    elifIsRuntime = true;
                }
            }

            if (!skipElif)
            {
                if (elifIsRuntime) _runtimeBranchDepth++;
                VisitStatement(elifBlock);
                if (elifIsRuntime) _runtimeBranchDepth--;
                if (!isLastElif || stmt.ElseBranch != null) Emit(new Jump(endLabel));
                branchSnaps.Add(new Dictionary<string, string>(strConstantVariables));
                strConstantVariables = new Dictionary<string, string>(snapBefore);
            }
        }

        if (stmt.ElseBranch != null)
        {
            Emit(new Label(nextLabel));
            // The else branch runs when the condition was false — still runtime-guarded.
            if (isRuntimeBranch) _runtimeBranchDepth++;
            VisitStatement(stmt.ElseBranch);
            if (isRuntimeBranch) _runtimeBranchDepth--;
            branchSnaps.Add(new Dictionary<string, string>(strConstantVariables));
            strConstantVariables = new Dictionary<string, string>(snapBefore);
        }

        Emit(new Label(endLabel));

        if (branchSnaps.Count <= 0) return;
        var changedKeys = new HashSet<string>();
        foreach (var kvp in branchSnaps.SelectMany(snap => snap))
        {
            if (!snapBefore.TryGetValue(kvp.Key, out var oldV) || oldV != kvp.Value)
                changedKeys.Add(kvp.Key);
        }

        foreach (var key in changedKeys)
        {
            var allAgree = true;
            var agreedVal = "";
            var first = true;
            foreach (var snap in branchSnaps)
            {
                if (!snap.TryGetValue(key, out var v))
                {
                    allAgree = false;
                    break;
                }

                if (first)
                {
                    agreedVal = v;
                    first = false;
                }
                else if (v != agreedVal)
                {
                    allAgree = false;
                    break;
                }
            }

            if (allAgree && !first && hasElse)
            {
                strConstantVariables[key] = agreedVal;
            }
        }
    }

    private void VisitMatch(MatchStmt stmt)
    {
        Val targetVal = VisitExpression(stmt.Target);
        bool ctAlreadyMatched = false;
        string endLabel = MakeLabel();

        foreach (var branch in stmt.Branches)
        {
            string nextCaseLabel = MakeLabel();

            if (branch.Pattern != null)
            {
                if (branch.Pattern is ListExpr seq)
                {
                    string arrName = "";
                    if (targetVal is Variable v) arrName = v.Name;
                    else throw UserError("match/case sequence pattern: subject must be an array variable");

                    int patSize = seq.Elements.Count;
                    if (arraySizes.TryGetValue(arrName, out int size) && size != patSize)
                    {
                        Emit(new Jump(nextCaseLabel));
                        Emit(new Label(nextCaseLabel));
                        continue;
                    }

                    bool useSram = arraysWithVariableIndex.Contains(arrName) || moduleSramArrays.Contains(arrName);
                    DataType elemDt = arrayElemTypes.TryGetValue(arrName, out var dt) ? dt : DataType.UINT8;

                    var captures = new List<(int Idx, string Name)>();
                    for (int i = 0; i < patSize; ++i)
                    {
                        Expression elem = seq.Elements[i];
                        Val elemVal;
                        if (useSram)
                        {
                            Temporary tmp = MakeTemp(elemDt);
                            Emit(new ArrayLoad(arrName, new Constant(i), tmp, elemDt, patSize));
                            elemVal = tmp;
                        }
                        else
                        {
                            elemVal = new Variable(arrName + "__" + i, elemDt);
                        }

                        if (elem is VariableExpr ve)
                        {
                            string qname = string.IsNullOrEmpty(currentFunction)
                                ? ve.Name
                                : currentFunction + "." + ve.Name;
                            captures.Add((i, qname));
                        }
                        else
                        {
                            Val patVal = VisitExpression(elem);
                            Temporary cmp = MakeTemp();
                            Emit(new Binary(PyMCU.IR.BinaryOp.Equal, elemVal, patVal, cmp));
                            Emit(new JumpIfZero(cmp, nextCaseLabel));
                        }
                    }

                    if (branch.Guard != null)
                    {
                        Val g = VisitExpression(branch.Guard);
                        Emit(new JumpIfZero(g, nextCaseLabel));
                    }

                    foreach (var cap in captures)
                    {
                        Val src = useSram ? (Val)MakeTemp(elemDt) : new Variable(arrName + "__" + cap.Idx, elemDt);
                        if (useSram) Emit(new ArrayLoad(arrName, new Constant(cap.Idx), src, elemDt, patSize));
                        Emit(new Copy(src, new Variable(cap.Name, elemDt)));
                        variableTypes[cap.Name] = elemDt;
                    }

                    if (!string.IsNullOrEmpty(branch.CaptureName))
                    {
                        string qname = string.IsNullOrEmpty(currentFunction)
                            ? branch.CaptureName
                            : currentFunction + "." + branch.CaptureName;
                        Emit(new Copy(targetVal, new Variable(qname, elemDt)));
                        variableTypes[qname] = elemDt;
                    }

                    if (branch.Body != null) VisitBlock((Block)branch.Body);
                    Emit(new Jump(endLabel));
                    Emit(new Label(nextCaseLabel));
                    continue;
                }

                var alts = new List<Expression>();

                void Flatten(Expression e)
                {
                    if (e is BinaryExpr bin && bin.Op == Frontend.BinaryOp.BitOr) // AST BitOr for match alternation
                    {
                        Flatten(bin.Left);
                        Flatten(bin.Right);
                        return;
                    }

                    alts.Add(e);
                }

                Flatten(branch.Pattern);

                var altVals = alts.Select(VisitExpression).ToList<Val>();
                bool allAltsConst = targetVal is Constant;
                if (allAltsConst)
                {
                    foreach (var v in altVals)
                        if (!(v is Constant))
                        {
                            allAltsConst = false;
                            break;
                        }
                }
                bool skipBody = false;
                if (allAltsConst)
                {
                    bool anyMatch = false;
                    var ct = targetVal as Constant;
                    foreach (var v in altVals)
                    {
                        if (((Constant)v).Value == ct!.Value)
                        {
                            anyMatch = true;
                            break;
                        }
                    }

                    if (!anyMatch)
                    {
                        Emit(new Jump(nextCaseLabel));
                        skipBody = true;
                    }
                    else
                    {
                        ctAlreadyMatched = true;
                    }
                }
                else if (alts.Count == 1)
                {
                    Temporary cmpRes = MakeTemp();
                    Emit(new Binary(PyMCU.IR.BinaryOp.Equal, targetVal, altVals[0], cmpRes));
                    Emit(new JumpIfZero(cmpRes, nextCaseLabel));
                }
                else
                {
                    string matchLabel = MakeLabel();
                    foreach (var altVal in altVals)
                    {
                        Temporary cmp = MakeTemp();
                        Emit(new Binary(PyMCU.IR.BinaryOp.Equal, targetVal, altVal, cmp));
                        Emit(new JumpIfNotZero(cmp, matchLabel));
                    }

                    Emit(new Jump(nextCaseLabel));
                    Emit(new Label(matchLabel));
                }

                if (!skipBody)
                {
                    if (!string.IsNullOrEmpty(branch.CaptureName))
                    {
                        string qname = string.IsNullOrEmpty(currentFunction)
                            ? branch.CaptureName
                            : currentFunction + "." + branch.CaptureName;
                        DataType dt = targetVal is Variable v2
                            ? v2.Type
                            : (targetVal is Temporary t2 ? t2.Type : DataType.UINT8);
                        Emit(new Copy(targetVal, new Variable(qname, dt)));
                        variableTypes[qname] = dt;
                    }

                    if (branch.Guard != null)
                    {
                        Val g = VisitExpression(branch.Guard);
                        Emit(new JumpIfZero(g, nextCaseLabel));
                    }

                    // Non-CT match body: the pattern comparison was runtime, so the body
                    // is guarded by a runtime condition. Increment depth so that any
                    // CompileError raise inside the body is not a false-positive abort.
                    bool matchBodyIsRuntime = !allAltsConst;
                    if (matchBodyIsRuntime) _runtimeBranchDepth++;
                    if (branch.Body != null) VisitBlock((Block)branch.Body);
                    if (matchBodyIsRuntime) _runtimeBranchDepth--;
                    Emit(new Jump(endLabel));
                }
            }
            else
            {
                // Wildcard (case _:) — only runs if no prior case matched.
                // When ctAlreadyMatched is false the subject was runtime, so the wildcard
                // body is also runtime-guarded (we arrive here only if no case matched).
                if (!ctAlreadyMatched)
                {
                    if (!string.IsNullOrEmpty(branch.CaptureName))
                    {
                        string qname = string.IsNullOrEmpty(currentFunction)
                            ? branch.CaptureName
                            : currentFunction + "." + branch.CaptureName;
                        DataType dt = targetVal is Variable v2
                            ? v2.Type
                            : (targetVal is Temporary t2 ? t2.Type : DataType.UINT8);
                        Emit(new Copy(targetVal, new Variable(qname, dt)));
                        variableTypes[qname] = dt;
                    }

                    if (branch.Guard != null)
                    {
                        Val g = VisitExpression(branch.Guard);
                        Emit(new JumpIfZero(g, nextCaseLabel));
                    }

                    bool wildcardIsRuntime = !(targetVal is Constant);
                    if (wildcardIsRuntime) _runtimeBranchDepth++;
                    if (branch.Body != null) VisitBlock((Block)branch.Body);
                    if (wildcardIsRuntime) _runtimeBranchDepth--;
                    Emit(new Jump(endLabel));
                }
            }

            Emit(new Label(nextCaseLabel));
        }

        Emit(new Label(endLabel));
    }

    private void VisitWhile(WhileStmt stmt)
    {
        string startLabel = MakeLabel();
        string endLabel = MakeLabel();
        loopStack.Add(new LoopLabels { ContinueLabel = startLabel, BreakLabel = endLabel,
                                       FinallyDepth = finallyStack.Count });

        Emit(new Label(startLabel));

        int whileOpt = EmitOptimizedConditionalJump(stmt.Condition, endLabel, false);
        if (whileOpt == -1)
        {
            Emit(new Label(endLabel));
            loopStack.RemoveAt(loopStack.Count - 1);
            return;
        }

        if (whileOpt == 0)
        {
            Val condVal = VisitExpression(stmt.Condition);
            if (condVal is Constant c)
            {
                if (c.Value == 0) Emit(new Jump(endLabel));
            }
            else
            {
                Emit(new JumpIfZero(condVal, endLabel));
            }
        }

        VisitStatement(stmt.Body);
        Emit(new Jump(startLabel));
        Emit(new Label(endLabel));
        loopStack.RemoveAt(loopStack.Count - 1);
    }

    private void VisitBreak(BreakStmt stmt)
    {
        if (loopStack.Count == 0) throw UserError("Break statement outside of loop");
        var loop = Enumerable.Last<LoopLabels>(loopStack);
        EmitPendingFinally(loop.FinallyDepth);   // run finallys between this break and the loop
        Emit(new Jump(loop.BreakLabel));
    }

    private void VisitContinue(ContinueStmt stmt)
    {
        if (loopStack.Count == 0) throw UserError("Continue statement outside of loop");
        var loop = Enumerable.Last<LoopLabels>(loopStack);
        EmitPendingFinally(loop.FinallyDepth);   // run finallys between this continue and the loop
        Emit(new Jump(loop.ContinueLabel));
    }

    private void VisitRaise(RaiseStmt stmt)
    {
        if (stmt.ErrorType == "CompileError")
        {
            string msg = stmt.Message.Length > 0 ? stmt.Message : "CompileError";
            if (_runtimeBranchDepth == 0)
            {
                // Statically unconditional: the raise is reachable without any runtime
                // guard. Abort compilation immediately — this is the intended ZCA path.
                throw new ArchitectureError(msg, stmt.Line, 0);
            }

            // Inside a runtime-conditional branch: the const-propagation chain failed to
            // fold the guard to a compile-time value (e.g. mode: uint8 instead of
            // const[uint8]). Aborting would be a false positive — the raise might never
            // execute at runtime.
            // CompileError must NEVER mutate into a runtime instruction; it is a
            // compile-time-only concept. Emit nothing and warn the developer.
            Console.Error.WriteLine(
                $"warning: CompileError guard could not be verified at compile time " +
                $"(line {stmt.Line}): {msg}. " +
                "Ensure the guarding parameter is declared as const[...] so the branch can be pruned.");
            return;
        }

        Val code = ResolveBinding(stmt.ErrorType);

        // Inside a try body in the same function -> deliver to the local catch
        // dispatcher (jump, no T-flag, no return). Otherwise propagate to the caller.
        string? localCatch = tryCatchStack.Count > 0 ? tryCatchStack[^1] : null;
        Emit(new SignalError(code, localCatch));
    }

    private void VisitTry(TryStmt stmt)
    {
        // T-flag propagation model (replaces SJLJ setjmp/longjmp):
        //
        //   - Each CALL in the try body is followed by BranchOnError(catchDispatch)
        //     which emits BRTS on AVR — fires only when the callee set T=1 (SignalError).
        //   - Non-CanFail callees clear T via CLT before RET (injected by the backend for
        //     CanFail functions) or leave T=0 (they never touch T unless they signal error).
        //   - At catchDispatch, R22 holds the error code loaded by SignalError; the
        //     dispatch compares R22 against each handler's expected exception code.
        //
        // This replaces the 22-byte jmpbuf + _setjmp/_longjmp overhead with a single
        // BRTS per call site — zero cost on the happy path.

        bool hasFinally = stmt.Finally != null && stmt.Finally.Count > 0;
        string catchDispatch = MakeLabel();
        string afterLabel    = MakeLabel();

        // The error code lives in the error register (R22 on AVR) when BranchOnError
        // fires.  We use the sentinel Variable("__exn_r22_capture") as a read-only alias
        // for that register: the backend compiles LoadIntoReg("__exn_r22_capture", "R24")
        // as MOV R24, R22, with zero SRAM overhead.
        //
        // This avoids the stack-overlay collision that would occur if we saved R22 to an
        // SRAM slot that the StackAllocator might alias with a callee's local variable.
        // The comparison chain runs before any handler body, so R22 is stable throughout.
        Val exnCode = new Variable("__exn_r22_capture", DataType.UINT8);

        // Compile the try body. After each Call instruction, insert BranchOnError so
        // that any SignalError from the callee jumps to the catch dispatcher.
        int bodyStart = currentInstructions.Count;

        // A `raise` lexically inside this body is caught here (delivered straight to
        // catchDispatch) rather than propagated to the caller. Scope this to the body
        // only: a `raise` in a handler/finally is a re-raise and must propagate.
        // A finally is also pushed so a `return` escaping the body (or else) runs it first.
        bool pushedFinally = hasFinally;
        if (pushedFinally) finallyStack.Add(stmt.Finally!);
        tryCatchStack.Add(catchDispatch);
        foreach (var s in stmt.Body)
            VisitStatement(s);
        tryCatchStack.RemoveAt(tryCatchStack.Count - 1);

        // Post-process: find every Call emitted inside the try body and insert a
        // BranchOnError guard immediately after it. We iterate in reverse so that
        // inserting at position i does not shift the indices of earlier Calls.
        var callIndices = new List<int>();
        for (int i = bodyStart; i < currentInstructions.Count; i++)
            if (currentInstructions[i] is Call) callIndices.Add(i);

        for (int i = callIndices.Count - 1; i >= 0; i--)
            currentInstructions.Insert(callIndices[i] + 1, new BranchOnError(catchDispatch));

        // Happy path: the try body raised nothing. Run the `else` block (if any) FIRST — it is
        // emitted here, after the body's BranchOnError guards were inserted above, so a raise in
        // `else` is NOT caught by this try (it propagates), matching Python. Then the finally.
        if (stmt.ElseBody != null)
            foreach (var s in stmt.ElseBody)
                VisitStatement(s);
        // Pop the pending finally now: the remaining exits (happy, handlers, unmatched) emit it
        // explicitly, and a `return` inside the finally itself must not re-trigger it.
        if (pushedFinally) finallyStack.RemoveAt(finallyStack.Count - 1);
        EmitFinallyBody(stmt);
        Emit(new Jump(afterLabel));

        // ── Catch dispatcher ─────────────────────────────────────────────────
        Emit(new Label(catchDispatch));

        for (int i = 0; i < stmt.Handlers.Count; i++)
        {
            var (exnType, handlerBody) = stmt.Handlers[i];
            string skipLabel = MakeLabel();

            Val expectedCode = ResolveBinding(exnType);
            Val matchTemp = MakeTemp(DataType.UINT8);
            Emit(new Binary(PyMCU.IR.BinaryOp.Equal, exnCode, expectedCode, matchTemp));
            Emit(new JumpIfZero(matchTemp, skipLabel));

            // The finally is pending while the handler body runs, so a `return`/`break`/`continue`
            // inside the handler runs it first. Pop before the explicit finally on the handler's
            // normal exit (and so a return inside the finally does not re-trigger it).
            if (pushedFinally) finallyStack.Add(stmt.Finally!);
            foreach (var s in handlerBody)
                VisitStatement(s);
            if (pushedFinally) finallyStack.RemoveAt(finallyStack.Count - 1);

            EmitFinallyBody(stmt);
            Emit(new Jump(afterLabel));

            Emit(new Label(skipLabel));
        }

        // No handler matched (or finally-only): the error is NOT handled here, so it must keep
        // propagating — not halt unconditionally. Run finally, then re-deliver the still-pending
        // error (R22 holds its code; SignalError code 0 leaves R22 untouched):
        //   - an enclosing try in this function catches it (re-deliver to its dispatcher);
        //   - otherwise re-raise to the caller (RET with T set) so normal uncaught propagation
        //     carries it up — reaching main, where it halts via __pymcu_unhandled_exn;
        //   - in main itself there is no caller, so halt directly.
        if (hasFinally) EmitFinallyBody(stmt);
        string? enclosingCatch = tryCatchStack.Count > 0 ? tryCatchStack[^1] : null;
        if (enclosingCatch != null)
            Emit(new SignalError(new Constant(0), enclosingCatch));
        else if (currentFunction != "main")
            Emit(new SignalError(new Constant(0), null));
        else
            Emit(new Call("__pymcu_unhandled_exn", new List<Val>(), new NoneVal()));

        Emit(new Label(afterLabel));
    }

    private void EmitFinallyBody(TryStmt stmt)
    {
        if (stmt.Finally == null) return;
        foreach (var s in stmt.Finally)
            VisitStatement(s);
    }

    // Run the pending finally blocks above `floor` (innermost first) on a control-flow exit that
    // escapes them: `return` runs all (floor 0); `break`/`continue` run only those between the
    // statement and the loop. The run slice is removed while running so a return inside one of
    // those finallys does not re-run it (outer finallys below `floor` stay pending).
    private void EmitPendingFinally(int floor = 0)
    {
        if (finallyStack.Count <= floor) return;
        var slice = finallyStack.GetRange(floor, finallyStack.Count - floor);
        var saved = finallyStack;
        finallyStack = finallyStack.GetRange(0, floor);
        for (int k = slice.Count - 1; k >= 0; k--)
            foreach (var s in slice[k])
                VisitStatement(s);
        finallyStack = saved;
    }
}