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

using PyMCU.Backend.Serialization;
using PyMCU.Common;

namespace PyMCU.Pipeline.Phases;

/// <summary>
/// Replaces <see cref="BackendPhase"/> when <c>--emit-ir</c> is specified.
/// Serializes the optimized <see cref="PyMCU.IR.ProgramIR"/> to a .mir JSON file
/// so that an external backend runner (e.g. <c>pymcuc-avr</c>) can read it.
/// </summary>
public class IrSerializerPhase : CompilerPhaseBase
{
    public override string Name => "IR Emit";

    protected override bool Guard(CompilationContext context)
    {
        if (context.IntermediateRepresentation != null) return true;
        context.HasErrors = true;
        return false;
    }

    protected override void Run(CompilationContext context)
    {
        var ir     = context.IntermediateRepresentation!;
        var output = context.Options.EmitIrPath!;

        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        IrSerializer.Serialize(ir, output);
        Logger.Verbose("pymcuc", $"IR written to {output}");
        Logger.BuildSuccess(output);
    }
}
