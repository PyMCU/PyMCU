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

public class BackendPhase : ICompilerPhase
{
    public string Name => "Backend Phase";
    public void Execute(CompilationContext context)
    {
        if (context.IntermediateRepresentation == null)
        {
            context.HasErrors = true;
            return;
        }

        try
        {
            var ir = context.IntermediateRepresentation;
            var deviceConfig = context.DeviceConfig;
            var options = context.Options;

            if (!string.IsNullOrEmpty(context.Config.Chip)) deviceConfig.Chip = context.Config.Chip;
            if (!string.IsNullOrEmpty(context.Config.Arch)) deviceConfig.Arch = context.Config.Arch;
            if (!string.IsNullOrEmpty(context.Config.TargetChip))
                deviceConfig.TargetChip = context.Config.TargetChip;
            if (context.Config.RamSize > 0) deviceConfig.RamSize = context.Config.RamSize;

            string targetArch = deviceConfig.Arch;
            if (string.IsNullOrEmpty(targetArch))
            {
                targetArch = deviceConfig.Chip;
                if (string.IsNullOrEmpty(targetArch)) targetArch = options.Arch;
            }

            if (string.IsNullOrEmpty(deviceConfig.Chip)) deviceConfig.Chip = options.Arch;
            if (string.IsNullOrEmpty(deviceConfig.Arch)) deviceConfig.Arch = options.Arch;
            if (string.IsNullOrEmpty(deviceConfig.TargetChip)) deviceConfig.TargetChip = deviceConfig.Chip;

            var backend = CodeGenFactory.Create(targetArch, deviceConfig);

            var outputParent = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrEmpty(outputParent) && !Directory.Exists(outputParent))
            {
                Directory.CreateDirectory(outputParent);
            }

            Console.WriteLine(
                $"[pymcuc] Compiling {options.FilePath} -> {options.OutputPath} ({targetArch} @ {deviceConfig.Frequency}Hz)");

            using (var asmFile = new StreamWriter(options.OutputPath))
            {
                backend.Compile(ir, asmFile);
            }

            Console.WriteLine($"[pymcuc] Success! Output written to {options.OutputPath}");
        }
        catch (CompilerError e)
        {
            Diagnostic.Report(e, context.SourceCode, context.Options.FilePath);
            context.HasErrors = true;
        }
    }
}