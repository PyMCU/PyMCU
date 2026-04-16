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

        LoadPostConditionalModules(resolutionOrder, context);
    }

    // Loads any modules that conditional compilation revealed after the graph was built.
    private void LoadPostConditionalModules(IEnumerable<ProgramNode> nodes, CompilationContext context)
    {
        foreach (var node in nodes)
        {
            foreach (var imp in node.Imports)
            {
                if (BuiltinModuleNames.IsBuiltin(imp.ModuleName)) continue;
                if (!context.NamedModules.ContainsKey(imp.ModuleName))
                    moduleLoader.LoadModule(imp.ModuleName, context.Options.FilePath, context);
            }
        }
    }
}