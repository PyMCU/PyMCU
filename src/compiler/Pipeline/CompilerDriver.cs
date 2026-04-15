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

namespace PyMCU.Pipeline;

public class CompilerDriver
{
    private readonly List<ICompilerPhase> _phases = [];

    public CompilerDriver AddPhase(ICompilerPhase phase)
    {
        _phases.Add(phase);
        return this;
    }

    public int Run(CompilerOptions options)
    {
        var context = new CompilationContext(options);

        foreach (var phase in _phases)
        {
            try
            {
                phase.Execute(context);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Fatal] Unhandled exception in phase '{phase.Name}': {ex.Message}");
                context.HasErrors = true;
            }

            if (!context.HasErrors) continue;
            Console.Error.WriteLine($"[Pipeline] Compilation aborted in phase: {phase.Name}");
            return 1;
        }

        Console.WriteLine($"[pymcuc] Compilation successful -> {options.OutputPath}");
        return 0;
    }
}