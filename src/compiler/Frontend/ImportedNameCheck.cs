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

/// <summary>
/// Rejects `from m import name` when `m` resolves and binds no `name`.
///
/// It used to be accepted. The name then existed nowhere, so every use site invented an
/// unassigned local and the firmware shipped reading whatever the RAM held, with a clean
/// build (issue #158). Where it did fail, it failed somewhere else entirely: `from
/// pymcu.hal.gpio import Pin, OUTPUT` names an OUTPUT the stdlib does not define, and the
/// first complaint was several paragraphs about unrolling loops at a call four steps away.
///
/// Runs after conditional compilation, never at parse time. A HAL facade binds its names
/// inside `if __CHIP__.name == ...`, so before folding the winning branch has not been
/// promoted yet and every one of those imports would look unsatisfied.
///
/// Deliberately silent whenever the answer is not certain: a module whose top level raises
/// CompileError (its own sentence is the better message), a module that star-imports
/// something this build never loaded, a name bound in any branch at any depth including the
/// ones folding removed. A false positive here refuses a program that works, which is worse
/// than the silence this replaces.
/// </summary>
public static class ImportedNameCheck
{
    public static void Check(CompilationContext context)
    {
        var pathOf = new Dictionary<ProgramNode, string>(ReferenceEqualityComparer.Instance);
        foreach (var kv in context.ModuleCache) pathOf[kv.Value] = kv.Key;

        var bindingsOf = new Dictionary<ProgramNode, HashSet<string>?>(ReferenceEqualityComparer.Instance);

        foreach (var (owner, ownerName) in Owners(context))
        {
            foreach (var imp in owner.Imports)
            {
                if (imp.Symbols.Count == 0) continue;

                // An import written inside a function or a method binds a local name, and
                // folding promoted it here with nowhere else to put it. Whether the name it
                // asks for exists only matters if that function is compiled, and the call
                // path reports that far better than this could. pymcu.hal.pic14.gpio's
                // `Pin.irq` imports a pin_irq_setup that pic16f628a_gpio does not define,
                // which is a real bug in a method nothing in the suite calls; refusing the
                // whole build for it turned 8 green PIC tests red.
                if (imp.InFunctionScope) continue;

                if (BuiltinModuleNames.IsBuiltin(imp.ModuleName)) continue;
                if (!context.NamedModules.TryGetValue(imp.ModuleName, out var target)) continue;
                if (ReferenceEquals(target, owner)) continue;

                if (!bindingsOf.TryGetValue(target, out var bound))
                    bindingsOf[target] = bound = BoundNames(target);
                if (bound == null) continue;

                foreach (var sym in imp.Symbols)
                {
                    if (sym == StarImportExpander.Star || bound.Contains(sym)) continue;

                    // A builtin exception resolves whether or not the module names it, so
                    // `from pymcu.exceptions import ValueError` is accepted here even though
                    // CPython rejects it and exceptions.py deliberately does not declare it.
                    if (BuiltinExceptionNames.IsBuiltin(sym)) continue;

                    // A Python builtin resolves wherever it is written, so importing one is not
                    // an ImportError even when the module does not define it. This is not an
                    // edge case here: `from pymcu.hal.console import print` is how a program
                    // says which sink print writes to, and 237 fixtures use it.
                    if (PyMCU.Common.PythonBuiltinNames.IsBuiltin(sym)) continue;

                    string where = ownerName == null
                        ? context.Options.FilePath
                        : pathOf.TryGetValue(owner, out var p) ? p : context.Options.FilePath;

                    throw new CompilerError("ImportError", Message(imp.ModuleName, sym, target, bound),
                                            imp.Line > 0 ? imp.Line : 1, imp.Column)
                    {
                        File = where,
                    };
                }
            }
        }
    }

    private static string Message(string moduleName, string symbol, ProgramNode module,
                                  HashSet<string> bound)
    {
        // What the module OFFERS, not everything its namespace holds. Listing the bindings
        // put every helper import beside the real API: `machine` advertised `Callable` and
        // `CompileError`, which arrive from `from pymcu.types import ...` and
        // `from pymcu.exceptions import CompileError`, and with the list elided at ten names
        // two of the ten a reader saw did not exist (issue #194). A reader who has just been
        // refused a name reads this list to find out what IS there, so a fifth of it being
        // unusable is worse than it being shorter.
        //
        // The CHECK above still tests against the bindings, because a re-export really is
        // importable. Only the advertisement is narrowed.
        var offered = StarImportExpander.ExportedNames(module)
                          .Where(n => n.Length > 0 && n[0] != '_')
                          .OrderBy(n => n, StringComparer.Ordinal)
                          .ToList();

        // Everything importable from here, which for a facade is all of it.
        var importable = bound.Where(n => n.Length > 0 && n[0] != '_')
                              .OrderBy(n => n, StringComparer.Ordinal)
                              .ToList();

        // A near miss first, when there is one. The reader who wrote `Pn` wants to be told
        // `Pin`, not handed the export list to search. #54 established that at the CALL site;
        // this check reports at the IMPORT, which is earlier and better, so it has to carry the
        // suggestion too or the move loses it.
        // Appended to the sentence, never returned in place of it. Returning the fragment
        // alone produced "ImportError:  Did you mean 'Pin'?", which never says what failed,
        // and reads as a stray remark with a doubled space in front of it.
        //
        // Computed over the BINDINGS, not the narrowed list. The same reasoning that keeps the
        // check against the bindings applies here: a re-export really is importable, so a
        // near miss for one is a real suggestion. Narrowing this too silently removed the
        // suggestion from every facade, `pymcu.hal.gpio` included, where it is most wanted.
        string near = Nearest(importable, symbol);

        // A module that defines nothing of its own is a pure facade, and re-exporting IS what
        // it offers. Advertising its bindings there is not the `machine` problem: there are no
        // helper imports to separate out, because separating them is what defines the facade.
        var advertised = offered.Count > 0 ? offered : importable;
        string verb = offered.Count > 0 ? "defines" : "offers";
        string list = advertised.Count == 0
            ? ""
            : $" '{moduleName}' {verb} {string.Join(", ", advertised.Take(10))}"
              + (advertised.Count > 10 ? ", ..." : "") + ".";

        return $"cannot import '{symbol}' from '{moduleName}': the module was found and does "
             + $"not define that name."
             + (near.Length > 0 ? $" Did you mean '{near}'?" : list);
    }

    // The entry file first (its name is null), then every module loaded under a name. The
    // same AST can be registered under two names; checking it twice is harmless because the
    // question asked is about the IMPORT, and each import is visited once per owning AST.
    private static IEnumerable<(ProgramNode Ast, string? Name)> Owners(CompilationContext context)
    {
        if (context.RootAst != null) yield return (context.RootAst, null);

        var seen = new HashSet<ProgramNode>(ReferenceEqualityComparer.Instance);
        if (context.RootAst != null) seen.Add(context.RootAst);

        foreach (var kv in context.NamedModules)
            if (seen.Add(kv.Value))
                yield return (kv.Value, kv.Key);
    }

    /// <summary>
    /// Every name <paramref name="module"/> can bind at module level, or null when the set
    /// cannot be known and no question should be asked of it.
    /// </summary>
    private static HashSet<string>? BoundNames(ProgramNode module)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var imp in module.Imports)
        {
            // A star this build never expanded could bind anything. Say nothing about a
            // module that has one.
            if (imp.Symbols.Count == 1 && imp.Symbols[0] == StarImportExpander.Star) return null;

            foreach (var sym in imp.Symbols)
                names.Add(imp.Aliases.TryGetValue(sym, out var alias) ? alias : sym);

            if (imp.Symbols.Count == 0)
                names.Add(string.IsNullOrEmpty(imp.ModuleAlias)
                    ? Head(imp.ModuleName)
                    : imp.ModuleAlias);
        }

        foreach (var fn in module.Functions) names.Add(fn.Name);

        bool guarded = false;
        foreach (var stmt in module.GlobalStatements)
            Collect(stmt, names, ref guarded);

        // A module-level `raise CompileError(...)` is a HAL saying it cannot be built for
        // this target, and none of its names exist on purpose. EmitRegularFunctionCall and
        // ResolveBinding already report that sentence at the use site, which says far more
        // than a missing name would.
        return guarded ? null : names;
    }

    // Every module-level binding, at any depth, in EVERY branch. Folding has already removed
    // the branches it could decide; whatever is left could not be decided, so a name bound in
    // any of it must not be demanded.
    private static void Collect(Statement? stmt, HashSet<string> names, ref bool guarded)
    {
        switch (stmt)
        {
            case null:
                return;

            case RaiseStmt { ErrorType: "CompileError" }:
                guarded = true;
                return;

            case FunctionDef fn:
                names.Add(fn.Name);
                return;

            case ClassDef cls:
                names.Add(cls.Name);
                return;

            case VarDecl decl:
                names.Add(decl.Name);
                return;

            case AnnAssign ann:
                names.Add(ann.Target);
                return;

            case AssignStmt assign:
                AddTarget(assign.Target, names);
                return;

            case ImportStmt imp:
                foreach (var sym in imp.Symbols)
                    names.Add(imp.Aliases.TryGetValue(sym, out var alias) ? alias : sym);
                if (imp.Symbols.Count == 0)
                    names.Add(string.IsNullOrEmpty(imp.ModuleAlias)
                        ? Head(imp.ModuleName)
                        : imp.ModuleAlias);
                return;

            case Block block:
                foreach (var inner in block.Statements) Collect(inner, names, ref guarded);
                return;

            case IfStmt ifStmt:
                Collect(ifStmt.ThenBranch, names, ref guarded);
                foreach (var (_, body) in ifStmt.ElifBranches) Collect(body, names, ref guarded);
                Collect(ifStmt.ElseBranch, names, ref guarded);
                return;

            case MatchStmt matchStmt:
                foreach (var branch in matchStmt.Branches) Collect(branch.Body, names, ref guarded);
                return;

            case TryStmt tryStmt:
                foreach (var s in tryStmt.Body) Collect(s, names, ref guarded);
                foreach (var h in tryStmt.Handlers)
                    foreach (var s in h.Handler) Collect(s, names, ref guarded);
                if (tryStmt.ElseBody != null)
                    foreach (var s in tryStmt.ElseBody) Collect(s, names, ref guarded);
                if (tryStmt.Finally != null)
                    foreach (var s in tryStmt.Finally) Collect(s, names, ref guarded);
                return;

            case WhileStmt loop:
                Collect(loop.Body, names, ref guarded);
                return;

            case ForStmt forStmt:
                Collect(forStmt.Body, names, ref guarded);
                return;
        }
    }

    private static void AddTarget(Expression? target, HashSet<string> names)
    {
        switch (target)
        {
            case VariableExpr v: names.Add(v.Name); return;
            case TupleExpr t: foreach (var e in t.Elements) AddTarget(e, names); return;
            case ListExpr l: foreach (var e in l.Elements) AddTarget(e, names); return;
        }
    }

    // `import a.b.c` binds the top name `a`.
    private static string Head(string moduleName)
    {
        int dot = moduleName.IndexOf('.');
        return dot < 0 ? moduleName : moduleName[..dot];
    }

    /// <summary>
    /// The closest name the module does bind, or "" when nothing is close enough. Two edits on
    /// a short name is already a different word and suggesting it would be noise, which is the
    /// same threshold the call-site diagnostic uses.
    /// </summary>
    private static string Nearest(IReadOnlyCollection<string> offered, string wanted)
    {
        string best = "";
        int bestDistance = int.MaxValue;
        foreach (var name in offered)
        {
            int d = string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)
                ? 0
                : EditDistance(name, wanted);
            if (d < bestDistance) { bestDistance = d; best = name; }
        }

        int allowed = Math.Max(2, wanted.Length / 3);
        return bestDistance <= allowed ? best : "";
    }

    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

}
