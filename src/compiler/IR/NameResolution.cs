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
using PyMCU.Frontend;

namespace PyMCU.IR;

/// <summary>
/// Name resolution, in OBSERVER mode. Decides, once and before anything is lowered, which
/// binding each simple name refers to, and derives the storage key from that binding plus the
/// expansion it is being read in. It writes nothing the compiler reads: the existing ladder
/// keeps deciding, and this pass only says where the two answers differ.
///
/// Enabled with PYMCU_RESOLVE_OBSERVE=1. Off, every entry point here returns immediately and
/// the build is byte- and stderr-identical to one compiled without it.
///
/// WHY a separate pass at all. The compiler has 381 sites in 167 methods that turn a source
/// name into a storage key, under eleven different spellings, through seven implementations of
/// the same scope qualification that do not agree with each other. A name written under one
/// spelling and read under another produces two slots and the second is never written; two
/// names that collapse to one spelling share a slot. Both are silent wrong code, and both are
/// the same missing fact: nobody decided what the name IS.
///
/// The factorization that makes one function enough:
///
///     storage key = KeyOf(binding, expansion instance)
///
/// The binding answers "which declaration does this name refer to", which does not depend on
/// the expansion. The expansion instance answers "which copy of that declaration's frame",
/// which does not depend on the name. The inline prefix stops being a string 195 sites paste
/// onto a name and becomes an argument of one function.
///
/// WHAT IT CLAIMS, deliberately narrow for this step: parameters, function locals, and module
/// globals. A field, a class, a function, a builtin, a compile-time sequence element and an
/// alias are all answered <c>null</c> (Unclaimed) and never compared.
///
/// The pass must run AFTER the AST transforms that rewrite bindings -- AsyncTransform,
/// NamedtupleTransform, LoopElseDesugar and the module-level splice into main -- because they
/// add and move the very statements that bind names. It runs immediately before the lowering
/// loop, which is the first point where all of them have happened.
/// </summary>
public sealed class NameResolution
{
    public static bool Enabled
        => Environment.GetEnvironmentVariable("PYMCU_RESOLVE_OBSERVE") is "1";

    public enum Kind
    {
        /// A declared parameter of the scope.
        Param,
        /// A name the scope's body binds.
        Local,
        /// A name bound at a module's top level (including the entry file's, which the
        /// compiler splices into `main`).
        ModuleGlobal,
    }

    public sealed record Binding(Kind Kind, string Scope, string Bare, string File, int Line)
    {
        public override string ToString() => $"{Kind}:{Scope}.{Bare}";
    }

    /// <summary>One scope: a function body, or a module's top level.</summary>
    private sealed class Scope
    {
        public string Name = "";           // what the IR generator calls currentFunction
        public string ModulePrefix = "";
        public bool IsModuleLevel;
        public readonly Dictionary<string, Binding> Bound = new();
        public readonly HashSet<string> DeclaredGlobal = new();
    }

    // Keyed the way the IR generator spells currentFunction: modulePrefix + funcName.
    private readonly Dictionary<string, Scope> scopes = new();
    // Keyed modulePrefix + name, which is the spelling mutableGlobals uses.
    private readonly Dictionary<string, Binding> moduleGlobals = new();
    // Bare function name -> the scopes that spell it, so an inline prefix (which carries the
    // callee's BARE name) can be pointed at the callee's frame. A bare name several modules
    // define is ambiguous and the pass declines to answer rather than guess.
    private readonly Dictionary<string, List<Scope>> scopesByBareName = new();

    private readonly HashSet<string> reported = new();
    private readonly Dictionary<string, int> counters = new();

    private void Count(string what) => counters[what] = counters.GetValueOrDefault(what) + 1;

    // ---- building -------------------------------------------------------------------------

    /// <summary>
    /// Registers a module's top-level bindings as that module's globals. The entry file and
    /// every imported module go through here before Build, because a module's top level is
    /// not always a function: only a module that needs one gets a __module_init, and the
    /// globals of the rest would otherwise be invisible to the pass.
    /// </summary>
    public void AddModuleLevel(string prefix, ProgramNode module)
    {
        var scope = new Scope { Name = prefix, ModulePrefix = prefix, IsModuleLevel = true };
        var body = TypeInference.WalkStatements(module.GlobalStatements).ToList();
        foreach (var st in body)
            foreach (var (name, line) in BoundNames(st))
            {
                if (string.IsNullOrEmpty(name) || name == "_") continue;
                moduleGlobals.TryAdd(prefix + name,
                    new Binding(Kind.ModuleGlobal, prefix, name, "", line));
            }
    }

    public void Build(IEnumerable<FunctionEntry> functions)
    {
        foreach (var entry in functions)
        {
            string prefix = entry.Prefix ?? "";
            var scope = new Scope
            {
                Name = prefix + entry.Func.Name,
                ModulePrefix = prefix,
                // `main` of the entry file IS the module's top level: the splice puts the
                // module-level statements in its body, and every read of a name bound there
                // resolves the bare global. The same is true of a module's __module_init.
                IsModuleLevel = entry.Func.Name == "__module_init"
                    || (prefix.Length == 0 && entry.Func.Name == "main"),
            };

            foreach (var p in entry.Func.Params)
                Record(scope, p.Name, Kind.Param, entry, entry.Func.Line);

            // `global x` in a body makes every write to x the MODULE's binding, so the
            // whole body is scanned for those first: a global declared below an assignment
            // still governs that assignment.
            var body = TypeInference.WalkStatements(entry.Func.Body.Statements).ToList();
            foreach (var st in body)
                if (st is GlobalStmt g)
                    foreach (var n in g.Names) scope.DeclaredGlobal.Add(n);
            foreach (var st in body)
                foreach (var (name, line) in BoundNames(st))
                    Record(scope, name, Kind.Local, entry, line);

            if (scopes.ContainsKey(scope.Name)) continue;   // an overload set shares one name
            scopes[scope.Name] = scope;
            scopesByBareName.TryAdd(entry.Func.Name, new List<Scope>());
            scopesByBareName[entry.Func.Name].Add(scope);
        }
    }

    private void Record(Scope scope, string name, Kind kind, FunctionEntry entry, int line)
    {
        if (string.IsNullOrEmpty(name) || name == "_") return;
        if (scope.DeclaredGlobal.Contains(name)) return;

        if (scope.IsModuleLevel)
        {
            string key = scope.ModulePrefix + name;
            moduleGlobals.TryAdd(key,
                new Binding(Kind.ModuleGlobal, scope.ModulePrefix, name, entry.SourceFile, line));
            return;
        }

        scope.Bound.TryAdd(name,
            new Binding(kind, scope.Name, name, entry.SourceFile, line));
    }

    /// <summary>Every simple name a statement binds, with the line it binds it on.</summary>
    private static IEnumerable<(string Name, int Line)> BoundNames(Statement st)
    {
        switch (st)
        {
            case VarDecl vd:
                yield return (vd.Name, vd.Line);
                break;
            case AnnAssign an:
                yield return (an.Target, an.Line);
                break;
            case AssignStmt a when a.Target is VariableExpr av:
                yield return (av.Name, a.Line);
                break;
            case AugAssignStmt aug when aug.Target is VariableExpr augv:
                yield return (augv.Name, aug.Line);
                break;
            case TupleUnpackStmt tu:
                foreach (var t in tu.Targets) yield return (t, tu.Line);
                break;
            case ForStmt f:
                yield return (f.VarName, f.Line);
                if (!string.IsNullOrEmpty(f.Var2Name)) yield return (f.Var2Name, f.Line);
                break;
            case WithStmt w when !string.IsNullOrEmpty(w.AsName):
                yield return (w.AsName, w.Line);
                break;
            case TryStmt t2:
                for (int i = 0; i < t2.Handlers.Count; i++)
                    if (t2.BoundName(i) is { Length: > 0 } bound)
                        yield return (bound, t2.Line);
                break;
            case MatchStmt m:
                foreach (var branch in m.Branches)
                {
                    if (!string.IsNullOrEmpty(branch.CaptureName))
                        yield return (branch.CaptureName, m.Line);
                    // A class pattern's sub-patterns bind too: `case P(x=px)` binds px, and
                    // `case P(a, b)` binds a and b through __match_args__.
                    if (branch.Pattern is CallExpr pat)
                        foreach (var arg in pat.Args)
                        {
                            if (arg is KeywordArgExpr kw && kw.Value is VariableExpr kv)
                                yield return (kv.Name, m.Line);
                            else if (arg is VariableExpr pv)
                                yield return (pv.Name, m.Line);
                        }
                }
                break;
        }
    }

    // ---- resolving ------------------------------------------------------------------------

    /// <summary>The eleven spellings of 1.5 of the front-end report, named.</summary>
    public static string SpellingOf(string key, string modulePrefix, string function,
                                    string inlinePrefix)
    {
        if (key.Length == 0) return "empty";
        if (inlinePrefix.Length > 0 && key.StartsWith(inlinePrefix, StringComparison.Ordinal))
            return "inline-prefix";
        if (key.StartsWith("__lam", StringComparison.Ordinal)) return "lambda-prefix";
        if (key.Contains('|')) return "class-member";
        if (key.Contains("__") && char.IsDigit(key[^1])) return "sequence-element";
        if (function.Length > 0
            && key.StartsWith(function + ".", StringComparison.Ordinal)) return "function-qualified";
        if (modulePrefix.Length > 0
            && key.StartsWith(modulePrefix, StringComparison.Ordinal)) return "module-qualified";
        if (key.StartsWith("tmp_", StringComparison.Ordinal)) return "temporary";
        if (key.Contains('.')) return "other-dotted";
        if (key.Contains('_')) return "flattened-field";
        return "bare";
    }

    /// <summary>
    /// The key <paramref name="bare"/> resolves to, or null when this pass does not claim the
    /// name (a field, a class, a function, a builtin, a compile-time binding).
    /// </summary>
    public string? Resolve(string modulePrefix, string function, string inlinePrefix, string bare)
    {
        // 1. The frame of the expansion being lowered. An @inline body is visited once per
        //    call site over the SAME AST nodes, so the binding is the callee's and the key
        //    is that binding plus this expansion instance.
        if (inlinePrefix.Length > 0 && CalleeScopeOf(inlinePrefix) is { } callee)
        {
            if (callee.Bound.ContainsKey(bare)) return inlinePrefix + bare;
        }

        // 2. The enclosing function's own frame.
        if (function.Length > 0 && scopes.TryGetValue(function, out var own))
        {
            if (own.IsModuleLevel)
            {
                // "main" IS the module's top level, so a name bound there is the global.
                if (moduleGlobals.ContainsKey(own.ModulePrefix + bare))
                    return own.ModulePrefix + bare;
            }
            else if (own.Bound.ContainsKey(bare))
            {
                return function + "." + bare;
            }
            else if (own.DeclaredGlobal.Contains(bare))
            {
                return own.ModulePrefix + bare;
            }
        }

        // 3. A global of the module being lowered, then of the entry file.
        if (moduleGlobals.ContainsKey(modulePrefix + bare)) return modulePrefix + bare;
        if (moduleGlobals.ContainsKey(bare)) return bare;

        return null;
    }

    /// <summary>
    /// The callee frame an inline prefix names. The dominant spelling is
    /// <c>inline&lt;N&gt;.&lt;callee&gt;.</c>; the three others the compiler also produces
    /// (the super-call form with underscores, the lambda form, the property-setter form) are
    /// counted and declined rather than guessed at.
    /// </summary>
    private Scope? CalleeScopeOf(string inlinePrefix)
    {
        string p = inlinePrefix.TrimEnd('.');
        int dot = p.LastIndexOf('.');
        if (dot < 0) { Count("prefix-unparsed"); return null; }
        string bare = p[(dot + 1)..];
        if (!scopesByBareName.TryGetValue(bare, out var candidates))
        {
            Count("prefix-callee-unknown");
            return null;
        }
        if (candidates.Count != 1) { Count("prefix-callee-ambiguous"); return null; }
        return candidates[0];
    }

    // ---- observing ------------------------------------------------------------------------

    /// <summary>
    /// Compare one decision of the ladder against this pass's. Records nothing when the pass
    /// does not claim the name, and reports each distinct (site, name, both answers) once.
    /// </summary>
    public void Observe(string site, string modulePrefix, string function, string inlinePrefix,
                        string bare, string ladderKey, string file, int line)
    {
        Count("observed");
        string? mine = Resolve(modulePrefix, function, inlinePrefix, bare);
        if (mine == null) { Count("unclaimed"); return; }
        if (mine == ladderKey) { Count("agreed"); return; }

        Count("discrepancy");
        string ladderSpelling = SpellingOf(ladderKey, modulePrefix, function, inlinePrefix);
        string mineSpelling = SpellingOf(mine, modulePrefix, function, inlinePrefix);
        string signature = $"{site}|{bare}|{ladderKey}|{mine}";
        if (!reported.Add(signature)) return;

        Logger.Warning("resolve-observe",
            $"[{site}] {file}:{line}: '{bare}' ladder={ladderKey} ({ladderSpelling}) "
            + $"resolution={mine} ({mineSpelling}) "
            + $"scope=fn:{(function.Length == 0 ? "-" : function)} "
            + $"mod:{(modulePrefix.Length == 0 ? "-" : modulePrefix)} "
            + $"inline:{(inlinePrefix.Length == 0 ? "-" : inlinePrefix)}");
    }

    /// <summary>The run's totals, printed once at the end of IR generation.</summary>
    public void ReportTotals()
    {
        var parts = counters.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}");
        Logger.Warning("resolve-observe",
            $"[totals] scopes={scopes.Count} globals={moduleGlobals.Count} "
            + $"distinct={reported.Count} " + string.Join(" ", parts));
    }
}
