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
using PyMCU.Common.Abstractions;
using PyMCU.Frontend;

namespace PyMCU.Infrastructure;

public class FileSystemModuleLoader : IModuleLoader
{
    public string ResolveModulePath(string moduleName, string currentFilePath, CompilationContext context,
                                    IReadOnlyList<string>? importedSymbols = null)
    {
        return ResolveModulePath(moduleName, context.IncludePaths, currentFilePath, 0, importedSymbols);
    }

    public ProgramNode LoadModule(string moduleName, string currentFilePath, CompilationContext context,
                                  IReadOnlyList<string>? importedSymbols = null)
    {
        string path;
        try
        {
            path = ResolveModulePath(moduleName, context.IncludePaths, currentFilePath, 0, importedSymbols);
        }
        catch (Exception ex)
        {
            throw new CompilerError("ImportError", ex.Message, 0, 0);
        }

        RecordProjectModule(moduleName, path, context);

        // Before the cache check, so a file imported under two qualified names records a path
        // under BOTH. IR generation looks this up by whichever name it is lowering under.
        context.ModulePaths[moduleName] = path;

        if (context.ModuleCache.TryGetValue(path, out var cachedAst))
        {
            // Register the module under the requested name even on a cache hit.
            // The same physical file can be imported under different qualified names
            // (e.g. "time" via `import time` and "pymcu.time" via
            // `from pymcu.time import …`).  Without this, the second name is never
            // added to NamedModules, which causes a KeyNotFoundException in
            // FrontendResolutionPhase when it tries context.NamedModules[imp.ModuleName].
            context.NamedModules[moduleName] = cachedAst;
            return cachedAst;
        }

        if (context.LoadingModules.Contains(path))
        {
            throw new CompilerError("ImportError", $"Attempt to concurrent cyclic load: {path}", 0, 0);
        }

        Logger.Verbose("pymcuc", $"I/O: Loading {path}");
        context.LoadingModules.Add(path);

        var src = File.ReadAllText(path);
        context.ModuleSourceLines[moduleName] = new List<string>(File.ReadAllLines(path));

        // PYMCU_PY_PARSER=1 builds the same AST with CPython's parser instead of ours.
        ProgramNode modAst;
        if (PythonAstReader.Enabled)
        {
            modAst = PythonAstReader.ParseFile(path);
        }
        else
        {
            var lexer = new Lexer(src);
            var parser = new Parser(lexer.Tokenize());
            modAst = parser.ParseProgram();
        }

        // A relative import is resolved against the file it is WRITTEN in, so it has to be
        // rewritten here, once, while that file is still the subject. Later phases call the
        // loader with the entry file as `currentFilePath`, which is the wrong package.
        RelativeImportResolver.Rewrite(modAst, path, context.IncludePaths);

        // Same check the entry file gets. It used to be a post-hoc collision test on emitted
        // IR functions, which an imported module's @inline/plain pair never reached.
        DuplicateDefinitionCheck.Check(modAst, path);

        context.ModuleCache[path] = modAst;
        context.NamedModules[moduleName] = modAst;
        context.LoadingModules.Remove(path);

        return modAst;
    }

    /// <summary>
    /// Record a module as the user's own when its file sits in the project's own source tree.
    /// Everything else is an installed distribution: the pymcu stdlib, and the compat layers
    /// that provide `machine`, `board` and `busio`.
    ///
    /// Two roots, because the driver stages the entry file: it compiles `dist/_generated/main.py`
    /// while the imports still resolve out of `src/`, so the entry file's own directory does not
    /// cover both layouts. The driver names the second one with --project-root. Guessing it from
    /// the include paths instead does not work: a direct pymcuc invocation orders -I however it
    /// likes, and taking the first one made the STDLIB the project and ran its module level.
    /// </summary>
    private static void RecordProjectModule(string moduleName, string path, CompilationContext context)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in new[] { Path.GetDirectoryName(Path.GetFullPath(context.Options.FilePath)),
                                     string.IsNullOrEmpty(context.Options.ProjectRoot)
                                         ? null
                                         : Path.GetFullPath(context.Options.ProjectRoot) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                context.ProjectModules.Add(moduleName);
                return;
            }
        }
    }

    private static string ResolveModulePath(string moduleName, List<string> includePaths, string currentFilePath,
                                            int relativeLevel, IReadOnlyList<string>? importedSymbols = null)
    {
        // Builtin module aliases: bare `import asyncio` / `math` / `random` / `time` resolve
        // to the pymcu stdlib file of the same name. The module still registers under the bare
        // name (its symbols mangle as asyncio_*), only the FILE lookup is redirected -- and the
        // redirect is a FALLBACK, tried after the project's own files, so a local math.py still
        // wins the way it does in Python.
        var pathRel = moduleName.Replace('.', Path.DirectorySeparatorChar);

        if (relativeLevel > 0)
        {
            var searchDir = Path.GetDirectoryName(currentFilePath) ?? string.Empty;
            for (var i = 1; i < relativeLevel; i++) searchDir = Path.GetDirectoryName(searchDir) ?? string.Empty;

            var fullPathRel = Path.Combine(searchDir, pathRel + ".py");
            if (File.Exists(fullPathRel)) return fullPathRel;

            fullPathRel = Path.Combine(searchDir, pathRel, "__init__.py");
            return File.Exists(fullPathRel) ? fullPathRel : throw new Exception($"Relative import not found: {fullPathRel}");
        }

        foreach (var baseDir in includePaths)
        {
            var fullPath = Path.Combine(baseDir, pathRel + ".py");
            if (File.Exists(fullPath)) return fullPath;

            fullPath = Path.Combine(baseDir, pathRel, "__init__.py");
            if (File.Exists(fullPath)) return fullPath;
        }

        // The same module under the name every Python program types. `import math` used to
        // report "Module not found: math -- install it with `pymcu install math`", advice
        // nobody could follow: math is not a library, it is the stdlib under another name.
        if (!moduleName.Contains('.'))
        {
            var stdlibRel = Path.Combine("pymcu", pathRel);
            foreach (var baseDir in includePaths)
            {
                var fullPath = Path.Combine(baseDir, stdlibRel + ".py");
                if (File.Exists(fullPath)) return fullPath;

                fullPath = Path.Combine(baseDir, stdlibRel, "__init__.py");
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        // `board` is not shipped by any package: the driver GENERATES it from the board the
        // project declares. Sending the reader to check the compat package and the stdlib list
        // is advice they have already followed -- both are usually right, and the missing piece
        // is the board line.
        if (moduleName == "board")
            throw new Exception(
                "Module not found: board -- it is generated from the board this project " +
                "declares, not shipped by a package. Set board = \"arduino_uno\" (or your " +
                "board) under [tool.pymcu] in pyproject.toml; with only target = \"...\" there " +
                "is no board to generate it from.");

        // Compat-flavor modules: point at the fix instead of a bare not-found.
        var flavorHint = moduleName switch
        {
            "machine" or "utime" or "micropython" or "network" or "rp2"
                => "micropython",
            // `neopixel` is deliberately absent: it ships as a library now, and
            // sending someone to the circuitpython package for it is advice that
            // no longer installs anything.
            "board" or "digitalio" or "analogio" or "busio" or "pwmio"
             or "microcontroller" or "supervisor" or "wifi" or "socketpool" or "alarm"
                => "circuitpython",
            _ => null,
        };
        // Two different things produce this, and the loader cannot tell them
        // apart: the flavor may not be declared, or it may be declared and
        // simply not installed in the project's environment. Saying "add
        // stdlib = [...]" outright sent a user to re-add a line that was
        // already there, so name both checks instead of guessing.
        if (flavorHint is not null)
            throw new Exception(
                $"Module not found: {moduleName} -- this module comes from the {flavorHint} " +
                $"compat package. Check that stdlib = [\"{flavorHint}\"] is set under " +
                $"[tool.pymcu] in pyproject.toml, and that pymcu-{flavorHint} is installed " +
                $"in the project's environment (uv sync, or pip install -r requirements.txt)");

        // The name resolves to a DIRECTORY with no __init__.py — a namespace package, which
        // `pymcu` itself is. It is present, so "Module not found" contradicts the imports of
        // its submodules two lines above, and `pymcu install pymcu` names a command that
        // leads nowhere. Say the package is there and where the name being imported lives.
        {
            var pkgDir = includePaths
                .Select(baseDir => Path.Combine(baseDir, pathRel))
                .FirstOrDefault(Directory.Exists);

            if (pkgDir != null)
            {
                string want = importedSymbols is { Count: > 0 } ? importedSymbols[0] : "";
                string where = want.Length > 0 ? FindSubmoduleDefining(pkgDir, moduleName, want) : "";

                if (where.Length > 0)
                    throw new Exception(
                        $"cannot import '{want}' from '{moduleName}': '{moduleName}' is a package "
                        + $"with no module of its own, and its names live in its submodules. "
                        + $"'{want}' is defined in '{where}' -- write "
                        + $"`from {where} import {want}`.");

                var subs = SubmoduleNames(pkgDir, moduleName);
                string list = subs.Count > 0
                    ? $" Its submodules are {string.Join(", ", subs.Take(8))}"
                      + (subs.Count > 8 ? ", ..." : "") + "."
                    : "";
                throw new Exception(
                    (want.Length > 0
                        ? $"cannot import '{want}' from '{moduleName}': no submodule of "
                          + $"'{moduleName}' defines it. "
                        : "")
                    + $"'{moduleName}' is a package with no module of its own, so an import must "
                    + $"name a submodule (`from {moduleName}.<submodule> import ...`)."
                    + list);
            }
        }

        // Anything else with no dots is a plain top-level import, which is what a
        // third-party library provides. The name is the one people type.
        if (!moduleName.Contains('.'))
            throw new Exception(
                $"Module not found: {moduleName} -- if it is a PyMCU library, install it " +
                $"into this project with `pymcu install {moduleName}`");

        throw new Exception($"Module not found: {moduleName}");
    }

    /// <summary>
    /// The dotted name of the submodule of <paramref name="pkgDir"/> that defines
    /// <paramref name="symbol"/> at top level, or "" when none does. Only ever called on the
    /// import-failed path, so a plain textual scan is cheap enough and needs no parser.
    /// </summary>
    private static string FindSubmoduleDefining(string pkgDir, string packageName, string symbol)
    {
        foreach (var file in Directory.EnumerateFiles(pkgDir, "*.py").OrderBy(f => f))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (stem == "__init__") continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            foreach (var raw in lines)
            {
                // Top-level only: an indented `def` is a method, not an export.
                if (raw.Length == 0 || char.IsWhiteSpace(raw[0])) continue;
                var line = raw.TrimEnd();
                if (line.StartsWith($"def {symbol}(", StringComparison.Ordinal)
                    || line.StartsWith($"class {symbol}(", StringComparison.Ordinal)
                    || line.StartsWith($"class {symbol}:", StringComparison.Ordinal)
                    || line.StartsWith($"{symbol} =", StringComparison.Ordinal)
                    || line.StartsWith($"{symbol}:", StringComparison.Ordinal)
                    || line.StartsWith($"{symbol}=", StringComparison.Ordinal))
                    return $"{packageName}.{stem}";
            }
        }

        return "";
    }

    /// <summary>The importable submodule names directly inside a package directory.</summary>
    private static List<string> SubmoduleNames(string pkgDir, string packageName)
    {
        var names = new List<string>();

        foreach (var file in Directory.EnumerateFiles(pkgDir, "*.py"))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (stem != "__init__") names.Add($"{packageName}.{stem}");
        }

        foreach (var dir in Directory.EnumerateDirectories(pkgDir))
        {
            string stem = Path.GetFileName(dir);
            if (!stem.StartsWith("__", StringComparison.Ordinal)) names.Add($"{packageName}.{stem}");
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }
}