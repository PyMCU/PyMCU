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

    /// <summary>
    /// The literal a comparison operand stands for, when it stands for one (#330).
    ///
    /// An @inline expansion records its folded return value under the result temporary's name
    /// rather than handing back a Constant, so the operands of a comparison have to be asked
    /// the same question every other consumer of an @inline result asks. Anything that is not
    /// a temporary carrying a known value comes back untouched.
    ///
    /// The TEXT travels with the number. A one-character string reaches this compiler as its
    /// character code in one position and as an interned id in another, and comparing the two
    /// numbers compares two encodings of the same string (#211); the comparison above decides
    /// a pair by text exactly when both sides carry text, so dropping it here would answer a
    /// string comparison with an id comparison.
    /// </summary>
    private Val FoldedOperand(Val v)
    {
        // A function-local last assigned a compile-time value -- `dot = s.find(".")`
        // binds -1 -- reaches here as its storage Variable, and the comparison has to
        // be decided on the value the name provably holds. localConstantValues is
        // reconciled at every join to the entry all arms agree on, so a hit IS the
        // run-time value; a miss answers as before (adafruit_ht16k33's `if dot < 0`,
        // where the unfollowed arm slices a compile-time string by dot).
        if (v is Variable fv && localConstantValues.TryGetValue(fv.Name, out int lcv))
            return new Constant(lcv);
        if (v is not Temporary t) return v;
        if (!TryFoldedConstant(t, out int value)) return v;
        strConstantVariables.TryGetValue(t.Name, out string? text);
        return new Constant(value, text);
    }

    /// <summary>
    /// How the reader wrote the operand a comparison could not decide, for the guard warning
    /// to name (#330). Null when there is nothing worth naming -- a literal is decided by
    /// definition, and a shape with no short spelling is better left out of the sentence than
    /// described inaccurately.
    /// </summary>
    private static string? DescribeOperand(Expression e) => e switch
    {
        VariableExpr v => v.Name,
        MemberAccessExpr m when DescribeOperand(m.Object) is { } o => o + "." + m.Member,
        CallExpr c when DescribeOperand(c.Callee) is { } f => f + "(...)",
        _ => null,
    };

    /// The operand of the most recently emitted run-time comparison, as the reader spelled it.
    /// Read when a branch is entered, so a `raise CompileError` inside it can say which value
    /// the compiler could not decide instead of repeating advice the program already followed.
    private string? _pendingUndecidedOperand;

    /// What each open run-time branch could not decide, by depth. A branch entered for a reason
    /// that names nothing -- a match arm, a loop whose exit is unknown -- records null, so the
    /// warning falls back to the general sentence rather than naming a value from a branch that
    /// closed two statements ago.
    private readonly Dictionary<int, string?> _undecidedByBranchDepth = new();

    private void EnterRuntimeBranch(string? undecided)
    {
        _runtimeBranchDepth++;
        _undecidedByBranchDepth[_runtimeBranchDepth] = undecided;
        _runtimeBranchTokens.Add(_nextBranchToken++);
    }

    private void LeaveRuntimeBranch()
    {
        _undecidedByBranchDepth.Remove(_runtimeBranchDepth);
        _runtimeBranchDepth--;
        _runtimeBranchTokens.RemoveAt(_runtimeBranchTokens.Count - 1);
    }

    private int EmitOptimizedConditionalJump(Expression cond, string targetLabel, bool jumpIfTrue = false)
    {
        _pendingUndecidedOperand = null;

        // Every condition that reaches here is a truth test, including each operand of an
        // `and` / `or`, which recurses into this method. `if x and True:` tested the raw
        // instance handle for x and answered false for an object whose __bool__ says true.
        cond = LowerInstanceTruthiness(cond);

        // `if self.dp:` on a field that holds None. None is falsy in Python and its falseness
        // is known here, so the branch is decided at compile time and the side that cannot run
        // is not lowered. Without this the dead side WAS lowered, and an optional peripheral --
        // `self.dp = None` guarded by `if self.dp:` before every use, which is how every driver
        // writes one -- failed inside it, calling a method on a receiver with no class and
        // reporting a mangled symbol the user never wrote (PyMCU#334).
        if (IsNoneValued(cond))
        {
            if (jumpIfTrue) return -1;      // never true: fall through, skip nothing
            Emit(new Jump(targetLabel));    // always false: take the not-taken path
            return -1;
        }

        // `not x` where x is bound to None -- the mirror of the check above: None is
        // falsy, so `not x` is always true and only the taken side may be lowered.
        // Without this the UnaryExpr was lowered as a run-time `not` on a slot that
        // never received a value (None has no storage), and BOTH branches were
        // compiled -- `if not pixel_order:` in neopixel.py's __init__ is this shape.
        if (cond is UnaryExpr { Op: AstUnOp.Not } notCond && IsNoneValued(notCond.Operand))
        {
            if (jumpIfTrue) Emit(new Jump(targetLabel));
            return 2;
        }

        // RFC 0009 section 5: `if v:` on a live optional is a tag read first -- None is
        // falsy whatever sits in the payload, and only a payload tag falls through to
        // the payload's own truthiness. Reading the payload under a None tag is the
        // exact garbage the tag exists to prevent.
        if (LiveOptionalTag(cond) is { } optCond)
        {
            Val optPayload = EvalOptionalCarry(cond);
            EmitOptionalTruthJump(optCond.tag, optPayload, optCond.noneIdx, targetLabel, jumpIfTrue);
            return 1;
        }
        if (cond is UnaryExpr { Op: AstUnOp.Not } notOpt
            && LiveOptionalTag(notOpt.Operand) is { } notOptTag)
        {
            Val notPayload = EvalOptionalCarry(notOpt.Operand);
            EmitOptionalTruthJump(notOptTag.tag, notPayload, notOptTag.noneIdx, targetLabel, !jumpIfTrue);
            return 1;
        }

        // `not x` where x is a compile-time string: the text decides the branch --
        // `not ""` is always true, `not "GRB"` always false. Without the fold the
        // `not` lowered as a run-time test, BOTH sides compiled, and a dead side's
        // rebind (`pixel_order = GRB` in neopixel's __init__) discarded the string
        // binding the `elif` arm still needed to see.
        if (cond is UnaryExpr { Op: AstUnOp.Not } notStr
            && TryConstStrTruthiness(notStr.Operand, out bool notStrTruthy))
        {
            if (notStrTruthy)
            {
                if (!jumpIfTrue) Emit(new Jump(targetLabel));
                return -1;
            }
            if (jumpIfTrue) Emit(new Jump(targetLabel));
            return 2;
        }

        // An `if` does not lower its comparison through VisitBinary; it comes straight here and
        // becomes a conditional jump. That is how `a == b` over two bytes names emitted a
        // one-byte `jne` between the two array names and answered without reading either.
        if (cond is BinaryExpr seqCmp) RefuseSequenceComparison(seqCmp);

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

            // A binary operator that is not a comparison can never be decided here -- only
            // the six comparisons have cases below. Evaluating the operands anyway and then
            // returning 0 replays their side effects when the caller lowers the whole
            // expression itself: `while self._get_status() & 0x08:` in adafruit_bmp280's
            // conversion poll sent the status-register write+read twice per iteration.
            // VisitBinary applies the same register-operand rejection on that path.
            if (binExpr.Op is not (Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual
                                   or Frontend.BinaryOp.Less or Frontend.BinaryOp.LessEq
                                   or Frontend.BinaryOp.Greater or Frontend.BinaryOp.GreaterEq))
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

            // An @inline call in a CONDITION folds like one anywhere else (#330).
            //
            // An @inline expansion never hands back a Constant: it returns the temporary its
            // `return` was assigned into, and the literal is recorded under that temporary's
            // NAME. Every other consumer asks -- a call argument, an assignment, a later read
            // of a name -- so `x = port_of(p)` then `if x != 0` folded, and the same test
            // written in one line did not. A `Temporary` standing for a known literal was
            // indistinguishable here from a genuine run-time temporary, so the branch was
            // classified run-time, and a `raise CompileError` inside it was downgraded to a
            // warning telling the reader to declare `const` a parameter already declared
            // `const`.
            //
            // Temporaries only: a Variable already resolves through ResolveBinding on the way
            // in, so chasing one here would be a second answer to a question already answered.
            v1 = FoldedOperand(v1);
            v2 = FoldedOperand(v2);

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
                // Two Constants standing for STRINGS compare by their text, not by their value.
                // The same string reaches this point with two different values depending on how
                // it got here: a one-character literal in expression position is its character
                // code (97), and the same literal read back through a name is an interned id
                // (256). Comparing the numbers compared two encodings of "a" and answered false,
                // and the `if` then folded away the branch that should have run (#211).
                // BOTH sides, never one. Deciding a mixed pair here as "a string is never equal
                // to a non-string" is the Python answer and it broke two compile-time guards:
                // `if name == "B"` inside an @inline, where the parameter carries no text and
                // the literal does, was numerically true and stopped firing. Requiring both
                // makes this strictly additive -- a pair that has no text on one side is
                // compared exactly as it was before.
                if (c1.Text != null && c2.Text != null
                    && binExpr.Op is Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual)
                {
                    bool textEq = c1.Text == c2.Text;
                    bool wantEq = binExpr.Op == Frontend.BinaryOp.Equal;
                    bool res = textEq == wantEq;

                    if (jumpIfTrue) { if (res) Emit(new Jump(targetLabel)); }
                    else            { if (!res) Emit(new Jump(targetLabel)); }
                    return res ? 2 : -1;
                }

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
                if (FoldComparisonByRange(binExpr.Op, v1, v2) is { } known)
                {
                    if (jumpIfTrue) { if (known) Emit(new Jump(targetLabel)); }
                    else            { if (!known) Emit(new Jump(targetLabel)); }
                    return known ? 2 : -1;
                }
                DataType cmpType = ComparisonType(v1, v2);
                v1 = WidenForComparison(v1, cmpType, left: true);
                v2 = WidenForComparison(v2, cmpType);

                // The operand that kept this comparison from being decided, for a guard
                // warning inside the branch to name (#330). The side that is NOT a literal is
                // the one the reader has to change; when both are, the left one is named.
                _pendingUndecidedOperand =
                    (v1 is Constant ? null : DescribeOperand(binExpr.Left))
                    ?? (v2 is Constant ? null : DescribeOperand(binExpr.Right));
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

    /// <summary>
    /// The truthiness of an operand that is a name bound to a compile-time string:
    /// "" is falsy, any other text truthy. False when the operand is not such a name --
    /// a run-time string's length is not known here, and a non-string is not this
    /// helper's question.
    /// </summary>
    private bool TryConstStrTruthiness(Expression operand, out bool truthy)
    {
        truthy = false;
        if (operand is not VariableExpr ve) return false;
        string q = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + ve.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + ve.Name : ve.Name);
        string? text = ResolveStrConstant(q) ?? ResolveStrConstant(ve.Name);
        if (text == null) return false;
        truthy = text.Length > 0;
        return true;
    }

    // Python's truthiness for an instance: __bool__, else __len__ != 0, else always true.
    // PyMCU evaluates an instance as whatever scalar it collapsed to -- 0 for a multi-field
    // class -- so `if obj:` silently took the false branch for every object. Rewrite to the
    // protocol method the class defines, and refuse when it defines neither: a condition that
    // is constant-false by accident is worse to debug than a compile error.
    private Expression LowerInstanceTruthiness(Expression cond)
    {
        // RFC 0008: an open romfs handle is truthy until close() -- both are
        // compile-time facts, so the condition is a literal.
        if (ResolveRomfsHandleExpr(cond) is { } romTruth)
            return new IntegerLiteral(romTruth.Closed ? 0 : 1);

        if (cond is MemberAccessExpr fieldAccess && FieldInstanceClass(fieldAccess) is { } fieldCls)
        {
            foreach (var m in new[] { "__bool__", "__len__" })
                if (ClassDefinesMethod(ResolveMROMethod(fieldCls, m), m))
                    return new CallExpr(new MemberAccessExpr(fieldAccess, m), new List<Expression>())
                        { Line = cond.Line, Column = cond.Column, Length = cond.Length };
        }

        if (cond is not VariableExpr ve) return cond;
        if (InstanceClassOfName(ve.Name) is not { } cls) return cond;
        foreach (var m in new[] { "__bool__", "__len__" })
            if (TryResolveInstanceMethodAst(ve.Name, m) != null)
                return new CallExpr(new MemberAccessExpr(ve, m), new List<Expression>())
                    { Line = cond.Line };
        string shown = cls.Contains('_') ? cls[(cls.LastIndexOf('_') + 1)..] : cls;
        throw UserError(
            $"'{ve.Name}' is an instance of '{shown}' with no __bool__ or __len__, so it has no " +
            $"truth value. Test a field or a method result instead (e.g. `if {ve.Name}.<field>:`).", ve);
    }

    private string? FieldInstanceClass(MemberAccessExpr access)
    {
        if (access.Object is not VariableExpr recv) return null;
        string? recvCls = InstanceClassOfName(recv.Name);
        if (string.IsNullOrEmpty(recvCls))
            foreach (string? key in new[]
                     {
                         string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + recv.Name,
                         string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + recv.Name,
                         recv.Name,
                     })
            {
                if (key == null) continue;
                recvCls = ReceiverClassThroughAliases(key);
                if (!string.IsNullOrEmpty(recvCls)) break;
            }
        if (string.IsNullOrEmpty(recvCls) && recv.Name == "self" && !string.IsNullOrEmpty(currentFunction))
            foreach (var kv in fieldClasses)
            {
                int bar = kv.Key.IndexOf('|');
                if (bar <= 0 || kv.Key[(bar + 1)..] != access.Member) continue;
                string owner = kv.Key[..bar];
                if (currentFunction.StartsWith(owner + "_", StringComparison.Ordinal)) { recvCls = owner; break; }
            }
        if (string.IsNullOrEmpty(recvCls)) return null;
        return fieldClasses.TryGetValue(recvCls + "|" + access.Member, out var raw)
            ? ResolveConcreteClass(raw) : null;
    }

    /// <summary>
    /// The origin of the argument an if-chain is dispatching on, when it is testing one.
    /// `if pin_name == "PD2": ... else: raise` is the same shape as a match on a parameter and
    /// wants the same answer, and it is the shape the two sensor drivers use where the LCD uses
    /// a match (#193). Either side of the comparison, because `if "PD2" == pin_name` is the
    /// same test.
    /// </summary>
    private Expression? IfChainSubjectOrigin(Expression condition)
    {
        if (condition is not BinaryExpr { Op: PyMCU.Frontend.BinaryOp.Equal } cmp) return null;

        foreach (var side in new[] { cmp.Left, cmp.Right })
        {
            if (side is not VariableExpr ve) continue;
            if (argumentOrigin.TryGetValue(currentInlinePrefix + ve.Name, out var origin)
                || argumentOrigin.TryGetValue(ve.Name, out origin))
                return origin;
        }
        return null;
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

        // An if-chain that dispatches on an argument is, when one of its arms refuses, a
        // refusal OF THAT ARGUMENT. Held for the whole chain, so the `else: raise` at the end
        // is covered too, which is where these drivers put theirs.
        var chainSubject = IfChainSubjectOrigin(stmt.Condition);
        if (chainSubject != null) blamedArgument.Add(chainSubject);
        try
        {
            VisitIfBody(stmt);
        }
        finally
        {
            if (chainSubject != null) blamedArgument.RemoveAt(blamedArgument.Count - 1);
        }
    }

    private void VisitIfBody(IfStmt stmt)
    {

        string endLabel = MakeLabel();
        string nextLabel = (stmt.ElifBranches.Count == 0 && stmt.ElseBranch == null) ? endLabel : MakeLabel();

        int condStart = currentInstructions.Count;
        int optResult = EmitOptimizedConditionalJump(stmt.Condition, nextLabel, false);
        // Captured now, before the body is lowered: anything inside it that emits its own
        // run-time comparison overwrites the field (#330).
        string? condUndecided = _pendingUndecidedOperand;
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

        // True once any arm of the chain ran under a run-time guard: it decides whether an
        // arm that ends the sequence (return/raise) kills the statements after the chain
        // too. With no run-time arm, a compile-time-taken arm is the chain's whole story,
        // so its termination propagates; with one, the chain's end is reachable through it.
        bool anyRuntimeArm = isRuntimeBranch;

        var snapBefore = new Dictionary<string, string>(strConstantVariables);
        var branchSnaps = new List<Dictionary<string, string>>();
        // The same bookkeeping for integer constants. Arms are mutually exclusive, so a value
        // one arm assigns cannot be in effect while a sibling arm runs: without this, a field
        // set to a constant in one arm was still believed when another arm READ it, the read
        // folded, and `self._n = self._n + 1` in the second arm became a store of the constant
        // 1. The field never accumulated and a state machine never left that arm.
        var snapBeforeInt = new Dictionary<string, int>(constantVariables);
        var branchSnapsInt = new List<Dictionary<string, int>>();
        // The locals map travels with constantVariables through every arm and is reconciled on
        // the same rule. Left out, a name each arm assigns differently kept whichever arm was
        // lowered last, and a call AFTER the chain handed the callee that arm's value: a PWM
        // duty came out 0x3F where 0x7F was asked for, and a list sum printed 0 (PyMCU#327).
        var snapBeforeLocals = new Dictionary<string, int>(localConstantValues);
        var branchSnapsLocals = new List<Dictionary<string, int>>();
        bool hasElse = stmt.ElseBranch != null;

        // RFC 0009 narrowing: each arm lowers under the condition's effect on that arm
        // (`v is not None` narrows the then-path, `v is None` narrows the fall-through),
        // and the join keeps a name narrowed only when every surviving path proves it.
        var optArmEnds = new List<OptionalSnap?>();
        OptionalSnap inheritOpt = SnapOptionalState();

        if (!skipThen)
        {
            RestoreOptionalState(inheritOpt);
            ApplyOptionalCondEffect(stmt.Condition, true);
            if (isRuntimeBranch) EnterRuntimeBranch(condUndecided);
            VisitStatement(stmt.ThenBranch);
            if (isRuntimeBranch) LeaveRuntimeBranch();
            // A run-time-guarded arm's termination is conditional on its guard.
            if (isRuntimeBranch) _seqTerminated = false;
            if (stmt.ElifBranches.Count > 0 || stmt.ElseBranch != null)
                Emit(new Jump(endLabel));
            branchSnaps.Add(new Dictionary<string, string>(strConstantVariables));
            strConstantVariables = new Dictionary<string, string>(snapBefore);
            branchSnapsInt.Add(new Dictionary<string, int>(constantVariables));
            constantVariables = new Dictionary<string, int>(snapBeforeInt);
            branchSnapsLocals.Add(new Dictionary<string, int>(localConstantValues));
            localConstantValues = new Dictionary<string, int>(snapBeforeLocals);
            optArmEnds.Add(AlwaysLeaves(stmt.ThenBranch) ? null : SnapOptionalState());
        }
        RestoreOptionalState(inheritOpt);
        ApplyOptionalCondEffect(stmt.Condition, false);
        inheritOpt = SnapOptionalState();

        for (int i = 0; i < stmt.ElifBranches.Count; ++i)
        {
            Emit(new Label(nextLabel));
            bool isLastElif = i == stmt.ElifBranches.Count - 1;
            nextLabel = (isLastElif && stmt.ElseBranch == null) ? endLabel : MakeLabel();

            var elifCond = stmt.ElifBranches[i].Condition;
            var elifBlock = stmt.ElifBranches[i].Body;

            int elifCondStart = currentInstructions.Count;
            int elifOpt = EmitOptimizedConditionalJump(elifCond, nextLabel, false);
            string? elifUndecided = _pendingUndecidedOperand;
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
                // Unconditional only when nothing earlier ran under a run-time guard.
                if (anyRuntimeArm) _seqTerminated = false;
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
                    else
                    {
                        // A constant-true elif is the chain's answer, same as elifOpt == 2
                        // folded inline: lower the arm and stop, or the `else:` below it is
                        // lowered too and its `raise` aborts a build that never reaches it.
                        VisitStatement(elifBlock);
                        if (anyRuntimeArm) _seqTerminated = false;
                        Emit(new Label(endLabel));
                        return;
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
                RestoreOptionalState(inheritOpt);
                ApplyOptionalCondEffect(elifCond, true);
                if (elifIsRuntime) EnterRuntimeBranch(elifUndecided);
                VisitStatement(elifBlock);
                if (elifIsRuntime) LeaveRuntimeBranch();
                if (elifIsRuntime) { anyRuntimeArm = true; _seqTerminated = false; }
                if (!isLastElif || stmt.ElseBranch != null) Emit(new Jump(endLabel));
                branchSnaps.Add(new Dictionary<string, string>(strConstantVariables));
                strConstantVariables = new Dictionary<string, string>(snapBefore);
                branchSnapsInt.Add(new Dictionary<string, int>(constantVariables));
                constantVariables = new Dictionary<string, int>(snapBeforeInt);
                branchSnapsLocals.Add(new Dictionary<string, int>(localConstantValues));
                localConstantValues = new Dictionary<string, int>(snapBeforeLocals);
                optArmEnds.Add(AlwaysLeaves(elifBlock) ? null : SnapOptionalState());
            }
            RestoreOptionalState(inheritOpt);
            ApplyOptionalCondEffect(elifCond, false);
            inheritOpt = SnapOptionalState();
        }

        if (stmt.ElseBranch != null)
        {
            Emit(new Label(nextLabel));
            // The else runs when every earlier condition failed, so it is guarded by ANY
            // run-time arm in the chain, not only the `if`'s own condition.
            RestoreOptionalState(inheritOpt);
            if (anyRuntimeArm) EnterRuntimeBranch(condUndecided);
            VisitStatement(stmt.ElseBranch);
            if (anyRuntimeArm) LeaveRuntimeBranch();
            // With a run-time arm before it the else is conditional; without one it is the
            // chain's unconditional continuation and its termination stands.
            if (anyRuntimeArm) _seqTerminated = false;
            branchSnaps.Add(new Dictionary<string, string>(strConstantVariables));
            strConstantVariables = new Dictionary<string, string>(snapBefore);
            branchSnapsInt.Add(new Dictionary<string, int>(constantVariables));
            constantVariables = new Dictionary<string, int>(snapBeforeInt);
            branchSnapsLocals.Add(new Dictionary<string, int>(localConstantValues));
            localConstantValues = new Dictionary<string, int>(snapBeforeLocals);
            optArmEnds.Add(AlwaysLeaves(stmt.ElseBranch) ? null : SnapOptionalState());
        }

        Emit(new Label(endLabel));
        JoinOptionalState(optArmEnds, hasElse ? null : inheritOpt);

        // Past the chain, an integer constant survives only when every arm agrees on it and one
        // arm always runs, which is the rule the string constants below already follow. Every
        // arm has to contribute its snapshot, the else included: with one missing, a value only
        // one arm assigns looks unanimous and is re-established as a constant, which is what
        // #132 relies on NOT happening for an @inline returning a different value per branch.
        if (branchSnapsInt.Count > 0)
        {
            // The pre-branch snapshot predates anything an arm killed, so restoring it
            // wholesale resurrects a constant a write inside an arm had already marked mutable
            // and the read then folds to a value the program has since overwritten.
            constantVariables = new Dictionary<string, int>(snapBeforeInt);
            foreach (var dead in killedConstants) constantVariables.Remove(dead);

            var changedInt = new HashSet<string>();
            foreach (var kvp in branchSnapsInt.SelectMany(snap => snap))
                if (!snapBeforeInt.TryGetValue(kvp.Key, out var oldV) || oldV != kvp.Value)
                    changedInt.Add(kvp.Key);

            foreach (var key in changedInt)
            {
                bool allAgree = true;
                int agreed = 0;
                bool first = true;
                foreach (var snap in branchSnapsInt)
                {
                    if (!snap.TryGetValue(key, out var v)) { allAgree = false; break; }
                    if (first) { agreed = v; first = false; }
                    else if (v != agreed) { allAgree = false; break; }
                }

                if (allAgree && !first && hasElse) constantVariables[key] = agreed;
                else constantVariables.Remove(key);
            }
        }

        // The locals, on the same rule, stated the other way round: what the chain leaves is
        // what EVERY path agrees on. Asking instead which keys changed misses the ones an arm
        // DROPPED -- `prescaler: uint8 = 0` then a call in each arm -- because a key nothing
        // records is absent from the arm's snapshot rather than different in it, and the
        // pre-chain 0 was resurrected past a chain that had overwritten it.
        //
        // Without an `else` the fall-through is a path of its own, carrying the state from
        // before the chain, so it counts as one more arm.
        if (branchSnapsLocals.Count > 0)
        {
            var paths = new List<Dictionary<string, int>>(branchSnapsLocals);
            if (!hasElse) paths.Add(snapBeforeLocals);

            var agreedLocals = new Dictionary<string, int>();
            foreach (var kvp in paths[0])
            {
                if (killedConstants.Contains(kvp.Key)) continue;
                bool allAgree = true;
                for (int pi = 1; pi < paths.Count; ++pi)
                    if (!paths[pi].TryGetValue(kvp.Key, out var v) || v != kvp.Value)
                    {
                        allAgree = false;
                        break;
                    }
                if (allAgree) agreedLocals[kvp.Key] = kvp.Value;
            }

            localConstantValues = agreedLocals;
        }

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
                                   Val targetVal, string nextCaseLabel, string endLabel,
                                   out bool bodyLowered)
    {
        bodyLowered = false;
        if (pattern.Callee is not VariableExpr patName)
            throw UserError("match/case: a call is not a pattern; `case Cls(...)` matches a "
                          + "class, and its callee has to be a class name", pattern);

        string subjectClass = GetValClass(targetVal);
        if (string.IsNullOrEmpty(subjectClass))
            throw UserError(
                $"match/case: `case {patName.Name}(...)` needs the subject's class to be known "
                + "at compile time, and it is not here. Match on a value the program builds "
                + $"from a constructor (`p = {patName.Name}(...)` then `match p:`), or compare "
                + "the fields directly with if/elif", patName);

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
                    + $"(`case {patName.Name}(x=..., y=...)`)", patName);

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
        EnterRuntimeBranch(null);
        if (branch.Body != null) VisitBlock((Block)branch.Body);
        LeaveRuntimeBranch();
        _seqTerminated = false;
        bodyLowered = true;

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
        // Read the subject's None-ness from the AST, before lowering it. A name bound to None
        // has no value to read -- that is what None means here -- so visiting it would emit a
        // read of a name nothing writes, and the match would be decided on it (#306).
        bool subjectIsNone = IsNoneValued(stmt.Target);
        Val targetVal = subjectIsNone ? new NoneVal() : VisitExpression(stmt.Target);
        bool ctAlreadyMatched = false;
        string endLabel = MakeLabel();

        // Match arms are sibling paths, like the if/elif chain's: a compile-time binding
        // recorded while lowering one arm must not answer reads in the next arm's body,
        // and what survives the match is what every reachable path agrees on -- the arm
        // states plus the pre-match state when no arm catches the fall-through.
        var snapBeforeInt = new Dictionary<string, int>(constantVariables);
        var snapBeforeStr = new Dictionary<string, string>(strConstantVariables);
        var snapBeforeLocals = new Dictionary<string, int>(localConstantValues);
        var armSnapsInt = new List<Dictionary<string, int>>();
        var armSnapsStr = new List<Dictionary<string, string>>();
        var armSnapsLocals = new List<Dictionary<string, int>>();
        // The first arm whose body runs unconditionally (a compile-time-matched pattern, or
        // an unguarded wildcard under a decided subject) decides the state after the match.
        int firstUncondArm = -1;
        // An unguarded wildcard answers every path no earlier arm took: nothing falls past it.
        bool wildcardCoversAll = false;

        void CloseArmLocals()
        {
            armSnapsInt.Add(new Dictionary<string, int>(constantVariables));
            armSnapsStr.Add(new Dictionary<string, string>(strConstantVariables));
            armSnapsLocals.Add(new Dictionary<string, int>(localConstantValues));
            constantVariables = new Dictionary<string, int>(snapBeforeInt);
            strConstantVariables = new Dictionary<string, string>(snapBeforeStr);
            localConstantValues = new Dictionary<string, int>(snapBeforeLocals);
        }

        foreach (var branch in stmt.Branches)
        {
            string nextCaseLabel = MakeLabel();

            if (branch.Pattern != null)
            {
                if (branch.Pattern is ListExpr seq)
                {
                    string arrName = "";
                    if (targetVal is Variable v) arrName = v.Name;
                    else throw UserError("match/case sequence pattern: subject must be an array variable", stmt);

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
                    _seqTerminated = false;
                    CloseArmLocals();
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
                    if (VisitClassPattern(stmt, branch, classPat, targetVal, nextCaseLabel, endLabel,
                                          out bool classArmLowered))
                    {
                        // The class-pattern arm's body is always lowered behind run-time
                        // compares -- a sibling path, never the unconditional one.
                        if (classArmLowered) CloseArmLocals();
                        continue;
                    }
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

                // A subject that folded to a compile-time CONSTANT is never None, so a
                // `case None` alternative cannot match it. Left in, the arm was lowered as a
                // run-time comparison against a value with no representation -- an arbitrary
                // register -- and which arm ran was decided by whatever that register held.
                // `busio.UART(parity=Parity.EVEN)` compared 1 against it to choose between
                // "no parity" and the parity it was given (#324).
                if (targetVal is Constant && altVals.Any(v => v is NoneVal))
                {
                    var keptAlts = new List<Expression>();
                    var keptVals = new List<Val>();
                    for (int ai = 0; ai < altVals.Count; ++ai)
                    {
                        if (altVals[ai] is NoneVal) continue;
                        keptAlts.Add(alts[ai]);
                        keptVals.Add(altVals[ai]);
                    }
                    alts = keptAlts;
                    altVals = keptVals;
                }

                // `match x:` where x is None. None is not a Constant -- it has no value to
                // compare against -- so every arm stayed a run-time comparison and every arm
                // was LOWERED, including arms whose bodies refuse at compile time. `pull = None`
                // is how CircuitPython spells "no pull", and it was refused with "Pull-down
                // resistor not supported on AVR", from the arm the program never selected
                // (#306).
                //
                // None is decidable here: it matches `case None` and the wildcard, and nothing
                // else. A pattern that is itself None is the only hit.
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
                bool subjectIsDecided = allAltsConst || subjectIsNone;
                bool skipBody = false;
                if (subjectIsNone)
                {
                    if (altVals.Any(v => v is NoneVal))
                    {
                        ctAlreadyMatched = true;
                    }
                    else
                    {
                        Emit(new Jump(nextCaseLabel));
                        skipBody = true;
                    }
                }
                else if (allAltsConst)
                {
                    bool anyMatch = false;
                    var ct = targetVal as Constant;
                    foreach (var v in altVals)
                    {
                        // Match a STRING pattern on its text, not on its value: the same
                        // one-character string is its character code in expression position and
                        // an interned id through a name, so `match c: case "a"` compared two
                        // spellings of "a" and fell through to the wildcard (#211). Both sides
                        // or neither, so a numeric pattern is compared exactly as before.
                        var alt = (Constant)v;
                        bool hit = alt.Text != null && ct!.Text != null
                            ? alt.Text == ct.Text
                            : alt.Value == ct!.Value;
                        if (hit)
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
                    bool matchBodyIsRuntime = !subjectIsDecided;
                    if (matchBodyIsRuntime) EnterRuntimeBranch(null);
                    if (branch.Body != null) VisitBlock((Block)branch.Body);
                    if (matchBodyIsRuntime) LeaveRuntimeBranch();
                    // A compile-time-decided subject makes the matched arm unconditional,
                    // so its termination propagates; a run-time match keeps it conditional.
                    if (matchBodyIsRuntime) _seqTerminated = false;
                    if (!matchBodyIsRuntime && firstUncondArm < 0)
                        firstUncondArm = armSnapsLocals.Count;
                    CloseArmLocals();
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

                    // A None subject is as decided as a constant one: reaching the wildcard
                    // means no `case None` matched it, which is a compile-time fact (#306).
                    bool wildcardIsRuntime = !(targetVal is Constant) && targetVal is not NoneVal;
                    if (wildcardIsRuntime) EnterRuntimeBranch(null);
                    if (branch.Body != null) VisitBlock((Block)branch.Body);
                    if (wildcardIsRuntime) LeaveRuntimeBranch();
                    if (wildcardIsRuntime) _seqTerminated = false;
                    if (!wildcardIsRuntime && firstUncondArm < 0)
                        firstUncondArm = armSnapsLocals.Count;
                    if (branch.Guard == null) wildcardCoversAll = true;
                    CloseArmLocals();
                    Emit(new Jump(endLabel));
                }
            }

            Emit(new Label(nextCaseLabel));
        }

        // Reconcile the per-arm states the way the if/elif chain does. An arm that ran
        // unconditionally is the state after the match; otherwise a name keeps its
        // compile-time value only where every reachable path -- each lowered arm, plus the
        // pre-match state when no arm catches the fall-through -- agrees on it.
        if (firstUncondArm >= 0)
        {
            constantVariables = armSnapsInt[firstUncondArm];
            strConstantVariables = armSnapsStr[firstUncondArm];
            localConstantValues = armSnapsLocals[firstUncondArm];
        }
        else if (armSnapsLocals.Count > 0)
        {
            var pathsInt = new List<Dictionary<string, int>>(armSnapsInt);
            var pathsStr = new List<Dictionary<string, string>>(armSnapsStr);
            var pathsLocals = new List<Dictionary<string, int>>(armSnapsLocals);
            if (!wildcardCoversAll)
            {
                pathsInt.Add(snapBeforeInt);
                pathsStr.Add(snapBeforeStr);
                pathsLocals.Add(snapBeforeLocals);
            }

            var agreedInt = new Dictionary<string, int>();
            foreach (var kvp in pathsInt[0])
            {
                bool allAgree = true;
                for (int pi = 1; pi < pathsInt.Count; ++pi)
                    if (!pathsInt[pi].TryGetValue(kvp.Key, out var v) || v != kvp.Value)
                    {
                        allAgree = false;
                        break;
                    }
                if (allAgree) agreedInt[kvp.Key] = kvp.Value;
            }
            constantVariables = agreedInt;

            var agreedStr = new Dictionary<string, string>();
            foreach (var kvp in pathsStr[0])
            {
                bool allAgree = true;
                for (int pi = 1; pi < pathsStr.Count; ++pi)
                    if (!pathsStr[pi].TryGetValue(kvp.Key, out var v) || v != kvp.Value)
                    {
                        allAgree = false;
                        break;
                    }
                if (allAgree) agreedStr[kvp.Key] = kvp.Value;
            }
            strConstantVariables = agreedStr;

            var agreedLocals = new Dictionary<string, int>();
            foreach (var kvp in pathsLocals[0])
            {
                if (killedConstants.Contains(kvp.Key)) continue;
                bool allAgree = true;
                for (int pi = 1; pi < pathsLocals.Count; ++pi)
                    if (!pathsLocals[pi].TryGetValue(kvp.Key, out var v) || v != kvp.Value)
                    {
                        allAgree = false;
                        break;
                    }
                if (allAgree) agreedLocals[kvp.Key] = kvp.Value;
            }
            localConstantValues = agreedLocals;
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
                // A loop body is lowered once and runs many times, so what the name held on the
                // first iteration is not what a call inside the loop may hand a callee.
                localConstantValues.Remove(key);
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
                    // A single-field instance IS its field: the value lives under the
                    // instance's own name, with no `_field` suffix to drop. Left in place,
                    // `self.value = self.value + 1` inside a method's loop folded every later
                    // read -- and the method's `return self.value` -- to the constructor's
                    // value, so a Fader that summed 0..9 returned 3.
                    constantVariables.Remove(prefix);
                    strConstantVariables.Remove(prefix);
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
    /// The method-name -> AST map <see cref="MethodMutatesField"/> needs to resolve
    /// `self.m()` inside a method of <paramref name="cls"/>: the class's own methods plus
    /// every inherited one, the nearest definition winning. With no map a self-call counts
    /// as writing every field -- `_adjusted_index` calling `self._bytes_per_buffer()`
    /// reported `_buffer_size` mutated, and the loop invalidation dropped a compile-time
    /// field constant it then had to fold (adafruit_ht16k33's scroll/marquee).
    /// </summary>
    private IReadOnlyDictionary<string, FunctionDef> SiblingMethodsOf(string cls)
    {
        var map = new Dictionary<string, FunctionDef>();
        for (string? cur = cls; cur != null;)
        {
            // classBasePrefixes values carry the trailing '_' of a mangled class prefix;
            // OwningClassOf's answer does not. One separator, no more: 'Cls__put' strips to
            // '_put', and two separators would eat the leading underscore of a private name.
            string pfx = cur.EndsWith("_", StringComparison.Ordinal) ? cur : cur + "_";
            foreach (var src in new Dictionary<string, FunctionDef>[] { instanceMethodDefs, methodAstByName })
                foreach (var kv in src)
                    if (kv.Key.StartsWith(pfx, StringComparison.Ordinal))
                        map.TryAdd(kv.Key[pfx.Length..], kv.Value);
            foreach (var kv in inlineFunctions)
                if (kv.Key.StartsWith(pfx, StringComparison.Ordinal) && kv.Value != null)
                    map.TryAdd(kv.Key[pfx.Length..], kv.Value!);
            cur = classBasePrefixes.TryGetValue(cur, out var b) ? b : null;
        }
        return map;
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

        var sibs = SiblingMethodsOf(cls);
        foreach (var (field, type, _) in layout)
        {
            if (MethodMutatesFieldPublic(def, field, sibs)) return false;

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

        var sibsN = SiblingMethodsOf(cls);
        foreach (var (field, type, _) in layout)
        {
            // Only below the first hop: at prefix "" this is the receiver's own field, which the
            // caller marks off the layout.
            if (prefix.Length > 0 && MethodMutatesFieldPublic(def, field, sibsN))
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

        var sibsW = SiblingMethodsOf(cls);
        foreach (var (field, type, _) in layout)
        {
            if (MethodMutatesFieldPublic(def, field, sibsW)) paths.Add(prefix + field);

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

        // RFC 0009: the condition's narrowing holds inside the body (`while r is not
        // None:` narrows r there). Past the loop nothing the condition proved survives:
        // it may never have run, and a name the body rewrites answers from its new
        // value. Tag slots gained inside DO persist -- the byte exists whichever path
        // ran -- so the slot map is the body's, the narrowing is the pre-loop's.
        var preLoopOpt = SnapOptionalState();
        ApplyOptionalCondEffect(stmt.Condition, true);
        if (isRuntimeLoop) EnterRuntimeBranch(null);
        VisitStatement(stmt.Body);
        if (isRuntimeLoop) LeaveRuntimeBranch();
        var postLoopOpt = SnapOptionalState();
        {
            var bodyAssigned = new HashSet<string>();
            CollectAssignedNames(stmt.Body, bodyAssigned);
            postLoopOpt.Narrowed.Clear();
            postLoopOpt.Narrowed.UnionWith(preLoopOpt.Narrowed);
            postLoopOpt.Narrowed.RemoveWhere(k => bodyAssigned.Contains(SourcePartOf(k)));
            postLoopOpt.None.Clear();
            postLoopOpt.None.UnionWith(preLoopOpt.None);
            postLoopOpt.None.RemoveWhere(k => bodyAssigned.Contains(SourcePartOf(k)));
            RestoreOptionalState(postLoopOpt);
        }
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
        if (loopStack.Count == 0) throw UserError("Break statement outside of loop", stmt);
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
        if (loopStack.Count == 0) throw UserError("Continue statement outside of loop", stmt);
        var loop = Enumerable.Last<LoopLabels>(loopStack);
        EmitPendingFinally(loop.FinallyDepth);   // run finallys between this continue and the loop
        Emit(new Jump(loop.ContinueLabel));
    }

    /// The file the CALL SITE is written in, to pair with `currentStmtLine`.
    ///
    /// Frame ZERO of the inline stack, not the innermost. `currentStmtLine` is only updated at
    /// `inlineDepth == 0`, so the line belongs to the statement in the function being lowered
    /// at depth 0, and the file that pairs with it is that function's. Reading the innermost
    /// frame instead names the driver that expanded the last helper: measured, `Servo("PZ9")`
    /// written in the entry file reported the servo package's `__init__.py` line 5, which is a
    /// licence comment.
    ///
    /// Null means the entry file, and here that is a statement rather than a fallback: the
    /// caller is the entry file exactly when frame zero recorded no path of its own.
    private string? CallSiteSourcePath()
    {
        string path = inlineStack.Count > 0 ? inlineStack[0].CallerSourcePath : currentSourcePath;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private void VisitRaise(RaiseStmt stmt)
    {
        string resolvedMessage = stmt.Message;
        Expression? dynamicMessage = stmt.MessageExpr;
        if (!string.IsNullOrEmpty(stmt.MessageName))
        {
            string qualified = string.IsNullOrEmpty(currentFunction)
                ? stmt.MessageName
                : currentFunction + "." + stmt.MessageName;
            string? named = ResolveStrConstant(qualified) ?? ResolveStrConstant(stmt.MessageName);
            if (named != null)
                resolvedMessage = named;
            else if (stmt.ErrorType == "CompileError")
                throw UserError(
                    $"raise {stmt.ErrorType}({stmt.MessageName}): '{stmt.MessageName}' is not a " +
                    "string constant known at compile time. The message must be one or more " +
                    "string literals, or the name of a module-level constant declared as " +
                    $"`{stmt.MessageName}: str = \"...\"`", stmt);
            else
                dynamicMessage ??= new VariableExpr(stmt.MessageName) { Line = stmt.Line };
        }

        if (dynamicMessage != null && stmt.ErrorType != "CompileError"
            && TryFoldRaiseMessageToString(dynamicMessage, out string folded))
        {
            resolvedMessage = folded;
            dynamicMessage = null;
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
                //
                // Both of these name the caller's FILE alongside the caller's line, and both
                // declare the choice. Leaving the file to the fallback meant "the entry file",
                // which is the caller only when the caller happens to be the entry file: with
                // the call written in a middle module, `Servo("PZ9")` on mid.py:5 reported
                // main.py:5, a line that mentions no pin in a file that contains none.
                // Issue #230.
                if (blamedArgument.Count > 0)
                {
                    var at = blamedArgument[^1];
                    throw new ArchitectureError(msg, at.Line > 0 ? at.Line : stmt.Line,
                                                at.Column, at.Length > 0 ? at.Length : 1)
                        { File = CallSiteSourcePath(), LocationIsFinal = true };
                }
                int raiseLine = inlineDepth > 0 && currentStmtLine > 0 ? currentStmtLine : stmt.Line;
                throw new ArchitectureError(msg, raiseLine, 0)
                    { File = CallSiteSourcePath(), LocationIsFinal = true };
            }

            // Inside a runtime-conditional branch: the const-propagation chain failed to
            // fold the guard to a compile-time value (e.g. mode: uint8 instead of
            // const[uint8]). Aborting would be a false positive — the raise might never
            // execute at runtime.
            // CompileError must NEVER mutate into a runtime instruction; it is a
            // compile-time-only concept. Emit nothing and warn the developer.
            //
            // The advice NAMES THE VALUE the compiler could not decide (#330). It used to say
            // "declare the guarding parameter as const[...]" unconditionally, which for the
            // shape that produced this issue told the reader to do what the program had
            // already done: both parameters were `const`, and what was undecided was the
            // comparison, not the declaration. A reader cannot tell a false warning from a
            // real one when the advice is the same sentence either way.
            _undecidedByBranchDepth.TryGetValue(_runtimeBranchDepth, out string? undecided);
            string advice = undecided != null
                ? $"`{undecided}` is not known at compile time here, so the branch could not be "
                  + "pruned. A guard is decided only when every value in its condition is fixed "
                  + "at compile time: a literal, a parameter declared `const` and passed a "
                  + "literal at every call site, a chip fact such as `__CHIP__`, or an `@inline` "
                  + "call over those."
                : "The branch could not be pruned, so the guard was not checked. A guard is "
                  + "decided only when every value in its condition is fixed at compile time: a "
                  + "literal, a parameter declared `const` and passed a literal at every call "
                  + "site, a chip fact such as `__CHIP__`, or an `@inline` call over those.";
            Console.Error.WriteLine(
                $"warning: CompileError guard could not be verified at compile time " +
                $"(line {stmt.Line}): {msg}. " + advice);
            return;
        }

        // A runtime exception raised at a point the expansion always reaches has no handler and
        // no diagnostic, so it aborts compilation. `SawDynamicLoop` is what keeps that rule off
        // the shape it does not mean: a lookup that probes a table and raises when the search
        // runs out reaches its raise only for data the compiler cannot see.
        //
        // `handlerCodeStack` is the other exclusion, for the same reason: a `raise` inside an
        // `except` body is a re-raise to the NEXT handler up, and it is reached only when the
        // try body actually raised -- a run-time condition the compiler does not decide.
        // Lowering it as an abort made `try: x = s.index(v) except ValueError: raise
        // ValueError(...)` fail the build on a string the try always resolves
        // (adafruit_pixelbuf.parse_byteorder).
        if (!string.IsNullOrEmpty(stmt.ErrorType) && inlineStack.Count > 0 &&
            tryCatchStack.Count == 0 && handlerCodeStack.Count == 0 &&
            !inlineStack[^1].SawDynamicLoop &&
            _runtimeBranchDepth <= inlineStack[^1].EntryBranchDepth)
        {
            string reason = resolvedMessage.Length > 0 ? resolvedMessage : stmt.ErrorType;
            int line = currentStmtLine > 0 ? currentStmtLine : stmt.Line;

            // Deliberately unlocated, and deliberately NOT `stmt`.
            //
            // This fires while an @inline callee is being expanded, so `stmt` is the callee's
            // `raise` and `currentStmtLine` is the CALLER's call site. The call site is the
            // right place: the reader wrote `probe(3)` and that is the line they can change;
            // the `raise` inside the library is not theirs to edit. Measured with the callee in
            // another module, it reports the caller's file and line.
            //
            // Passing `stmt` would move the report onto the callee's raise and take its line
            // while the file comes from whichever module is being lowered -- the two halves of
            // a location arriving from different places, which is the failure this issue
            // exists to remove. So the node is in hand and is not passed, on purpose. Pinned by
            // test_a_raise_inside_an_inlined_callee_reports_the_call_site.
            //
            // The FILE is named, and the choice is declared. Leaving it null used to mean the
            // entry file by omission, and that is the caller only when the caller happens to be
            // the entry file: with the call written in a middle module, `Servo("PZ9")` on
            // mid.py:5 was reported as main.py:5, a line that mentions no pin in a file that
            // contains none. `LocationIsFinal` is what keeps the stamp in `Generate` off it,
            // since the exception class cannot tell this raise from a compiler-generated one.
            // Issue #230.
            throw new ArchitectureError($"{stmt.ErrorType}: {reason}", line, 0)
                { File = CallSiteSourcePath(), LocationIsFinal = true };
        }

        // A bare `raise` (no type) re-raises the exception currently being handled. Re-signal from
        // the handler's saved code variable (SignalError reloads R22 from it), so it is correct even
        // if the handler clobbered R22. Outside a handler (no saved code) fall back to keeping R22.
        Val code = !string.IsNullOrEmpty(stmt.ErrorType)
            ? ResolveBinding(stmt.ErrorType)
            : handlerCodeStack.Count > 0
                ? new Variable(handlerCodeStack[^1], DataType.UINT8)
                : new Constant(0);

        // The message, alongside the code. A string literal is one store of the flash address
        // of the interned text (#369). A non-literal (f-string, concatenation, call) stores
        // each runtime piece into the exception-args record and a raise-site id; print(e)
        // dispatches on the id and replays that site's print sequence (#435).
        //
        // A bare re-raise writes nothing: the word still holds the message of the exception
        // being handled, which is the one being re-raised.
        bool dynamicStored = false;
        if (dynamicMessage != null && programRecordsRaiseMessages && !string.IsNullOrEmpty(stmt.ErrorType))
        {
            EmitDynamicRaiseMessage(dynamicMessage, stmt);
            dynamicStored = true;
        }
        else if (programHasDynamicRaiseMessage && !string.IsNullOrEmpty(stmt.ErrorType))
        {
            // A literal raise in a program that also has a deferred-print raise must clear
            // the site id, or print(e) would replay the previous raise's pieces.
            DeclareExceptionSiteVar();
            Emit(new Copy(new Constant(0), new Variable(ExceptionSiteVar, DataType.UINT8)));
        }

        if (programRecordsRaiseMessages && !string.IsNullOrEmpty(stmt.ErrorType)
            && !string.IsNullOrEmpty(resolvedMessage) && !dynamicStored)
        {
            DeclareExceptionMessageVar();
            Emit(new Copy(new FlashStrAddr(InternStringAsFlash(resolvedMessage!)),
                          new Variable(ExceptionMessageVar, DataType.UINT16)));
            sawRaiseMessageStore = true;
        }
        else if (programRecordsRaiseMessages && programReportsRaiseMessage
                 && !string.IsNullOrEmpty(stmt.ErrorType)
                 && string.IsNullOrEmpty(resolvedMessage) && dynamicMessage == null)
        {
            // A typed raise with no message must not leave the previous raise's message
            // behind: the unhandled report would print `E:<Type>: <stale>` for an
            // exception that said nothing.
            DeclareExceptionMessageVar();
            Emit(new Copy(new Constant(0), new Variable(ExceptionMessageVar, DataType.UINT16)));
        }

        // Inside a try body in the same function -> deliver to the local catch
        // dispatcher (jump, no T-flag, no return). Otherwise propagate to the caller.
        string? localCatch = tryCatchStack.Count > 0 ? tryCatchStack[^1] : null;

        // Except in the entry function, which has no caller. Propagating there emits
        // `SET; RET`, and the RET pops a return address that was never pushed: the stack
        // pointer is at the top of SRAM and execution goes wherever those bytes point.
        // avr8sharp reports a stack underflow at that instruction and nothing reaches the
        // UART, where the documented behaviour is `E:<TypeName>` and a halt (#339).
        //
        // The unhandled path was reached only by an exception RETURNING into main from a
        // callee; a raise written in main's own body, or in an @inline expansion inside it
        // -- a driver's `raise ValueError` in a method called from the top level -- took the
        // propagate form. `currentFunction` stays "main" through an expansion, so both are
        // this one test.
        //
        // The code still has to reach R22 for the handler to name the type, which is what
        // the label form of SignalError does: it loads R22 and jumps. The jump lands on the
        // next instruction, which is the call the exception runtime keys off.
        if (localCatch == null && currentFunction == "main")
        {
            string unhandled = MakeLabel();
            Emit(new SignalError(code, unhandled));
            Emit(new Label(unhandled));
            Emit(new Call("__pymcu_unhandled_exn", new List<Val>(), new NoneVal()));
            return;
        }

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
        {
            if (_seqTerminated) break;
            VisitStatement(s);
        }
        // A return/raise inside the body ended only the body's own sequence; the handlers
        // and the statements after the try stay reachable.
        _seqTerminated = false;
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
        {
            foreach (var s in stmt.ElseBody)
            {
                if (_seqTerminated) break;
                VisitStatement(s);
            }
            _seqTerminated = false;
        }
        // Pop the pending finally now: the remaining exits (happy, handlers, unmatched) emit it
        // explicitly, and a `return` inside the finally itself must not re-trigger it.
        if (pushedFinally) finallyStack.RemoveAt(finallyStack.Count - 1);
        EmitFinallyBody(stmt);
        Emit(new Jump(afterLabel));

        // RFC 0008: `try: self._font = open(...); ... except OSError:` -- a body that
        // emitted no Call and no raise cannot throw, so the handlers are dead code and
        // the dispatch they hang off unreachable. Folded here rather than lowered: an
        // `except OSError` around a compile-time open() cannot fire, exactly like the
        // `except ImportError` around a resolved import never does. Gated on romfs
        // being in play so programs that open nothing keep their emission, byte for
        // byte.
        if (romfsHandles.Count > 0 && callIndices.Count == 0
            && !currentInstructions.Skip(bodyStart).Any(i => i is SignalError))
        {
            Emit(new Label(afterLabel));
            return;
        }

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

            // `except (A, B):` arrives as the comma-joined text of its alternatives (#346).
            // One name is the ordinary case and is the single-element split, so the two
            // spellings share this code rather than one of them getting a second path.
            string[] alternatives = exnType.Split(',', StringSplitOptions.RemoveEmptyEntries);
            bool catchAll = alternatives.Length == 0
                            || alternatives.Any(a => a is "Exception" or "BaseException");
            if (!catchAll)
            {
                if (alternatives.Length == 1)
                {
                    // The single-type form keeps the instructions it has had, to the byte: one
                    // comparison and a skip. A tuple of one is the same program as a bare name
                    // and must not cost more than one.
                    Val expectedCode = ResolveExceptionCode(alternatives[0], stmt);
                    Val matchTemp = MakeTemp(DataType.UINT8);
                    Emit(new Binary(PyMCU.IR.BinaryOp.Equal, exnCode, expectedCode, matchTemp));
                    Emit(new JumpIfZero(matchTemp, skipLabel));
                }
                else
                {
                    // Each alternative jumps INTO the body, and only after the last one has
                    // failed does control fall through to the next handler.
                    string bodyLabel = MakeLabel();
                    foreach (string alternative in alternatives)
                    {
                        Val expectedCode = ResolveExceptionCode(alternative, stmt);
                        Val matchTemp = MakeTemp(DataType.UINT8);
                        Emit(new Binary(PyMCU.IR.BinaryOp.Equal, exnCode, expectedCode, matchTemp));
                        Emit(new JumpIfNotZero(matchTemp, bodyLabel));
                    }
                    Emit(new Jump(skipLabel));
                    Emit(new Label(bodyLabel));
                }
            }

            // The finally is pending while the handler body runs, so a `return`/`break`/`continue`
            // inside the handler runs it first. The saved code is pushed so a bare `raise` re-raises
            // the right exception. Both are popped before the explicit finally on the normal exit.
            if (pushedFinally) finallyStack.Add(stmt.Finally!);
            handlerCodeStack.Add(exnCodeVar);

            // `except X as e`: the name is in scope for this handler body and nowhere else, so
            // a read of it afterwards is an ordinary undefined name rather than a stale object
            // (#369). A nested try binds its own name over this one and restores it on the way
            // out, which is what lets two bindings be live at once.
            string? bound = stmt.BoundName(i);
            string boundKey = bound == null ? "" : QualifyExceptionBinding(bound);
            bool hadOuter = bound != null && exceptionBindings.TryGetValue(boundKey, out var outerBinding);
            var savedOuter = hadOuter ? exceptionBindings[boundKey] : default;
            if (bound != null) exceptionBindings[boundKey] = (exnCodeVar, exnType);

            foreach (var s in handlerBody)
            {
                if (_seqTerminated) break;
                VisitStatement(s);
            }
            _seqTerminated = false;

            if (bound != null)
            {
                if (hadOuter) exceptionBindings[boundKey] = savedOuter;
                else exceptionBindings.Remove(boundKey);
            }
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


    /// <summary>
    /// The code an except alternative compares against. A dotted type
    /// (`except adafruit_irremote.IRNECRepeatException:`) names the class through its module:
    /// the qualifier resolves an `import ... as` alias first, then dots mangle to underscores,
    /// which is the key the module's exception classes are scanned under. A module member that
    /// exists but is not an exception has no entry in constantVariables, so the refusal names
    /// the type rather than finding the member and comparing the code against, say, a pin.
    /// </summary>
    private Val ResolveExceptionCode(string name, ASTNode at)
    {
        if (!name.Contains('.')) return ResolveBinding(name);

        string head = name.Split('.')[0];
        string qualified = TryImportedAlias(head, out var realMod) && realMod != null
            ? realMod + name.Substring(head.Length)
            : name;
        string mangled = qualified.Replace('.', '_');
        if (constantVariables.TryGetValue(mangled, out int code)
            || constantVariables.TryGetValue(currentModulePrefix + mangled, out code))
            return new Constant(code);

        throw UserError(
            $"'{name}' is not a known exception type -- no class by that name deriving "
            + "from Exception is defined on the module it names", at);
    }

    /// The key an `except ... as` name is held under, qualified the way every other local is,
    /// so a name bound in one @inline expansion is not the name bound in another.
    private string QualifyExceptionBinding(string name) =>
        !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name
        : !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name
        : name;

    /// The binding an `except ... as` name is currently in scope under, if any. The lookup
    /// walks the same qualifications ResolveNameKey does, because the name may be read from
    /// inside an expansion nested under the handler that bound it.
    private bool TryGetExceptionBinding(string name, out (string CodeVar, string ExnType) binding)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)
            && exceptionBindings.TryGetValue(currentInlinePrefix + name, out binding)) return true;
        if (!string.IsNullOrEmpty(currentFunction)
            && exceptionBindings.TryGetValue(currentFunction + "." + name, out binding)) return true;
        return exceptionBindings.TryGetValue(name, out binding);
    }


    /// Whether an expression reads the MESSAGE of an exception bound by `except ... as`, and
    /// the word holding its flash address if so.
    ///
    /// Three spellings, one answer: the bound name itself (`print(e)`), `str(e)`, and
    /// `e.args[0]`, which is the one adafruit_dht's simpletest writes. They read the same word
    /// because there is one message and one live exception; `args` is a one-element sequence
    /// by construction, so any index but 0 is refused rather than folded to the same thing.
    internal bool TryExceptionMessage(Expression e, out Val pointer)
    {
        pointer = new Constant(0);
        string? name = e switch
        {
            VariableExpr v => v.Name,
            CallExpr { Callee: VariableExpr { Name: "str" }, Args: [VariableExpr sv] } => sv.Name,
            IndexExpr
            {
                Target: MemberAccessExpr { Object: VariableExpr av, Member: "args" },
                Index: IntegerLiteral { Value: 0 },
            } => av.Name,
            _ => null,
        };
        if (name == null || !TryGetExceptionBinding(name, out _)) return false;

        DeclareExceptionMessageVar();
        pointer = new Variable(ExceptionMessageVar, DataType.UINT16);
        return true;
    }

    /// `e.args[<not 0>]`, which would otherwise read the one message under another index.
    internal void RefuseBadArgsIndex(Expression e)
    {
        if (e is not IndexExpr
            {
                Target: MemberAccessExpr { Object: VariableExpr av, Member: "args" },
                Index: var idx,
            }) return;
        if (!TryGetExceptionBinding(av.Name, out _)) return;
        if (idx is IntegerLiteral { Value: 0 }) return;

        throw UserError(
            $"'{av.Name}.args' holds one item, the message written at the raise, so "
            + $"'{av.Name}.args[0]' is the only index it has.", e);
    }


    /// The message word is a MODULE-LEVEL global, and saying so is what keeps it alive.
    ///
    /// The store happens in the function that raises and the read in the function that
    /// handles, which are never the same function. A word the optimizer does not know is
    /// global is one whose store is dead in the only function that performs it, so the
    /// address was written and then deleted, and `print(e)` printed whatever the word held --
    /// nothing. The strings stayed in flash, unreferenced, which is what the firmware looked
    /// like: correct apart from the one instruction that mattered.
    private void DeclareExceptionMessageVar()
    {
        variableTypes[ExceptionMessageVar] = DataType.UINT16;
        mutableGlobals[ExceptionMessageVar] = DataType.UINT16;
    }

    private void DeclareExceptionSiteVar()
    {
        variableTypes[ExceptionSiteVar] = DataType.UINT8;
        mutableGlobals[ExceptionSiteVar] = DataType.UINT8;
    }

    /// <summary>
    /// Fold a raise message to a compile-time string when every piece is already known.
    /// <c>f"bad {1}"</c> is "bad 1"; a name whose value is not a string constant fails.
    /// </summary>
    private bool TryFoldRaiseMessageToString(Expression e, out string text)
    {
        text = "";
        e = RewriteStringBuilding(e);
        if (e is CallExpr { Callee: VariableExpr { Name: "str" }, Args.Count: 1 } sc)
            e = sc.Args[0];

        if (StaticStringOf(e) is { } s) { text = s; return true; }
        if (e is IntegerLiteral il) { text = il.Value.ToString(); return true; }
        if (e is BooleanLiteral bl) { text = bl.Value ? "True" : "False"; return true; }
        if (e is NoneLiteral) { text = "None"; return true; }
        if (e is VariableExpr ve)
        {
            foreach (string key in RaiseMessageNameKeys(ve.Name))
                if (constantVariables.TryGetValue(key, out int cv)) { text = cv.ToString(); return true; }
        }

        if (e is FStringExpr fs)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in fs.Parts)
            {
                if (!p.IsExpr) { sb.Append(p.Text); continue; }
                if (!string.IsNullOrEmpty(p.FormatSpec)) return false;
                if (p.Expr == null || !TryFoldRaiseMessageToString(p.Expr, out var piece)) return false;
                sb.Append(piece);
            }
            text = sb.ToString();
            return true;
        }

        if (e is BinaryExpr { Op: Frontend.BinaryOp.Add } add
            && TryFoldRaiseMessageToString(add.Left, out var left)
            && TryFoldRaiseMessageToString(add.Right, out var right))
        {
            text = left + right;
            return true;
        }

        return false;
    }

    private IEnumerable<string> RaiseMessageNameKeys(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix))
            yield return currentInlinePrefix + name;
        if (!string.IsNullOrEmpty(currentFunction))
            yield return currentFunction + "." + name;
        yield return name;
    }

    /// <summary>
    /// Store a non-literal raise message as a site id plus typed slots, so print(e) can
    /// replay the same sequence the streaming f-string printer would have written (#435).
    /// </summary>
    private void EmitDynamicRaiseMessage(Expression message, RaiseStmt at)
    {
        Expression prepared = RewriteStringBuilding(message);
        if (prepared is CallExpr { Callee: VariableExpr { Name: "str" }, Args.Count: 1 } sc)
            prepared = sc.Args[0];

        FStringExpr fs;
        if (prepared is FStringExpr f)
            fs = f;
        else
            fs = new FStringExpr(new List<FStringPart>
            {
                new() { IsExpr = true, Expr = prepared },
            }) { Line = prepared.Line };

        var pieces = new List<FStringPart>();
        void Flatten(FStringExpr fse)
        {
            foreach (var p in fse.Parts)
            {
                if (p.IsExpr && p.Expr is FStringExpr nested && string.IsNullOrEmpty(p.FormatSpec))
                    Flatten(nested);
                else
                    pieces.Add(p);
            }
        }
        Flatten(fs);

        var site = new RaiseMessageSite { Id = nextRaiseSiteId++ };
        int intUsed = 0, floatUsed = 0;

        foreach (var p in pieces)
        {
            if (!p.IsExpr)
            {
                if (p.Text.Length > 0)
                    site.Pieces.Add(new RaiseMessagePiece { Literal = p.Text });
                continue;
            }

            Expression piece = p.Expr!;
            if (string.IsNullOrEmpty(p.FormatSpec) && TryFoldRaiseMessageToString(piece, out var foldedPiece))
            {
                site.Pieces.Add(new RaiseMessagePiece { Literal = foldedPiece });
                continue;
            }

            RejectInstanceInterpolation(piece);
            Val v = VisitExpression(piece);
            DataType vt = GetValType(v);
            var rec = new RaiseMessagePiece { FormatSpec = p.FormatSpec ?? "" };

            if (vt == DataType.FLOAT || v is FloatConstant
                || (p.FormatSpec ?? "").EndsWith("f", StringComparison.Ordinal))
            {
                // A float piece, or an int under an f spec -- print converts it the same
                // way (`f"{5:.1f}"` -> "5.0"), so the slot is a float either way.
                int slot = floatUsed++;
                string name = ExceptionFloatArgVar(slot);
                variableTypes[name] = DataType.FLOAT;
                mutableGlobals[name] = DataType.FLOAT;
                Val farg = v is Constant icv ? new FloatConstant(icv.Value) : v;
                if (farg is not FloatConstant && GetValType(farg) != DataType.FLOAT)
                {
                    Temporary ftmp = MakeTemp(DataType.FLOAT);
                    Emit(new Copy(farg, ftmp));
                    farg = ftmp;
                }
                Emit(new Copy(farg, new Variable(name, DataType.FLOAT)));
                rec.FloatSlot = slot;
                rec.PrintAs = DataType.FLOAT;
            }
            else
            {
                int slot = intUsed++;
                string name = ExceptionArgVar(slot);
                variableTypes[name] = DataType.INT32;
                mutableGlobals[name] = DataType.INT32;
                Temporary widened = MakeTemp(DataType.INT32);
                Emit(new Copy(v, widened));
                Emit(new Copy(widened, new Variable(name, DataType.INT32)));
                rec.IntSlot = slot;
                rec.PrintAs = IsScalarIntType(vt) ? vt : DataType.INT32;
                rec.IsBool = IsBoolExpr(piece);
            }

            site.Pieces.Add(rec);
        }

        DeclareExceptionSiteVar();
        Emit(new Copy(new Constant(site.Id), new Variable(ExceptionSiteVar, DataType.UINT8)));
        raiseMessageSites.Add(site);
        sawRaiseMessageStore = true;
    }

    /// <summary>
    /// Replay the live exception's message: site 0 is the flash string, any other id is
    /// the print sequence recorded at that raise.
    /// </summary>
    internal void EmitExceptionMessagePrint()
    {
        Emit(new Call(ExceptionMessagePrinter, new List<Val>(), new NoneVal()));
    }

    internal Function SynthesizeExceptionMessagePrinter()
    {
        var savedInstructions = currentInstructions;
        var savedFunction = currentFunction;
        var savedModulePrefix = currentModulePrefix;
        var savedInlinePrefix = currentInlinePrefix;
        int savedInlineDepth = inlineDepth;
        var savedLoopStack = loopStack;
        var savedInlineStack = inlineStack;
        int savedLastLine = lastLine;
        var savedFunctionGlobals = currentFunctionGlobals;

        currentInstructions = new List<Instruction>();
        currentFunction = ExceptionMessagePrinter;
        currentModulePrefix = "";
        currentInlinePrefix = "";
        inlineDepth = 0;
        loopStack = new List<LoopLabels>();
        inlineStack = new List<InlineContext>();
        lastLine = -1;
        currentFunctionGlobals = new HashSet<string>();

        string writeStrFn = ResolveWriteStrFn();
        string floatFn = ResolveFloatWriteFn();
        string after = MakeLabel();
        string litPath = MakeLabel();

        DeclareExceptionMessageVar();
        var msgVar = new Variable(ExceptionMessageVar, DataType.UINT16);

        // The site dispatch exists only while some raise stores one -- a program whose
        // raises are all literals never declares __exn_site, so reading it would
        // dispatch on an unwritten word.
        if (raiseMessageSites.Count > 0)
        {
            DeclareExceptionSiteVar();
            var siteVar = new Variable(ExceptionSiteVar, DataType.UINT8);
            Emit(new JumpIfZero(siteVar, litPath));

            foreach (var site in raiseMessageSites)
            {
                string next = MakeLabel();
                Emit(new JumpIfNotEqual(siteVar, new Constant(site.Id), next));
                foreach (var piece in site.Pieces)
                    EmitRaiseMessagePiece(piece, writeStrFn, floatFn);
                Emit(new Jump(after));
                Emit(new Label(next));
            }

            Emit(new Jump(after));
        }

        Emit(new Label(litPath));
        // A raise that carried no message leaves the word at zero; printing through it
        // would stream flash from address 0, so a cleared message prints nothing.
        Emit(new JumpIfZero(msgVar, after));
        Emit(new Call(ResolveRuntimeWriteStrFn(),
            new List<Val> { msgVar },
            new NoneVal()));
        Emit(new Label(after));
        Emit(new Return(new NoneVal()));

        var fn = new Function
        {
            Name = ExceptionMessagePrinter,
            ReturnType = DataType.VOID,
            Body = new List<Instruction>(currentInstructions),
            CanFail = false,
        };

        currentInstructions = savedInstructions;
        currentFunction = savedFunction;
        currentModulePrefix = savedModulePrefix;
        currentInlinePrefix = savedInlinePrefix;
        inlineDepth = savedInlineDepth;
        loopStack = savedLoopStack;
        inlineStack = savedInlineStack;
        lastLine = savedLastLine;
        currentFunctionGlobals = savedFunctionGlobals;
        return fn;
    }

    /// <summary>
    /// The tail the unhandled-exception runtime calls after printing `E:<Type>`:
    /// ": " and the recorded message when one exists, then the CRLF the type table
    /// leaves off when this function is linked. Reached from raw asm only.
    /// </summary>
    internal Function SynthesizeExceptionMessageTail()
    {
        var savedInstructions = currentInstructions;
        var savedFunction = currentFunction;
        var savedModulePrefix = currentModulePrefix;
        var savedInlinePrefix = currentInlinePrefix;
        int savedInlineDepth = inlineDepth;
        var savedLoopStack = loopStack;
        var savedInlineStack = inlineStack;
        int savedLastLine = lastLine;
        var savedFunctionGlobals = currentFunctionGlobals;

        currentInstructions = new List<Instruction>();
        currentFunction = ExceptionMessageTail;
        currentModulePrefix = "";
        currentInlinePrefix = "";
        inlineDepth = 0;
        loopStack = new List<LoopLabels>();
        inlineStack = new List<InlineContext>();
        lastLine = -1;
        currentFunctionGlobals = new HashSet<string>();

        string writeStrFn = ResolveRuntimeWriteStrFn();
        string printMsg = MakeLabel();
        string done = MakeLabel();

        DeclareExceptionMessageVar();
        var msgVar = new Variable(ExceptionMessageVar, DataType.UINT16);

        // A deferred-print raise marks the site id; a literal one marks the word.
        // Either means "there is a message" and earns the ": " ahead of it.
        if (raiseMessageSites.Count > 0)
        {
            DeclareExceptionSiteVar();
            Emit(new JumpIfNotZero(new Variable(ExceptionSiteVar, DataType.UINT8), printMsg));
        }
        Emit(new JumpIfZero(msgVar, done));
        Emit(new Label(printMsg));
        Emit(new Call(writeStrFn,
            new List<Val> { new FlashStrAddr(InternStringAsFlash(": ")) },
            new NoneVal()));
        Emit(new Call(ExceptionMessagePrinter, new List<Val>(), new NoneVal()));
        Emit(new Label(done));
        Emit(new Call(writeStrFn,
            new List<Val> { new FlashStrAddr(InternStringAsFlash("\r\n")) },
            new NoneVal()));
        Emit(new Return(new NoneVal()));

        var fn = new Function
        {
            Name = ExceptionMessageTail,
            ReturnType = DataType.VOID,
            Body = new List<Instruction>(currentInstructions),
            CanFail = false,
        };

        currentInstructions = savedInstructions;
        currentFunction = savedFunction;
        currentModulePrefix = savedModulePrefix;
        currentInlinePrefix = savedInlinePrefix;
        inlineDepth = savedInlineDepth;
        loopStack = savedLoopStack;
        inlineStack = savedInlineStack;
        lastLine = savedLastLine;
        currentFunctionGlobals = savedFunctionGlobals;
        return fn;
    }

    private void EmitRaiseMessagePiece(RaiseMessagePiece piece, string writeStrFn, string floatFn)
    {
        if (piece.Literal != null)
        {
            EmitStreamStr(writeStrFn, piece.Literal);
            return;
        }

        if (piece.FloatSlot >= 0)
        {
            var fvar = new Variable(ExceptionFloatArgVar(piece.FloatSlot), DataType.FLOAT);
            if (!string.IsNullOrEmpty(piece.FormatSpec))
            {
                // Same call print() emits for f"{v:W.Nf}" -- the spec was captured at the
                // raise, so the replayed text matches what print would have written.
                var (fwidth, prec, fpad) = ParseFloatFormatSpec(piece.FormatSpec);
                Emit(new Call(ResolveFloatFmtFn(), new List<Val>
                {
                    fvar,
                    new Constant(prec),
                    new Constant(fwidth),
                    new Constant(fpad == '0' ? 1 : 0),
                }, MakeTemp(DataType.UINT8)));
                return;
            }
            EmitStreamVal(floatFn, fvar);
            return;
        }

        var stored = new Variable(ExceptionArgVar(piece.IntSlot), DataType.INT32);
        if (piece.IsBool)
        {
            EmitStreamBool(writeStrFn, new VariableExpr(ExceptionArgVar(piece.IntSlot)));
            return;
        }

        if (!string.IsNullOrEmpty(piece.FormatSpec))
        {
            var (width, radix, pad, upper) = ParseFormatSpec(piece.FormatSpec);
            bool signed = piece.PrintAs is DataType.INT8 or DataType.INT16 or DataType.INT32;
            int flags = (upper ? 0x01 : 0) | (signed ? 0x02 : 0) | (pad == '0' ? 0x04 : 0);
            Temporary valArg = MakeTemp(DataType.INT32);
            Emit(new Copy(stored, valArg));
            // A bare :x/:X (no field width, no zero pad) takes the minimal hex writer;
            // the generic formatter stays for everything with padding or another radix.
            if (radix == 16 && width == 0 && pad != '0')
            {
                string? hexFn = ResolveHexFn();
                if (hexFn != null)
                {
                    Emit(new Call(hexFn, new List<Val> { valArg, new Constant(flags) },
                        MakeTemp(DataType.UINT8)));
                    return;
                }
            }
            Emit(new Call(ResolveFmtFn(), new List<Val>
            {
                valArg,
                new Constant(radix),
                new Constant(width),
                new Constant(flags),
            }, MakeTemp(DataType.UINT8)));
            return;
        }

        EmitStreamVal(floatFn, stored, piece.PrintAs);
    }

    private void EmitFinallyBody(TryStmt stmt)
    {
        if (stmt.Finally == null) return;
        // Emitted once per exit path, so a return inside one emission must not bleed into
        // the lowering of the next.
        foreach (var s in stmt.Finally)
        {
            if (_seqTerminated) break;
            VisitStatement(s);
        }
        _seqTerminated = false;
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
        {
            // Every pending finally must emit even when an inner one ends in `return`:
            // the terminated flag is per-sequence state, and each finally is its own.
            _seqTerminated = false;
            foreach (var s in slice[k])
            {
                if (_seqTerminated) break;
                VisitStatement(s);
            }
        }
        _seqTerminated = false;
        finallyStack = saved;
    }
}