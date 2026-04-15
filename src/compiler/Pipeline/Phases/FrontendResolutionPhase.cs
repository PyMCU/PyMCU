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

namespace PyMCU.Pipeline.Phases;

public class FrontendResolutionPhase : ICompilerPhase
{
    public string Name => "Semantic & Dependency Resolution";
    private const int MaxIterations = 100;

    public void Execute(CompilationContext context)
    {
        if (context.RootAst == null)
        {
            context.HasErrors = true;
            return;
        }

        try
        {
            LoadImportsRecursively(context.RootAst, context, context.Options.FilePath);

            var preScanner = new PreScanVisitor(context.Config);
            preScanner.Scan(context.RootAst);
            foreach (var modAst in context.NamedModules.Values)
            {
                preScanner.Scan(modAst);
            }

            if (string.IsNullOrEmpty(context.Config.Chip)) context.Config.Chip = context.Options.Arch;
            if (string.IsNullOrEmpty(context.Config.Arch)) context.Config.Arch = context.Options.Arch;

            var conditional = new ConditionalCompilator(context.Config);
            conditional.Process(context.RootAst);

            var ccProcessed = new HashSet<ProgramNode> { context.RootAst };
            foreach (var modAst in context.NamedModules.Values)
            {
                if (ccProcessed.Add(modAst))
                {
                    conditional.Process(modAst);
                }
            }

            ResolveIteratively(context, conditional, ccProcessed);
        }
        catch (CompilerError e)
        {
            Diagnostic.Report(e, context.SourceCode, context.Options.FilePath);
            context.HasErrors = true;
        }
    }

    private void ResolveIteratively(CompilationContext context, ConditionalCompilator conditional, HashSet<ProgramNode> ccProcessed)
    {
        var anyNew = true;
        var iteration = 0;

        while (anyNew && iteration < MaxIterations)
        {
            anyNew = false;
            iteration++;

            foreach (var modAst in context.NamedModules.Values.ToList())
            {
                if (!ccProcessed.Add(modAst)) continue;
                conditional.Process(modAst);
                anyNew = true;
            }

            var beforeCount = context.NamedModules.Count;

            LoadImportsRecursively(context.RootAst!, context, context.Options.FilePath);
            foreach (var modAst in context.NamedModules.Values.ToList())
            {
                LoadImportsRecursively(modAst, context, context.Options.FilePath);
            }

            if (context.NamedModules.Count > beforeCount)
            {
                anyNew = true;
            }
        }

        if (iteration >= MaxIterations)
        {
            throw new CompilerError("SemanticError", "The iteration limit was exceeded in dependency resolution. Check for circular macros.", 0, 0);
        }
    }

    private void LoadImportsRecursively(ProgramNode ast, CompilationContext context, string currentPath)
    {
        foreach (var imp in ast.Imports)
        {
            if (imp.ModuleName is "pymcu.types" or "pymcu.chips") continue;

            string path = ResolveModule(imp.ModuleName, context.IncludePaths, currentPath, imp.RelativeLevel);

            if (context.ModuleCache.TryGetValue(path, out var cachedAst))
            {
                context.NamedModules[imp.ModuleName] = cachedAst;
                continue;
            }

            if (context.LoadingModules.Contains(path)) continue;

            Console.WriteLine($"[pymcuc] Loading module: {path}");
            context.LoadingModules.Add(path);

            var src = File.ReadAllText(path);
            context.ModuleSourceLines[imp.ModuleName] = new List<string>(File.ReadAllLines(path));

            var lexer = new Lexer(src);
            var parser = new Parser(lexer.Tokenize());
            var modAst = parser.ParseProgram();

            context.ModuleCache[path] = modAst;
            context.NamedModules[imp.ModuleName] = modAst;

            LoadImportsRecursively(modAst, context, path);

            context.LoadingModules.Remove(path);
        }
    }

    private static string ResolveModule(string moduleName, List<string> includePaths, string currentFilePath,
        int relativeLevel)
    {
        var pathRel = moduleName.Replace('.', Path.DirectorySeparatorChar);

        if (relativeLevel > 0)
        {
            var searchDir = Path.GetDirectoryName(currentFilePath) ?? string.Empty;

            for (var i = 1; i < relativeLevel; i++)
            {
                searchDir = Path.GetDirectoryName(searchDir) ?? string.Empty;
            }

            var fullPath = Path.Combine(searchDir, pathRel + ".py");
            if (File.Exists(fullPath)) return fullPath;

            fullPath = Path.Combine(searchDir, pathRel, "__init__.py");
            return File.Exists(fullPath) ? fullPath : throw new Exception($"Relative import not found: {fullPath}");
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