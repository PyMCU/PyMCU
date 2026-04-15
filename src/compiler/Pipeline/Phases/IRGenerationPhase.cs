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

public class IrGenerationPhase : ICompilerPhase
{
    public string Name => "IR Generation";
    public void Execute(CompilationContext context)
    {
        if (context.RootAst == null)
        {
            context.HasErrors = true;
            return;
        }

        try
        {
            var irGen = new IRGenerator();
            var ir = irGen.Generate(context.RootAst, context.NamedModules, context.DeviceConfig, context.SourceLines,
                context.ModuleSourceLines);

            context.IntermediateRepresentation = Optimizer.Optimize(ir);
        }
        catch (CompilerError e)
        {
            Diagnostic.Report(e, context.SourceCode, context.Options.FilePath);
            context.HasErrors = true;
        }
    }
}