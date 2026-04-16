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

// Walks the AST and eliminates compile-time if/match blocks, promoting the
// selected branch into the surrounding statement list.
// Condition and pattern evaluation is delegated to CompileTimeEvaluator.
public class ConditionalCompilator(DeviceConfig config)
{
    private readonly CompileTimeEvaluator _evaluator = new(config);

    public void Process(ProgramNode program)
    {
        var newGlobals = new List<Statement>();

        foreach (var stmt in program.GlobalStatements)
        {
            if (!ProcessStatement(stmt, program, newGlobals))
            {
                newGlobals.Add(stmt);
            }
        }

        program.GlobalStatements.Clear();
        program.GlobalStatements.AddRange(newGlobals);

        foreach (var stmt in program.GlobalStatements)
        {
            ProcessBlock(stmt, program);
        }

        foreach (var func in program.Functions)
        {
            ProcessBlock(func.Body, program);
        }
    }

    // Moves all statements in a Block into newStmts, routing ImportStmt to prog.Imports.
    private static void FlushBlock(Statement? body, ProgramNode prog, List<Statement> newStmts)
    {
        if (body is not Block block) return;

        foreach (var inner in block.Statements)
        {
            if (inner is ImportStmt imp)
                prog.Imports.Add(CloneImport(imp));
            else
                newStmts.Add(inner);
        }
    }

    private void ProcessBlock(Statement? stmt, ProgramNode prog)
    {
        while (true)
        {
            switch (stmt)
            {
                case null:
                    return;
                case Block block:
                {
                    var newStmts = new List<Statement>();
                    foreach (var s in block.Statements)
                    {
                        if (!ProcessStatement(s, prog, newStmts))
                            newStmts.Add(s);
                    }

                    block.Statements.Clear();
                    block.Statements.AddRange(newStmts);

                    foreach (var s in block.Statements) ProcessBlock(s, prog);
                    break;
                }
                case FunctionDef func:
                    stmt = func.Body;
                    continue;
                case ClassDef cls:
                    stmt = cls.Body;
                    continue;
                case WhileStmt loop:
                    stmt = loop.Body;
                    continue;
                case ForStmt forLoop:
                    stmt = forLoop.Body;
                    continue;
                case IfStmt ifStmt:
                {
                    ProcessBlock(ifStmt.ThenBranch, prog);
                    foreach (var branch in ifStmt.ElifBranches) ProcessBlock(branch.Body, prog);
                    if (ifStmt.ElseBranch != null)
                    {
                        stmt = ifStmt.ElseBranch;
                        continue;
                    }

                    break;
                }
                case MatchStmt matchStmt:
                {
                    foreach (var branch in matchStmt.Branches) ProcessBlock(branch.Body, prog);
                    break;
                }
            }

            break;
        }
    }

    private static ImportStmt CloneImport(ImportStmt src) =>
        new(src.ModuleName, [..src.Symbols], src.RelativeLevel)
        {
            ModuleAlias = src.ModuleAlias,
            Aliases = new Dictionary<string, string>(src.Aliases),
        };

    // Returns the winning branch body for a compile-time if/elif/else.
    // Throws if any condition is not evaluable at compile time.
    private Statement? ChooseBranch(IfStmt ifStmt)
    {
        if (_evaluator.EvaluateCondition(ifStmt.Condition)) return ifStmt.ThenBranch;

        foreach (var (cond, body) in ifStmt.ElifBranches)
        {
            if (_evaluator.EvaluateCondition(cond)) return body;
        }

        return ifStmt.ElseBranch;
    }

    private bool ProcessStatement(Statement stmt, ProgramNode prog, List<Statement> newStmts)
    {
        switch (stmt)
        {
            case ImportStmt imp:
                prog.Imports.Add(CloneImport(imp));
                return true;
            case IfStmt ifStmt:
                try
                {
                    FlushBlock(ChooseBranch(ifStmt), prog, newStmts);
                    return true;
                }
                catch
                {
                    return false;
                }
            case MatchStmt matchStmt:
                try
                {
                    var targetVal = _evaluator.Resolve(matchStmt.Target);

                    foreach (var branch in matchStmt.Branches.Where(branch =>
                                 _evaluator.MatchesPattern(branch.Pattern, targetVal)))
                    {
                        FlushBlock(branch.Body, prog, newStmts);
                        return true;
                    }

                    return true; // No case matched — eliminate the match
                }
                catch
                {
                    return false;
                }
            default:
                return false;
        }
    }
}