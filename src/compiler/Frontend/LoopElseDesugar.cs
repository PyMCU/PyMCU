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

namespace PyMCU.Frontend;

/// <summary>
/// The `else` clause of a `for` or `while` loop runs only when the loop finished WITHOUT
/// executing a break. This lowers it the way Python itself describes: a flag set before the
/// loop, cleared by every break that belongs to that loop, tested after it.
///
/// The rewrite uses node kinds that already exist, so every pass after the front end (scan,
/// conditional compilation, type inference, IR generation) sees plain statements it already
/// handles and needs no knowledge of loop-else at all.
///
/// Shared by both front ends -- the hand-written parser and the CPython-AST reader -- because
/// the two are required to produce byte-identical firmware, which they can only do if they
/// desugar identically, flag name included. The name is derived from the loop's line rather
/// than from a counter so it does not depend on the order in which each front end happens to
/// walk the file.
/// </summary>
public static class LoopElseDesugar
{
    /// <summary>
    /// The loop, wrapped with its else clause. <paramref name="body"/> is the loop's own body
    /// (the breaks to tag live in it) and <paramref name="elseBlock"/> its else clause; a null
    /// else clause returns the loop untouched.
    /// </summary>
    public static Statement Attach(Statement loop, Statement body, Block? elseBlock, int line)
    {
        if (elseBlock == null) return loop;

        // No break in the body: nothing can skip the else clause, so it always runs. Emitting
        // it straight after the loop costs neither the flag nor the test.
        string flag = $"__loopelse{line}";
        if (!TagBreaks(body, flag))
        {
            var flat = new Block { Line = line };
            flat.Statements.Add(loop);
            flat.Statements.AddRange(elseBlock.Statements);
            return flat;
        }

        var wrapped = new Block { Line = line };
        wrapped.Statements.Add(
            new AssignStmt(new VariableExpr(flag) { Line = line }, new IntegerLiteral(1) { Line = line })
                { Line = line });
        wrapped.Statements.Add(loop);
        wrapped.Statements.Add(new IfStmt(
            new BinaryExpr(new VariableExpr(flag) { Line = line }, BinaryOp.Equal,
                           new IntegerLiteral(1) { Line = line }) { Line = line },
            elseBlock) { Line = line });
        return wrapped;
    }

    /// <summary>
    /// Marks every break that exits the loop owning <paramref name="flag"/>, and reports whether
    /// there was one. A break inside a nested for/while belongs to THAT loop and must not clear
    /// this flag, so the walk stops at one. The container set mirrors
    /// IRGenerator.LoopBodyHasBreakOrContinue, which answers the same question about the same
    /// bodies.
    /// </summary>
    private static bool TagBreaks(Statement? s, string flag)
    {
        switch (s)
        {
            case null: return false;
            case BreakStmt b: b.LoopElseFlag = flag; return true;
            case ForStmt:
            case WhileStmt: return false;
            case Block blk:
            {
                bool any = false;
                foreach (var st in blk.Statements) any |= TagBreaks(st, flag);
                return any;
            }
            case IfStmt i:
            {
                bool any = TagBreaks(i.ThenBranch, flag);
                foreach (var (_, eb) in i.ElifBranches) any |= TagBreaks(eb, flag);
                any |= TagBreaks(i.ElseBranch, flag);
                return any;
            }
            case MatchStmt m:
            {
                bool any = false;
                foreach (var br in m.Branches) any |= TagBreaks(br.Body, flag);
                return any;
            }
            case WithStmt w: return TagBreaks(w.Body, flag);
            case TryStmt t:
            {
                bool any = false;
                foreach (var st in t.Body) any |= TagBreaks(st, flag);
                foreach (var (_, h) in t.Handlers)
                    foreach (var st in h) any |= TagBreaks(st, flag);
                if (t.ElseBody != null)
                    foreach (var st in t.ElseBody) any |= TagBreaks(st, flag);
                if (t.Finally != null)
                    foreach (var st in t.Finally) any |= TagBreaks(st, flag);
                return any;
            }
            default: return false;
        }
    }
}
