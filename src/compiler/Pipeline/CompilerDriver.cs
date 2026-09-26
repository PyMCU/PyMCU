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

        // A run that finds an unannotated slot narrower than a value stored into it asks for
        // the whole compilation again with that slot widened (WidthSeeds). Each run starts from
        // the source: the phases rewrite the tree as they go, so it cannot be lowered twice.
        // Warnings wait until a run is known to be the last one, or each would print once per
        // run. Widths only grow and are capped at 32 bits, so a handful of runs is the most a
        // program can ask for; the last one is kept whatever it recorded.
        const int maxRuns = 5;
        var seeds = new WidthSeeds();
        CompilationContext context = null!;
        for (int run = 1; ; run++)
        {
            context = new CompilationContext(options) { WidthSeeds = seeds, RerunAllowed = run < maxRuns };
            seeds.BeginRun();
            Diagnostic.HoldWarnings();
            bool rerun = false;

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
                    Diagnostic.ReleaseWarnings();
                    Logger.BuildFailed(phase.Name);
                    return 1;
                }

                Logger.PhaseEnd(phase.Name, sw.ElapsedMilliseconds);

                if (context.RerunWithWiderSlots)
                {
                    rerun = true;
                    break;
                }
            }

            if (rerun)
            {
                Diagnostic.DropHeldWarnings();
                continue;
            }
            Diagnostic.ReleaseWarnings();
            if (run > 1)
                Logger.Info("width", $"compiled {run} times: an unannotated slot was stored a "
                    + "value wider than the width first chosen for it");
            break;
        }

        Logger.PrintTargetSummary(context.DeviceConfig.Chip, context.DeviceConfig.Frequency);
        Logger.BuildSuccess(options.OutputPath);
        return 0;
    }
}