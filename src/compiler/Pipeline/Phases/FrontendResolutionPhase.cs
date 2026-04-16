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
using PyMCU.Pipeline.Phases.Processors;

namespace PyMCU.Pipeline.Phases;

public class FrontendResolutionPhase(
    IModuleLoader moduleLoader,
    IDependencyGraphBuilder graphBuilder) : CompilerPhaseBase
{
    public override string Name => "Semantic & Dependency Resolution";

    protected override bool Guard(CompilationContext context)
    {
        if (context.RootAst != null) return true;
        context.HasErrors = true;
        return false;
    }

    protected override void Run(CompilationContext context)
    {
        var resolutionOrder = graphBuilder
            .Build(context.RootAst!, context.Options.FilePath, context)
            .GetTopologicalSort();

        // Build the processor chain once, bound to the shared DeviceConfig instance.
        IAstProcessor[] processors =
        [
            new PreScanProcessor(new PreScanVisitor(context.DeviceConfig)),
            new DeviceConfigFallbackProcessor(),
            new ConditionalCompilationProcessor(new ConditionalCompilator(context.DeviceConfig)),
        ];

        foreach (var node in resolutionOrder)
        {
            foreach (var processor in processors)
                processor.Process(node, context);
        }

        LoadPostConditionalModulesRecursive(processors, context);
    }

    // Recursively loads and processes any modules that conditional compilation revealed
    // after the initial graph was built. This ensures transitive imports are fully resolved.
    // For example: _lcd/gpio.py imports time.py → time.py's inline functions must be registered.
    private void LoadPostConditionalModulesRecursive(
        IAstProcessor[] processors,
        CompilationContext context)
    {
        const int maxIterations = 10;
        var processedModules = new HashSet<string>(context.NamedModules.Keys);
        var iteration = 0;

        while (iteration++ < maxIterations)
        {
            var newModules = new List<ProgramNode>();

            // Create snapshot of current modules to avoid modification-during-iteration
            var currentModules = context.NamedModules.ToList();

            // Scan all currently processed modules for imports
            foreach (var (moduleName, node) in currentModules)
            {
                foreach (var imp in node.Imports)
                {
                    if (BuiltinModuleNames.IsBuiltin(imp.ModuleName)) continue;
                    if (processedModules.Contains(imp.ModuleName)) continue;

                    // Load the module if not yet loaded
                    if (!context.NamedModules.ContainsKey(imp.ModuleName))
                        moduleLoader.LoadModule(imp.ModuleName, context.Options.FilePath, context);

                    var importedModule = context.NamedModules[imp.ModuleName];
                    if (processedModules.Add(imp.ModuleName))
                        newModules.Add(importedModule);
                }
            }

            // No new modules discovered → we're done
            if (newModules.Count == 0)
                break;

            // Process all newly discovered modules through the full processor pipeline
            foreach (var module in newModules)
            {
                foreach (var processor in processors)
                    processor.Process(module, context);
            }
        }

        if (iteration >= maxIterations)
            throw new CompilerError("ImportError",
                "Exceeded maximum iterations while loading transitive imports. Possible circular dependency.", 0, 0);
    }
}