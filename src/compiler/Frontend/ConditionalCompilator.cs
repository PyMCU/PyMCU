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

using PyMCU.Common;

namespace PyMCU.Frontend;

public class ConditionalCompilator
{
    private readonly DeviceConfig config;

    public ConditionalCompilator(DeviceConfig config)
    {
        this.config = config;
    }

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
            if (func != null)
            {
                ProcessBlock(func.Body, program);
            }
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

    private bool ProcessStatement(Statement stmt, ProgramNode prog, List<Statement> newStmts)
    {
        if (stmt is ImportStmt imp)
        {
            prog.Imports.Add(new ImportStmt(imp.ModuleName, new List<string>(imp.Symbols), imp.RelativeLevel));
            return true; // Stripped
        }

        if (stmt is IfStmt ifStmt)
        {
            bool? conditionResult = null;

            try
            {
                if (EvaluateCondition(ifStmt.Condition))
                {
                    conditionResult = true;
                }
                else
                {
                    bool elifTaken = false;
                    foreach (var branch in ifStmt.ElifBranches)
                    {
                        if (EvaluateCondition(branch.Condition))
                        {
                            if (branch.Body is Block block)
                            {
                                foreach (var inner in block.Statements)
                                {
                                    if (inner is ImportStmt innerImp)
                                    {
                                        prog.Imports.Add(new ImportStmt(innerImp.ModuleName,
                                            new List<string>(innerImp.Symbols), innerImp.RelativeLevel));
                                    }
                                    else
                                    {
                                        newStmts.Add(inner);
                                    }
                                }
                            }

                            elifTaken = true;
                            break;
                        }
                    }

                    if (!elifTaken)
                    {
                        if (ifStmt.ElseBranch is Block block)
                        {
                            foreach (var inner in block.Statements)
                            {
                                if (inner is ImportStmt innerImp)
                                {
                                    prog.Imports.Add(new ImportStmt(innerImp.ModuleName,
                                        new List<string>(innerImp.Symbols), innerImp.RelativeLevel));
                                }
                                else
                                {
                                    newStmts.Add(inner);
                                }
                            }
                        }
                    }

                    return true;
                }
            }
            catch
            {
                // Not compile time
                return false;
            }

            if (conditionResult.HasValue && conditionResult.Value)
            {
                if (ifStmt.ThenBranch is Block block)
                {
                    foreach (var inner in block.Statements)
                    {
                        if (inner is ImportStmt innerImp)
                        {
                            prog.Imports.Add(new ImportStmt(innerImp.ModuleName, new List<string>(innerImp.Symbols),
                                innerImp.RelativeLevel));
                        }
                        else
                        {
                            newStmts.Add(inner);
                        }
                    }
                }

                return true;
            }
        }

        if (stmt is MatchStmt matchStmt)
        {
            try
            {
                string targetVal = EvaluateMatchTarget(matchStmt.Target);

                foreach (var branch in matchStmt.Branches)
                {
                    if (branch.Pattern == null)
                    {
                        // Wildcard
                        if (branch.Body is Block block)
                        {
                            foreach (var inner in block.Statements)
                            {
                                if (inner is ImportStmt innerImp)
                                {
                                    prog.Imports.Add(new ImportStmt(innerImp.ModuleName,
                                        new List<string>(innerImp.Symbols), innerImp.RelativeLevel));
                                }
                                else
                                {
                                    newStmts.Add(inner);
                                }
                            }
                        }

                        return true;
                    }

                    if (branch.Pattern is IntegerLiteral intLit)
                    {
                        if (intLit.Value.ToString() == targetVal)
                        {
                            if (branch.Body is Block block)
                            {
                                foreach (var inner in block.Statements)
                                {
                                    if (inner is ImportStmt innerImp)
                                    {
                                        prog.Imports.Add(new ImportStmt(innerImp.ModuleName,
                                            new List<string>(innerImp.Symbols), innerImp.RelativeLevel));
                                    }
                                    else
                                    {
                                        newStmts.Add(inner);
                                    }
                                }
                            }

                            return true;
                        }
                    }

                    if (branch.Pattern is StringLiteral strLit)
                    {
                        if (strLit.Value == targetVal)
                        {
                            if (branch.Body is Block block)
                            {
                                foreach (var inner in block.Statements)
                                {
                                    if (inner is ImportStmt innerImp)
                                    {
                                        prog.Imports.Add(new ImportStmt(innerImp.ModuleName,
                                            new List<string>(innerImp.Symbols), innerImp.RelativeLevel));
                                    }
                                    else
                                    {
                                        newStmts.Add(inner);
                                    }
                                }
                            }

                            return true;
                        }
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
                            if (alt == targetVal)
                            {
                                anyAlt = true;
                                break;
                            }
                        }

                        if (anyAlt)
                        {
                            if (branch.Body is Block block)
                            {
                                foreach (var inner in block.Statements)
                                {
                                    if (inner is ImportStmt innerImp)
                                    {
                                        prog.Imports.Add(new ImportStmt(innerImp.ModuleName,
                                            new List<string>(innerImp.Symbols), innerImp.RelativeLevel));
                                    }
                                    else
                                    {
                                        newStmts.Add(inner);
                                    }
                                }
                            }

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
            if (bin.Op == BinaryOp.Or)
            {
                return EvaluateCondition(bin.Left) || EvaluateCondition(bin.Right);
            }

            if (bin.Op == BinaryOp.And)
            {
                return EvaluateCondition(bin.Left) && EvaluateCondition(bin.Right);
            }

            if (bin.Op == BinaryOp.Equal || bin.Op == BinaryOp.NotEqual)
            {
                string GetVal(Expression e)
                {
                    if (e is VariableExpr varExpr)
                    {
                        if (varExpr.Name == "__CHIP__") return config.Chip;
                        if (varExpr.Name == "__FREQ__" || varExpr.Name == "F_CPU") return config.Frequency.ToString();
                        throw new Exception("Unknown var");
                    }

                    if (e is MemberAccessExpr memExpr)
                    {
                        if (memExpr.Object is VariableExpr objVar && objVar.Name == "__CHIP__")
                        {
                            if (memExpr.Member == "arch") return config.Arch;
                            if (memExpr.Member == "chip" || memExpr.Member == "name") return config.Chip;
                        }

                        throw new Exception("Unknown member");
                    }

                    if (e is StringLiteral str) return str.Value;
                    if (e is IntegerLiteral intLit) return intLit.Value.ToString();
                    throw new Exception("Not a constant");
                }

                string left = GetVal(bin.Left);
                string right = GetVal(bin.Right);

                if (bin.Op == BinaryOp.Equal) return left == right;
                return left != right;
            }
        }

        if (expr is CallExpr call && call.Callee is MemberAccessExpr mem)
        {
            if (mem.Member == "startswith" && call.Args.Count == 1 && call.Args[0] is StringLiteral argStr)
            {
                string ObjectVal(Expression e)
                {
                    if (e is MemberAccessExpr m && m.Object is VariableExpr objVar && objVar.Name == "__CHIP__")
                    {
                        if (m.Member == "arch") return config.Arch;
                        if (m.Member == "chip" || m.Member == "name") return config.Chip;
                    }

                    throw new Exception("Unsupported startswith object");
                }

                return ObjectVal(mem.Object).StartsWith(argStr.Value);
            }
        }

        throw new Exception("Unsupported condition");
    }

    private string EvaluateMatchTarget(Expression expr)
    {
        if (expr is VariableExpr varExpr)
        {
            if (varExpr.Name == "__CHIP__") return config.Chip;
            if (varExpr.Name == "__FREQ__" || varExpr.Name == "F_CPU") return config.Frequency.ToString();
        }

        if (expr is MemberAccessExpr memExpr && memExpr.Object is VariableExpr objVar && objVar.Name == "__CHIP__")
        {
            if (memExpr.Member == "arch") return config.Arch;
            if (memExpr.Member == "chip" || memExpr.Member == "name") return config.Chip;
        }

        throw new Exception("Unsupported match target");
    }
}