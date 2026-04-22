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

using PyMCU.Common.Models;

namespace PyMCU.Backend;

/// <summary>
/// CodeGenFactory — routing table for architecture → backend binary.
///
/// All backends have been extracted to external plugin packages
/// (pymcu-backend-avr, pymcu-backend-pic, pymcu-backend-riscv, pymcu-backend-pio).
/// Direct instantiation is no longer supported. Use 'pymcu build' or
/// '--emit-ir' + the appropriate pymcuc-{arch} binary.
/// </summary>
public static class CodeGenFactory
{
    private static readonly (string[] Prefixes, string Binary, string Hint)[] Backends =
    [
        (["avr", "avr8", "atmega", "attiny", "at90", "atxmega"],
            "pymcuc-avr", "pip install pymcu-backend-avr"),

        (["pic12", "baseline", "pic10f", "pic12f", "pic14", "pic14e", "midrange", "pic16f",
          "pic18", "advanced", "pic18f"],
            "pymcuc-pic", "pip install pymcu-backend-pic"),

        (["riscv", "rv32ec", "ch32v"],
            "pymcuc-riscv", "pip install pymcu-backend-riscv"),

        (["pio", "rp2040-pio"],
            "pymcuc-pio", "pip install pymcu-backend-pio"),
    ];

    public static CodeGen Create(string arch, DeviceConfig config)
    {
        var a = arch.ToLowerInvariant();

        foreach (var (prefixes, binary, hint) in Backends)
        {
            foreach (var prefix in prefixes)
            {
                if (a == prefix || a.StartsWith(prefix))
                {
                    throw new NotSupportedException(
                        $"Direct codegen for '{arch}' is not available in pymcuc.\n" +
                        $"  Install the backend plugin:  {hint}\n" +
                        $"  Then use 'pymcu build', or:\n" +
                        $"    pymcuc --emit-ir output.mir --target {arch}\n" +
                        $"    {binary} output.mir -o firmware.asm --target {arch}");
                }
            }
        }

        throw new ArgumentException($"Unknown architecture: {arch}", nameof(arch));
    }
}
