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

public class ConditionalCompilator(DeviceConfig config)
{
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
        if (stmt == null) return;

        if (stmt is Block block)
        {
            var newStmts = new List<Statement>();
            foreach (var s in block.Statements)
            {
                if (!ProcessStatement(s, prog, newStmts))
                {
                    newStmts.Add(s);
                }
            }

            block.Statements.Clear();
            block.Statements.AddRange(newStmts);

            foreach (var s in block.Statements)
            {
                ProcessBlock(s, prog);
            }
        }
        else if (stmt is FunctionDef func)
        {
            ProcessBlock(func.Body, prog);
        }
        else if (stmt is ClassDef cls)
        {
            ProcessBlock(cls.Body, prog);
        }
        else if (stmt is IfStmt ifStmt)
        {
            ProcessBlock(ifStmt.ThenBranch, prog);
            foreach (var branch in ifStmt.ElifBranches)
            {
                ProcessBlock(branch.Body, prog);
            }

            if (ifStmt.ElseBranch != null)
            {
                ProcessBlock(ifStmt.ElseBranch, prog);
            }
        }
        else if (stmt is MatchStmt matchStmt)
        {
            foreach (var branch in matchStmt.Branches)
            {
                ProcessBlock(branch.Body, prog);
            }
        }
        else if (stmt is WhileStmt loop)
        {
            ProcessBlock(loop.Body, prog);
        }
        else if (stmt is ForStmt forLoop)
        {
            ProcessBlock(forLoop.Body, prog);
        }
    }

    private static ImportStmt CloneImport(ImportStmt src) =>
        new(src.ModuleName, new List<string>(src.Symbols), src.RelativeLevel)
        {
            ModuleAlias = src.ModuleAlias,
            Aliases = new Dictionary<string, string>(src.Aliases),
        };

    // Resolves a compile-time config expression to its string value.
    // Throws if the expression is not a known compile-time constant.
    private string ResolveConfigValue(Expression e)
    {
        if (e is VariableExpr varExpr)
        {
            if (varExpr.Name == "__CHIP__") return config.Chip;
            if (varExpr.Name == "__FREQ__" || varExpr.Name == "F_CPU") return config.Frequency.ToString();
            throw new Exception("Unknown var");
        }

        if (e is MemberAccessExpr memExpr && memExpr.Object is VariableExpr objVar && objVar.Name == "__CHIP__")
        {
            if (memExpr.Member == "arch") return config.Arch;
            if (memExpr.Member == "chip" || memExpr.Member == "name") return config.Chip;
        }

        if (e is StringLiteral str) return str.Value;
        if (e is IntegerLiteral intLit) return intLit.Value.ToString();

        throw new Exception("Not a constant");
    }

    // Returns the winning branch body for a compile-time if/elif/else, or null if no branch applies.
    // Throws if any condition cannot be evaluated at compile time.
    private Statement? ChooseBranch(IfStmt ifStmt)
    {
        if (EvaluateCondition(ifStmt.Condition)) return ifStmt.ThenBranch;

        foreach (var (cond, body) in ifStmt.ElifBranches)
        {
            if (EvaluateCondition(cond)) return body;
        }

        return ifStmt.ElseBranch;
    }

    private bool ProcessStatement(Statement stmt, ProgramNode prog, List<Statement> newStmts)
    {
        if (stmt is ImportStmt imp)
        {
            prog.Imports.Add(CloneImport(imp));
            return true;
        }

        if (stmt is IfStmt ifStmt)
        {
            try
            {
                FlushBlock(ChooseBranch(ifStmt), prog, newStmts);
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (stmt is MatchStmt matchStmt)
        {
            try
            {
                string targetVal = ResolveConfigValue(matchStmt.Target);

                foreach (var branch in matchStmt.Branches)
                {
                    if (branch.Pattern == null)
                    {
                        // Wildcard
                        FlushBlock(branch.Body, prog, newStmts);
                        return true;
                    }

                    if (branch.Pattern is IntegerLiteral intLit && intLit.Value.ToString() == targetVal)
                    {
                        FlushBlock(branch.Body, prog, newStmts);
                        return true;
                    }

                    if (branch.Pattern is StringLiteral strLit && strLit.Value == targetVal)
                    {
                        FlushBlock(branch.Body, prog, newStmts);
                        return true;
                    }

                    if (branch.Pattern is BinaryExpr binExpr)
                    {
                        var alts = new List<string>();

                        void Flatten(Expression e)
                        {
                            if (e is BinaryExpr b && b.Op == BinaryOp.BitOr)
                            {
                                Flatten(b.Left);
                                Flatten(b.Right);
                                return;
                            }

                            if (e is StringLiteral s) alts.Add(s.Value);
                            else if (e is IntegerLiteral il) alts.Add(il.Value.ToString());
                        }

                        Flatten(binExpr);

                        bool anyAlt = false;
                        foreach (var alt in alts)
                        {
                            if (alt == targetVal) { anyAlt = true; break; }
                        }

                        if (anyAlt)
                        {
                            FlushBlock(branch.Body, prog, newStmts);
                            return true;
                        }
                    }
                }

                return true; // No case matched, eliminate match
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private bool EvaluateCondition(Expression? expr)
    {
        if (expr == null) return false;

        if (expr is BinaryExpr bin)
        {
            if (bin.Op == BinaryOp.Or)  return EvaluateCondition(bin.Left) || EvaluateCondition(bin.Right);
            if (bin.Op == BinaryOp.And) return EvaluateCondition(bin.Left) && EvaluateCondition(bin.Right);

            if (bin.Op == BinaryOp.Equal || bin.Op == BinaryOp.NotEqual)
            {
                string left  = ResolveConfigValue(bin.Left);
                string right = ResolveConfigValue(bin.Right);
                return bin.Op == BinaryOp.Equal ? left == right : left != right;
            }
        }

        if (expr is CallExpr call && call.Callee is MemberAccessExpr mem && mem.Member == "startswith"
            && call.Args.Count == 1 && call.Args[0] is StringLiteral argStr)
        {
            return ResolveConfigValue(mem.Object).StartsWith(argStr.Value);
        }

        throw new Exception("Unsupported condition");
    }
}