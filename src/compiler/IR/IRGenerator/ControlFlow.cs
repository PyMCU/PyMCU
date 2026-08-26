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
    /// <summary>
    /// The label an instruction jumps to, or null when it does not jump.
    /// </summary>
    private static string? JumpTargetOf(Instruction i) => i switch
    {
        Jump j => j.Target,
        JumpIfZero j => j.Target,
        JumpIfNotZero j => j.Target,
        JumpIfEqual j => j.Target,
        JumpIfNotEqual j => j.Target,
        JumpIfLessThan j => j.Target,
        JumpIfLessOrEqual j => j.Target,
        JumpIfGreaterThan j => j.Target,
        JumpIfGreaterOrEqual j => j.Target,
        JumpIfBitSet j => j.Target,
        JumpIfBitClear j => j.Target,
        _ => null,
    };

    /// <summary>
    /// True when anything emitted since <paramref name="from"/> jumps to
    /// <paramref name="label"/>.
    /// </summary>
    /// <remarks>
    /// EmitOptimizedConditionalJump lowers each operand of an `and` / `or` as it walks them,
    /// so it can emit a jump to the label it was given and only afterwards discover that the
    /// whole condition folds. `name == "PD2" or name == 2` under `jumpIfTrue = false` does
    /// exactly that: the left operand folds true and jumps over the rest, the right folds
    /// false and jumps to the caller's else label, and the two together answer "statically
    /// true". The caller then keeps only the then branch and never defines that else label.
    ///
    /// The jump left behind is unreachable -- the static path always jumps past it -- but a
    /// label that no one defines is not a dead instruction, it is a link error, and only
    /// PYMCU_NO_OPT=1 shows it because the optimizer deletes the jump first. So the caller
    /// asks this before abandoning a label, and defines it when the answer is yes.
    /// </remarks>
    private bool ConditionJumpedTo(string label, int from)
    {
        for (int i = from; i < currentInstructions.Count; ++i)
            if (JumpTargetOf(currentInstructions[i]) == label)
                return true;
        return false;
    }

    private int EmitOptimizedConditionalJump(Expression cond, string targetLabel, bool jumpIfTrue = false)
    {
        // Every condition that reaches here is a truth test, including each operand of an
        // `and` / `or`, which recurses into this method. `if x and True:` tested the raw
        // instance handle for x and answered false for an object whose __bool__ says true.
        cond = LowerInstanceTruthiness(cond);

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

                bool? EmitSub(Expression sub, string label, bool ifTrue)
                {
                    int r = EmitOptimizedConditionalJump(sub, label, ifTrue);
                    if (r == 2) return true;
                    if (r == -1) return false;
                    if (r != 0) return null;
                    // The recursive call lowered its own copy; this fallback path evaluates the
                    // operand as a value and has to lower it too, or an instance operand is
                    // tested as its raw handle after all.
                    Val v = VisitExpression(LowerInstanceTruthiness(sub));
                    if (v is Constant c)
                    {
                        bool cval = c.Value != 0;
                        if (cval == ifTrue) Emit(new Jump(label));
                        return cval;
                    }

                    if (ifTrue) Emit(new JumpIfNotZero(v, label));
                    else Emit(new JumpIfZero(v, label));
                    return null;
                }

                bool? leftTruth;
                bool? rightTruth;
                if ((!jumpIfTrue && isAnd) || (jumpIfTrue && !isAnd))
                {
                    leftTruth = EmitSub(binExpr.Left, targetLabel, jumpIfTrue);
                    rightTruth = EmitSub(binExpr.Right, targetLabel, jumpIfTrue);
                }
                else
                {
                    string skipLabel = MakeLabel();
                    leftTruth = EmitSub(binExpr.Left, skipLabel, !jumpIfTrue);
                    rightTruth = EmitSub(binExpr.Right, targetLabel, jumpIfTrue);
                    Emit(new Label(skipLabel));
                }

                if (leftTruth is not { } lt || rightTruth is not { } rt) return 1;
                return (isAnd ? lt && rt : lt || rt) ? 2 : -1;
            }

            if (binExpr.Op == Frontend.BinaryOp.In || binExpr.Op == Frontend.BinaryOp.NotIn ||
                binExpr.Op == Frontend.BinaryOp.Is || binExpr.Op == Frontend.BinaryOp.IsNot)
                return 0;

            RejectBareRegisterOperands(binExpr);

            // `if s == "running":` where s holds one of several texts. Interning gives equal
            // texts the same id, so the test is the id comparison and it is decided at run
            // time: reading the id is what this comparison means (see VisitBinary).
            bool cmpAgainstStrLiteral = binExpr.Op is Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual
                && (binExpr.Left is StringLiteral || binExpr.Right is StringLiteral);
            if (cmpAgainstStrLiteral) multiStrHandleReads++;
            Val v1 = VisitExpression(binExpr.Left);
            Val v2 = VisitExpression(binExpr.Right);
            if (cmpAgainstStrLiteral) multiStrHandleReads--;

            // ONLY a comparison can be decided here. The switch below covers the six
            // comparison operators and nothing else, so a folded `3 & 1` or `3 + 1` used bare
            // as a condition fell through every case and kept `condResult`'s initial false:
            // `if 3 & 1:` took the else branch, and the LCD driver never sent its 4-bit init
            // handshake because its four datasheet nibbles are written that way. Anything else
            // falls through to the generic path, which folds the arithmetic and tests the
            // value's truthiness.
            bool isComparison = binExpr.Op is Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual
                or Frontend.BinaryOp.Less or Frontend.BinaryOp.LessEq
                or Frontend.BinaryOp.Greater or Frontend.BinaryOp.GreaterEq;

            if (v1 is Constant c1 && v2 is Constant c2 && isComparison)
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

            // Both sides of the test have to be read in a type that can hold both, exactly as
            // in VisitBinary: an `if` takes this path instead, and without the same widening
            // `int8(100) > uint8(200)` read the 200 as the -56 its bits spell in int8.
            if (binExpr.Op is Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual
                or Frontend.BinaryOp.Less or Frontend.BinaryOp.LessEq
                or Frontend.BinaryOp.Greater or Frontend.BinaryOp.GreaterEq)
            {
                DataType cmpType = ComparisonType(v1, v2);
                v1 = WidenForComparison(v1, cmpType);
                v2 = WidenForComparison(v2, cmpType);
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

    // Python's truthiness for an instance: __bool__, else __len__ != 0, else always true.
    // PyMCU evaluates an instance as whatever scalar it collapsed to -- 0 for a multi-field
    // class -- so `if obj:` silently took the false branch for every object. Rewrite to the
    // protocol method the class defines, and refuse when it defines neither: a condition that
    // is constant-false by accident is worse to debug than a compile error.
    private Expression LowerInstanceTruthiness(Expression cond)
    {
        if (cond is not VariableExpr ve) return cond;
        if (InstanceClassOfName(ve.Name) is not { } cls) return cond;
        foreach (var m in new[] { "__bool__", "__len__" })
            if (TryResolveInstanceMethodAst(ve.Name, m) != null)
                return new CallExpr(new MemberAccessExpr(ve, m), new List<Expression>())
                    { Line = cond.Line };
        string shown = cls.Contains('_') ? cls[(cls.LastIndexOf('_') + 1)..] : cls;
        throw UserError(
            $"'{ve.Name}' is an instance of '{shown}' with no __bool__ or __len__, so it has no " +
            $"truth value. Test a field or a method result instead (e.g. `if {ve.Name}.<field>:`).");
    }

    private void VisitIf(IfStmt stmt)
    {
        if (LowerInstanceTruthiness(stmt.Condition) is var loweredCond
            && !ReferenceEquals(loweredCond, stmt.Condition))
        {
            VisitIf(new IfStmt(loweredCond, stmt.ThenBranch,
                stmt.ElifBranches.Select(b => ((Expression)b.Condition, b.Body)).ToList(),
                stmt.ElseBranch) { Line = stmt.Line });
            return;
        }

        string endLabel = MakeLabel();
        string nextLabel = (stmt.ElifBranches.Count == 0 && stmt.ElseBranch == null) ? endLabel : MakeLabel();

        int condStart = currentInstructions.Count;
        int optResult = EmitOptimizedConditionalJump(stmt.Condition, nextLabel, false);
        bool skipThen = false;
        bool isRuntimeBranch = false;

        if (optResult == 1) isRuntimeBranch = true;

        if (optResult == -1) skipThen = true;
        else if (optResult == 2)
        {
            // CT-true: only visit then branch, skip else entirely (prevents CT
            // side-effects like compile_isr from the else branch being processed).
            // The condition may still have jumped to nextLabel on a path it then decided
            // was not taken (see ConditionJumpedTo), and dropping the else branch drops the
            // only definition of that label. Define it here: the jump is unreachable, so
            // falling straight out of the `if` is where it would go if it ever ran.
            bool nextIsTargeted = ConditionJumpedTo(nextLabel, condStart);
            VisitStatement(stmt.ThenBranch);
            if (nextIsTargeted) Emit(new Label(nextLabel));
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

            int elifCondStart = currentInstructions.Count;
            int elifOpt = EmitOptimizedConditionalJump(elifCond, nextLabel, false);
            bool skipElif = false;
            bool elifIsRuntime = false;

            if (elifOpt == 1) elifIsRuntime = true;

            if (elifOpt == -1) skipElif = true;
            else if (elifOpt == 2)
            {
                // CT-true elif: only visit this block, skip remaining branches. Same as the
                // `if` above -- the condition may have jumped to nextLabel before folding.
                bool elifNextIsTargeted = ConditionJumpedTo(nextLabel, elifCondStart);
                VisitStatement(elifBlock);
                if (elifNextIsTargeted) Emit(new Label(nextLabel));
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
                continue;
            }

            // The branches left the name holding different texts (or one of them left it
            // alone, so the value from before the `if` survives on that path). There is no
            // single text here: the name keeps its id at run time and a read dispatches on
            // it. Restoring the pre-branch value is what printed the initializer on every
            // path, with the store dropped and nothing said (issue #145).
            var candidates = new List<string?>();
            if (snapBefore.TryGetValue(key, out var beforeVal)) candidates.Add(beforeVal);
            foreach (var snap in branchSnaps)
                if (snap.TryGetValue(key, out var bv)) candidates.Add(bv);
            MarkMultiStr(key, candidates);
        }
    }

    /// <summary>
    /// Lowers `case Cls(...)`. Returns true when the branch is fully handled.
    ///
    /// The class test is decided at compile time from the subject's known class. Each
    /// sub-pattern is either a value to compare the field against or a name to bind to it,
    /// and the binds are applied only after every comparison has passed, so a pattern that
    /// fails half way leaves nothing behind.
    /// </summary>
    private bool VisitClassPattern(MatchStmt stmt, CaseBranch branch, CallExpr pattern,
                                   Val targetVal, string nextCaseLabel, string endLabel)
    {
        if (pattern.Callee is not VariableExpr patName)
            throw UserError("match/case: a call is not a pattern; `case Cls(...)` matches a "
                          + "class, and its callee has to be a class name");

        string subjectClass = GetValClass(targetVal);
        if (string.IsNullOrEmpty(subjectClass))
            throw UserError(
                $"match/case: `case {patName.Name}(...)` needs the subject's class to be known "
                + "at compile time, and it is not here. Match on a value the program builds "
                + $"from a constructor (`p = {patName.Name}(...)` then `match p:`), or compare "
                + "the fields directly with if/elif");

        string subjectShort = subjectClass.Contains('_')
            ? subjectClass[(subjectClass.LastIndexOf('_') + 1)..]
            : subjectClass;

        // A different class can never match: there is one static type per value here.
        if (subjectShort != patName.Name)
        {
            Emit(new Jump(nextCaseLabel));
            Emit(new Label(nextCaseLabel));
            return true;
        }

        classMatchArgs.TryGetValue(subjectClass, out var matchArgs);

        var tests = new List<(string Field, Expression Sub)>();
        int positional = 0;
        foreach (var arg in pattern.Args)
        {
            if (arg is KeywordArgExpr kw)
            {
                tests.Add((kw.Key, kw.Value));
                continue;
            }

            // Positional sub-patterns need __match_args__ to know which field each one is,
            // exactly as CPython does. Substituting the field layout would accept a program
            // CPython rejects with "accepts 0 positional sub-patterns".
            if (matchArgs == null || positional >= matchArgs.Count)
                throw UserError(
                    $"'{patName.Name}' accepts {matchArgs?.Count ?? 0} positional sub-pattern(s) "
                    + $"and {pattern.Args.Count} were given. A positional class pattern takes its "
                    + $"field order from __match_args__; add it to '{patName.Name}' "
                    + $"(`__match_args__ = (\"x\", \"y\")`), or name the fields in the pattern "
                    + $"(`case {patName.Name}(x=..., y=...)`)");

            tests.Add((matchArgs[positional], arg));
            positional++;
        }

        string Qualify(string name) => string.IsNullOrEmpty(currentFunction)
            ? name
            : currentFunction + "." + name;

        // Comparisons first, binds after: a half-matched pattern must bind nothing.
        var binds = new List<(string Field, string Target)>();
        foreach (var (field, sub) in tests)
        {
            if (sub is VariableExpr cap && cap.Name != "_")
            {
                binds.Add((field, Qualify(cap.Name)));
                continue;
            }
            if (sub is VariableExpr) continue; // `_` matches anything and binds nothing

            Val fieldVal = VisitExpression(new MemberAccessExpr(stmt.Target, field));
            Val subVal = VisitExpression(sub);
            Temporary cmp = MakeTemp();
            Emit(new Binary(PyMCU.IR.BinaryOp.Equal, fieldVal, subVal, cmp));
            Emit(new JumpIfZero(cmp, nextCaseLabel));
        }

        foreach (var (field, qname) in binds)
        {
            Val fieldVal = VisitExpression(new MemberAccessExpr(stmt.Target, field));
            DataType dt = fieldVal switch
            {
                Variable v => v.Type,
                Temporary t => t.Type,
                _ => DataType.UINT8,
            };
            Emit(new Copy(fieldVal, new Variable(qname, dt)));
            variableTypes[qname] = dt;
        }

        if (!string.IsNullOrEmpty(branch.CaptureName))
        {
            string qname = Qualify(branch.CaptureName);
            variableAliases[qname] = targetVal is Variable tv ? tv.Name : qname;
            if (!string.IsNullOrEmpty(subjectClass)) instanceClasses[qname] = subjectClass;
        }

        if (branch.Guard != null)
        {
            Val g = VisitExpression(branch.Guard);
            Emit(new JumpIfZero(g, nextCaseLabel));
        }

        // The body runs under a run-time condition whenever the pattern tested anything.
        _runtimeBranchDepth++;
        if (branch.Body != null) VisitBlock((Block)branch.Body);
        _runtimeBranchDepth--;

        Emit(new Jump(endLabel));
        Emit(new Label(nextCaseLabel));
        return true;
    }

    private void VisitMatch(MatchStmt stmt)
    {
        // A match on a parameter whose origin is known is, when one of its arms refuses, a
        // refusal OF THAT ARGUMENT. Remember which one for the duration of the match.
        bool blaming = false;
        if (stmt.Target is VariableExpr subjVe)
        {
            if (argumentOrigin.TryGetValue(currentInlinePrefix + subjVe.Name, out var subjOrigin)
                || argumentOrigin.TryGetValue(subjVe.Name, out subjOrigin))
            {
                blamedArgument.Add(subjOrigin);
                blaming = true;
            }
        }
        try
        {
            VisitMatchBody(stmt);
        }
        finally
        {
            if (blaming) blamedArgument.RemoveAt(blamedArgument.Count - 1);
        }
    }

    private void VisitMatchBody(MatchStmt stmt)
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

                // A CLASS PATTERN: `case Point(x=0)` or `case Point(a, b)`. A call cannot
                // appear in a pattern in Python, so any CallExpr here is one of these, and
                // it was previously visited as an expression: the keyword form was lowered
                // as a constructor CALL and asked for the argument it was missing, and the
                // positional form resolved its capture names as reads and reported the very
                // names the pattern binds as undefined (issue #173).
                //
                // There is no runtime type tag on this target, so the isinstance half is a
                // compile-time decision: the subject either IS that class, and only the
                // sub-patterns cost anything, or it is not, and the case is dead.
                if (branch.Pattern is CallExpr classPat)
                {
                    if (VisitClassPattern(stmt, branch, classPat, targetVal, nextCaseLabel, endLabel))
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

    /// <summary>
    /// Drops the compile-time value of everything <paramref name="body"/> can assign to, before
    /// a loop body is lowered. A loop body is emitted once and executed many times, so folding
    /// it against the state of the first iteration is wrong for every iteration after it.
    ///
    /// Deliberately conservative about method calls: any call on an instance drops that
    /// instance's fields, because deciding which fields a method touches means walking the
    /// method (and everything it calls). Losing a fold is a size cost; keeping a stale one is
    /// a wrong number.
    /// </summary>
    private void InvalidateConstantsAssignedIn(Statement body, Expression? condition = null)
    {
        var names = new HashSet<string>();
        var receivers = new HashSet<(string Instance, string Method)>();
        CollectMutatedNames(body, names, receivers);
        CollectCalls(condition, receivers);
        if (names.Count == 0 && receivers.Count == 0) return;

        foreach (var name in names)
            foreach (var key in CandidateKeys(name))
            {
                constantVariables.Remove(key);
                strConstantVariables.Remove(key);
            }

        // `obj.method()` writes only the fields that method assigns to. Dropping every field
        // of the receiver instead was too much: a Pin's `_bit` is written once in __init__ and
        // read as a compile-time constant forever after, and without it the backend cannot
        // emit the bit access at all ("Bit index must be constant for reading").
        foreach (var (instance, method) in receivers)
        {
            // A pair tagged with "=" is a direct member WRITE, not a call: the field is named
            // outright, so there is nothing to look up in the method.
            var written = method.StartsWith("=", StringComparison.Ordinal)
                ? new[] { method[1..] }
                : FieldsMutatedBy(instance, method).ToArray();

            foreach (var field in written)
                foreach (var prefix in ResolvedKeys(instance))
                {
                    constantVariables.Remove(prefix + "_" + field);
                    constantVariables.Remove(prefix + "." + field);
                    strConstantVariables.Remove(prefix + "_" + field);
                    strConstantVariables.Remove(prefix + "." + field);
                }
        }
    }

    /// <summary>
    /// Every storage name <paramref name="name"/> can stand for: the qualified spellings, plus
    /// whatever it is aliased to. `self` inside an expanded method is an alias for the caller's
    /// instance, and the fields live under the instance's name, not under `self`.
    /// </summary>
    private IEnumerable<string> ResolvedKeys(string name)
    {
        var seen = new HashSet<string>();
        foreach (var key in CandidateKeys(name))
        {
            string? cur = key;
            for (int depth = 0; depth < 10 && cur != null; depth++)
            {
                if (!seen.Add(cur)) break;
                yield return cur;
                if (!variableAliases.TryGetValue(cur, out cur)) break;
            }
        }
    }

    /// <summary>
    /// The fields `<paramref name="instance"/>.<paramref name="method"/>()` can assign to,
    /// following the methods it calls on itself. Empty when the class or the method cannot be
    /// resolved, which leaves the folds in place: this runs to prevent a stale value, and a
    /// method nobody can find writes nothing that this loop will observe.
    /// </summary>
    private IEnumerable<string> FieldsMutatedBy(string instance, string method)
    {
        string? cls = InstanceClassOfName(instance);
        return cls == null ? Enumerable.Empty<string>() : FieldsWrittenBy(cls + "_" + method);
    }

    /// <summary>
    /// True when `<paramref name="callee"/>` (`Class_method`) is fully resolvable AND writes no
    /// field of its receiver, directly or through a method it calls on one of its own fields.
    ///
    /// This exists because <see cref="FieldsWrittenBy"/> returns an empty sequence for BOTH
    /// "writes nothing" and "cannot be resolved". Where that method is used -- invalidating
    /// folds around a loop -- collapsing the two is safe, because an unresolvable method writes
    /// nothing the loop will observe. A caller deciding whether a field may stay constant needs
    /// them apart: reading "unknown" as "writes nothing" there is a silent wrong value.
    ///
    /// So this answers only the question it can answer safely, and returns false for anything
    /// it cannot see through, including a nested call on a field whose class does not resolve.
    /// </summary>
    private bool MethodWritesNoField(string callee)
    {
        string? cls = OwningClassOf(callee);
        if (cls == null || !classFieldLayout.TryGetValue(cls, out var layout)) return false;

        FunctionDef? def = null;
        if (!instanceMethodDefs.TryGetValue(callee, out def)
            && !methodAstByName.TryGetValue(callee, out def)
            && !inlineFunctions.TryGetValue(callee, out def)) return false;
        if (def == null) return false;

        foreach (var (field, type, _) in layout)
        {
            if (MethodMutatesFieldPublic(def, field)) return false;

            // A call on a field that holds an instance can write through it. Following that
            // is what FieldsWrittenBy does; here it is enough to refuse to answer, because
            // the caller's fallback is the conservative mark it would have made anyway.
            if (NestedMethodsCalledOn(def, field).Any()) return false;
        }

        return true;
    }

    /// <summary>
    /// The fields the method compiled under <paramref name="callee"/> (`Class_method`) can
    /// assign to, following the methods it calls on itself. Empty when the class or the method
    /// cannot be resolved, which leaves the folds in place: this runs to prevent a stale value,
    /// and a method nobody can find writes nothing the caller will observe.
    /// </summary>
    private IEnumerable<string> FieldsWrittenBy(string callee)
    {
        var paths = new List<string>();
        CollectFieldsWrittenBy(callee, "", paths, new HashSet<string>());
        return paths;
    }

    /// <summary>
    /// The same set as <see cref="FieldsWrittenBy"/>, but only the paths that reach THROUGH a
    /// field holding an instance, each with the declared type of the leaf.
    ///
    /// The receiver's own fields are excluded because their caller already handles them off the
    /// class layout; what it cannot handle is `inner_v`, which is not a field name of any class
    /// and so was never given storage. The type comes back because the layout the caller would
    /// look the name up in does not contain it either.
    ///
    /// Only WRITTEN paths, never every nested field: a driver holding a Pin and calling
    /// `self.pin.high()` must keep `_bit` a compile-time constant, which the backend requires
    /// for the mask and is not an optimization to trade away.
    /// </summary>
    private IEnumerable<(string Path, string Type)> NestedFieldsWrittenBy(string callee)
    {
        var typed = new List<(string, string)>();
        CollectNestedFieldsWrittenBy(callee, "", typed, new HashSet<string>());
        return typed;
    }

    private void CollectNestedFieldsWrittenBy(string callee, string prefix,
                                              List<(string, string)> typed, HashSet<string> visiting)
    {
        if (!visiting.Add(callee) || visiting.Count > 8) return;

        string? cls = OwningClassOf(callee);
        if (cls == null || !classFieldLayout.TryGetValue(cls, out var layout)) return;

        FunctionDef? def = null;
        if (!instanceMethodDefs.TryGetValue(callee, out def)
            && !methodAstByName.TryGetValue(callee, out def)
            && !inlineFunctions.TryGetValue(callee, out def)) return;
        if (def == null) return;

        foreach (var (field, type, _) in layout)
        {
            // Only below the first hop: at prefix "" this is the receiver's own field, which the
            // caller marks off the layout.
            if (prefix.Length > 0 && MethodMutatesFieldPublic(def, field))
                typed.Add((prefix + field, type));

            if (!classFieldLayout.ContainsKey(type)) continue;
            foreach (string inner in NestedMethodsCalledOn(def, field))
                CollectNestedFieldsWrittenBy(type + "_" + inner, prefix + field + "_", typed, visiting);
        }
    }

    /// <summary>
    /// Accumulate into <paramref name="paths"/> the flattened field paths the method writes,
    /// relative to its receiver: `_value` for its own field, `inner__state` for a field of an
    /// instance it holds. A nested instance is reached through a call, so the recursion follows
    /// `self.&lt;field&gt;.&lt;method&gt;()` -- an Outer that only forwards to its Inner still
    /// leaves the Inner's state changed.
    /// </summary>
    private void CollectFieldsWrittenBy(string callee, string prefix, List<string> paths,
                                        HashSet<string> visiting)
    {
        if (!visiting.Add(callee) || visiting.Count > 8) return;

        string? cls = OwningClassOf(callee);
        if (cls == null || !classFieldLayout.TryGetValue(cls, out var layout)) return;

        FunctionDef? def = null;
        if (!instanceMethodDefs.TryGetValue(callee, out def)
            && !methodAstByName.TryGetValue(callee, out def)
            && !inlineFunctions.TryGetValue(callee, out def)) return;
        if (def == null) return;

        foreach (var (field, type, _) in layout)
        {
            if (MethodMutatesFieldPublic(def, field)) paths.Add(prefix + field);

            // `self.<field>.<method>()` where <field> holds an instance: what that method
            // writes lives under the flattened `<field>_<its field>` name.
            if (!classFieldLayout.ContainsKey(type)) continue;
            foreach (string inner in NestedMethodsCalledOn(def, field))
                CollectFieldsWrittenBy(type + "_" + inner, prefix + field + "_", paths, visiting);
        }
    }

    /// <summary>The methods `<paramref name="method"/>` calls on `self.<paramref name="field"/>`.</summary>
    private static IEnumerable<string> NestedMethodsCalledOn(FunctionDef method, string field)
    {
        var found = new HashSet<string>();
        void E(Expression? e)
        {
            switch (e)
            {
                case null: return;
                case CallExpr { Callee: MemberAccessExpr { Object: MemberAccessExpr
                        { Object: VariableExpr { Name: "self" }, Member: var f } } m } c when f == field:
                    found.Add(m.Member);
                    foreach (var a in c.Args) E(a);
                    return;
                case CallExpr c2: E(c2.Callee); foreach (var a in c2.Args) E(a); return;
                case MemberAccessExpr ma: E(ma.Object); return;
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                case KeywordArgExpr kw: E(kw.Value); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                case ListExpr le: foreach (var el in le.Elements) E(el); return;
            }
        }
        void S(Statement? st)
        {
            switch (st)
            {
                case null: return;
                case Block bl: foreach (var cs in bl.Statements) S(cs); return;
                case AssignStmt a: E(a.Value); return;
                case AugAssignStmt aug: E(aug.Value); return;
                case AnnAssign an: E(an.Value); return;
                case VarDecl vd: E(vd.Init); return;
                case ExprStmt es: E(es.Expr); return;
                case ReturnStmt r: E(r.Value); return;
                case IfStmt i:
                    E(i.Condition); S(i.ThenBranch);
                    foreach (var (c, br) in i.ElifBranches) { E(c); S(br); }
                    S(i.ElseBranch);
                    return;
                case WhileStmt w: E(w.Condition); S(w.Body); return;
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
        return found;
    }

    /// <summary>The class a `Class_method` symbol belongs to, or null when there is no such class.</summary>
    private string? OwningClassOf(string callee)
    {
        int cut = callee.LastIndexOf('_');
        while (cut > 0)
        {
            string cls = callee.Substring(0, cut);
            if (classFieldLayout.ContainsKey(cls)) return cls;
            cut = callee.LastIndexOf('_', cut - 1);
        }
        return null;
    }

    private IEnumerable<string> CandidateKeys(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)) yield return currentInlinePrefix + name;
        if (!string.IsNullOrEmpty(currentFunction)) yield return currentFunction + "." + name;
        if (!string.IsNullOrEmpty(currentModulePrefix)) yield return currentModulePrefix + name;
        yield return name;
    }

    /// <summary>
    /// Names a statement can write: assignment targets (plain, member and subscript), loop
    /// variables, and the receiver of any method call.
    /// </summary>
    private static void CollectMutatedNames(Statement? st, HashSet<string> names, HashSet<(string, string)> receivers)
    {
        switch (st)
        {
            case null: return;
            case Block b: foreach (var s in b.Statements) CollectMutatedNames(s, names, receivers); return;
            case AssignStmt a: CollectTarget(a.Target, names, receivers); CollectCalls(a.Value, receivers); return;
            case AugAssignStmt aug: CollectTarget(aug.Target, names, receivers); CollectCalls(aug.Value, receivers); return;
            case AnnAssign an: names.Add(an.Target); CollectCalls(an.Value, receivers); return;
            case VarDecl vd: names.Add(vd.Name); CollectCalls(vd.Init, receivers); return;
            case TupleUnpackStmt tu: foreach (var t in tu.Targets) names.Add(t); CollectCalls(tu.Value, receivers); return;
            case ExprStmt es: CollectCalls(es.Expr, receivers); return;
            case ReturnStmt r: CollectCalls(r.Value, receivers); return;
            case ForStmt f:
                names.Add(f.VarName);
                if (!string.IsNullOrEmpty(f.Var2Name)) names.Add(f.Var2Name);
                CollectMutatedNames(f.Body, names, receivers);
                return;
            case WhileStmt w: CollectCalls(w.Condition, receivers); CollectMutatedNames(w.Body, names, receivers); return;
            case IfStmt i:
                CollectCalls(i.Condition, receivers);
                CollectMutatedNames(i.ThenBranch, names, receivers);
                foreach (var (cond, br) in i.ElifBranches)
                {
                    CollectCalls(cond, receivers);
                    CollectMutatedNames(br, names, receivers);
                }
                CollectMutatedNames(i.ElseBranch, names, receivers);
                return;
            case WithStmt wi:
                if (!string.IsNullOrEmpty(wi.AsName)) names.Add(wi.AsName);
                CollectMutatedNames(wi.Body, names, receivers);
                return;
            case MatchStmt m:
                CollectCalls(m.Target, receivers);
                foreach (var br in m.Branches)
                {
                    if (!string.IsNullOrEmpty(br.CaptureName)) names.Add(br.CaptureName);
                    CollectMutatedNames(br.Body, names, receivers);
                }
                return;
            case TryStmt t:
                foreach (var s in t.Body) CollectMutatedNames(s, names, receivers);
                foreach (var (_, h) in t.Handlers) foreach (var s in h) CollectMutatedNames(s, names, receivers);
                if (t.ElseBody != null) foreach (var s in t.ElseBody) CollectMutatedNames(s, names, receivers);
                if (t.Finally != null) foreach (var s in t.Finally) CollectMutatedNames(s, names, receivers);
                return;
            default: return;
        }
    }

    private static void CollectTarget(Expression target, HashSet<string> names, HashSet<(string, string)> receivers)
    {
        switch (target)
        {
            case VariableExpr v: names.Add(v.Name); return;
            case MemberAccessExpr { Object: VariableExpr obj } m:
                names.Add(obj.Name + "_" + m.Member);
                names.Add(obj.Name + "." + m.Member);
                // The receiver may be an alias: inside a state machine's poll() the body
                // writes `self.total`, and the storage is named after the instance the caller
                // bound. Recording the pair lets the caller resolve `self` before building
                // the key, which the flattened names above cannot do on their own.
                receivers.Add((obj.Name, "=" + m.Member));
                return;
            case IndexExpr { Target: VariableExpr arr }: names.Add(arr.Name); return;
            default: return;
        }
    }

    /// <summary>The receivers of method calls inside an expression: each may write its fields.</summary>
    private static void CollectCalls(Expression? e, HashSet<(string, string)> receivers)
    {
        switch (e)
        {
            case null: return;
            case CallExpr c:
                if (c.Callee is MemberAccessExpr { Object: VariableExpr recv } rm)
                    receivers.Add((recv.Name, rm.Member));
                CollectCalls(c.Callee, receivers);
                foreach (var a in c.Args) CollectCalls(a, receivers);
                return;
            case BinaryExpr b: CollectCalls(b.Left, receivers); CollectCalls(b.Right, receivers); return;
            case UnaryExpr u: CollectCalls(u.Operand, receivers); return;
            case MemberAccessExpr m: CollectCalls(m.Object, receivers); return;
            case IndexExpr ix: CollectCalls(ix.Target, receivers); CollectCalls(ix.Index, receivers); return;
            case TernaryExpr t:
                CollectCalls(t.Condition, receivers);
                CollectCalls(t.TrueVal, receivers);
                CollectCalls(t.FalseVal, receivers);
                return;
            case FStringExpr fs:
                foreach (var p in fs.Parts) CollectCalls(p.Expr, receivers);
                return;
            case ListExpr l: foreach (var x in l.Elements) CollectCalls(x, receivers); return;
            case TupleExpr tp: foreach (var x in tp.Elements) CollectCalls(x, receivers); return;
            case KeywordArgExpr k: CollectCalls(k.Value, receivers); return;
            default: return;
        }
    }

    private void VisitWhile(WhileStmt stmt)
    {
        if (LowerInstanceTruthiness(stmt.Condition) is var loweredWhileCond
            && !ReferenceEquals(loweredWhileCond, stmt.Condition))
        {
            VisitWhile(new WhileStmt(loweredWhileCond, stmt.Body) { Line = stmt.Line });
            return;
        }

        string startLabel = MakeLabel();
        string endLabel = MakeLabel();
        loopStack.Add(new LoopLabels { ContinueLabel = startLabel, BreakLabel = endLabel,
                                       FinallyDepth = finallyStack.Count });

        // The condition and the body are emitted ONCE but run many times, so nothing either
        // of them can change may be folded from the value it happens to hold on the way in.
        // Without this a counter method expanded in a loop folded its own field
        // (`self.n = self.n + 1` became `n = 1`, and the loop printed 1, 1, 1); doing it after
        // the condition instead left `while c.bump() < 4:` folded to the first value it
        // returned, so the comparison vanished and the loop never ended.
        var strBeforeLoop = new Dictionary<string, string?>(strConstantVariables);
        InvalidateConstantsAssignedIn(stmt.Body, stmt.Condition);

        Emit(new Label(startLabel));

        int whileOpt = EmitOptimizedConditionalJump(stmt.Condition, endLabel, false);
        if (whileOpt == -1)
        {
            Emit(new Label(endLabel));
            loopStack.RemoveAt(loopStack.Count - 1);
            return;
        }

        bool isRuntimeLoop = whileOpt == 1;

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
                isRuntimeLoop = true;
            }
        }

        // Inside an @inline expansion, a loop whose exit the compiler cannot predict makes
        // everything after it conditional on run-time data. `raise` uses that: the abort rule
        // in VisitRaise must not treat the "not found" arm of a search loop as an
        // unconditional raise (FixedDict's `while ...: probe` followed by `raise KeyError`).
        if (inlineStack.Count > 0) inlineStack[^1].SawDynamicLoop = true;

        if (isRuntimeLoop) _runtimeBranchDepth++;
        VisitStatement(stmt.Body);
        if (isRuntimeLoop) _runtimeBranchDepth--;
        Emit(new Jump(startLabel));
        Emit(new Label(endLabel));
        loopStack.RemoveAt(loopStack.Count - 1);

        // The body may run any number of times, zero included, so a str it rebinds holds
        // either the value it came in with or the one the body last wrote. Folding the body's
        // value here made a loop that never ran print the text from inside it.
        MarkStrReboundBy(strBeforeLoop);
    }

    /// <summary>
    /// Takes the compile-time value away from every str that a loop body rebound, keeping both
    /// the value from before the loop and the one the body leaves as the candidates a read
    /// dispatches over. A name the body binds for the FIRST time is left alone: it had no
    /// value to disagree with, and the shape is a name the loop introduces.
    /// </summary>
    private void MarkStrReboundBy(Dictionary<string, string?> before)
    {
        var rebound = new List<(string Key, string? Before, string? After)>();
        foreach (var kv in before)
        {
            strConstantVariables.TryGetValue(kv.Key, out var after);
            if (after != kv.Value) rebound.Add((kv.Key, kv.Value, after));
        }

        foreach (var (key, beforeVal, afterVal) in rebound)
            MarkMultiStr(key, new[] { beforeVal, afterVal });
    }

    private void VisitBreak(BreakStmt stmt)
    {
        if (loopStack.Count == 0) throw UserError("Break statement outside of loop");
        var loop = Enumerable.Last<LoopLabels>(loopStack);

        // `for/while ... else`: this break exits the loop whose else clause must NOT run, so
        // clear the flag the desugared test reads (Parser.AttachLoopElse). The flag then stops
        // being a compile-time constant -- it was folded to 1 by the initialiser emitted before
        // the loop, and leaving that in place would fold the trailing test to "always taken"
        // and run the else body on the broken-out path, which is the bug this lowering exists
        // to avoid. A break that is never emitted (its branch folded away) can never run, so
        // leaving the constant alone in that case is right.
        if (stmt.LoopElseFlag.Length > 0)
        {
            VisitStatement(new AssignStmt(new VariableExpr(stmt.LoopElseFlag), new IntegerLiteral(0)));
            foreach (var key in new[]
                     {
                         stmt.LoopElseFlag,
                         currentInlinePrefix + stmt.LoopElseFlag,
                         currentFunction + "." + stmt.LoopElseFlag,
                         currentModulePrefix + stmt.LoopElseFlag,
                     })
                constantVariables.Remove(key);
        }

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
        string resolvedMessage = stmt.Message;
        if (!string.IsNullOrEmpty(stmt.MessageName))
        {
            string qualified = string.IsNullOrEmpty(currentFunction)
                ? stmt.MessageName
                : currentFunction + "." + stmt.MessageName;
            resolvedMessage = ResolveStrConstant(qualified)
                ?? ResolveStrConstant(stmt.MessageName)
                ?? throw UserError(
                    $"raise {stmt.ErrorType}({stmt.MessageName}): '{stmt.MessageName}' is not a " +
                    "string constant known at compile time. The message must be one or more " +
                    "string literals, or the name of a module-level constant declared as " +
                    $"`{stmt.MessageName}: str = \"...\"`");
        }

        if (stmt.ErrorType == "CompileError")
        {
            string msg = resolvedMessage.Length > 0 ? resolvedMessage : "CompileError";
            // Only branches opened INSIDE the current inline expansion make the raise
            // conditional: a `raise` at the top of an @inline body must abort even when
            // the user wrapped the CALL in a while/if (the expansion is reachable
            // whenever the call is). Compare against the depth at expansion entry.
            int baseDepth = inlineStack.Count > 0 ? inlineStack[^1].EntryBranchDepth : 0;
            if (_runtimeBranchDepth <= baseDepth)
            {
                // Statically unconditional: the raise is reachable without any runtime
                // guard. Abort compilation immediately — this is the intended ZCA path.
                // Inside an @inline expansion the raise's own line belongs to the library,
                // while the diagnostic is printed against the file being compiled -- so a
                // library line number lands on an unrelated line of the user's program, or
                // past its end (machine.py:115 reported against an 8-line sketch).
                // currentStmtLine stays frozen at the call site during an expansion, which
                // is the line whoever reads the error can actually act on.
                // When the refusal is about an argument whose position is known, that is what
                // the caret belongs on: the reader has to change that value, and inside a six
                // pin constructor "one of these is wrong" is not enough to act on. Otherwise
                // the call site's line, unlocated, as before.
                if (blamedArgument.Count > 0)
                {
                    var at = blamedArgument[^1];
                    throw new ArchitectureError(msg, at.Line > 0 ? at.Line : stmt.Line,
                                                at.Column, at.Length > 0 ? at.Length : 1);
                }
                int raiseLine = inlineDepth > 0 && currentStmtLine > 0 ? currentStmtLine : stmt.Line;
                throw new ArchitectureError(msg, raiseLine, 0);
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

        // A runtime exception raised at a point the expansion always reaches has no handler and
        // no diagnostic, so it aborts compilation. `SawDynamicLoop` is what keeps that rule off
        // the shape it does not mean: a lookup that probes a table and raises when the search
        // runs out reaches its raise only for data the compiler cannot see.
        if (!string.IsNullOrEmpty(stmt.ErrorType) && inlineStack.Count > 0 &&
            tryCatchStack.Count == 0 && !inlineStack[^1].SawDynamicLoop &&
            _runtimeBranchDepth <= inlineStack[^1].EntryBranchDepth)
        {
            string reason = resolvedMessage.Length > 0 ? resolvedMessage : stmt.ErrorType;
            int line = currentStmtLine > 0 ? currentStmtLine : stmt.Line;
            throw new ArchitectureError($"{stmt.ErrorType}: {reason}", line, 0);
        }

        // A bare `raise` (no type) re-raises the exception currently being handled. Re-signal from
        // the handler's saved code variable (SignalError reloads R22 from it), so it is correct even
        // if the handler clobbered R22. Outside a handler (no saved code) fall back to keeping R22.
        Val code = !string.IsNullOrEmpty(stmt.ErrorType)
            ? ResolveBinding(stmt.ErrorType)
            : handlerCodeStack.Count > 0
                ? new Variable(handlerCodeStack[^1], DataType.UINT8)
                : new Constant(0);

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
        // The code is saved to a stable per-try variable at the dispatcher (below) so it survives
        // handler body code — which may clobber R22 — for a bare `raise` and the dispatch compares.
        string exnCodeVar = "__exn_code_" + (exnCodeId++);
        variableTypes[exnCodeVar] = DataType.UINT8;   // so the allocator gives it a home
        Val exnCode = new Variable(exnCodeVar, DataType.UINT8);

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

        // Save the error code (still in R22) to a stable variable so a bare `raise` and the
        // comparisons below survive handler code that clobbers R22. __exn_r22_capture is the
        // read-only R22 alias; the copy's destination gets a normal (call-surviving) home.
        Emit(new Copy(new Variable("__exn_r22_capture", DataType.UINT8), new Variable(exnCodeVar, DataType.UINT8)));

        for (int i = 0; i < stmt.Handlers.Count; i++)
        {
            var (exnType, handlerBody) = stmt.Handlers[i];
            string skipLabel = MakeLabel();

            bool catchAll = string.IsNullOrEmpty(exnType) || exnType is "Exception" or "BaseException";
            if (!catchAll)
            {
                Val expectedCode = ResolveBinding(exnType);
                Val matchTemp = MakeTemp(DataType.UINT8);
                Emit(new Binary(PyMCU.IR.BinaryOp.Equal, exnCode, expectedCode, matchTemp));
                Emit(new JumpIfZero(matchTemp, skipLabel));
            }

            // The finally is pending while the handler body runs, so a `return`/`break`/`continue`
            // inside the handler runs it first. The saved code is pushed so a bare `raise` re-raises
            // the right exception. Both are popped before the explicit finally on the normal exit.
            if (pushedFinally) finallyStack.Add(stmt.Finally!);
            handlerCodeStack.Add(exnCodeVar);
            foreach (var s in handlerBody)
                VisitStatement(s);
            handlerCodeStack.RemoveAt(handlerCodeStack.Count - 1);
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