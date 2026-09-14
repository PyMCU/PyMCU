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

namespace PyMCU.Frontend;

public class DependencyGraphBuilder(IModuleLoader moduleLoader) : IDependencyGraphBuilder
{
    private const int MaxQueueOperations = 5000;

    public DependencyGraph Build(ProgramNode root, string rootPath, CompilationContext context)
    {
        var graph = new DependencyGraph();
        var queue = new Queue<(ProgramNode Ast, string Path)>();
        var visitedModules = new HashSet<string>();
        var operations = 0;

        queue.Enqueue((root, rootPath));
        graph.AddNode(root);

        while (queue.Count > 0)
        {
            if (++operations > MaxQueueOperations)
                throw new CompilerError("ImportError",
                    "Dependency graph exceeded maximum size. Possible circular dependency.", 0, 0);

            var (currentAst, currentPath) = queue.Dequeue();

            // Collect imports from two sources:
            //   1. Top-level ImportStmt nodes already in Imports (unconditional).
            //   2. ImportStmt nodes inside compile-time if/match blocks in
            //      GlobalStatements (conditional — only the winning branch).
            // Both are needed so that chip-specific sub-modules referenced by
            // module-level `if __CHIP__.name == "..."` guards are loaded before
            // ConditionalCompilator runs and promotes the chosen imports.
            var allImports = currentAst.Imports
                .Concat(ConditionalImportExtractor.Extract(currentAst, context.DeviceConfig))
                .ToList();

            // A worklist rather than a foreach: rewriting `from <package> import <submodule>`
            // appends the submodule's own import, and it has to be loaded in this same pass.
            for (int impIndex = 0; impIndex < allImports.Count; ++impIndex)
            {
                var imp = allImports[impIndex];
                if (BuiltinModuleNames.IsBuiltin(imp.ModuleName)) continue;

                ProgramNode importedAst;
                string importedPath;
                try
                {
                    importedAst  = moduleLoader.LoadModule(imp.ModuleName, currentPath, context, imp.Symbols);
                    importedPath = moduleLoader.ResolveModulePath(imp.ModuleName, currentPath, context, imp.Symbols);
                }
                catch (CompilerError e) when (e.File == null)
                {
                    // The loader knows what failed; only the caller knows WHERE it was
                    // written. A failed import in a module used to be printed against the
                    // entry file's line 1, a line that does not mention the module named.
                    throw new CompilerError(e.TypeName, e.Message,
                        imp.Line > 0 ? imp.Line : 1, imp.Column) { File = currentPath };
                }

                // `from m import *`: replace the star with the names m exports, now that m's
                // AST is in hand. Everything downstream binds a symbol LIST, so a star left
                // in place imported nothing at all.
                StarImportExpander.Expand(imp, importedAst);

                // `from <package> import <submodule>` -- the first line of every Adafruit
                // guide, and the layout those packages exist for. Only the names of the
                // package's own __init__.py could be imported from it, so `servo`, a module
                // of adafruit_motor, was refused as a name the package does not define
                // (#323). A name the package does not bind but DOES have a file for is that
                // file: the import is rewritten to `import <package>.<submodule> as <name>`,
                // which is what Python binds too.
                foreach (var extra in RewriteSubmoduleImports(imp, importedAst, currentAst, currentPath, context))
                    allImports.Add(extra);

                graph.AddDependencyEdge(importedAst, currentAst);

                if (visitedModules.Add(imp.ModuleName))
                    queue.Enqueue((importedAst, importedPath));
            }
        }

        return graph;
    }

    /// <summary>
    /// Turns each `from P import S` whose S is a SUBMODULE of P rather than a name P binds
    /// into `import P.S as S`, and returns the imports that adds. A name P does bind, a name
    /// with no file behind it, and a star are all left alone -- the first is an ordinary
    /// import and the other two are ImportedNameCheck's to report.
    /// </summary>
    private IEnumerable<ImportStmt> RewriteSubmoduleImports(
        ImportStmt imp, ProgramNode importedAst, ProgramNode currentAst,
        string currentPath, CompilationContext context)
    {
        if (imp.Symbols.Count == 0) yield break;
        if (imp.WasStarImport) yield break;

        // Null means the module's bindings cannot be known (a star it never expanded, or a
        // module-level CompileError). Asking nothing is the same answer the name check gives.
        var bound = ImportedNameCheck.BoundNames(importedAst);
        if (bound == null) yield break;

        foreach (var sym in imp.Symbols.ToList())
        {
            if (sym == StarImportExpander.Star || bound.Contains(sym)) continue;

            string sub = imp.ModuleName + "." + sym;
            try
            {
                moduleLoader.ResolveModulePath(sub, currentPath, context);
            }
            catch
            {
                continue;   // no file behind the name: not a submodule
            }

            string local = imp.Aliases.TryGetValue(sym, out var alias) ? alias : sym;
            imp.Symbols.Remove(sym);
            imp.Aliases.Remove(sym);

            var rewritten = new ImportStmt(sub, new List<string>(), imp.RelativeLevel)
            {
                ModuleAlias = local,
                Line = imp.Line,
                Column = imp.Column,
                InFunctionScope = imp.InFunctionScope,
            };
            currentAst.Imports.Add(rewritten);
            yield return rewritten;
        }
    }
}

