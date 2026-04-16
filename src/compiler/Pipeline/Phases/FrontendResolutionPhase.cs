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

namespace PyMCU.Pipeline.Phases;

public class FrontendResolutionPhase(IModuleLoader moduleLoader) : ICompilerPhase
{
    public string Name => "Semantic & Dependency Resolution";

    private const int MaxQueueOperations = 5000;

    public void Execute(CompilationContext context)
    {
        if (context.RootAst == null)
        {
            context.HasErrors = true;
            return;
        }

        try
        {
            var moduleGraph = BuildDependencyGraph(context.RootAst, context.Options.FilePath, context);
            var resolutionOrder = moduleGraph.GetTopologicalSort();

            var preScanner = new PreScanVisitor(context.DeviceConfig);
            var conditional = new ConditionalCompilator(context.DeviceConfig);

            foreach (var astNode in resolutionOrder)
            {
                preScanner.Scan(astNode);

                if (string.IsNullOrEmpty(context.DeviceConfig.Chip)) context.DeviceConfig.Chip = context.Options.Chip;
                if (string.IsNullOrEmpty(context.DeviceConfig.Arch)) context.DeviceConfig.Arch = context.Options.Arch;

                conditional.Process(astNode);
            }

            foreach (var astNode in resolutionOrder)
            {
                foreach (var imp in astNode.Imports.Where(imp => imp.ModuleName is not ("pymcu.types" or "pymcu.chips")).Where(imp => !context.NamedModules.ContainsKey(imp.ModuleName)))
                {
                    moduleLoader.LoadModule(imp.ModuleName, context.Options.FilePath, context);
                }
            }
        }
        catch (CompilerError e)
        {
            Diagnostic.Report(e, context.SourceCode, context.Options.FilePath);
            context.HasErrors = true;
        }
    }

    private DependencyGraph BuildDependencyGraph(ProgramNode rootAst, string rootPath, CompilationContext context)
    {
        var graph = new DependencyGraph();
        var queue = new Queue<(ProgramNode Ast, string Path)>();
        var visitedModules = new HashSet<string>();
        int operations = 0;

        queue.Enqueue((rootAst, rootPath));
        graph.AddNode(rootAst);

        while (queue.Count > 0)
        {
            if (++operations > MaxQueueOperations)
                throw new CompilerError("ImportError",
                    "Dependency graph exceeded maximum size. Possible circular dependency.", 0, 0);

            var (currentAst, currentPath) = queue.Dequeue();

            foreach (var imp in currentAst.Imports)
            {
                if (imp.ModuleName is "pymcu.types" or "pymcu.chips") continue;

                var importedAst  = moduleLoader.LoadModule(imp.ModuleName, currentPath, context);
                var importedPath = moduleLoader.ResolveModulePath(imp.ModuleName, currentPath, context);

                graph.AddDependencyEdge(importedAst, currentAst);

                if (visitedModules.Add(imp.ModuleName))
                {
                    queue.Enqueue((importedAst, importedPath));
                }
            }
        }

        return graph;
    }
}