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
                    // Empty when no board was given, which is the normal case and not an
                    // error: a HAL comparing against it gets "" and must treat that as "not
                    // told", never as "no".
                    "board" => config.Board,
                    "ram_size" => config.RamSize.ToString(),
                    "flash_size" => config.FlashSize.ToString(),
                    "eeprom_size" => config.EepromSize.ToString(),
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
                bool leftNum = TryResolveNumber(bin.Left, out long ln);
                bool rightNum = TryResolveNumber(bin.Right, out long rn);
                if (leftNum && rightNum)
                    return bin.Op == BinaryOp.Equal ? ln == rn : ln != rn;
                RejectMixedComparison(bin, leftNum, rightNum);

                var left = Resolve(bin.Left);
                var right = Resolve(bin.Right);
                return bin.Op == BinaryOp.Equal ? left == right : left != right;
            }
            case BinaryExpr { Op: BinaryOp.Less or BinaryOp.LessEq
                or BinaryOp.Greater or BinaryOp.GreaterEq } rel:
            {
                bool lNum = TryResolveNumber(rel.Left, out long lv);
                bool rNum = TryResolveNumber(rel.Right, out long rv);
                if (!lNum || !rNum)
                {
                    RejectMixedComparison(rel, lNum, rNum);
                    throw new PyMCU.Common.CompilerError("ConfigError",
                        $"'{rel.Op}' compares sizes, so both sides must be numbers known at " +
                        "compile time (a literal, or __CHIP__.ram_size / flash_size / " +
                        "eeprom_size / __FREQ__)", rel.Line, 0);
                }

                return rel.Op switch
                {
                    BinaryOp.Less => lv < rv,
                    BinaryOp.LessEq => lv <= rv,
                    BinaryOp.Greater => lv > rv,
                    _ => lv >= rv,
                };
            }
            default:
                return expr is CallExpr { Callee: MemberAccessExpr { Member: "startswith" } mem, Args: [StringLiteral argStr] }
                    ? Resolve(mem.Object).StartsWith(argStr.Value)
                    : throw new Exception("Unsupported condition");
        }
    }

    // A compile-time value is either a number or a string; there is no coercion
    // between them. "2048" == 2048 used to be true because everything resolved to
    // a string -- a silent equality between a chip name and a size.
    private bool TryResolveNumber(Expression? expr, out long value)
    {
        value = 0;
        switch (expr)
        {
            case IntegerLiteral lit:
                value = lit.Value;
                return true;
            case VariableExpr { Name: "__FREQ__" or "F_CPU" }:
                value = (long)config.Frequency;
                return true;
            case MemberAccessExpr { Object: VariableExpr { Name: "__CHIP__" } } m
                when m.Member is "ram_size" or "flash_size" or "eeprom_size":
                value = m.Member switch
                {
                    "ram_size" => config.RamSize,
                    "flash_size" => config.FlashSize,
                    _ => config.EepromSize,
                };
                return true;
            default:
                return false;
        }
    }

    private static void RejectMixedComparison(BinaryExpr bin, bool leftNum, bool rightNum)
    {
        if (leftNum == rightNum) return;
        throw new PyMCU.Common.CompilerError("ConfigError",
            "comparing a number with a string at compile time: one side is a size or " +
            "frequency and the other is a name. There is no conversion between them -- " +
            "compare sizes with sizes and names with names", bin.Line, 0);
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