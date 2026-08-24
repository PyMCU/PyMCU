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
    public string ResolveModulePath(string moduleName, string currentFilePath, CompilationContext context)
    {
        return ResolveModulePath(moduleName, context.IncludePaths, currentFilePath, 0);
    }

    public ProgramNode LoadModule(string moduleName, string currentFilePath, CompilationContext context)
    {
        string path;
        try
        {
            path = ResolveModulePath(moduleName, context.IncludePaths, currentFilePath, 0);
        }
        catch (Exception ex)
        {
            throw new CompilerError("ImportError", ex.Message, 0, 0);
        }

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

        var lexer = new Lexer(src);
        var parser = new Parser(lexer.Tokenize());
        var modAst = parser.ParseProgram();

        context.ModuleCache[path] = modAst;
        context.NamedModules[moduleName] = modAst;
        context.LoadingModules.Remove(path);

        return modAst;
    }

    private static string ResolveModulePath(string moduleName, List<string> includePaths, string currentFilePath, int relativeLevel)
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

        // Anything else with no dots is a plain top-level import, which is what a
        // third-party library provides. The name is the one people type.
        if (!moduleName.Contains('.'))
            throw new Exception(
                $"Module not found: {moduleName} -- if it is a PyMCU library, install it " +
                $"into this project with `pymcu install {moduleName}`");

        throw new Exception($"Module not found: {moduleName}");
    }
}