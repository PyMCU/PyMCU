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
}