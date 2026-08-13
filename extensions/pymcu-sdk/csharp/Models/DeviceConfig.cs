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

namespace PyMCU.Common.Models;

public class DeviceConfig
{
    public string Chip { get; set; } = "";
    public string TargetChip { get; set; } = ""; // Source of Truth (CLI/TOML)
    public string DetectedChip { get; set; } = ""; // From source code (device_info)
    public string Arch { get; set; } = "";
    public ulong Frequency { get; set; }
    public int RamSize { get; set; } = 0;
    public int FlashSize { get; set; } = 0;
    public int EepromSize { get; set; } = 0;
    public Dictionary<string, string> Fuses { get; set; } = new();
    public int ResetVector { get; set; } = -1;
    public int InterruptVector { get; set; } = -1;
    public int InterruptVectorHigh { get; set; } = -1;
    public int InterruptVectorLow { get; set; } = -1;

    // When true the backend emits "; file:line: text" comments in the .asm output.
    // Defaults to false (release builds).  Set to true via --emit-linemap or --debug.
    public bool EmitDebugComments { get; set; } = false;

    // Native pointer size in bytes, derived from the target architecture.
    // AVR / PIC12 / PIC14 / PIC18 = 2 bytes; ARM Cortex-M / RISC-V 32 = 4 bytes.
    // Getting this wrong is not just a pointer-size issue: the IR generator uses
    // it to decide whether a wide constant store has to be split into byte-sized
    // pieces the way 8-bit AVR needs, so a 32-bit target reported as 2 would emit
    // split MMIO writes to consecutive addresses.
    public int PointerWidth => Is32BitArch(Arch) ? 4 : 2;

    private static bool Is32BitArch(string arch)
    {
        var a = arch.ToLowerInvariant();

        if (a is "arm" or "rp2040" or "cortex-m" or "cortex-m0" or "cortex-m0plus"
            or "cortex-m0+" or "rp2350" or "cortex-m33" or "cortex-m33f")
            return true;

        if (a is "riscv" or "riscv32" or "rv32ec" or "rv32i" or "rv32imac")
            return true;

        return a.StartsWith("ch32v");
    }
}