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

using System.Collections.Generic;
using System.Linq;
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    // Whole-program, non-flow-sensitive pre-scan backing the proven power-of-two modulo
    // rewrite (see Expr.cs's ProvenConstantDivisor / DivisorNameIsWholeProgramInvariant).
    //
    // Three rounds of silent miscompiles -- an exception handler inheriting the pre-try
    // value, a walrus on a short-circuited branch leaking into the fallthrough, a loop
    // walrus answering for every iteration with the entry value -- all came from trusting
    // localConstantValues (or a bespoke range table before it) as if it answered "what is
    // this name's value HERE", when it only ever answered "what is the most recent fact
    // recorded for this name", which is not the same question at a raise, a short-circuit,
    // or a loop back-edge.
    //
    // The fix drops that question entirely. A name is eligible for the rewrite only when
    // the ENTIRE PROGRAM contains exactly one textual write to it -- counted once, here,
    // before any lowering happens, by walking the AST only. A name written twice anywhere
    // (even twice in a row, even on two mutually exclusive branches) is refused, full stop,
    // regardless of what any lowering-time table momentarily holds for it. This is strictly
    // ELIGIBILITY: the resolved VALUE still comes from the existing constant-folding tables
    // (constantVariables / localConstantValues), which this only gates, never replaces.
    private HashSet<string> wholeProgramSingleWriteModuleNames = new();
    private HashSet<(string ClassName, string Field)> wholeProgramSingleWriteInitFields = new();
    private Dictionary<string, string> wholeProgramMethodOwningClass = new();
    // [function-or-method qualified name] -> local/param bare names eligible because they
    // have exactly one textual write in THEIR OWN function, and that one write is a direct
    // copy (no arithmetic, no call) of a name or field already eligible above. This is how
    // `w = self.width` in a method inherits self.width's proof without "local" ever
    // appearing in the module-name-or-field criterion itself.
    private Dictionary<string, HashSet<string>> wholeProgramLocalAliasOfProven = new();

    private void ScanWholeProgramConstantDivisors(ProgramNode ast)
    {
        wholeProgramSingleWriteModuleNames = new();
        wholeProgramSingleWriteInitFields = new();
        wholeProgramMethodOwningClass = new();
        wholeProgramLocalAliasOfProven = new();

        // ---- module-level (and `global`-declared) names ----
        var moduleWriteCounts = new Dictionary<string, int>();
        void BumpModule(string n, int by = 1) =>
            moduleWriteCounts[n] = (moduleWriteCounts.TryGetValue(n, out var c) ? c : 0) + by;

        void WalkModuleWrite(Statement s)
        {
            switch (s)
            {
                case AssignStmt { Target: VariableExpr v }: BumpModule(v.Name); break;
                case AnnAssign aa when !aa.Target.Contains('.'): BumpModule(aa.Target); break;
                case VarDecl vd: BumpModule(vd.Name); break;
                // An aug-assign presupposes an existing binding, so it is always a SECOND
                // write even standing alone -- bump by 2 so a lone `x += 1` disqualifies x
                // instead of reading as the one allowed write.
                case AugAssignStmt { Target: VariableExpr av }: BumpModule(av.Name, 2); break;
            }
        }
        foreach (var s in TypeInference.WalkStatements(ast.GlobalStatements)) WalkModuleWrite(s);
        foreach (var e in TypeInference.WalkExpressions(ast.GlobalStatements))
            if (e is WalrusExpr w) BumpModule(w.VarName, 2);

        // A function/method write only reaches module scope when the function declares the
        // name `global` -- everything else it writes is a local, handled separately below.
        foreach (var fn in ast.Functions.Concat(TypeInference.ClassMethods(ast)))
        {
            var globals = new HashSet<string>();
            foreach (var s in TypeInference.WalkStatements(fn.Body))
                if (s is GlobalStmt g) foreach (var n in g.Names) globals.Add(n);
            if (globals.Count == 0) continue;
            foreach (var s in TypeInference.WalkStatements(fn.Body))
            {
                switch (s)
                {
                    case AssignStmt { Target: VariableExpr v } when globals.Contains(v.Name):
                        BumpModule(v.Name); break;
                    case AugAssignStmt { Target: VariableExpr av } when globals.Contains(av.Name):
                        BumpModule(av.Name, 2); break;
                }
            }
            foreach (var e in TypeInference.WalkExpressions(new List<Statement> { fn.Body }))
                if (e is WalrusExpr w2 && globals.Contains(w2.VarName)) BumpModule(w2.VarName, 2);
        }
        wholeProgramSingleWriteModuleNames = new HashSet<string>(
            moduleWriteCounts.Where(kv => kv.Value == 1).Select(kv => kv.Key));

        // ---- instance fields, written only in __init__ ----
        var fieldWriteCounts = new Dictionary<(string ClassName, string Field), int>();
        var fieldWriteFunc = new Dictionary<(string ClassName, string Field), string>();
        void BumpField(string cls, string field, string inFunc, int by = 1)
        {
            var key = (cls, field);
            fieldWriteCounts[key] = (fieldWriteCounts.TryGetValue(key, out var c) ? c : 0) + by;
            fieldWriteFunc[key] = inFunc;
        }

        // A dynamic `setattr(obj, name, ...)` with a non-literal field name could reach any
        // field of any class; this never narrows which one, so it refuses the whole
        // mechanism rather than guess.
        bool dynamicSetattrSeen = false;

        void WalkClass(ClassDef cls, string prefix)
        {
            string className = prefix + cls.Name;
            if (cls.Body is not Block body) return;
            foreach (var member in body.Statements)
            {
                if (member is FunctionDef fn)
                {
                    string selfName = fn.Params.Count > 0 ? fn.Params[0].Name : "self";
                    wholeProgramMethodOwningClass[className + "_" + fn.Name] = className;
                    foreach (var s in TypeInference.WalkStatements(fn.Body))
                    {
                        switch (s)
                        {
                            case AssignStmt {
                                Target: MemberAccessExpr { Object: VariableExpr ov, Member: string f }
                            } when ov.Name == selfName:
                                BumpField(className, f, fn.Name); break;
                            case AugAssignStmt {
                                Target: MemberAccessExpr { Object: VariableExpr ov2, Member: string f2 }
                            } when ov2.Name == selfName:
                                BumpField(className, f2, fn.Name, 2); break;
                        }
                    }
                    foreach (var e in TypeInference.WalkExpressions(new List<Statement> { fn.Body }))
                        if (e is CallExpr { Callee: VariableExpr { Name: "setattr" } } ce
                            && ce.Args.Count >= 2 && ce.Args[1] is not StringLiteral)
                            dynamicSetattrSeen = true;
                }
                else if (member is ClassDef nested)
                {
                    WalkClass(nested, className + "_");
                }
            }
        }
        foreach (var s in ast.GlobalStatements)
            if (s is ClassDef cls) WalkClass(cls, "");

        if (!dynamicSetattrSeen)
            foreach (var kv in fieldWriteCounts)
                if (kv.Value == 1 && fieldWriteFunc[kv.Key] == "__init__")
                    wholeProgramSingleWriteInitFields.Add(kv.Key);

        // ---- local/param aliases of an already-eligible module name or field ----
        void WalkFunctionLocals(FunctionDef fn, string fullName, string className, string selfName)
        {
            var localCounts = new Dictionary<string, int>();
            var soleRhs = new Dictionary<string, Expression>();
            void Bump(string n, Expression? rhs, int by = 1)
            {
                localCounts[n] = (localCounts.TryGetValue(n, out var c) ? c : 0) + by;
                if (rhs != null && by == 1) soleRhs[n] = rhs; else soleRhs.Remove(n);
            }
            foreach (var s in TypeInference.WalkStatements(fn.Body))
            {
                switch (s)
                {
                    case AssignStmt { Target: VariableExpr v } a: Bump(v.Name, a.Value); break;
                    case AnnAssign aa when !aa.Target.Contains('.') && aa.Value != null:
                        Bump(aa.Target, aa.Value); break;
                    case VarDecl vd when vd.Init != null: Bump(vd.Name, vd.Init); break;
                    case VarDecl vd2: Bump(vd2.Name, null); break;
                    case AugAssignStmt { Target: VariableExpr av }: Bump(av.Name, null, 2); break;
                }
            }
            // A walrus can sit inside an expression a Statement-level switch never reaches
            // directly (a short-circuited `and`/`or`, a loop condition) -- it still writes.
            foreach (var e in TypeInference.WalkExpressions(new List<Statement> { fn.Body }))
                if (e is WalrusExpr we) Bump(we.VarName, null, 2);

            var elig = new HashSet<string>();
            foreach (var kv in localCounts)
            {
                if (kv.Value != 1 || !soleRhs.TryGetValue(kv.Key, out var rhs)) continue;
                // A local written exactly once, directly from a literal, is its own
                // one-write proof -- no chain to another name needed (criterion (a)
                // applied to a local instead of the divisor expression itself).
                if (rhs is IntegerLiteral)
                    elig.Add(kv.Key);
                else if (rhs is VariableExpr rv && wholeProgramSingleWriteModuleNames.Contains(rv.Name))
                    elig.Add(kv.Key);
                else if (rhs is MemberAccessExpr { Object: VariableExpr ro, Member: string rf }
                         && ro.Name == selfName
                         && wholeProgramSingleWriteInitFields.Contains((className, rf)))
                    elig.Add(kv.Key);
            }
            if (elig.Count > 0) wholeProgramLocalAliasOfProven[fullName] = elig;
        }

        foreach (var fn in ast.Functions)
            WalkFunctionLocals(fn, fn.Name, "", "self");

        void WalkClassLocals(ClassDef cls, string prefix)
        {
            string className = prefix + cls.Name;
            if (cls.Body is not Block body) return;
            foreach (var member in body.Statements)
            {
                if (member is FunctionDef fn)
                {
                    string selfName = fn.Params.Count > 0 ? fn.Params[0].Name : "self";
                    WalkFunctionLocals(fn, className + "_" + fn.Name, className, selfName);
                }
                else if (member is ClassDef nested) WalkClassLocals(nested, className + "_");
            }
        }
        foreach (var s in ast.GlobalStatements)
            if (s is ClassDef cls) WalkClassLocals(cls, "");
    }

    // The ORIGINAL (pre-inline) qualified function/method name code is lexically being
    // lowered for right now, regardless of which function's symbol names the IR currently
    // being emitted actually carries. Mirrors the frameMethod pattern already used
    // elsewhere (Expr.cs) for the same "what frame are we really in" question.
    private string CurrentFrameMethod() =>
        inlineStack.Count > 0 && !string.IsNullOrEmpty(inlineStack[^1].CalleeName)
            ? inlineStack[^1].CalleeName : currentFunction;

    // Whether `divisorExpr`, lexically written inside `frameMethod`, is eligible for the
    // proven power-of-two modulo rewrite under the whole-program, non-flow-sensitive
    // criterion computed by ScanWholeProgramConstantDivisors: a module name or instance
    // field with exactly one textual write anywhere in the program, or a local/parameter
    // of THIS function with exactly one textual write that directly copies one of those.
    // A literal divisor never reaches here -- ProvenConstantDivisor answers that case
    // itself, from the Val alone, with no AST or program-wide fact needed.
    private bool DivisorNameIsWholeProgramInvariant(Expression divisorExpr, string frameMethod)
    {
        switch (divisorExpr)
        {
            case VariableExpr ve:
                if (wholeProgramLocalAliasOfProven.TryGetValue(frameMethod, out var locals)
                    && locals.Contains(ve.Name))
                    return true;
                return wholeProgramSingleWriteModuleNames.Contains(ve.Name);
            case MemberAccessExpr { Object: VariableExpr { Name: "self" }, Member: string field }:
                return wholeProgramMethodOwningClass.TryGetValue(frameMethod, out var cls)
                    && wholeProgramSingleWriteInitFields.Contains((cls, field));
            default:
                return false;
        }
    }
}
