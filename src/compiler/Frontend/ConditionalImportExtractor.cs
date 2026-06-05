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

using PyMCU.Common.Models;

namespace PyMCU.Frontend;

// Pure read-only helper used by DependencyGraphBuilder to discover ImportStmt
// nodes that are hidden inside compile-time if/match blocks in GlobalStatements.
// This runs BEFORE ConditionalCompilator mutates the AST, so it must never
// modify the tree — only read it.
//
// Example pattern it resolves:
//
//   if __CHIP__.name == "atmega328p":
//       from pymcu.hal.avr.gpio.atmega328p import _PinRegs
//   elif __CHIP__.name == "attiny85":
//       from pymcu.hal.avr.gpio.attiny_b import select_port
//
// For the target chip the winning branch's imports are returned so the
// dependency graph only loads the relevant implementation module.
internal static class ConditionalImportExtractor
{
    // Walk GlobalStatements (top-level only — not inside function bodies) and
    // yield ImportStmt nodes reachable through compile-time-evaluable if/match
    // blocks. Branches that cannot be evaluated are silently skipped so the
    // caller continues without error.
    public static IEnumerable<ImportStmt> Extract(ProgramNode program, DeviceConfig config)
    {
        var eval = new CompileTimeEvaluator(config);
        foreach (var stmt in program.GlobalStatements)
        foreach (var imp in ExtractFromStatement(stmt, eval))
            yield return imp;
    }

    private static IEnumerable<ImportStmt> ExtractFromStatement(Statement stmt, CompileTimeEvaluator eval)
    {
        switch (stmt)
        {
            case ImportStmt imp:
                yield return imp;
                break;

            case IfStmt ifStmt:
            {
                Statement? winning = null;
                try { winning = ChooseBranch(ifStmt, eval); }
                catch { /* not compile-time — skip entire if chain */ }

                if (winning == null) yield break;
                foreach (var imp in ExtractFromBlock(winning, eval))
                    yield return imp;
                break;
            }

            case MatchStmt matchStmt:
            {
                string? targetVal = null;
                try { targetVal = eval.Resolve(matchStmt.Target); }
                catch { /* not compile-time — skip */ }

                if (targetVal == null) yield break;

                foreach (var branch in matchStmt.Branches)
                {
                    bool matches;
                    try { matches = eval.MatchesPattern(branch.Pattern, targetVal); }
                    catch { continue; }

                    if (!matches) continue;

                    foreach (var imp in ExtractFromBlock(branch.Body, eval))
                        yield return imp;
                    yield break; // first matching case only
                }

                break;
            }
        }
    }

    private static IEnumerable<ImportStmt> ExtractFromBlock(Statement? body, CompileTimeEvaluator eval)
    {
        if (body is not Block block) yield break;
        foreach (var inner in block.Statements)
        foreach (var imp in ExtractFromStatement(inner, eval))
            yield return imp;
    }

    private static Statement? ChooseBranch(IfStmt ifStmt, CompileTimeEvaluator eval)
    {
        if (eval.EvaluateCondition(ifStmt.Condition)) return ifStmt.ThenBranch;
        foreach (var (cond, body) in ifStmt.ElifBranches)
            if (eval.EvaluateCondition(cond)) return body;
        return ifStmt.ElseBranch;
    }
}
