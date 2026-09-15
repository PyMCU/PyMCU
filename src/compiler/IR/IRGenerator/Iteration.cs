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
    /// Binds one unrolled element to the loop variable, and says whether it could. A number
    /// binds as it always has; a STRING binds as a string constant, which is what a `const`
    /// parameter needs and what `case "PD6"` matches on.
    ///
    /// Only integers were accepted, so the CircuitPython idiom for a row of pins --
    /// `for pin in (board.D2, board.D3, board.D4)` -- was refused as "elements must be
    /// compile-time integer constants" for elements that are compile-time constants, and every
    /// guide with more than one pin had to be written out one call per pin (#308).
    /// </summary>
    private bool BindUnrolledElement(string key, Expression elem)
    {
        if (TryEvalConstElement(elem, out int iv))
        {
            constantVariables[key] = iv;
            strConstantVariables.Remove(key);
            return true;
        }

        if (!TryEvalConstStrElement(elem, out var text)) return false;

        strConstantVariables[key] = text;
        // A one-character string is its own character code in expression position and an
        // interned id through a name. The unrolled name has to be indistinguishable from the
        // literal it stands for, which is the state the read path expects.
        if (text.Length == 1) constantVariables[key] = text[0];
        else constantVariables.Remove(key);
        return true;
    }

    /// <summary>
    /// The compile-time TEXT of an element that is not a number: a string literal, a name
    /// bound to one, or a module or class constant such as `board.D2`. Emits nothing -- an
    /// element that turns out to need code is not a constant, and anything it wrote is undone.
    /// </summary>
    private bool TryEvalConstStrElement(Expression e, out string text)
    {
        text = "";
        if (e is StringLiteral sl) { text = sl.Value; return true; }
        if (e is not VariableExpr && e is not MemberAccessExpr) return false;

        int before = currentInstructions.Count;
        Val v;
        try
        {
            v = VisitExpression(e);
        }
        catch (PyMCU.Common.CompilerError)
        {
            if (currentInstructions.Count > before)
                currentInstructions.RemoveRange(before, currentInstructions.Count - before);
            return false;
        }

        if (currentInstructions.Count > before)
        {
            currentInstructions.RemoveRange(before, currentInstructions.Count - before);
            return false;
        }

        if (v is not Constant c) return false;
        if (!string.IsNullOrEmpty(c.Text)) { text = c.Text!; return true; }
        // Interned ids start at 256, so a value in that range is a string and nothing else.
        if (c.Value >= 256 && stringIdToStr.TryGetValue(c.Value, out var interned))
        {
            text = interned;
            return true;
        }
        return false;
    }

    // The compile-time array a bare name denotes: its base key and length, or a negative length
    // when the name is not one. The probe order is inline expansion, enclosing function, bare
    // name, then the alias chain -- the same order every other lookup on this path uses.
    private void ResolveForBase(string name, out string baseKey, out int size)
    {
        baseKey = "";
        size = -1;
        if (!string.IsNullOrEmpty(currentInlinePrefix))
        {
            string key = currentInlinePrefix + name;
            if (arraySizes.TryGetValue(key, out int s)) { size = s; baseKey = key; }
        }
        if (size < 0 && !string.IsNullOrEmpty(currentFunction))
        {
            string key = currentFunction + "." + name;
            if (arraySizes.TryGetValue(key, out int s)) { size = s; baseKey = key; }
        }
        if (size < 0 && arraySizes.TryGetValue(name, out int s2)) { size = s2; baseKey = name; }
        if (size < 0)
        {
            int s3 = ResolveAliasedArraySize(name, out var b3);
            if (s3 > 0) { size = s3; baseKey = b3; }
        }
    }

    // Unrolls `for v in <compile-time array>` over `base__0` .. `base__(size-1)`. The elements
    // are scalars, ZCA instances, or an SRAM-resident array read with an indexed load.
    private void EmitSequenceUnroll(ForStmt stmt, string forBase, int forSize)
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
    }

    /// <summary>
    /// The elements of a name bound to a short all-constant list/tuple, following aliases the
    /// way the parameter lookup does. Null when the name is not such a binding.
    /// </summary>
    /// <summary>
    /// The elements a `range(...)` or a `reversed(...)` stands for, when every bound folds and
    /// the receiver is a sequence the compiler can already see (PyMCU#363). Null when it is
    /// neither, or when a bound is only known at run time.
    ///
    /// This is what lets a range be given a NAME. `range()` is not a value on this target and
    /// stays refused as one; a name whose every use is `for x in name` or `reversed(name)` is
    /// not a value either, it is a compile-time sequence, which the compiler already has a
    /// representation for.
    /// </summary>
    private List<Expression>? ConstSequenceFromRange(Expression value)
    {
        if (value is not CallExpr call || call.Callee is not VariableExpr callee) return null;

        if (callee.Name == "reversed" && call.Args.Count == 1)
        {
            var inner = call.Args[0] switch
            {
                VariableExpr ve => ResolveConstSequence(ve.Name),
                ListExpr le => le.Elements,
                var other => ConstSequenceFromRange(other),
            };
            if (inner == null) return null;
            var flipped = new List<Expression>(inner);
            flipped.Reverse();
            return flipped;
        }

        if (callee.Name != "range" || call.Args.Count is < 1 or > 3) return null;

        var bounds = new List<int>();
        foreach (var a in call.Args)
        {
            if (!TryRangeBound(a, out int b)) return null;
            bounds.Add(b);
        }
        int start = call.Args.Count == 1 ? 0 : bounds[0];
        int stop = call.Args.Count == 1 ? bounds[0] : bounds[1];
        int step = call.Args.Count == 3 ? bounds[2] : 1;
        if (step == 0) return null;

        var elems = new List<Expression>();
        for (int i = start; step > 0 ? i < stop : i > stop; i += step)
        {
            elems.Add(new IntegerLiteral(i) { Line = value.Line });
            // A range whose element count runs away is not a sequence anyone meant to unroll;
            // the caller's limit rejects it, and this keeps the expansion bounded meanwhile.
            if (elems.Count > ConstSequenceUnrollLimit) break;
        }
        return elems;
    }

    /// <summary>
    /// One bound of a range that is being given a name, as a compile-time number.
    ///
    /// The cheap evaluator answers for a literal and for a name it is already tracking. A bound
    /// that is a FIELD is the case the register drivers write -- `range(self.register_width, 0,
    /// -1)` -- and it only becomes a number once the instance is folded, which is what lowering
    /// the expression does. Lowering a bound that turns out not to fold can leave a load behind,
    /// which is harmless: the caller then declines the binding and the assignment is refused, so
    /// nothing reaches a backend.
    /// </summary>
    private bool TryRangeBound(Expression e, out int value)
    {
        if (TryEvalConstElement(e, out value)) return true;
        if (VisitExpression(e) is Constant c) { value = c.Value; return true; }
        value = 0;
        return false;
    }

    /// <summary>
    /// The key a name is registered under when it was bound from a `range(...)`, or null. Same
    /// candidate order as ResolveConstSequence, so the two answer about the same binding.
    /// </summary>
    private string? RangeBoundKeyOf(string name)
    {
        string?[] candidates =
        {
            !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name : null,
            !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : null,
            !string.IsNullOrEmpty(currentModulePrefix) ? currentModulePrefix + name : null,
            name,
        };
        foreach (var candidate in candidates)
            if (candidate != null && rangeBoundSequences.Contains(candidate)) return name;
        return null;
    }

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

    // The (start, stop, step) of a plain `for x in range(...)` when all three are known at
    // compile time, whatever shape they were written in, or null when one of them is decided
    // at run time. The trip count is the caller's business.
    //
    // Only a literal or a NAME folded before, so an EXPRESSION that is a constant -- which is
    // what `range(total_us // 60000000)` is after the fold -- was lowered as a run-time counter
    // loop over a 32-bit bound: 1814 bytes on a 380-byte program (PyMCU#326). The evaluator
    // throws on anything whose value is not fixed, which is the answer wanted, and emits
    // nothing, so asking it speculatively costs nothing either.
    private (int Start, int Stop, int Step)? RangeFoldedBounds(ForStmt stmt)
    {
        int? Bound(Expression? e, int whenAbsent)
        {
            if (e == null) return whenAbsent;
            if (e is IntegerLiteral il) return il.Value;
            if (e is UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral n }) return -n.Value;
            // The general constant evaluator knows the unrolled loop variables, the
            // inline-expansion bindings and the module's own constants -- so `WIDTH = 4` then
            // `range(WIDTH)` folds exactly like `range(4)`, and so does `range(WIDTH // 2)`.
            // Asked here with locals in scope, which is what a bound computed into one needs.
            bool savedFold = foldLocalConstants;
            foldLocalConstants = true;
            try { return EvaluateConstantExpr(e); }
            catch { return null; }
            finally { foldLocalConstants = savedFold; }
        }

        if (Bound(stmt.RangeStart, 0) is not { } start) return null;
        if (Bound(stmt.RangeStop, 0) is not { } stop) return null;
        if (Bound(stmt.RangeStep, 1) is not { } step || step == 0) return null;

        return (start, stop, step);
    }

    // The same bounds, but only when the trip count is at most
    // <see cref="ConstSequenceUnrollLimit"/> -- the loops the unroller takes.
    private (int Start, int Stop, int Step)? RangeUnrollBounds(ForStmt stmt)
    {
        if (RangeFoldedBounds(stmt) is not { } b) return null;
        long trips = RangeTripCount(b.Start, b.Stop, b.Step);
        if (trips <= 0 || trips > ConstSequenceUnrollLimit) return null;
        return b;
    }

    // How many values range(start, stop, step) visits, for a non-zero step. Every reading of a
    // range -- the unroller, the comprehension expanders, reversed(range()) -- goes through
    // this one formula so they agree on the length.
    internal static long RangeTripCount(long start, long stop, long step)
        => step > 0
            ? (stop > start ? (stop - start + step - 1) / step : 0)
            : (stop < start ? (start - stop - step - 1) / -step : 0);

    // The program's own annotation for a loop variable, or null when it has none. Same
    // three-key lookup as InferExprType (Core.cs), but "absent" is not "uint8", and a type an
    // earlier range loop over the same name inferred is not a declaration (see
    // rangeInferredCounterKeys).
    private DataType? DeclaredLoopVarType(string bareName)
    {
        foreach (var start in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + bareName,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + bareName,
            bareName,
        })
        {
            if (start == null) continue;
            var key = start;
            for (var i = 0; i < 20; ++i)
            {
                if (rangeInferredCounterKeys.Contains(key)) return null;
                if (variableTypes.TryGetValue(key, out var t)) return IsIntegerType(t) ? t : null;
                if (variableAliases.TryGetValue(key, out var alias)) key = alias;
                else break;
            }
        }
        return null;
    }

    // The key a loop variable is stored under: the same qualification the body uses to read it.
    private string QualifyLoopVar(string bareName) =>
        !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + bareName
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + bareName : bareName);

    private (long Lo, long Hi) OperandRange(Val v)
        => v is Constant or Temporary or Variable ? ValRange(v) : RangeOfType(GetValType(v));

    // Every value the counter of range(start, stop, step) holds, including the one it stops
    // on. That last value is start + trips*step, which passes stop by up to |step| - 1: with a
    // unit step the counter never passes stop and the bounds alone decide; otherwise the
    // overshoot is exact when everything is constant, and one extra step wide when a bound is
    // only known at run time. stop is included even when the loop is empty, so both sides of
    // the exit test share one signedness (range(0, -5) compares signed, not by luck).
    private (long Lo, long Hi) CounterValueRange(Val startVal, Val stopVal, Val stepVal)
    {
        var (sLo, sHi) = OperandRange(startVal);
        var (eLo, eHi) = OperandRange(stopVal);
        long lo = Math.Min(sLo, eLo), hi = Math.Max(sHi, eHi);
        if (startVal is Constant sc && stopVal is Constant ec && stepVal is Constant kc && kc.Value != 0)
        {
            long last = sc.Value + RangeTripCount(sc.Value, ec.Value, kc.Value) * kc.Value;
            return (Math.Min(lo, last), Math.Max(hi, last));
        }
        var (kLo, kHi) = OperandRange(stepVal);
        if (kHi > 1) hi = Math.Max(hi, eHi + kHi - 1);
        if (kLo < -1) lo = Math.Min(lo, eLo + kLo + 1);
        return (lo, hi);
    }

    // `for v in reversed(range(a, b, s))` as the range it is: from the last value visited down
    // to a, by -s. Constant bounds give constants; runtime bounds are supported for a unit step
    // (`range(b - 1, a - 1, -1)`), and any other step is refused naming the spelling to use.
    private ForStmt ReversedRangeFor(ForStmt stmt, CallExpr range)
    {
        var args = range.Args;
        if (args.Count is < 1 or > 3) throw UserError("range() takes 1 to 3 arguments", range.Callee);
        Expression a = args.Count >= 2 ? args[0] : new IntegerLiteral(0) { Line = stmt.Line };
        Expression b = args.Count >= 2 ? args[1] : args[0];
        Expression s = args.Count == 3 ? args[2] : new IntegerLiteral(1) { Line = stmt.Line };
        int? Const(Expression e) { try { return EvaluateConstantExpr(e); } catch (Exception) { return null; } }

        ForStmt rewritten;
        if (Const(s) is not { } cs)
            throw UserError("reversed(range()) needs a compile-time constant step", args[2]);
        if (cs == 0) throw UserError("for-in range() step cannot be zero.", args[2]);
        if (Const(a) is { } ca && Const(b) is { } cb)
        {
            long trips = RangeTripCount(ca, cb, cs);
            int last = (int)(ca + (trips - 1) * cs);
            rewritten = trips > 0
                ? new ForStmt(stmt.VarName, new IntegerLiteral(last), new IntegerLiteral(ca - cs),
                              new IntegerLiteral(-cs), stmt.Body)
                : new ForStmt(stmt.VarName, new IntegerLiteral(ca), new IntegerLiteral(ca),
                              new IntegerLiteral(1), stmt.Body);
        }
        else if (cs is 1 or -1)
        {
            Expression Shift(Expression e, int by) =>
                new BinaryExpr(e, by < 0 ? Frontend.BinaryOp.Sub : Frontend.BinaryOp.Add,
                               new IntegerLiteral(Math.Abs(by))) { Line = stmt.Line };
            rewritten = new ForStmt(stmt.VarName, Shift(b, -cs), Shift(a, -cs), new IntegerLiteral(-cs), stmt.Body);
        }
        else
            throw UserError(
                "reversed(range()) with a step other than 1 or -1 needs compile-time constant " +
                "bounds; write the descending range directly, e.g. range(last, start - step, -step)",
                args[2]);
        rewritten.Line = stmt.Line;
        if (loopVarReadAfter.Contains(stmt)) loopVarReadAfter.Add(rewritten);
        return rewritten;
    }

    // `x in range(a, b, s)` as the comparisons it stands for: `a <= x and x < b` (descending:
    // `b < x and x <= a`), and for a step past 1 `(x - a) % s == 0` as well. Operands that
    // are not a name or a literal are bound to a local first, so each is evaluated once.
    private Expression RangeMembershipAst(Expression x, CallExpr range, bool negate, Expression at)
    {
        var args = range.Args;
        if (args.Count is < 1 or > 3) throw UserError("range() takes 1 to 3 arguments", range.Callee);
        Expression a = args.Count >= 2 ? args[0] : new IntegerLiteral(0) { Line = at.Line };
        Expression b = args.Count >= 2 ? args[1] : args[0];
        int step = 1;
        if (args.Count == 3)
        {
            try { step = EvaluateConstantExpr(args[2]); }
            catch (Exception) { throw UserError("'in range(...)' needs a compile-time constant step", args[2]); }
            if (step == 0) throw UserError("range() step cannot be zero.", args[2]);
        }

        Expression Pinned(Expression e)
        {
            if (e is IntegerLiteral or VariableExpr or UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral })
                return e;
            string name = "__in_range_" + (_inRangeCounter++);
            VisitStatement(new AssignStmt(new VariableExpr(name) { Line = at.Line }, e) { Line = at.Line });
            return new VariableExpr(name) { Line = at.Line };
        }
        x = Pinned(x); a = Pinned(a); b = Pinned(b);

        Expression Bin(Expression l, Frontend.BinaryOp op, Expression r) => new BinaryExpr(l, op, r) { Line = at.Line };
        Expression cond = step > 0
            ? Bin(Bin(a, Frontend.BinaryOp.LessEq, x), Frontend.BinaryOp.And, Bin(x, Frontend.BinaryOp.Less, b))
            : Bin(Bin(b, Frontend.BinaryOp.Less, x), Frontend.BinaryOp.And, Bin(x, Frontend.BinaryOp.LessEq, a));
        if (step is not (1 or -1))
            cond = Bin(cond, Frontend.BinaryOp.And,
                Bin(Bin(Bin(x, Frontend.BinaryOp.Sub, a), Frontend.BinaryOp.Mod, new IntegerLiteral(step)),
                    Frontend.BinaryOp.Equal, new IntegerLiteral(0)));
        return negate ? new UnaryExpr(Frontend.UnaryOp.Not, cond) { Line = at.Line } : cond;
    }

    private int _inRangeCounter;

    // The type of a range() counter: the program's annotation when there is one, otherwise the
    // narrowest integer type that holds every value the counter takes. `for i in range(N)` with
    // N up to 255 stays the 8-bit loop it always was; range(300), a uint16 stop variable, a
    // descending range past 127 or a step that overshoots widen exactly as far as they need.
    private DataType RangeCounterType(ForStmt stmt, string varName, Val startVal, Val stopVal, Val stepVal)
    {
        foreach (var (v, node) in new[]
                 {
                     (startVal, stmt.RangeStart ?? stmt.RangeStop!),
                     (stopVal, stmt.RangeStop!),
                     (stepVal, stmt.RangeStep ?? stmt.RangeStop!),
                 })
            if (!IsIntegerType(GetValType(v)))
                throw UserError("range() bounds must be integers", node);

        var (lo, hi) = CounterValueRange(startVal, stopVal, stepVal);
        bool exact = startVal is Constant && stopVal is Constant && stepVal is Constant;
        return ChooseCounterType(stmt, varName, lo, hi, exact);
    }

    // The declared type when the loop variable has one (refused when `exact` constant bounds
    // do not fit it), otherwise the narrowest type for [lo, hi], remembered as inferred.
    private DataType ChooseCounterType(ForStmt stmt, string varName, long lo, long hi, bool exact)
    {
        DataType inferred = NarrowestTypeFor(lo, hi);
        if (DeclaredLoopVarType(stmt.VarName) is { } declared)
        {
            var (dLo, dHi) = RangeOfType(declared);
            if (exact && (lo < dLo || hi > dHi))
                throw UserError(
                    $"loop variable '{stmt.VarName}' is declared {declared.ToString().ToLowerInvariant()} " +
                    $"but range(...) reaches {(hi > dHi ? hi : lo)}; declare it " +
                    $"{inferred.ToString().ToLowerInvariant()} or drop the annotation", stmt.RangeStop!);
            return declared;
        }
        rangeInferredCounterKeys.Add(varName);
        return inferred;
    }

    private void EmitUnrolledIteration(Statement body, string breakLabel)
    {
        if (breakLabel.Length == 0) { VisitStatement(body); return; }

        // A `continue` or a `break` gives this iteration MORE THAN ONE WAY IN AND OUT, and a
        // local is only known-constant between its assignment and the next write ON EVERY PATH
        // THAT REACHES THE READ. An unrolled body with either of those has paths the linear
        // lowering did not walk: control arrives at the next iteration from a `continue` in the
        // middle of this one, carrying whatever that path left. Folding from the linear state
        // then answers with a value no run of the program holds -- measured on
        // fixtures/continue-break, whose two accumulators came out 100, 100 where the program
        // computes 70, 30.
        //
        // An unrolled body WITHOUT them keeps its folds, which is where the win is: every
        // iteration is lowered on its own and its state is exactly what reaches it.
        InvalidateConstantsAssignedIn(body);

        string cont = MakeLabel();
        loopStack.Add(new LoopLabels { ContinueLabel = cont, BreakLabel = breakLabel, FinallyDepth = finallyStack.Count });
        VisitStatement(body);
        loopStack.RemoveAt(loopStack.Count - 1);
        Emit(new Label(cont));
    }

    private void VisitFor(ForStmt stmt)
    {
        // The loop rebinds its variable, so whatever the name held before the loop stops being
        // true inside it. The unroller puts a value back per iteration; a run-time loop does
        // not, and must not answer with the one from before.
        ForgetLocalConstant(stmt.VarName);
        if (!string.IsNullOrEmpty(stmt.Var2Name)) ForgetLocalConstant(stmt.Var2Name);

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
                    // A RUN-TIME loop: the body is lowered once and runs many times, so nothing
                    // it can write may be folded from the value it holds on the way in. The
                    // range and while paths have always done this; these two did not, and it
                    // went unnoticed until reads of locals began to fold (#331) -- an
                    // accumulator then read its starting value on every pass and
                    // `for v in x: total = total + v` answered 0.
                    InvalidateConstantsAssignedIn(stmt.Body);

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
                    // Deliberately unlocated. `elem` is the CALLER's literal, reached by
                    // resolving the parameter, while this diagnostic is reported against the
                    // callee's file and line. ASTNode carries no file, so pointing at the
                    // element would state main.py's column against helper.py's line: a location
                    // that does not exist. Measured, not assumed. Same rule at the two other
                    // resolved-by-name sites below (the dict/set walk and enumerate()).
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

            // `for v in self._levels:` — the same unroll when the sequence of NUMBERS was stored
            // in a field. Runs after the instance-sequence path above, so a field holding pins
            // binds instances and a field holding numbers binds constants.
            if (iter is MemberAccessExpr && ResolveConstSequenceExpr(iter) is { } memConstSeq)
            {
                string memBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (var elem in memConstSeq)
                {
                    if (!TryEvalConstElement(elem, out int mv))
                        throw UserError("for-in over a sequence held in a field needs compile-time integer elements.");
                    constantVariables[varKey] = mv;
                    EmitUnrolledIteration(stmt.Body, memBrk);
                }
                if (memBrk.Length > 0) Emit(new Label(memBrk));

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
                                $"pair, so there is nothing to give {stmt.Var2Name}.", elem);

                        var parts = elem is ListExpr pl ? pl.Elements : ((TupleExpr)elem).Elements;
                        if (parts.Count != 2)
                            throw UserError(
                                $"'for {stmt.VarName}, {stmt.Var2Name} in ...' unpacks two names, and this " +
                                $"element has {parts.Count}. Every element has to carry exactly two values.",
                                elem);

                        // Both are evaluated before the check so the diagnostic can point at
                        // WHICH of the two is not a constant. Short-circuiting the || would
                        // leave the second unevaluated and the caret with nothing to choose
                        // between.
                        bool pairOk0 = BindUnrolledElement(varKey, parts[0]);
                        bool pairOk1 = BindUnrolledElement(varKey2, parts[1]);
                        if (!pairOk0 || !pairOk1)
                            throw UserError(
                                "for-in over a list of pairs unrolls at compile time, so both values in " +
                                "each pair have to be constants -- a number, or a string such as a board " +
                                "pin name. Read the run-time value inside the body instead.",
                                pairOk0 ? parts[1] : parts[0]);

                        EmitUnrolledIteration(stmt.Body, llBrk);
                        continue;
                    }

                    if (BindUnrolledElement(varKey, elem))
                    {
                        EmitUnrolledIteration(stmt.Body, llBrk);
                    }
                    // A tuple element with a single loop name is the shape that used to be
                    // reported as a non-constant element while every element was a constant.
                    // What is missing is a name to unpack it into, which is what it says now.
                    else if (elem is ListExpr or TupleExpr)
                        throw UserError(
                            $"each element here is a pair, and '{stmt.VarName}' is one name, so there is " +
                            $"nowhere to put the second value. Write 'for {stmt.VarName}, second in ...' to " +
                            "unpack both.", elem);
                    else throw UserError(
                        "for-in list/tuple iterable elements must be compile-time constants -- a number, "
                        + "or a string such as a board pin name.", elem);
                }
                if (llBrk.Length > 0) Emit(new Label(llBrk));

                constantVariables.Remove(varKey);
                strConstantVariables.Remove(varKey);
                if (varKey2 != null)
                {
                    constantVariables.Remove(varKey2);
                    strConstantVariables.Remove(varKey2);
                }
                return;
            }

            // Iterating a dict or a set. Both bind a compile-time table and no storage, so
            // there is no run-time sequence to walk -- and none is needed: the entries are
            // known here, so the loop unrolls over them exactly as a constant list literal
            // already does, and the loop variable is a compile-time constant inside the body
            // the same way (issue #200). `d[k]` in the body then folds to that entry's value.
            //
            // The order is the order written. For a dict that is CPython's order too; for a
            // set CPython iterates in hash order, and a compile-time membership table has no
            // hashes, so source order is the only answer that is stable across builds.
            {
                // The element as the program wrote it, for a message that shows the list to
                // write instead. Anything else is shown as `...`: a wrong rendering in advice
                // is worse than an honest gap in it.
                static string SourceOfElement(Expression e) => e switch
                {
                    IntegerLiteral il => il.Value.ToString(),
                    StringLiteral sl2 => "\"" + sl2.Value + "\"",
                    BooleanLiteral bl2 => bl2.Value ? "True" : "False",
                    UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral ng } => "-" + ng.Value,
                    _ => "...",
                };

                DictExpr? DictOf(Expression e) =>
                    e as DictExpr
                    ?? (e is VariableExpr dv && TryGetDictBinding(dv.Name, out var bd) ? bd : null);
                SetExpr? SetOf(Expression e) =>
                    e as SetExpr
                    ?? (e is VariableExpr sv && TryGetSetBinding(sv.Name, out var bs) ? bs : null);

                List<Expression>? dsElems = null;                       // one name per element
                List<(Expression Key, Expression Value)>? dsPairs = null;  // two, from items()
                string dsWhat = "";

                // True when the entries came from a literal written at the `for`. A name
                // resolved to a binding may have been written in another module, and a node
                // carries no file, so blaming one of those entries would put this file's line
                // against that file's column. See the note at the list-parameter site above.
                bool dsWrittenHere = iter is DictExpr or SetExpr;

                if (DictOf(iter) is { } dIter)
                {
                    dsElems = dIter.Entries.Select(en => en.Key).ToList();
                    dsWhat = "dict";
                }
                else if (SetOf(iter) is { } sIter)
                {
                    // Not unrolled, and the order is why. CPython walks a set in hash-table
                    // slot order: {30, 5} gives 5 then 30, and {70, 7} gives 70 then 7, which
                    // is neither the order written nor sorted. A compile-time membership table
                    // has no hashes and no table, so any order chosen here would silently
                    // disagree with the host on some literal, which is worse than not walking
                    // it at all. A dict IS unrolled, because its order is insertion order and
                    // that is exactly what the source says.
                    throw UserError(
                        $"a set is not iterated here: CPython walks it in hash order, and a "
                        + "compile-time membership table has no hashes to reproduce it with, so "
                        + "the order would differ from the one your program prints on the host. "
                        + $"Write the values as a list to walk them in order (`for {stmt.VarName} "
                        + $"in [{string.Join(", ", sIter.Elements.Take(3).Select(SourceOfElement))}"
                        + (sIter.Elements.Count > 3 ? ", ..." : "") + "]`), or keep the set and "
                        + $"ask it what it holds (`{stmt.VarName} in ...`), which is what it is for.",
                        iter);
                }
                else if (iter is CallExpr { Callee: MemberAccessExpr dsMa } dsCall
                         && dsCall.Args.Count == 0 && DictOf(dsMa.Object) is { } mDict)
                {
                    switch (dsMa.Member)
                    {
                        case "keys":
                            dsElems = mDict.Entries.Select(en => en.Key).ToList();
                            dsWhat = "dict";
                            break;
                        case "values":
                            dsElems = mDict.Entries.Select(en => en.Value).ToList();
                            dsWhat = "dict";
                            break;
                        case "items":
                            dsPairs = mDict.Entries.Select(en => (en.Key, en.Value)).ToList();
                            dsWhat = "dict";
                            break;
                    }
                }

                if (dsElems != null || dsPairs != null)
                {
                    string dsWhich = dsPairs != null ? "items()" : dsWhat;
                    string? dsKey2 = string.IsNullOrEmpty(stmt.Var2Name) ? null
                        : (!string.IsNullOrEmpty(currentInlinePrefix)
                            ? currentInlinePrefix + stmt.Var2Name
                            : (!string.IsNullOrEmpty(currentFunction)
                                ? currentFunction + "." + stmt.Var2Name : stmt.Var2Name));

                    // Binds one entry to one loop name. A string key or value is bound as a
                    // string constant, which is what makes `d[k]` fold for the string-keyed
                    // dict this reads best on.
                    void BindOne(string key, Expression e, string role)
                    {
                        if (TryEvalConstElement(e, out int iv)) { constantVariables[key] = iv; return; }
                        if (e is StringLiteral sl)
                        {
                            strConstantVariables[key] = sl.Value;
                            // A one-character string is its own character code in expression
                            // position, and an interned id when read back through a name. The
                            // unrolled name has to be indistinguishable from the literal it
                            // stands for, so it is bound in both maps, which is the state the
                            // read path already expects for such a name.
                            if (sl.Value.Length == 1) constantVariables[key] = sl.Value[0];
                            return;
                        }
                        throw UserError(
                            $"a {dsWhat} is iterated by unrolling it at compile time, so every {role} has "
                            + $"to be a constant, and this one is not. Read the run-time value inside the "
                            + "body instead.",
                            dsWrittenHere ? e : null);
                    }
                    void Unbind(string key)
                    {
                        constantVariables.Remove(key);
                        strConstantVariables.Remove(key);
                    }

                    if (dsPairs != null && dsKey2 == null)
                        throw UserError(
                            $"'{stmt.VarName}' is one name and items() gives a key and a value, so there "
                            + $"is nowhere to put the value. Write 'for {stmt.VarName}, value in ...', or "
                            + "iterate the dict itself for the keys alone.");
                    if (dsPairs == null && dsKey2 != null)
                        throw UserError(
                            $"iterating a {dsWhich} gives one value at a time, and this unpacks two names. "
                            + (dsWhat == "dict"
                                ? "Write 'for k, v in d.items()' for both, or one name for the keys."
                                : "Write one name."));

                    string dsBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                    if (dsPairs != null)
                        foreach (var (kE, vE) in dsPairs)
                        {
                            BindOne(varKey, kE, "key");
                            BindOne(dsKey2!, vE, "value");
                            EmitUnrolledIteration(stmt.Body, dsBrk);
                        }
                    else
                        foreach (var elem in dsElems!)
                        {
                            BindOne(varKey, elem, dsWhat == "dict" ? "key" : "element");
                            EmitUnrolledIteration(stmt.Body, dsBrk);
                        }
                    if (dsBrk.Length > 0) Emit(new Label(dsBrk));

                    Unbind(varKey);
                    if (dsKey2 != null) Unbind(dsKey2);
                    return;
                }
            }

            if (iter is CallExpr call && call.Callee is VariableExpr calleeVar)
            {
                // The same check the expression dispatch runs. enumerate(), zip() and range()
                // reach the compiler as the ITERABLE of a `for` and never pass through
                // EmitBuiltinCall, so without this line their keywords fall through to the
                // lowerings below and are reported as something about the argument.
                call = CheckBuiltinKeywords(call, calleeVar.Name);
                iter = call;

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
                            throw UserError("for-in range() argument must be a compile-time constant.",
                                ArgAt(call, 0));
                        stop = sv.Value;
                    }
                    else if (call.Args.Count >= 2)
                    {
                        var sv = EvalConst(call.Args[0]);
                        var ev = EvalConst(call.Args[1]);
                        if (!sv.HasValue || !ev.HasValue)
                            throw UserError("for-in range() arguments must be compile-time constants.",
                                ArgAt(call, sv.HasValue ? 1 : 0));
                        start = sv.Value;
                        stop = ev.Value;
                        if (call.Args.Count >= 3)
                        {
                            var stv = EvalConst(call.Args[2]);
                            if (!stv.HasValue)
                                throw UserError("for-in range() step must be a compile-time constant.",
                                    ArgAt(call, 2));
                            step = stv.Value;
                        }
                    }
                    else throw UserError("for-in range() requires at least one argument.", call.Callee);

                    if (step == 0)
                        throw UserError("for-in range() step cannot be zero.", ArgAt(call, 2));
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
                    // Only the literal spelling was written at this `for`. The other two the
                    // switch below accepts reach a list through a NAME -- an @inline parameter
                    // bound to the caller's literal, or a module-level sequence -- and those
                    // elements belong to whichever file wrote them. See the note at the
                    // list-parameter site above.
                    bool enumWrittenHere = inner is ListExpr or TupleExpr;
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
                                    "enumerate() list/tuple elements must be compile-time integer constants.",
                                    enumWrittenHere ? elem : null);
                        }

                        constantVariables.Remove(idxKey);
                        constantVariables.Remove(valKey);
                        return;
                    }

                    // enumerate(range(...)) is the range loop itself with an index alongside:
                    // the plain range lowering runs the loop (unrolled when short and constant,
                    // a counter otherwise) and keeps the index for it. Before this only
                    // constant bounds were accepted, and they unrolled with no cap (PyMCU#288).
                    if (inner is CallExpr { Callee: VariableExpr { Name: "range" } } rcall)
                    {
                        var rargs = rcall.Args;
                        if (rargs.Count is < 1 or > 3)
                            throw UserError("range() takes 1 to 3 arguments", rcall.Callee);
                        var plain = new ForStmt(stmt.Var2Name,
                            rargs.Count >= 2 ? rargs[0] : null,
                            rargs.Count >= 2 ? rargs[1] : rargs[0],
                            rargs.Count == 3 ? rargs[2] : null,
                            stmt.Body) { Var2Name = stmt.VarName, Line = stmt.Line };
                        if (loopVarReadAfter.Contains(stmt)) loopVarReadAfter.Add(plain);
                        enumerateIndexFor[plain] = stmt.VarName;
                        VisitFor(plain);
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
                        "enumerate() argument must be a constant list literal, range(N), or a fixed-size array.",
                        ArgAt(call, 0));
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

                    // A zip side is anything `for x in ...` already walks: a fixed array by
                    // name, a compile-time sequence of instances held in a FIELD, or a ROW of a
                    // rectangular dict selected at run time. Each answers how many elements it
                    // has and how to bind the loop variable to element k; the unroll below does
                    // not care which it is. Before this, only the first was resolved, so
                    // `zip(self.segments, pattern)` -- the shape every segment driver has -- was
                    // refused as not being a constant array (PyMCU#337).
                    (int Len, Action<string, int> Bind)? ResolveSide(Expression e)
                    {
                        if (e is MemberAccessExpr && TryResolveInstanceSequence(e, out var isb, out int isn))
                            return (isn, (qk, k) => BindInstanceForIteration(isb + "__" + k, qk));

                        // A list of constants reached by name, through a parameter, or through
                        // a field. `zip(self.segments, ROW)` is the same walk with the values
                        // known, and a row selected by a CONSTANT key lands here too.
                        if (ResolveConstSequenceExpr(e) is { Count: > 0 } cseq)
                            return (cseq.Count, (qk, k) =>
                            {
                                if (TryEvalConstElement(cseq[k], out int cv)) constantVariables[qk] = cv;
                                else Emit(new Copy(VisitExpression(cseq[k]), new Variable(qk, DataType.UINT8)));
                                variableTypes[qk] = DataType.UINT8;
                            });

                        if (e is VariableExpr rve && ResolveRowView(rve.Name) is { } rv)
                            return (rv.Width, (qk, k) =>
                            {
                                Val off = EmitRowOffset(rv, k);
                                Val v = EmitFlashArrayRead(rv.Table, off, rv.Width);
                                Emit(new Copy(v, new Variable(qk, DataType.UINT8)));
                                variableTypes[qk] = DataType.UINT8;
                            });

                        if (ResolveArr(e) is not { } arr) return null;
                        bool sram = arraysWithVariableIndex.Contains(arr.Base) || moduleSramArrays.Contains(arr.Base);
                        return (arr.Size, (qk, k) =>
                        {
                            string ek = arr.Base + "__" + k;
                            if (instanceClasses.ContainsKey(ek)) { BindInstanceForIteration(ek, qk); return; }
                            variableTypes[qk] = arr.Elem;
                            if (sram)
                            {
                                Temporary tmp = MakeTemp(arr.Elem);
                                Emit(new ArrayLoad(arr.Base, new Constant(k), tmp, arr.Elem, arr.Size));
                                Emit(new Copy(tmp, new Variable(qk, arr.Elem)));
                            }
                            else if (constantVariables.TryGetValue(ek, out int cv)) constantVariables[qk] = cv;
                            else Emit(new Copy(new Variable(ek, arr.Elem), new Variable(qk, arr.Elem)));
                        });
                    }

                    if (ResolveSide(arg0) is { } side0 && ResolveSide(arg1) is { } side1)
                    {
                        string qk1 = !string.IsNullOrEmpty(currentInlinePrefix)
                            ? key1 : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);
                        string qk2 = !string.IsNullOrEmpty(currentInlinePrefix)
                            ? key2 : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.Var2Name : stmt.Var2Name);
                        int zlen = Math.Min(side0.Len, side1.Len);
                        bool zbrk = LoopBodyHasBreakOrContinue(stmt.Body);
                        string zBreak = zbrk ? MakeLabel() : "";

                        for (int k = 0; k < zlen; ++k)
                        {
                            string zCont = zbrk ? MakeLabel() : "";
                            if (zbrk) loopStack.Add(new LoopLabels { ContinueLabel = zCont, BreakLabel = zBreak, FinallyDepth = finallyStack.Count });
                            side0.Bind(qk1, k);
                            side1.Bind(qk2, k);
                            VisitStatement(stmt.Body);
                            if (zbrk) { loopStack.RemoveAt(loopStack.Count - 1); Emit(new Label(zCont)); }
                            CleanCtState(qk1);
                            CleanCtState(qk2);
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
                                        throw UserError(
                                            "zip() list elements must be compile-time integer constants.", elem);
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
                                        // The element has no node of its own: it is a slot of an
                                        // array, and what the reader wrote is the array's name.
                                        throw UserError(
                                            "zip() array elements must be compile-time integer constants.", v);
                                }

                                return vals;
                            }
                        }

                        throw UserError(
                            "zip() arguments must be constant list literals or constant arrays.", e);
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

                    // reversed(range(...)) is the same range walked from its last value down,
                    // so it goes through the one range lowering and unrolls, sizes its counter
                    // and keeps its variable exactly like a range written that way (PyMCU#288).
                    if (inner is CallExpr { Callee: VariableExpr { Name: "range" } } rrange)
                    {
                        VisitFor(ReversedRangeFor(stmt, rrange));
                        return;
                    }

                    // `reversed(order)` where the name is bound to a compile-time sequence, which
                    // is what `order = range(...)` binds it to (#363). Without this the message
                    // offered reversed() as a supported form and refused the name it was given.
                    if (inner is VariableExpr rve && ResolveConstSequence(rve.Name) is { } rseq)
                    {
                        for (int k = rseq.Count - 1; k >= 0; --k)
                        {
                            if (!TryEvalConstElement(rseq[k], out int rv))
                                throw UserError(
                                    $"reversed({rve.Name}): element {k} is not a compile-time constant.",
                                    rseq[k]);
                            constantVariables[valKey] = rv;
                            VisitStatement(stmt.Body);
                        }

                        constantVariables.Remove(valKey);
                        return;
                    }

                    if (inner is ListExpr le3)
                    {
                        for (int k = le3.Elements.Count - 1; k >= 0; --k)
                        {
                            if (le3.Elements[k] is IntegerLiteral il) constantVariables[valKey] = il.Value;
                            else
                                throw UserError(
                                    "reversed() list elements must be compile-time integer constants.",
                                    le3.Elements[k]);
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

                    throw UserError(
                        "reversed() argument must be a constant list literal or a constant array.", inner);
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
                    // A RUN-TIME loop: the body is lowered once and runs many times, so nothing
                    // it can write may be folded from the value it holds on the way in. The
                    // range and while paths have always done this; these two did not, and it
                    // went unnoticed until reads of locals began to fold (#331) -- an
                    // accumulator then read its starting value on every pass and
                    // `for v in x: total = total + v` answered 0.
                    InvalidateConstantsAssignedIn(stmt.Body);

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

            // for v in ct_array: — unroll over compile-time array (scalars or ZCA instances).
            // A MemberAccessExpr reaches the same shape through a field: `for p in self._pins:`
            // is how a driver that was handed a list of pins walks them.
            if (iter is VariableExpr or MemberAccessExpr)
            {
                string forBase = "";
                int forSize = -1;
                if (iter is MemberAccessExpr
                    && TryResolveInstanceSequence(iter, out var memSeqBase, out int memSeqCount))
                { forSize = memSeqCount; forBase = memSeqBase; }
                if (forSize < 0 && iter is VariableExpr forVarExpr2)
                    ResolveForBase(forVarExpr2.Name, out forBase, out forSize);

                if (forSize > 0)
                {
                    EmitSequenceUnroll(stmt, forBase, forSize);
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
                                "iterate range() with the stride explicitly",
                                slc.Step);
                        string slIdx = "__slci" + (++sliceLoopId);
                        // The index walks a fixed array, so it is as wide as the array's size
                        // needs and no wider: filed as declared, so the range lowering does not
                        // size it from a bound like `n + 1`, which promotes past a byte.
                        variableTypes[QualifyLoopVar(slIdx)] = NarrowestTypeFor(0, slSize);
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
                    if (step == 0) throw UserError("for-in slice step cannot be zero.", slc.Step);
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
                        $"'{sqVe.Name}' has a negative __len__ ({sqLen}), so the loop has no trip count.",
                        sqVe);
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
                    "range/fixed array instead. A `yield` generator function IS supported.", itVe);

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
                    $"v = {sqBadVe.Name}[i]`).", sqBadVe);

            throw UserError(
                "for-in loop iterable must be a compile-time string constant, a constant list literal [v0, v1, ...], range(N), enumerate(list/range), zip(a, b), reversed(iterable), or a fixed-array slice arr[lo:hi]. Use 'const[str]' type annotation for string parameters.",
                stmt.Iterable);
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
        // A range that is empty at compile time runs nothing. It used to be lowered as a
        // counter loop that immediately exits, which is a comparison, a jump and a counter of
        // whatever width the bound needed -- and the bound itself, recomputed at run time
        // (#326). Python never binds the loop variable for an empty range; the run-time
        // lowering left it at start, so a read after the loop keeps that.
        if (RangeFoldedBounds(stmt) is { } emptyBounds
            && RangeTripCount(emptyBounds.Start, emptyBounds.Stop, emptyBounds.Step) <= 0)
        {
            if (loopVarReadAfter.Contains(stmt))
            {
                string emptyKey = QualifyLoopVar(stmt.VarName);
                DataType emptyType = ChooseCounterType(stmt, emptyKey,
                    emptyBounds.Start, emptyBounds.Start, exact: true);
                variableTypes[emptyKey] = emptyType;
                Emit(new Copy(new Constant(emptyBounds.Start), new Variable(emptyKey, emptyType)));
            }
            return;
        }

        if (RangeUnrollBounds(stmt) is { } unroll)
        {
            (int unrollStart, int unrollStop, int unrollStep) = unroll;
            string unrollKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + stmt.VarName
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);
            bool unrollBreaks = LoopBodyHasBreakOrContinue(stmt.Body);
            string unrollBrk = unrollBreaks ? MakeLabel() : "";

            // Python leaves the loop variable at the last value visited. The body reads it as
            // a constant per iteration, but anything after the loop reads a real variable, so
            // the value has to be stored too -- once at the end when the loop always runs to
            // completion, or per iteration when a break decides where it stops. Unread, the
            // stores are dead and go with the rest. Before this the binding was dropped on
            // exit and `for i in range(3): ...` then `print(i)` printed 0.
            int unrollLast = (int)(unrollStart + (RangeTripCount(unrollStart, unrollStop, unrollStep) - 1) * unrollStep);
            DataType unrollType = ChooseCounterType(stmt, unrollKey,
                Math.Min(unrollStart, unrollLast), Math.Max(unrollStart, unrollLast), exact: true);
            variableTypes[unrollKey] = unrollType;
            var unrollVar = new Variable(unrollKey, unrollType);
            bool unrollReadAfter = loopVarReadAfter.Contains(stmt);

            // enumerate(range(...)): the index walks 0, 1, 2 ... next to the value.
            string? unrollIdxKey = null;
            Variable? unrollIdxVar = null;
            long unrollTrips = RangeTripCount(unrollStart, unrollStop, unrollStep);
            if (enumerateIndexFor.TryGetValue(stmt, out var unrollIdxName))
            {
                unrollIdxKey = QualifyLoopVar(unrollIdxName);
                var idxType = NarrowestTypeFor(0, unrollTrips);
                variableTypes[unrollIdxKey] = idxType;
                unrollIdxVar = new Variable(unrollIdxKey, idxType);
            }

            int unrollIdx = 0;
            for (int i = unrollStart; unrollStep > 0 ? i < unrollStop : i > unrollStop; i += unrollStep, unrollIdx++)
            {
                constantVariables[unrollKey] = i;
                if (unrollBreaks && unrollReadAfter) Emit(new Copy(new Constant(i), unrollVar));
                if (unrollIdxKey != null)
                {
                    constantVariables[unrollIdxKey] = unrollIdx;
                    if (unrollBreaks && unrollReadAfter) Emit(new Copy(new Constant(unrollIdx), unrollIdxVar!));
                }
                EmitUnrolledIteration(stmt.Body, unrollBrk);
            }
            if (unrollBrk.Length > 0) Emit(new Label(unrollBrk));

            constantVariables.Remove(unrollKey);
            if (unrollIdxKey != null) constantVariables.Remove(unrollIdxKey);
            if (!unrollBreaks && unrollReadAfter)
            {
                Emit(new Copy(new Constant(unrollLast), unrollVar));
                if (unrollIdxKey != null) Emit(new Copy(new Constant((int)unrollTrips - 1), unrollIdxVar!));
            }
            return;
        }

        Val startVal = stmt.RangeStart != null ? VisitExpression(stmt.RangeStart) : new Constant(0);
        Val stopVal = VisitExpression(stmt.RangeStop!);
        Val stepVal = stmt.RangeStep != null ? VisitExpression(stmt.RangeStep) : new Constant(1);

        // A zero step never advances the loop variable (Python raises ValueError). The
        // compile-time-unrolled path above already rejects this; mirror it for the runtime
        // loop, where a literal-zero step would otherwise emit an infinite loop.
        if (stepVal is Constant stepZero && stepZero.Value == 0)
            throw UserError("for-in range() step cannot be zero.", stmt.RangeStep);

        // Qualify the loop variable the same way the body resolves a variable reference:
        // function-scoped names get the `func.` prefix when not inline-expanded. Using the
        // inline-only prefix left a top-level loop variable bare ("i") while the body read it
        // as "func.i", so the counter and the body's reads were different registers — using `i`
        // in the body read 0 (e.g. `for i in range(n): acc += i` produced 0).
        string varName = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + stmt.VarName
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + stmt.VarName : stmt.VarName);
        // The counter was an unconditional UINT8 here, whatever the bounds said: range(300) ran
        // 44 times, range(0, 256) never ran, a descending range from 200 never ran, and a
        // uint16 stop variable or a uint16 annotation on the loop variable were both ignored.
        // Filed in variableTypes before the body is visited, so the body's reads and the
        // storage allocator see the same width the loop compares and steps.
        DataType counterType = RangeCounterType(stmt, varName, startVal, stopVal, stepVal);
        constantVariables.Remove(varName);
        variableTypes[varName] = counterType;
        var loopVar = new Variable(varName, counterType);
        Emit(new Copy(startVal, loopVar));
        // The exit fix-up below compares the counter with where it started; a start that is
        // not a constant is kept in a temporary of its own, since the body may rewrite the
        // variable it was read from.
        bool readAfter = loopVarReadAfter.Contains(stmt);

        // enumerate(range(...)): an index that starts at 0 and steps by 1 with the counter.
        Variable? enumIdxVar = null;
        if (enumerateIndexFor.TryGetValue(stmt, out var idxName))
        {
            string idxKey = QualifyLoopVar(idxName);
            var (cLo, cHi) = CounterValueRange(startVal, stopVal, stepVal);
            var idxType = NarrowestTypeFor(0, cHi - cLo + 1);
            variableTypes[idxKey] = idxType;
            enumIdxVar = new Variable(idxKey, idxType);
            Emit(new Copy(new Constant(0), enumIdxVar));
        }

        Val startKeep = startVal;
        if (readAfter && startVal is not Constant)
        {
            var keep = MakeTemp(counterType);
            Emit(new Copy(startVal, keep));
            startKeep = keep;
        }

        string startLabel = MakeLabel();
        string contLabel = MakeLabel();
        string endLabel = MakeLabel();
        // Where the exit test lands. A `break` lands on endLabel with the counter already at
        // the value Python leaves; the exit test lands one step past it, so when the value is
        // read after the loop that path gets a label of its own and a step back (below).
        string exitLabel = readAfter ? MakeLabel() : endLabel;
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
            Emit(new JumpIfLessOrEqual(loopVar, stopVal, exitLabel));
        else if (stepVal is not Constant && GetValType(stepVal).IsSigned())
        {
            // A step held in a signed variable is decided at run time, so the direction of
            // the exit test is too: step < 0 counts down to stop, anything else counts up. An
            // unsigned step cannot be negative and keeps the single compare below. Before
            // this the ascending test was used for every non-constant step, so range(10, 0,
            // step) with step = -2 exited before its first iteration.
            string negLabel = MakeLabel();
            string bodyLabel = MakeLabel();
            Emit(new JumpIfLessThan(stepVal, new Constant(0), negLabel));
            Emit(new JumpIfGreaterOrEqual(loopVar, stopVal, exitLabel));
            Emit(new Jump(bodyLabel));
            Emit(new Label(negLabel));
            Emit(new JumpIfLessOrEqual(loopVar, stopVal, exitLabel));
            Emit(new Label(bodyLabel));
        }
        else
            Emit(new JumpIfGreaterOrEqual(loopVar, stopVal, exitLabel));

        VisitStatement(stmt.Body);

        Emit(new Label(contLabel));
        Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, loopVar, stepVal));
        if (enumIdxVar != null) Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, enumIdxVar, new Constant(1)));
        Emit(new Jump(startLabel));

        // Python leaves the loop variable at the last value visited; the exit test fires one
        // step past it. When something after the loop reads the name, step it back on that
        // path -- unless the loop never ran, in which case the counter still holds `start`
        // and stays there (Python would have left the name as it was). A `break` skips this:
        // it lands on endLabel with the value it broke at. Nothing is emitted for a variable
        // no one reads afterwards, which is the common case and stays free.
        if (readAfter)
        {
            Emit(new Label(exitLabel));
            Emit(new JumpIfEqual(loopVar, startKeep, endLabel));
            Emit(new AugAssign(PyMCU.IR.BinaryOp.Sub, loopVar, stepVal));
            if (enumIdxVar != null) Emit(new AugAssign(PyMCU.IR.BinaryOp.Sub, enumIdxVar, new Constant(1)));
        }
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

    /// <summary>
    /// A condition folded to true or false at compile time, or null when it does not fold.
    ///
    /// Local to `assert` on purpose. EvaluateConstantExpr folds arithmetic and bitwise
    /// operators and nothing else, so a comparison and a `BooleanLiteral` both throw out of
    /// it; teaching IT to fold comparisons would change every caller that asks it for an
    /// array size or an address, which is far wider than an assert needs.
    /// </summary>
    private bool? FoldAssertCondition(Expression? cond)
    {
        switch (cond)
        {
            case null: return null;
            case BooleanLiteral b: return b.Value;

            case UnaryExpr { Op: Frontend.UnaryOp.Not } u:
                return FoldAssertCondition(u.Operand) is { } inner ? !inner : null;

            case BinaryExpr { Op: Frontend.BinaryOp.And } a:
                // Short-circuit: one false operand decides the whole thing even when the
                // other does not fold, which is what makes `assert 0 and f()` answerable.
                if (FoldAssertCondition(a.Left) is false || FoldAssertCondition(a.Right) is false) return false;
                return FoldAssertCondition(a.Left) is true && FoldAssertCondition(a.Right) is true ? true : null;

            case BinaryExpr { Op: Frontend.BinaryOp.Or } o:
                if (FoldAssertCondition(o.Left) is true || FoldAssertCondition(o.Right) is true) return true;
                return FoldAssertCondition(o.Left) is false && FoldAssertCondition(o.Right) is false ? false : null;

            case BinaryExpr cmp when cmp.Op is Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual
                                          or Frontend.BinaryOp.Less or Frontend.BinaryOp.Greater
                                          or Frontend.BinaryOp.LessEq or Frontend.BinaryOp.GreaterEq:
            {
                if (TryEvalElemConst(cmp.Left, out int l) && TryEvalElemConst(cmp.Right, out int r))
                    return cmp.Op switch
                    {
                        Frontend.BinaryOp.Equal     => l == r,
                        Frontend.BinaryOp.NotEqual  => l != r,
                        Frontend.BinaryOp.Less      => l < r,
                        Frontend.BinaryOp.Greater   => l > r,
                        Frontend.BinaryOp.LessEq    => l <= r,
                        _                           => l >= r,
                    };
                return null;
            }

            default:
                return TryEvalElemConst(cond, out int v) ? v != 0 : null;
        }
    }

    // `assert` fired only for the literal `0`. Every other spelling of a false assertion was
    // dropped: this method evaluated the condition and the catch swallowed everything that was
    // not already an AssertionError. EvaluateConstantExpr folds an integer literal, so `0`
    // arrived, but it folds neither a comparison nor a BooleanLiteral, so `assert 1 == 2` and
    // `assert False` both threw, were both swallowed, and lowered to nothing. The one row that
    // agreed with CPython was the one nobody writes.
    //
    // Refusing a statically false assert wherever it stands, rather than only on a path always
    // reached, is the policy `assert 0` already shipped: it was refused inside a run-time `if`
    // and inside an `else` too. Keeping that and making the other spellings match it does mean
    // `assert False` cannot serve as an unreachable-branch marker, and it could not when
    // spelled `assert 0` either. `raise` is the construct that survives to run time.
    //
    // A condition that does NOT fold still emits no code. That is deliberate and measured:
    // `AssertionError` is not a defined name in PyMCU, so a run-time check has nothing to
    // raise, and the nearest equivalent that does compile, `if x != y: raise ValueError(...)`,
    // cost 24 bytes over the same program with no assert. Compiling asserts out is what
    // `python -O` does, so the behaviour is defensible; being silent about it is not, because
    // the reader cannot tell a checked assertion from a dropped one.
    private void VisitAssert(AssertStmt stmt)
    {
        bool? folded = FoldAssertCondition(stmt.Condition);

        if (folded == false)
            throw UserError(
                "AssertionError" + (string.IsNullOrEmpty(stmt.Message) ? "" : ": " + stmt.Message),
                stmt.Condition);

        if (folded == true) return;

        // Line only, no file. UserError(msg, node) takes the node's line and the file of the
        // module being LOWERED, so a name-resolved node yields a plausible cursor naming the
        // wrong file; a warning printed here has no better source of truth, so it does not
        // claim one.
        Console.Error.WriteLine(
            $"warning: assert on line {stmt.Line} is not checked. Its condition is not known at "
            + "compile time, and PyMCU emits no run-time check, the way `python -O` drops "
            + "asserts. Use `if <cond>: raise ...` for a check that survives to run time.");
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