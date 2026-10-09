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

    // RFC 0014 family 6: the folds below answer only for names the module BOUND, not for
    // spellings. RecordImportBinding fills the three tables as ConditionalCompilator and
    // ConditionalImportExtractor walk each module's imports:
    //
    //   ModuleAliases        local name -> module it resolves to, every import shape
    //                        (`import usys as s` gives s -> usys; `from os import uname`
    //                        gives uname -> os), mirroring IRGenerator's importedAliases.
    //   AliasToOriginal      local name -> the symbol a `from` import bound, recorded for
    //                        every symbol whether or not `as` renamed it. A name absent
    //                        here is a MODULE binding, which is how `from sys import x`
    //                        keeps `x.platform` from folding: x is not the module.
    //   ImportedModuleNames  every module name the file imported, any shape, so the
    //                        `pymcu.chips.X` member spelling knows `import pymcu.chips`
    //                        really was written.
    public Dictionary<string, string> ModuleAliases { get; } = new();
    public Dictionary<string, string> AliasToOriginal { get; } = new();
    public HashSet<string> ImportedModuleNames { get; } = new(StringComparer.Ordinal);

    // The source line each name was bound at. prog.Imports is seeded ahead of the walk
    // (the parser files top-level imports separately), so order information is the only
    // way a rebind can tell `x = 3` BEFORE the import from the same statement after it.
    private readonly Dictionary<string, int> _bindingLine = new();

    public void RecordImportBinding(ImportStmt imp)
    {
        ImportedModuleNames.Add(imp.ModuleName);
        if (imp.Symbols.Count == 0)
        {
            // `import a.b` binds the top package `a`; `import a.b as c` binds c to the
            // submodule itself -- the same rule IRGenerator's import loops apply.
            bool aliased = !string.IsNullOrEmpty(imp.ModuleAlias);
            string local = aliased ? imp.ModuleAlias! : Head(imp.ModuleName);
            ModuleAliases[local] = aliased ? imp.ModuleName : Head(imp.ModuleName);
            _bindingLine[local] = imp.Line;
            return;
        }
        foreach (var sym in imp.Symbols)
        {
            if (sym == StarImportExpander.Star) continue;
            string local = imp.Aliases.TryGetValue(sym, out var alias) ? alias : sym;
            ModuleAliases[local] = imp.ModuleName;
            AliasToOriginal[local] = sym;
            _bindingLine[local] = imp.Line;
        }
    }

    // The local name stops answering as the import's binding once the module rebinds it:
    // `from pymcu.chips import __FREQ__` followed by `__FREQ__ = 3` makes the name mean 3,
    // which the IR ladder answers from constantVariables -- folding the fact here anyway
    // would pick a different branch than the IR lowers. An assignment on an EARLIER line
    // is the one the import overwrote, so it leaves the binding alone.
    public void RecordRebinding(string name, int line = -1)
    {
        if (line >= 0 && _bindingLine.TryGetValue(name, out var boundAt) && line < boundAt)
            return;
        ModuleAliases.Remove(name);
        AliasToOriginal.Remove(name);
        _bindingLine.Remove(name);
    }

    private static string Head(string moduleName)
    {
        int dot = moduleName.IndexOf('.');
        return dot < 0 ? moduleName : moduleName[..dot];
    }

    // Whether `e` is a bare name bound to one of `mods` as a MODULE (`import sys`,
    // `import usys as s`). A `from sys import x` binding is not the module object --
    // `x.platform` is an attribute read on whatever x is, never the sys fact.
    private bool IsModuleName(Expression? e, params string[] mods) =>
        e is VariableExpr { Name: var n }
        && !AliasToOriginal.ContainsKey(n)
        && ModuleAliases.TryGetValue(n, out var real) && mods.Contains(real);

    // `name` bound by `from pymcu.chips import <symbol>` -- returns the symbol so the
    // caller answers the fact it names. `import pymcu.chips as c` puts c in
    // ModuleAliases without an AliasToOriginal entry, which is what keeps the module
    // alias out of this path.
    private string? BoundChipSymbol(string name) =>
        AliasToOriginal.TryGetValue(name, out var sym)
        && ModuleAliases.TryGetValue(name, out var mod) && mod == "pymcu.chips"
            ? sym : null;

    // `e` names the pymcu.chips MODULE itself: `import pymcu.chips as c` gives `c`, and
    // `import pymcu.chips` gives the `pymcu.chips` member spelling (`from pymcu import
    // chips` was already rewritten to the first shape by DependencyGraphBuilder).
    private bool IsChipsModuleExpr(Expression? e) => e switch
    {
        VariableExpr v => !AliasToOriginal.ContainsKey(v.Name)
            && ModuleAliases.TryGetValue(v.Name, out var m) && m == "pymcu.chips",
        MemberAccessExpr { Object: VariableExpr pv, Member: "chips" }
            => ImportedModuleNames.Contains("pymcu.chips")
               && ModuleAliases.TryGetValue(pv.Name, out var pm) && pm == "pymcu"
               && !AliasToOriginal.ContainsKey(pv.Name),
        _ => false,
    };

    // `e` is the chip-descriptor object: a name bound to `__CHIP__` (any alias), or the
    // `chips.__CHIP__` member of a bound chips module.
    private bool IsChipDescriptorExpr(Expression? e) => e switch
    {
        VariableExpr v => BoundChipSymbol(v.Name) == "__CHIP__",
        MemberAccessExpr { Member: "__CHIP__", Object: var m } => IsChipsModuleExpr(m),
        _ => false,
    };

    // Resolves a compile-time expression to its string representation.
    // Throws if the expression is not a known compile-time constant.
    public string Resolve(Expression e)
    {
        switch (e)
        {
            case VariableExpr { Name: "__name__" }:
                return ModuleName;
            // RFC 0014 family 6: `__CHIP__`, `__FREQ__`/`F_CPU`, `__TIMEBASE__` are facts
            // the module reads through `from pymcu.chips import ...` under whatever local
            // spelling it chose. A bare spelling the file never imported is not bound and
            // falls to the ordinary Unknown-var arm -- the IR reports the missing import.
            case VariableExpr v when BoundChipSymbol(v.Name) is { } sym:
                return sym switch
                {
                    "__CHIP__" => config.Chip,
                    "__FREQ__" or "F_CPU" => config.Frequency.ToString(),
                    "__TIMEBASE__" => config.Timebase ? "1" : "0",
                    _ => throw new Exception($"pymcu.chips has no fact '{sym}'"),
                };
            case VariableExpr varExpr:
                throw new Exception("Unknown var");
            // `chips.__FREQ__` / `pymcu.chips.__TIMEBASE__` / `c.__CHIP__` -- the module
            // spelling of the same bound facts.
            case MemberAccessExpr { Object: { } chipsMod, Member: var chipsMember }
                when IsChipsModuleExpr(chipsMod) && chipsMember is
                    "__CHIP__" or "__FREQ__" or "F_CPU" or "__TIMEBASE__":
                return chipsMember switch
                {
                    "__CHIP__" => config.Chip,
                    "__TIMEBASE__" => config.Timebase ? "1" : "0",
                    _ => config.Frequency.ToString(),
                };
            case MemberAccessExpr { Object: { } chipObj } memExpr when IsChipDescriptorExpr(chipObj):
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
            // `sys.implementation.name` -- another compile-time dunder, resolved the same way
            // __CHIP__ is (docs/rfcs/0007): a per-(stdlib) table, never the compat layer's own
            // sys.py parsed as source. `sys` here means "whatever the project's --stdlib names",
            // a single build-wide fact, exactly like __CHIP__ names a single chip.
            case MemberAccessExpr { Member: "name", Object: MemberAccessExpr { Member: "implementation" } implObj }
                when IsModuleName(implObj.Object, "sys", "usys"):
                return IntrospectionTable.ImplementationName(config);
            // `sys.platform`.
            case MemberAccessExpr { Member: "platform" } memExpr
                when IsModuleName(memExpr.Object, "sys", "usys"):
                return IntrospectionTable.SysPlatform(config);
            // `uname().<field>` / `os.uname().<field>` -- `from os import uname; uname()` and
            // `import os; os.uname()` are both written in the survey (docs/rfcs/0007 section 1).
            case MemberAccessExpr { Object: { } unameCall, Member: var field } when IsUnameCall(unameCall):
            {
                var u = IntrospectionTable.GetUname(config);
                return field switch
                {
                    "sysname" => u.Sysname,
                    "nodename" => u.Nodename,
                    "release" => u.Release,
                    "version" => u.Version,
                    "machine" => u.Machine,
                    _ => throw new Exception("Unknown member"),
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
            // `not <condition>` -- PyMCU#266's actual repro is `if not sys.implementation.name
            // == "circuitpython":`, `not` binding looser than `==` per Python precedence, so
            // this is a UnaryExpr wrapping the BinaryExpr, not a distinct comparison operator.
            case UnaryExpr { Op: UnaryOp.Not } un:
                return !EvaluateCondition(un.Operand);
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
            // `"Linux" not in uname()` (adafruit_dht.py) -- membership over the resolved
            // 5-tuple's string fields, matching Python's own tuple-membership semantics.
            // Only admissible when the right side is exactly the uname() call: an arbitrary
            // compile-time sequence is out of scope (nothing else in the survey needs it).
            case BinaryExpr { Op: BinaryOp.In or BinaryOp.NotIn } inExpr when IsUnameCall(inExpr.Right):
            {
                var needle = Resolve(inExpr.Left);
                var u = IntrospectionTable.GetUname(config);
                bool found = needle == u.Sysname || needle == u.Nodename || needle == u.Release
                    || needle == u.Version || needle == u.Machine;
                return inExpr.Op == BinaryOp.In ? found : !found;
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
            case VariableExpr v when BoundChipSymbol(v.Name) is { } numSym
                && numSym is "__FREQ__" or "F_CPU" or "__TIMEBASE__":
                value = numSym == "__TIMEBASE__" ? (config.Timebase ? 1 : 0) : (long)config.Frequency;
                return true;
            case MemberAccessExpr { Object: { } numChipsMod, Member: var numChipsMember }
                when IsChipsModuleExpr(numChipsMod) && numChipsMember is
                    "__FREQ__" or "F_CPU" or "__TIMEBASE__":
                value = numChipsMember == "__TIMEBASE__"
                    ? (config.Timebase ? 1 : 0) : (long)config.Frequency;
                return true;
            case MemberAccessExpr { Object: { } numChipObj, Member: var sizeMember }
                when IsChipDescriptorExpr(numChipObj)
                     && sizeMember is "ram_size" or "flash_size" or "eeprom_size":
                value = sizeMember switch
                {
                    "ram_size" => config.RamSize,
                    "flash_size" => config.FlashSize,
                    _ => config.EepromSize,
                };
                return true;
            // `sys.implementation.version[0]` (neopixel.py: `version[0] >= 7`, a feature-
            // detection proxy -- see docs/rfcs/0007 section 3 for why the tuple this answers
            // from is the upstream API surface version, not this layer package's own version).
            case IndexExpr { Index: IntegerLiteral idxLit } ixExpr
                when ixExpr.Target is MemberAccessExpr
                     {
                         Member: "version",
                         Object: MemberAccessExpr { Member: "implementation" } implObj
                     }
                     && IsModuleName(implObj.Object, "sys", "usys"):
            {
                var (major, minor, micro) = IntrospectionTable.ImplementationVersion(config);
                value = idxLit.Value switch
                {
                    0 => major,
                    1 => minor,
                    2 => micro,
                    _ => throw new Exception("sys.implementation.version has 3 elements"),
                };
                return true;
            }
            // `sys.version_info[0]` -- upstream's Python language version, (3, 4, 0) on
            // both flavors. Only the indexed read folds: the tuple has no honest
            // module-level form, so the bare attribute refuses (same contract as
            // sys.implementation.version).
            case IndexExpr { Index: IntegerLiteral viIdx } viExpr
                when viExpr.Target is MemberAccessExpr { Member: "version_info" } viObj
                     && IsModuleName(viObj.Object, "sys", "usys"):
            {
                var (vMajor, vMinor, vMicro) = IntrospectionTable.SysVersionInfo(config);
                value = viIdx.Value switch
                {
                    0 => vMajor,
                    1 => vMinor,
                    2 => vMicro,
                    _ => throw new Exception("sys.version_info has 3 elements"),
                };
                return true;
            }
            default:
                return false;
        }
    }

    // Whether `e` is exactly `uname()` (bare, after `from os import uname`) or `os.uname()`
    // / `uos.uname()` (dotted) -- the shapes the survey found (docs/rfcs/0007 section 1)
    // plus MicroPython's own u-spelling of the same module. No arguments, matching the
    // real signature. RFC 0014: binding, not spelling -- a bare `uname` the file never
    // imported is the program's own function, and a `uos` the file defined is its object.
    private bool IsUnameCall(Expression e) => e is CallExpr { Args.Count: 0 } call
        && call.Callee switch
        {
            VariableExpr { Name: var unameName } =>
                AliasToOriginal.TryGetValue(unameName, out var unameSym) && unameSym == "uname"
                && ModuleAliases.TryGetValue(unameName, out var unameMod)
                && unameMod is "os" or "uos" or "pymcu.os",
            MemberAccessExpr { Member: "uname", Object: var unameObj } =>
                IsModuleName(unameObj, "os", "uos"),
            _ => false,
        };

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