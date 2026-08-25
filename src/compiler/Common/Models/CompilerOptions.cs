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

public sealed record CompilerOptions(
    string FilePath,
    string OutputPath,
    string Arch,
    string Target,
    ulong Frequency,
    List<string> Configs,
    List<string> Includes,
    int ResetVector,
    int InterruptVector,
    bool Verbose,
    string? EmitIrPath = null,
    // The project's own source directory. Modules loaded from inside it are the USER's, and
    // only those have their module level executed on import; everything else is an installed
    // distribution (the pymcu stdlib, the MicroPython and CircuitPython compat layers), which
    // is written knowing that only the entry file's top level runs. The driver stages the entry
    // file into dist/_generated while the imports still resolve out of src/, so the entry
    // file's own directory is not enough on its own. Absent, the entry file's directory is used.
    string? ProjectRoot = null
);