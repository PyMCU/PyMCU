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
        context.DeviceConfig.Timebase = options.Timebase;
        context.DeviceConfig.ResetVector = options.ResetVector;
        context.DeviceConfig.InterruptVector = options.InterruptVector;
        context.DeviceConfig.Stdlib = options.Stdlib;

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            // The INPUT counterpart of OutputFile.Guard, and raised the same way: as a
            // diagnostic rather than a bare line on stderr.
            //
            // It used to print `Fatal Error: <what .NET said>` directly, which has two costs.
            // It is the one error an IDE hits most, because the file it hands the compiler is
            // a temporary copy of a buffer, and under `--error-format json` that line is the
            // only thing on stderr that is not JSON, so the consumer sees a parse failure
            // instead of a message. And .NET's sentence does not always name the path.
            //
            // Thrown, so CompilerPhaseBase reports it through Diagnostic and it comes out in
            // whichever format was asked for. Line 0 with no column: no line of the source is
            // responsible, and there is no source to quote.
            throw new CompilerError("OSError",
                $"cannot read the source file '{options.FilePath}': {ex.Message}",
                0, CompilerError.Unlocated);
        }

        context.IncludePaths.AddRange(options.Includes);
        var parentDir = Path.GetDirectoryName(options.FilePath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            context.IncludePaths.Add(parentDir);
        }
    }
}