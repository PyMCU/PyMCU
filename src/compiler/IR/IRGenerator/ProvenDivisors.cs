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

    // Every write ONE statement performs on a plain (undotted) name, as (name, rhs, weight).
    // `weight` is 2 for a form that presupposes an existing binding (aug-assign) so a lone
    // one still disqualifies the name without needing a second statement; every other form
    // is 1. `rhs` is non-null ONLY for a plain single-expression assignment (AssignStmt/
    // AnnAssign/VarDecl) -- the one shape the local-alias chain resolution ever follows; a
    // name bound by any other form (unpack, for-target, with-as, except-as, a local
    // import's bound name) still counts as a write, with no RHS to chain through.
    //
    // A nested tuple target (`(a, b), c = ...`) and a dotted attribute inside `del` are not
    // listed below because the parser refuses both outright (TupleUnpackStmt.Targets is a
    // flat list of names, and `del` is not supported at all on this target's static
    // storage) -- there is no successfully-compiled program this would ever need to count.
    // A dotted TupleUnpackStmt target (`self.a, self.b = ...`) is a field write, handled by
    // the caller, not here.
    private static IEnumerable<(string Name, Expression? Rhs, int Weight)> PlainStatementWrites(
        Statement s)
    {
        switch (s)
        {
            case AssignStmt { Target: VariableExpr v } a: yield return (v.Name, a.Value, 1); break;
            case AnnAssign aa when !aa.Target.Contains('.'):
                yield return (aa.Target, aa.Value, 1); break;
            case VarDecl vd: yield return (vd.Name, vd.Init, 1); break;
            case AugAssignStmt { Target: VariableExpr av }: yield return (av.Name, null, 2); break;
            case TupleUnpackStmt tu:
                foreach (var t in tu.Targets)
                    if (!t.Contains('.')) yield return (t, null, 1);
                break;
            case ForStmt fs:
                if (!string.IsNullOrEmpty(fs.VarName)) yield return (fs.VarName, null, 1);
                if (!string.IsNullOrEmpty(fs.Var2Name)) yield return (fs.Var2Name, null, 1);
                break;
            case WithStmt { AsName: { Length: > 0 } asName }: yield return (asName, null, 1); break;
            case TryStmt ts:
                foreach (var n in ts.HandlerNames)
                    if (!string.IsNullOrEmpty(n)) yield return (n, null, 1);
                break;
            case ImportStmt { InFunctionScope: true } im:
                if (!string.IsNullOrEmpty(im.ModuleAlias)) yield return (im.ModuleAlias, null, 1);
                foreach (var sym in im.Symbols)
                    yield return (im.Aliases.TryGetValue(sym, out var al) ? al : sym, null, 1);
                break;
        }
    }

    // Every FunctionDef nested anywhere inside fn's own body, at any depth, transitively.
    // TypeInference.WalkStatements stops at a nested def (it is a statement, not something
    // the shared walk recurses past), so each one is found once here; recursing into ITS
    // body (not through the shared walk, which would skip right past it again) reaches
    // further nesting as its own call.
    private static IEnumerable<FunctionDef> NestedFunctionDefs(FunctionDef fn)
    {
        foreach (var s in TypeInference.WalkStatements(fn.Body))
            if (s is FunctionDef nested)
            {
                yield return nested;
                foreach (var deeper in NestedFunctionDefs(nested)) yield return deeper;
            }
    }

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

        foreach (var s in TypeInference.WalkStatements(ast.GlobalStatements))
            foreach (var (name, _, weight) in PlainStatementWrites(s)) BumpModule(name, weight);
        foreach (var e in TypeInference.WalkExpressions(ast.GlobalStatements))
            if (e is WalrusExpr w) BumpModule(w.VarName, 2);

        // A function/method write only reaches module scope when the function declares the
        // name `global` -- everything else it writes is a local, handled separately below.
        // `nonlocal` never reaches module scope (by definition it skips the module), so it
        // plays no part here; a nested function's `nonlocal` write instead feeds the
        // OUTER FUNCTION's local count, in WalkFunctionLocals below.
        foreach (var fn in ast.Functions.Concat(TypeInference.ClassMethods(ast)))
        {
            var globals = new HashSet<string>();
            foreach (var s in TypeInference.WalkStatements(fn.Body))
                if (s is GlobalStmt g) foreach (var n in g.Names) globals.Add(n);
            if (globals.Count == 0) continue;
            foreach (var s in TypeInference.WalkStatements(fn.Body))
                foreach (var (name, _, weight) in PlainStatementWrites(s))
                    if (globals.Contains(name)) BumpModule(name, weight);
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
                            // `self.a, self.b = ...`: TupleUnpackStmt.Targets flattens a
                            // dotted target to "self.a" (a plain name has no dot at all).
                            case TupleUnpackStmt tu:
                                foreach (var t in tu.Targets)
                                {
                                    int dot = t.IndexOf('.');
                                    if (dot > 0 && t[..dot] == selfName)
                                        BumpField(className, t[(dot + 1)..], fn.Name);
                                }
                                break;
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
        //
        // Every binding FORM counts, not just plain assignment: tuple/list unpacking
        // (including a starred target -- its name is just another entry in
        // TupleUnpackStmt.Targets), +=, a for-loop's target(s), `with ... as`,
        // `except ... as`, a walrus, a function-local `import ... as`, and a write a
        // NESTED function makes through `nonlocal`. A name this function also declares
        // `global` or `nonlocal` for is excluded entirely from this function's own local
        // table -- it is not a local at all, and double-counting it here let a global
        // with two writes program-wide (one of them this function's own `global x; x = ...`)
        // read as single-write through the LOCAL alias path, bypassing the module check
        // that was supposed to be the only thing answering for it.
        void WalkFunctionLocals(FunctionDef fn, string fullName, string className, string selfName)
        {
            var localCounts = new Dictionary<string, int>();
            var soleRhs = new Dictionary<string, Expression>();
            var notLocal = new HashSet<string>();
            foreach (var s in TypeInference.WalkStatements(fn.Body))
            {
                if (s is GlobalStmt g) foreach (var n in g.Names) notLocal.Add(n);
                if (s is NonlocalStmt nl) foreach (var n in nl.Names) notLocal.Add(n);
            }
            void Bump(string n, Expression? rhs, int by = 1)
            {
                if (notLocal.Contains(n)) return;
                localCounts[n] = (localCounts.TryGetValue(n, out var c) ? c : 0) + by;
                if (rhs != null && by == 1) soleRhs[n] = rhs; else soleRhs.Remove(n);
            }
            foreach (var s in TypeInference.WalkStatements(fn.Body))
                foreach (var (name, rhs, weight) in PlainStatementWrites(s)) Bump(name, rhs, weight);
            // A walrus can sit inside an expression a Statement-level switch never reaches
            // directly (a short-circuited `and`/`or`, a loop condition) -- it still writes.
            foreach (var e in TypeInference.WalkExpressions(new List<Statement> { fn.Body }))
                if (e is WalrusExpr we) Bump(we.VarName, null, 2);

            // A nested (necessarily @inline) function's `nonlocal x` write reaches an
            // enclosing function's local. Which enclosing scope exactly is real scope
            // resolution this does not attempt -- crediting the write to EVERY function fn
            // transitively encloses is conservative (it can only ADD a disqualifying write,
            // never remove one), matching the rest of this scan's "unrecognized shape
            // disqualifies" stance.
            foreach (var nested in NestedFunctionDefs(fn))
            {
                var nestedNonlocals = new HashSet<string>();
                foreach (var s in TypeInference.WalkStatements(nested.Body))
                    if (s is NonlocalStmt nl) foreach (var n in nl.Names) nestedNonlocals.Add(n);
                if (nestedNonlocals.Count == 0) continue;
                foreach (var s in TypeInference.WalkStatements(nested.Body))
                    foreach (var (name, _, weight) in PlainStatementWrites(s))
                        if (nestedNonlocals.Contains(name)) Bump(name, null, weight);
                foreach (var e in TypeInference.WalkExpressions(new List<Statement> { nested.Body }))
                    if (e is WalrusExpr nwe && nestedNonlocals.Contains(nwe.VarName))
                        Bump(nwe.VarName, null, 2);
            }

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
