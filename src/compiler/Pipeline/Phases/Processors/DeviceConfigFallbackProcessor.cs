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
using PyMCU.Common.Abstractions;
using PyMCU.Frontend;

namespace PyMCU.Pipeline.Phases.Processors;

// Applies fallback values for Chip and Arch from CLI options when not yet
// populated by the chip definition file or a previous scan.
public class DeviceConfigFallbackProcessor : IAstProcessor
{
    public void Process(ProgramNode node, CompilationContext context)
    {
        if (string.IsNullOrEmpty(context.DeviceConfig.Chip))
            context.DeviceConfig.Chip = context.Options.Target;

        if (string.IsNullOrEmpty(context.DeviceConfig.Arch))
            context.DeviceConfig.Arch = context.Options.Arch;
    }
}
