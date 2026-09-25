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

using PyMCU.Backend;
using PyMCU.Common;

namespace PyMCU.Pipeline.Phases;

public class BackendPhase : CompilerPhaseBase
{
    public override string Name => "Backend Phase";

    protected override bool Guard(CompilationContext context)
    {
        if (context.IntermediateRepresentation != null) return true;
        context.HasErrors = true;
        return false;
    }

    protected override void Run(CompilationContext context)
    {
        var ir = context.IntermediateRepresentation!;
        var deviceConfig = context.DeviceConfig;
        var options = context.Options;


        string targetArch = deviceConfig.Arch;
        if (string.IsNullOrEmpty(targetArch))
        {
            targetArch = deviceConfig.Chip;
            if (string.IsNullOrEmpty(targetArch)) targetArch = options.Arch;
        }

        if (string.IsNullOrEmpty(deviceConfig.Chip)) deviceConfig.Chip = options.Target;
        if (string.IsNullOrEmpty(deviceConfig.Arch)) deviceConfig.Arch = options.Arch;
        if (string.IsNullOrEmpty(deviceConfig.TargetChip)) deviceConfig.TargetChip = deviceConfig.Chip;

        var backend = CodeGenFactory.Create(targetArch, deviceConfig);

        var outputParent = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(outputParent) && !Directory.Exists(outputParent))
            OutputFile.Guard(options.OutputPath, "create the directory of",
                () => Directory.CreateDirectory(outputParent));

        Logger.Verbose("pymcuc",
            $"Compiling {options.FilePath} -> {options.OutputPath} ({targetArch} @ {deviceConfig.Frequency}Hz)");

        StreamWriter asmFile = null!;
        OutputFile.Guard(options.OutputPath, "open", () => asmFile = new StreamWriter(options.OutputPath));

        using (asmFile)
        {
            backend.Compile(ir, asmFile);

            // Flushed here rather than left to the `using`, so that a disk that fills on the
            // last buffer is a named failure of this path and not an IOException out of a
            // dispose that no guard is watching.
            OutputFile.Guard(options.OutputPath, "write", asmFile.Flush);
        }

        Logger.Verbose("pymcuc", $"Output written to {options.OutputPath}");
    }
}