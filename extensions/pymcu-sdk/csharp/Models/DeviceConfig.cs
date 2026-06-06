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
    public int PointerWidth => Arch switch
    {
        "arm" or "rp2040" or "cortex-m" => 4,
        "riscv32" => 4,
        _ => 2,
    };
}