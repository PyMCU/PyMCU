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

namespace PyMCU.Common;

// Maps arch strings and chip name prefixes to canonical architecture families.
// A "family" is the coarsest unit of ABI/ISR model compatibility.
// Two arch strings in the same family are mutually importable (same backend,
// same calling convention, same register file model).
// Two arch strings from different families are NEVER compatible and attempting
// to import chip definitions across families is a fatal compiler error.
public static class ArchFamilyResolver
{
    // Returns the canonical family name for an arch or chip string, or "unknown"
    // if the string is not recognised. Comparison is case-insensitive.
    public static string Resolve(string archOrChip)
    {
        if (string.IsNullOrWhiteSpace(archOrChip)) return "unknown";

        var s = archOrChip.Trim().ToLowerInvariant();

        if (s == "avr" || s == "avr8"
            || s.StartsWith("atmega") || s.StartsWith("attiny")
            || s.StartsWith("at90"))
            return "avr";

        if (s == "pic12" || s == "baseline"
            || s.StartsWith("pic10f") || s.StartsWith("pic12f"))
            return "pic12";

        if (s == "pic14" || s == "pic14e" || s == "midrange"
            || s.StartsWith("pic16f"))
            return "pic14";

        if (s == "pic18" || s == "advanced"
            || s.StartsWith("pic18f"))
            return "pic18";

        if (s == "riscv" || s == "rv32ec" || s == "rv32i"
            || s.StartsWith("ch32v"))
            return "riscv";

        // ARM Cortex-M family.  RP2040 ships a dual Cortex-M0+; the "arm"
        // family is the ABI/ISR-compatible unit for all Cortex-M targets.
        // Note: this must precede the "pio" branch so the bare "rp2040" arch
        // resolves to the CPU family rather than the PIO co-processor.
        if (s == "arm" || s == "cortex-m" || s == "cortex-m0"
            || s == "cortex-m0plus" || s == "cortex-m0+" || s == "rp2040")
            return "arm";

        if (s == "pio" || s == "rp2040-pio")
            return "pio";

        return "unknown";
    }

    // Returns true when both strings belong to the same architecture family.
    public static bool SameFamily(string a, string b)
        => Resolve(a) == Resolve(b) && Resolve(a) != "unknown";
}

