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

// Evaluates compile-time expressions against a fixed DeviceConfig.
// Responsible solely for: resolving config values, evaluating boolean conditions,
// and matching case-branch patterns. Does not touch or mutate the AST.
public class CompileTimeEvaluator(DeviceConfig config)
{
    // Current module name — "__main__" for the entry file, dotted name for libraries.
    public string ModuleName { get; set; } = "__main__";

    // Resolves a compile-time expression to its string representation.
    // Throws if the expression is not a known compile-time constant.
    public string Resolve(Expression e)
    {
        switch (e)
        {
            case VariableExpr { Name: "__CHIP__" }:
                return config.Chip;
            case VariableExpr { Name: "__FREQ__" or "F_CPU" }:
                return config.Frequency.ToString();
            case VariableExpr { Name: "__name__" }:
                return ModuleName;
            case VariableExpr varExpr:
                throw new Exception("Unknown var");
            case MemberAccessExpr { Object: VariableExpr { Name: "__CHIP__" } } memExpr:
            {
                return memExpr.Member switch
                {
                    "arch" => config.Arch,
                    "chip" or "name" => config.Chip,
                    _ => throw new Exception("Unknown member")
                };
            }
            case StringLiteral str:
                return str.Value;
            case IntegerLiteral intLit:
                return intLit.Value.ToString();
            default:
                throw new Exception("Not a constant");
        }
    }

    // Evaluates a boolean compile-time condition.
    // Throws if any sub-expression cannot be resolved at compile time.
    public bool EvaluateCondition(Expression? expr)
    {
        switch (expr)
        {
            case null:
                return false;
            case BinaryExpr { Op: BinaryOp.Or } bin:
                return EvaluateCondition(bin.Left) || EvaluateCondition(bin.Right);
            case BinaryExpr { Op: BinaryOp.And } bin:
                return EvaluateCondition(bin.Left) && EvaluateCondition(bin.Right);
            case BinaryExpr { Op: BinaryOp.Equal or BinaryOp.NotEqual } bin:
            {
                var left = Resolve(bin.Left);
                var right = Resolve(bin.Right);
                return bin.Op == BinaryOp.Equal ? left == right : left != right;
            }
            default:
                return expr is CallExpr { Callee: MemberAccessExpr { Member: "startswith" } mem, Args: [StringLiteral argStr] }
                    ? Resolve(mem.Object).StartsWith(argStr.Value)
                    : throw new Exception("Unsupported condition");
        }
    }

    // Returns true if the case-branch pattern matches the given target value.
    // Supports: null (wildcard), IntegerLiteral, StringLiteral, BinaryExpr OR-pattern.
    public bool MatchesPattern(Expression? pattern, string targetVal)
    {
        switch (pattern)
        {
            case null:
                return true; // wildcard
            case IntegerLiteral intLit:
                return intLit.Value.ToString() == targetVal;
            case StringLiteral strLit:
                return strLit.Value == targetVal;
        }

        if (pattern is not BinaryExpr binExpr) return false;
        var alts = new List<string>();
        FlattenOrPattern(binExpr, alts);
        return alts.Any(alt => alt == targetVal);
    }

    private static void FlattenOrPattern(Expression e, List<string> alts)
    {
        while (true)
        {
            switch (e)
            {
                case BinaryExpr { Op: BinaryOp.BitOr } b:
                    FlattenOrPattern(b.Left, alts);
                    e = b.Right;
                    continue;
                case StringLiteral s:
                    alts.Add(s.Value);
                    break;
                case IntegerLiteral il:
                    alts.Add(il.Value.ToString());
                    break;
            }

            break;
        }
    }
}