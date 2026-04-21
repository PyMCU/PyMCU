/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Backend.Targets.PIC12;
using PyMCU.Backend.Targets.PIC14;
using PyMCU.Backend.Targets.PIO;
using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common;
using PyMCU.Backend.Targets.PIC18;
using PyMCU.Common.Models;

namespace PyMCU.Backend;

public static class CodeGenFactory
{
    // Chip/arch prefixes that are handled by the external pymcuc-avr backend.
    private static readonly string[] AvrPrefixes =
        ["avr", "avr8", "atmega", "attiny", "at90", "atxmega"];

    private static bool IsAvrArch(string arch)
    {
        var a = arch.ToLowerInvariant();
        foreach (var prefix in AvrPrefixes)
            if (a == prefix || a.StartsWith(prefix)) return true;
        return false;
    }

    public static CodeGen Create(string arch, DeviceConfig config)
    {
        // AVR has moved to an external backend binary (pymcuc-avr).
        // Direct compilation is no longer supported; use --emit-ir to produce a
        // .mir file and then invoke pymcuc-avr, or use 'pymcu build' which does
        // this automatically.
        if (IsAvrArch(arch))
        {
            throw new NotSupportedException(
                $"AVR codegen ({arch}) is not available in pymcuc directly.\n" +
                "  Use '--emit-ir output.mir' to produce IR and run:\n" +
                "    pymcuc-avr output.mir -o firmware.asm --target <chip>\n" +
                "  Or use 'pymcu build' which handles this automatically.");
        }

        if (arch == "pic12" || arch == "baseline" || arch.StartsWith("pic10f") || arch.StartsWith("pic12f"))
        {
            return new PIC12CodeGen(config);
        }
        
        if (arch == "pic14" || arch == "pic14e" || arch == "midrange" || arch.StartsWith("pic16f"))
        {
            return new PIC14CodeGen(config);
        }
        
        if (arch == "pic18" || arch == "advanced" || arch.StartsWith("pic18f"))
        {
            return new PIC18CodeGen(config);
        }
        
        if (arch == "riscv" || arch == "rv32ec" || arch.StartsWith("ch32v"))
        {
            return new RiscvCodeGen(config);
        }
        
        if (arch == "pio" || arch == "rp2040-pio")
        {
            return new PIOCodeGen(config);
        }

        throw new ArgumentException($"Unknown architecture: {arch}", nameof(arch));
    }
}