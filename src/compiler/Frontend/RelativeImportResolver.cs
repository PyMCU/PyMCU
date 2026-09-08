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
/// Rewrites relative imports (`from .util import half`, `from . import util`) into the
/// absolute dotted name the rest of the compiler already handles, using the importing
/// file's position under the include roots to name its package.
///
/// The leading dot used to be counted by the parser and then dropped: the loader was
/// always called with level 0, so `.util` was looked up as a top-level `util` and
/// `from . import util` as the empty module name. Both spellings of the same import must
/// reach the dependency graph identical, because module keys and symbol mangling are
/// derived from the name string.
///
/// Runs once per file, right after the file is parsed, so every later phase (the graph
/// builder, the conditional-import extractor, the IR generator) sees absolute names only.
/// </summary>
public static class RelativeImportResolver
{
    /// <summary>
    /// Rewrites every relative ImportStmt in <paramref name="ast"/> in place.
    /// <paramref name="filePath"/> is the file the AST was parsed from, and
    /// <paramref name="includePaths"/> the roots a module name is resolved against.
    /// </summary>
    public static void Rewrite(ProgramNode ast, string filePath, IReadOnlyList<string> includePaths)
    {
        RewriteList(ast.Imports, filePath, includePaths);

        foreach (var stmt in ast.GlobalStatements)
            RewriteInStatement(stmt, filePath, includePaths);

        foreach (var fn in ast.Functions)
            RewriteInStatement(fn.Body, filePath, includePaths);
    }

    // Conditional imports live inside if/match blocks in GlobalStatements and are promoted
    // later; an import inside a function body is legal Python too. Both are rewritten here
    // so no spelling of a relative import survives into a later phase.
    private static void RewriteInStatement(Statement? stmt, string filePath,
                                           IReadOnlyList<string> includePaths)
    {
        switch (stmt)
        {
            case null:
                return;

            case Block block:
                RewriteStatements(block.Statements, filePath, includePaths);
                return;

            case IfStmt ifStmt:
                RewriteInStatement(ifStmt.ThenBranch, filePath, includePaths);
                foreach (var (_, body) in ifStmt.ElifBranches)
                    RewriteInStatement(body, filePath, includePaths);
                RewriteInStatement(ifStmt.ElseBranch, filePath, includePaths);
                return;

            case MatchStmt matchStmt:
                foreach (var branch in matchStmt.Branches)
                    RewriteInStatement(branch.Body, filePath, includePaths);
                return;

            case WhileStmt whileStmt:
                RewriteInStatement(whileStmt.Body, filePath, includePaths);
                return;

            case ForStmt forStmt:
                RewriteInStatement(forStmt.Body, filePath, includePaths);
                return;

            case TryStmt tryStmt:
                RewriteStatements(tryStmt.Body, filePath, includePaths);
                foreach (var handler in tryStmt.Handlers)
                    RewriteStatements(handler.Handler, filePath, includePaths);
                RewriteStatements(tryStmt.ElseBody, filePath, includePaths);
                RewriteStatements(tryStmt.Finally, filePath, includePaths);
                return;

            case FunctionDef fn:
                RewriteInStatement(fn.Body, filePath, includePaths);
                return;

            case ClassDef cls:
                RewriteInStatement(cls.Body, filePath, includePaths);
                return;
        }
    }

    // A plain statement list (a Block's body, a try/except arm): rewrite the imports it holds
    // directly, then recurse into everything else.
    private static void RewriteStatements(List<Statement>? statements, string filePath,
                                          IReadOnlyList<string> includePaths)
    {
        if (statements == null) return;
        RewriteList(statements, filePath, includePaths);
        foreach (var inner in statements)
            RewriteInStatement(inner, filePath, includePaths);
    }

    // Rewrites the relative imports in a statement list, splicing in the extra nodes that
    // `from . import a, b` expands to. The list is typed loosely so the same code serves
    // ProgramNode.Imports (List<ImportStmt>) and a Block's List<Statement>.
    private static void RewriteList<T>(List<T> statements, string filePath,
                                       IReadOnlyList<string> includePaths) where T : Statement
    {
        for (int i = 0; i < statements.Count; i++)
        {
            if (statements[i] is not ImportStmt imp) continue;
            if (imp.RelativeLevel <= 0 && !WantsSubmoduleFallback(imp, includePaths)) continue;

            var expanded = imp.RelativeLevel > 0
                ? ResolveOne(imp, filePath, includePaths)
                : ResolveAbsoluteSubmodules(imp, includePaths);
            statements[i] = (T)(Statement)expanded[0];
            for (int k = 1; k < expanded.Count; k++)
                statements.Insert(++i, (T)(Statement)expanded[k]);
        }
    }

    /// <summary>
    /// Turns one relative import into the equivalent absolute import(s). `from .util import
    /// half` becomes `from pkg.util import half`; `from . import util` becomes `import
    /// pkg.util as util`, which is what binding a submodule under its bare name means.
    /// </summary>
    private static List<ImportStmt> ResolveOne(ImportStmt imp, string filePath,
                                               IReadOnlyList<string> includePaths)
    {
        string package = PackageOf(imp, filePath, includePaths);

        // `from .sub.mod import names` -- the ordinary form. One node, absolute name.
        if (imp.ModuleName.Length > 0)
        {
            imp.ModuleName = package.Length > 0 ? package + "." + imp.ModuleName : imp.ModuleName;
            imp.RelativeLevel = 0;
            return [imp];
        }

        // `from . import a, b`. Each name is either a submodule of the package (bind it as a
        // module under its own name) or a name defined in the package's __init__ (an ordinary
        // symbol import from the package).
        if (package.Length == 0)
            throw ImportedFrom(imp, filePath,
                $"relative import '{Spell(imp)}' names the sources root itself, which is not a "
                + "package; import the module directly (`import "
                + string.Join(", ", imp.Symbols) + "`)");

        var result = new List<ImportStmt>();
        var plainSymbols = new List<string>();

        foreach (var sym in imp.Symbols)
        {
            if (!IsSubmoduleOf(package, sym, includePaths))
            {
                plainSymbols.Add(sym);
                continue;
            }

            var asModule = new ImportStmt(package + "." + sym, new List<string>())
            {
                Line = imp.Line,
                ModuleAlias = imp.Aliases.TryGetValue(sym, out var alias) ? alias : sym,
            };
            result.Add(asModule);
        }

        if (plainSymbols.Count > 0 || result.Count == 0)
        {
            imp.ModuleName = package;
            imp.RelativeLevel = 0;
            imp.Symbols.Clear();
            imp.Symbols.AddRange(plainSymbols);
            result.Insert(0, imp);
        }

        return result;
    }

    /// <summary>
    /// The dotted name of the package a relative import at <paramref name="imp"/>'s level
    /// resolves against, or "" for the sources root itself.
    /// </summary>
    private static string PackageOf(ImportStmt imp, string filePath, IReadOnlyList<string> includePaths)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));

        // Level 1 is the directory holding the file; each extra dot climbs one more.
        for (int i = 1; i < imp.RelativeLevel && dir != null; i++)
            dir = Path.GetDirectoryName(dir);

        string? root = dir == null ? null : RootContaining(dir, includePaths);
        if (root == null)
            throw ImportedFrom(imp, filePath,
                $"relative import '{Spell(imp)}' goes above the sources root "
                + $"({imp.RelativeLevel} leading dots from '{Path.GetFileName(filePath)}'); "
                + "write the absolute path from the sources root instead");

        string rel = Path.GetRelativePath(root, dir!);
        if (rel == "." || rel.Length == 0) return "";
        return rel.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
    }

    // The include root that contains dir, longest first so a nested root wins over the
    // parent it sits under (the project's own sources root is added before the stdlib's).
    private static string? RootContaining(string dir, IReadOnlyList<string> includePaths)
    {
        string? best = null;
        foreach (var raw in includePaths)
        {
            string root;
            try { root = Path.GetFullPath(raw); }
            catch { continue; }

            if (!IsSameOrUnder(dir, root)) continue;
            if (best == null || root.Length > best.Length) best = root;
        }
        return best;
    }

    private static bool IsSameOrUnder(string dir, string root)
    {
        string a = dir.TrimEnd(Path.DirectorySeparatorChar);
        string b = root.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        return a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// `from pkg import sub`, where `sub` is a submodule file rather than a name the package's
    /// own module defines (PyMCU#264).
    ///
    /// CPython imports `pkg`, fails the attribute lookup, and falls back to importing
    /// `pkg.sub`. That fallback is the reason an EMPTY `__init__.py` is a working layout, and
    /// it is the default in the Adafruit wheels -- `adafruit_bus_device/__init__.py` and
    /// `adafruit_bme280/__init__.py` are both 0 bytes. Without it the import failed with "the
    /// module was found and does not define that name", which is true and useless: the module
    /// really is empty, and the name really is a file sitting next to it.
    ///
    /// Rewritten to `import pkg.sub as sub`, which already worked before this existed, using
    /// the same IsSubmoduleOf the relative branch has always used. No new resolution: the two
    /// spellings now reach the loader identically, which is the property the class docstring
    /// asks for.
    ///
    /// DELIBERATELY NARROWER THAN CPYTHON. The rewrite applies only when the package's own
    /// module does not so much as MENTION the name (PackageModuleBinds). CPython prefers a
    /// name the `__init__` binds and falls back to the submodule only if that fails; deciding
    /// that precedence here would need the package's AST, which is not loaded yet at this
    /// phase. Refusing to rewrite whenever the name appears in the `__init__` at all keeps
    /// every resolution that works today working, at the cost of not fixing a package that
    /// both binds `sub` and ships `sub.py`. No library measured needs that case, and widening
    /// it later is a decision with its own evidence rather than a side effect of this one.
    /// </summary>
    private static List<ImportStmt> ResolveAbsoluteSubmodules(ImportStmt imp,
                                                              IReadOnlyList<string> includePaths)
    {
        var result = new List<ImportStmt>();
        var plainSymbols = new List<string>();

        foreach (var sym in imp.Symbols)
        {
            if (!IsSubmoduleOf(imp.ModuleName, sym, includePaths)
                || PackageModuleBinds(imp.ModuleName, sym, includePaths))
            {
                plainSymbols.Add(sym);
                continue;
            }

            result.Add(new ImportStmt(imp.ModuleName + "." + sym, new List<string>())
            {
                Line = imp.Line,
                ModuleAlias = imp.Aliases.TryGetValue(sym, out var alias) ? alias : sym,
            });
        }

        if (plainSymbols.Count > 0 || result.Count == 0)
        {
            imp.Symbols.Clear();
            imp.Symbols.AddRange(plainSymbols);
            result.Insert(0, imp);
        }

        return result;
    }

    // Cheap pre-check so the common case -- every `from x import y` in the program that is not
    // a package submodule -- costs one IsSubmoduleOf and no rewrite.
    private static bool WantsSubmoduleFallback(ImportStmt imp, IReadOnlyList<string> includePaths)
    {
        if (imp.ModuleName.Length == 0 || imp.Symbols.Count == 0) return false;
        foreach (var sym in imp.Symbols)
            if (sym != StarImportExpander.Star
                && IsSubmoduleOf(imp.ModuleName, sym, includePaths)
                && !PackageModuleBinds(imp.ModuleName, sym, includePaths))
                return true;
        return false;
    }

    // Does the package's own `__init__.py` so much as mention `name`? Deliberately a substring
    // test rather than a parse: a false "yes" only means the import keeps the behaviour it has
    // today, and the file is not loaded at this phase. An empty or docstring-only `__init__`,
    // which is what the wheels ship, answers no.
    private static bool PackageModuleBinds(string package, string name,
                                           IReadOnlyList<string> includePaths)
    {
        string rel = Path.Combine(package.Replace('.', Path.DirectorySeparatorChar), "__init__.py");
        foreach (var baseDir in includePaths)
        {
            string init = Path.Combine(baseDir, rel);
            if (!File.Exists(init)) continue;
            try { return File.ReadAllText(init).Contains(name, StringComparison.Ordinal); }
            catch (IOException) { return true; }
        }
        return false;
    }

    private static bool IsSubmoduleOf(string package, string name, IReadOnlyList<string> includePaths)
    {
        string rel = Path.Combine(package.Replace('.', Path.DirectorySeparatorChar), name);
        foreach (var baseDir in includePaths)
        {
            if (File.Exists(Path.Combine(baseDir, rel + ".py"))) return true;
            if (File.Exists(Path.Combine(baseDir, rel, "__init__.py"))) return true;
        }
        return false;
    }

    // The import as the user wrote it, so the message quotes their line rather than a
    // normalised form they would have to translate back.
    private static string Spell(ImportStmt imp)
    {
        string dots = new string('.', imp.RelativeLevel);
        string what = imp.Symbols.Count > 0 ? string.Join(", ", imp.Symbols) : "...";
        return $"from {dots}{imp.ModuleName} import {what}";
    }

    private static CompilerError ImportedFrom(ImportStmt imp, string filePath, string message)
        => new("ImportError", message, imp.Line > 0 ? imp.Line : 1, 1) { File = filePath };
}
