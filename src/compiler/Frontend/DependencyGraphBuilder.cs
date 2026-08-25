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
                .Concat(ConditionalImportExtractor.Extract(currentAst, context.DeviceConfig));

            foreach (var imp in allImports)
            {
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
                        imp.Line > 0 ? imp.Line : 1, 1) { File = currentPath };
                }

                // `from m import *`: replace the star with the names m exports, now that m's
                // AST is in hand. Everything downstream binds a symbol LIST, so a star left
                // in place imported nothing at all.
                StarImportExpander.Expand(imp, importedAst);

                graph.AddDependencyEdge(importedAst, currentAst);

                if (visitedModules.Add(imp.ModuleName))
                    queue.Enqueue((importedAst, importedPath));
            }
        }

        return graph;
    }
}

