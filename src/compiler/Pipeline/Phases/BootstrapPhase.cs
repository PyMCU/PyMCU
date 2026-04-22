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
using PyMCU.Frontend;

namespace PyMCU.Pipeline.Phases;

public class BootstrapPhase : CompilerPhaseBase
{
    public override string Name => "Bootstrapping";

    protected override void Run(CompilationContext context)
    {
        var options = context.Options;
        var deviceConfig = context.DeviceConfig;
        var includePaths = context.IncludePaths;

        if (string.IsNullOrEmpty(options.Target)) return;
        try
        {
            var target = TargetLoader.Bootstrap(options.Target, includePaths);

            deviceConfig.Chip = target.Config.Chip;
            deviceConfig.DetectedChip = target.Config.DetectedChip;
            deviceConfig.Arch = target.Config.Arch;
            if (target.Config.RamSize > 0) deviceConfig.RamSize = target.Config.RamSize;
            if (target.Config.FlashSize > 0) deviceConfig.FlashSize = target.Config.FlashSize;
            if (target.Config.EepromSize > 0) deviceConfig.EepromSize = target.Config.EepromSize;

            if (string.IsNullOrEmpty(deviceConfig.TargetChip))
            {
                deviceConfig.TargetChip = options.Target;
            }

            context.ModuleSourceLines[target.ModuleName] = target.SourceLines;
            context.ModuleCache[target.FilePath] = target.Ast;
            context.NamedModules[target.ModuleName] = target.Ast;

            // Mark the target as established so PreScanVisitor treats any
            // subsequent device_info() calls as module annotations to validate,
            // not as target directives to apply.
            context.IsTargetEstablished = true;
        }
        catch (Exception ex)
        {
            Logger.Warning("Bootstrap", $"Chip bootstrap failed: {ex.Message}");
            context.HasErrors = true;
        }
    }
}