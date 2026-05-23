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

namespace PyMCU.Pipeline.Phases;

// Detects whether the compiled program uses GC-managed heap references (GC_REF).
// When detected, sets context.ProgramNeedsGc = true and program.NeedsGc = true so
// the AVR backend injects the gc_runtime.S GC runtime.
//
// Programs that use only ZCAs or primitive types are NOT affected: NeedsGc stays false
// and no GC overhead is introduced in the generated code.
public class GcAnalysisPhase : CompilerPhaseBase
{
    public override string Name => "GC Analysis";

    protected override bool Guard(CompilationContext context)
    {
        if (context.IntermediateRepresentation != null) return true;
        context.HasErrors = true;
        return false;
    }

    protected override void Run(CompilationContext context)
    {
        var program = context.IntermediateRepresentation!;
        bool needsGc = false;

        // Check globals for GC_REF type.
        foreach (var global in program.Globals)
        {
            if (global.Type == DataType.GC_REF)
            {
                needsGc = true;
                break;
            }
        }

        if (!needsGc)
        {
            foreach (var func in program.Functions)
            {
                foreach (var instr in func.Body)
                {
                    if (instr is GcAlloc or GcRoot or GcUnroot)
                    {
                        needsGc = true;
                        break;
                    }

                    // Also catch any GC_REF-typed Variable/Temporary in any instruction.
                    if (!needsGc && HasGcRefOperand(instr))
                        needsGc = true;

                    if (needsGc) break;
                }
                if (needsGc) break;
            }
        }

        if (needsGc)
        {
            string arch = context.DeviceConfig.Arch ?? "";
            if (arch != "avr" && arch != "")
            {
                throw new CompilerError("GcAnalysis",
                    $"GC_REF / gc_alloc is only supported on AVR targets (detected arch: '{arch}'). " +
                    "Use @value classes or primitive types for other architectures.", 0, 0);
            }

            context.ProgramNeedsGc = true;
            program.NeedsGc = true;
        }
    }

    private static bool HasGcRefOperand(Instruction instr)
    {
        bool IsGcRef(Val? v) => v is Variable vv && vv.Type == DataType.GC_REF
                             || v is Temporary tt && tt.Type == DataType.GC_REF;

        return instr switch
        {
            Copy cp        => IsGcRef(cp.Src) || IsGcRef(cp.Dst),
            Return ret     => IsGcRef(ret.Value),
            Call c         => IsGcRef(c.Dst) || c.Args.Any(a => IsGcRef(a)),
            Binary b       => IsGcRef(b.Src1) || IsGcRef(b.Src2) || IsGcRef(b.Dst),
            Unary u        => IsGcRef(u.Src) || IsGcRef(u.Dst),
            _              => false
        };
    }
}
