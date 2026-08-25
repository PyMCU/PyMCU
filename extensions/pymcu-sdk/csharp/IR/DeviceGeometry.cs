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

namespace PyMCU.IR;

/// <summary>
/// The memory geometry of the target part, exactly as the chip file declares it
/// through <c>device_info(ram_size=..., flash_size=..., eeprom_size=...)</c>.
///
/// This is the ONLY place a backend may learn these numbers. Before it existed
/// the values stopped at the frontend and every backend that needed one grew a
/// private table, which is how the 16F84A came to place its mul/div scratch
/// inside __BADRAM and how the ATmega2560 came to pick LPM over ELPM from a
/// hardcoded list of chip names.
///
/// Every size is nullable ON PURPOSE. Zero-means-unknown is the exact defect
/// this type replaces: <c>DeviceConfig.RamSize</c> defaulted to 0, the PIC14
/// backend tested <c>RamSize > 0 &amp;&amp; RamSize &lt; 96</c>, the test was
/// permanently false, and nothing said so. A backend that needs a number it was
/// not given must call the matching <c>Require*</c> and fail the build.
/// </summary>
public class DeviceGeometry
{
    /// <summary>The chip this geometry describes, for diagnostics.</summary>
    public string Chip { get; set; } = "";

    /// <summary>Total SRAM in bytes, or null when the chip file does not declare it.</summary>
    public int? RamSize { get; set; }

    /// <summary>Total program flash in bytes, or null when the chip file does not declare it.</summary>
    public int? FlashSize { get; set; }

    /// <summary>Total EEPROM in bytes, or null when the chip file does not declare it.</summary>
    public int? EepromSize { get; set; }

    /// <summary>SRAM size, or a build error naming the chip and the missing field.</summary>
    public int RequireRamSize(string need) => Require(RamSize, "ram_size", need);

    /// <summary>Flash size, or a build error naming the chip and the missing field.</summary>
    public int RequireFlashSize(string need) => Require(FlashSize, "flash_size", need);

    /// <summary>EEPROM size, or a build error naming the chip and the missing field.</summary>
    public int RequireEepromSize(string need) => Require(EepromSize, "eeprom_size", need);

    private int Require(int? value, string field, string need)
    {
        if (value is { } v) return v;

        var chip = string.IsNullOrEmpty(Chip) ? "<unnamed chip>" : Chip;
        throw new InvalidOperationException(
            $"{chip} declares no {field}, and the backend needs it to {need}. " +
            $"Add {field}=... to the device_info() call in the chip file for {chip}.");
    }
}
