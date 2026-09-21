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

/// The per-board answer to the introspection questions a CircuitPython/MicroPython library
/// asks at compile time: `os.uname()`, `sys.implementation.name`/`.version`, `sys.platform`.
/// See docs/rfcs/0007-compile-time-introspection.md for the survey and the argument behind
/// every value here; this class is the table that RFC proposed, made executable.
///
/// This is a lookup table, not a parser: it never reads the compat layer's own sys.py/os.py
/// (pymcu_circuitpython, pymcu_micropython) to derive an answer, for the same reason
/// CompileTimeEvaluator never reads pymcu.chips' `_ChipInfo` class body to answer
/// `__CHIP__.arch` -- both files exist for IDEs and for the layer's own CPython-side parity
/// suite, and the compiler substitutes the real answer directly at the point it is read.
public static class IntrospectionTable
{
    public const string CircuitPython = "circuitpython";
    public const string MicroPython = "micropython";

    // The CircuitPython API surface pymcu-circuitpython's own parity suite is pinned against
    // (circuitpython-stubs>=10.3.1), not that package's own 0.x release number -- see
    // docs/rfcs/0007 section 3 for why a 0.x version would silently break every
    // `sys.implementation.version[0] >= N` feature-detection guard forever.
    private static readonly (int Major, int Minor, int Micro) CircuitPythonVersion = (10, 3, 1);
    private const string CircuitPythonRelease = "10.3.1";

    // The MicroPython API surface pymcu-micropython's own parity suite is pinned against
    // (micropython-rp2-stubs>=1.29.0.post1).
    private static readonly (int Major, int Minor, int Micro) MicroPythonVersion = (1, 29, 0);
    private const string MicroPythonRelease = "1.29.0";

    /// Upstream board display names, used only to build the `machine` field
    /// ("<board display name> with <sysname>"). Boards not listed fall back to the board or
    /// chip identifier as written -- see BoardDisplayName below.
    private static readonly Dictionary<string, string> BoardDisplayNames = new()
    {
        ["raspberry_pi_pico"] = "Raspberry Pi Pico",
        ["pico"] = "Raspberry Pi Pico",
        ["raspberry_pi_pico_w"] = "Raspberry Pi Pico W",
        ["pico_w"] = "Raspberry Pi Pico W",
        ["raspberry_pi_pico2"] = "Raspberry Pi Pico 2",
        ["pico2"] = "Raspberry Pi Pico 2",
        ["raspberry_pi_pico2_w"] = "Raspberry Pi Pico 2 W",
        ["pico2_w"] = "Raspberry Pi Pico 2 W",
    };

    // MicroPython's board display name differs from CircuitPython's on at least one board
    // this layer supports (Pico 2: "Raspberry Pi Pico2" vs "Raspberry Pi Pico 2" -- no space
    // before the 2 upstream, read from micropython/ports/rp2/boards/RPI_PICO2/mpconfigboard.h).
    // This is not a typo to "fix"; it is upstream's own string.
    private static readonly Dictionary<string, string> MicroPythonBoardDisplayNames = new()
    {
        ["raspberry_pi_pico2"] = "Raspberry Pi Pico2",
        ["pico2"] = "Raspberry Pi Pico2",
        ["raspberry_pi_pico2_w"] = "Raspberry Pi Pico2 W",
        ["pico2_w"] = "Raspberry Pi Pico2 W",
    };

    public sealed record Uname(string Sysname, string Nodename, string Release, string Version, string Machine);

    /// True when `config.Stdlib` names a flavor this table knows how to answer for. Callers
    /// (CompileTimeEvaluator) use this to decide whether `sys`/`os` are the compat layer's
    /// shims at all -- an empty or unknown Stdlib means the program has no compat layer, and
    /// `sys.implementation`/`os.uname` should fail resolution exactly like any other
    /// undefined member, not silently answer with a guess.
    public static bool IsKnownStdlib(string? stdlib) => stdlib is CircuitPython or MicroPython;

    public static string ImplementationName(DeviceConfig config) => config.Stdlib switch
    {
        CircuitPython => "circuitpython",
        MicroPython => "micropython",
        _ => throw new InvalidOperationException(
            $"no compile-time answer for sys.implementation.name: project has no CircuitPython/" +
            $"MicroPython compat layer selected (--stdlib), stdlib='{config.Stdlib}'"),
    };

    public static (int Major, int Minor, int Micro) ImplementationVersion(DeviceConfig config) => config.Stdlib switch
    {
        CircuitPython => CircuitPythonVersion,
        MicroPython => MicroPythonVersion,
        _ => throw new InvalidOperationException(
            $"no compile-time answer for sys.implementation.version: project has no " +
            $"CircuitPython/MicroPython compat layer selected (--stdlib), stdlib='{config.Stdlib}'"),
    };

    /// `sys.platform`. Upstream CircuitPython's raspberrypi port and MicroPython's rp2 port
    /// answer this with DIFFERENT strings for the same board ("RP2040" vs "rp2"), and
    /// CircuitPython's own sys.platform ("RP2040") disagrees in case with its own
    /// os.uname().sysname ("rp2040") -- both are reproduced exactly, not reconciled.
    public static string SysPlatform(DeviceConfig config)
    {
        RequireKnownStdlib(config);
        var chip = config.Chip.ToLowerInvariant();
        return config.Stdlib switch
        {
            CircuitPython => chip switch
            {
                "rp2040" => "RP2040",
                "rp2350" => "RP2350",
                _ => config.Chip, // No upstream CircuitPython port: honest generic fallback.
            },
            MicroPython => chip switch
            {
                "rp2040" or "rp2350" => "rp2", // MicroPython's rp2 port answers "rp2" for both.
                _ => config.Chip, // No upstream MicroPython port: honest generic fallback.
            },
            _ => throw UnreachableStdlib(config),
        };
    }

    /// `os.uname()`. See docs/rfcs/0007 section 2.1: CircuitPython's sysname is the MCU name,
    /// MicroPython's is the port short name -- two different upstream concepts that only
    /// coincide by accident on some chips, never merged into one shared answer here.
    public static Uname GetUname(DeviceConfig config)
    {
        RequireKnownStdlib(config);
        var chip = config.Chip.ToLowerInvariant();

        return config.Stdlib switch
        {
            CircuitPython => chip switch
            {
                // Pico 2 reports the exact silicon variant upstream ships ("rp2350a"), not
                // PyMCU's own simplified "rp2350" chip id that folds every RP2350 stepping
                // into one name -- see docs/rfcs/0007 section 0.3.
                "rp2350" => new Uname("rp2350a", "rp2350a", CircuitPythonRelease,
                    BuildVersion(), MachineOf(config, "rp2350a", BoardDisplayNames)),
                "rp2040" => new Uname("rp2040", "rp2040", CircuitPythonRelease,
                    BuildVersion(), MachineOf(config, "rp2040", BoardDisplayNames)),
                // No upstream CircuitPython port for this chip: sysname is the plain chip
                // name, so every `sysname == "rp2040"`/`"samd21"`/`"esp32"`/`"Linux"` guard
                // takes its generic (false) branch, correctly.
                _ => new Uname(config.Chip, config.Chip, CircuitPythonRelease,
                    BuildVersion(), MachineOf(config, config.Chip, BoardDisplayNames)),
            },
            MicroPython => chip switch
            {
                "rp2040" or "rp2350" => new Uname("rp2", "rp2", MicroPythonRelease,
                    BuildVersion(),
                    MachineOf(config, chip == "rp2350" ? "RP2350" : "RP2040", MicroPythonBoardDisplayNames)),
                _ => new Uname(config.Chip, config.Chip, MicroPythonRelease,
                    BuildVersion(), MachineOf(config, config.Chip, MicroPythonBoardDisplayNames)),
            },
            _ => throw UnreachableStdlib(config),
        };
    }

    // The free-text build stamp upstream fills with a git tag and a build date. No surveyed
    // library reads this field (docs/rfcs/0007 section 1); a fixed, honestly-labelled string
    // beats fabricating a commit and a date nothing here tracks.
    private static string BuildVersion() => "PyMCU (ahead-of-time)";

    private static string MachineOf(DeviceConfig config, string mcuName, Dictionary<string, string> displayNames)
    {
        var boardKey = config.Board.ToLowerInvariant();
        if (!string.IsNullOrEmpty(boardKey) && displayNames.TryGetValue(boardKey, out var display))
            return $"{display} with {mcuName}";
        // No board given, or a board this table has no display name for: the chip/board
        // identifier as written stands in for itself, same policy as DeviceConfig.Board's own
        // "empty is normal" rule.
        var boardPart = string.IsNullOrEmpty(config.Board) ? mcuName : config.Board;
        return $"{boardPart} with {mcuName}";
    }

    private static void RequireKnownStdlib(DeviceConfig config)
    {
        if (!IsKnownStdlib(config.Stdlib))
            throw UnreachableStdlib(config);
    }

    private static InvalidOperationException UnreachableStdlib(DeviceConfig config) =>
        new($"no compile-time answer for os.uname()/sys.platform: project has no " +
            $"CircuitPython/MicroPython compat layer selected (--stdlib), stdlib='{config.Stdlib}'");
}
