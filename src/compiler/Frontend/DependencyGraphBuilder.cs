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

            foreach (var imp in currentAst.Imports)
            {
                if (BuiltinModuleNames.IsBuiltin(imp.ModuleName)) continue;

                var importedAst  = moduleLoader.LoadModule(imp.ModuleName, currentPath, context);
                var importedPath = moduleLoader.ResolveModulePath(imp.ModuleName, currentPath, context);

                graph.AddDependencyEdge(importedAst, currentAst);

                if (visitedModules.Add(imp.ModuleName))
                    queue.Enqueue((importedAst, importedPath));
            }
        }

        return graph;
    }
}

