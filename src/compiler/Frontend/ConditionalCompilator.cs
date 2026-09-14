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

    // Current module name — set by ConditionalCompilationProcessor before Process().
    public string ModuleName
    {
        get => _evaluator.ModuleName;
        set => _evaluator.ModuleName = value;
    }

    // Set while walking a function or class body, so an import promoted out of one is
    // marked as what it is. See ImportStmt.InFunctionScope.
    private bool _inFunctionBody;

    public void Process(ProgramNode program)
    {
        var newGlobals = new List<Statement>();
        _inFunctionBody = false;

        foreach (var stmt in program.GlobalStatements)
        {
            if (!ProcessStatement(stmt, program, newGlobals))
                newGlobals.Add(stmt);
        }

        program.GlobalStatements.Clear();
        program.GlobalStatements.AddRange(newGlobals);

        foreach (var stmt in program.GlobalStatements)
        {
            _inFunctionBody = false;
            ProcessBlock(stmt, program);
        }

        foreach (var func in program.Functions)
        {
            _inFunctionBody = true;
            ProcessBlock(func.Body, program);
        }
        _inFunctionBody = false;
    }

    // Moves all statements in a Block into newStmts, routing ImportStmt to prog.Imports.
    // Recursively applies static evaluation so nested compile-time blocks
    // (e.g. `match __CHIP__.arch:` inside a `case _:` body) are also eliminated.
    private void FlushBlock(Statement? body, ProgramNode prog, List<Statement> newStmts)
    {
        if (body is not Block block) return;

        foreach (var inner in block.Statements)
        {
            if (inner is ImportStmt imp)
                prog.Imports.Add(CloneImport(imp));
            else if (!ProcessStatement(inner, prog, newStmts))
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
                    _inFunctionBody = true;
                    stmt = func.Body;
                    continue;
                case ClassDef cls:
                    _inFunctionBody = true;
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

    /// <summary>
    /// The statements a try holding an optional import reduces to, or null when it holds none
    /// and must keep its own lowering (#351).
    ///
    /// The try body when every optional import in it resolved, the first handler's body when
    /// any did not. `else` is folded in with the body, as Python runs it when nothing raised;
    /// a `finally` runs either way, so it follows the chosen branch.
    /// </summary>
    private static List<Statement>? FoldedBranchOf(TryStmt tryStmt)
    {
        var optional = tryStmt.Body.OfType<ImportStmt>().Where(i => i.IsOptional).ToList();
        if (optional.Count == 0) return null;

        var chosen = new List<Statement>();
        if (optional.Any(i => i.OptionalLoadFailed))
        {
            if (tryStmt.Handlers.Count > 0) chosen.AddRange(tryStmt.Handlers[0].Handler);
        }
        else
        {
            chosen.AddRange(tryStmt.Body);
            if (tryStmt.ElseBody != null) chosen.AddRange(tryStmt.ElseBody);
        }
        if (tryStmt.Finally != null) chosen.AddRange(tryStmt.Finally);
        return chosen;
    }

    private ImportStmt CloneImport(ImportStmt src) =>
        new(src.ModuleName, [..src.Symbols], src.RelativeLevel)
        {
            InFunctionScope = src.InFunctionScope || _inFunctionBody,
            ModuleAlias = src.ModuleAlias,
            Aliases = new Dictionary<string, string>(src.Aliases),
            WasStarImport = src.WasStarImport,
            IsOptional = src.IsOptional,
            Line = src.Line,
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
            // A `try` holding an optional import is a COMPILE-TIME branch, like `if __CHIP__`
            // above it (#351). Whether the module is there is decided by the loader and by
            // nothing at run time, so one of the two branches is dead and folding it away is
            // what keeps the program honest: the try body's `_USE_PULSEIO = True` used to be
            // emitted whether or not pulseio existed, because there is no run-time ImportError
            // for the handler to catch. The flag then said True on a chip with no pulseio, and
            // the program took a branch that cannot work.
            //
            // Untouched for every other try: a try with no optional import in it is ordinary
            // control flow and keeps its own lowering.
            case TryStmt tryStmt when FoldedBranchOf(tryStmt) is { } chosen:
                foreach (var inner in chosen)
                {
                    if (inner is ImportStmt nested) prog.Imports.Add(CloneImport(nested));
                    else if (!ProcessStatement(inner, prog, newStmts)) newStmts.Add(inner);
                }
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