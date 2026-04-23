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

        throw new Exception($"Module not found: {moduleName}");
    }
}