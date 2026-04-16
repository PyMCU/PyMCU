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

using System.Diagnostics;
using PyMCU.Common;
using PyMCU.Common.Models;

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
        // Initialize early so PhaseStart/PhaseEnd work correctly for every phase,
        // including Initialization itself. InitializationPhase re-calls this
        // (idempotent) as a no-op.
        Logger.Initialize(options.Verbose);

        var version = CompilerInfo.Version;
        Logger.PrintBanner(version);

        var context = new CompilationContext(options);

        foreach (var phase in _phases)
        {
            var sw = Stopwatch.StartNew();
            Logger.PhaseStart(phase.Name);
            try
            {
                phase.Execute(context);
            }
            catch (Exception ex)
            {
                Logger.Error("Fatal", $"Unhandled exception in phase '{phase.Name}': {ex.Message}");
                context.HasErrors = true;
            }
            sw.Stop();

            if (context.HasErrors)
            {
                Logger.BuildFailed(phase.Name);
                return 1;
            }

            Logger.PhaseEnd(phase.Name, sw.ElapsedMilliseconds);
        }

        Logger.BuildSuccess(options.OutputPath);
        return 0;
    }
}