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

namespace PyMCU.Pipeline.Phases;

public class InitializationPhase : CompilerPhaseBase
{
    public override string Name => "Initialization";

    protected override void Run(CompilationContext context)
    {
        var options = context.Options;

        // Initialize logger with verbose mode setting
        Logger.Initialize(options.Verbose);

        context.DeviceConfig.Frequency = options.Frequency;
        context.DeviceConfig.ResetVector = options.ResetVector;
        context.DeviceConfig.InterruptVector = options.InterruptVector;

        if (!string.IsNullOrEmpty(options.Arch))
        {
            context.DeviceConfig.TargetChip = options.Arch;
        }

        foreach (var item in options.Configs)
        {
            var eqPos = item.IndexOf('=');
            if (eqPos == -1) continue;
            var key = item[..eqPos];
            var val = item[(eqPos + 1)..];
            context.DeviceConfig.Fuses[key] = val;
        }

        try
        {
            context.SourceCode = File.ReadAllText(options.FilePath);
            using var reader = new StringReader(context.SourceCode);
            while (reader.ReadLine() is { } line)
            {
                context.SourceLines.Add(line);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal Error: {ex.Message}");
            context.HasErrors = true;
            return;
        }

        context.IncludePaths.AddRange(options.Includes);
        var parentDir = Path.GetDirectoryName(options.FilePath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            context.IncludePaths.Add(parentDir);
        }
    }
}