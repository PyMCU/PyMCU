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
        var irGen = new IRGenerator();
        var ir = irGen.Generate(context.RootAst!, context.NamedModules, context.DeviceConfig,
            context.SourceLines, context.ModuleSourceLines);

        context.IntermediateRepresentation = Optimizer.Optimize(ir);
    }
}