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
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.Pipeline.Phases;

public class IrGenerationPhase : CompilerPhaseBase
{
    public override string Name => "IR Generation";

    protected override bool Guard(CompilationContext context)
    {
        if (context.RootAst != null) return true;
        context.HasErrors = true;
        return false;
    }

    protected override void Run(CompilationContext context)
    {
        DataTypeExtensions.SetPointerWidth(context.DeviceConfig.PointerWidth);

        var irGen = new IRGenerator();
        var ir = irGen.Generate(context.RootAst!, context.NamedModules, context.DeviceConfig,
            context.SourceLines, context.ModuleSourceLines, context.ProjectModules,
            context.ModulePaths);

        // PYMCU_NO_OPT=1 skips the optimizer: lets a miscompile be bisected to the
        // IR generator (raw IR wrong) vs an optimizer pass (raw IR right).
        var optimized = Environment.GetEnvironmentVariable("PYMCU_NO_OPT") == "1"
            ? ir
            : Optimizer.Optimize(ir);

        // CanFail analysis runs after optimization so that dead-code-eliminated
        // functions and cloned bodies are the final IR seen by the backend.
        CanFailAnalyzer.Analyze(optimized);

        // Guard every unguarded CanFail call so an uncaught error halts (top-level) or re-raises
        // to the caller, instead of being silently swallowed by the next happy-path CLT.
        CanFailAnalyzer.InsertUncaughtPropagation(optimized);

        context.IntermediateRepresentation = optimized;
    }
}