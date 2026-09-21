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
    // The board name, when the caller knows it. OPTIONAL, and with a default rather than
    // positional: empty is the normal case, because a project sets `board` or `target` and the
    // driver refuses both at once, so a program built by target has no board to give.
    //
    // It also has to be optional for a duller reason worth keeping: five test files build this
    // record by name, and a required parameter would have made them all stop compiling for a
    // field none of them has an opinion about. A field that is genuinely optional in the model
    // should be optional in the signature.
    string Board = "",
    string? EmitIrPath = null,
    // The project's own source directory. Modules loaded from inside it are the USER's, and
    // only those have their module level executed on import; everything else is an installed
    // distribution (the pymcu stdlib, the MicroPython and CircuitPython compat layers), which
    // is written knowing that only the entry file's top level runs. The driver stages the entry
    // file into dist/_generated while the imports still resolve out of src/, so the entry
    // file's own directory is not enough on its own. Absent, the entry file's directory is used.
    string? ProjectRoot = null,
    // The program runs the millisecond time base (millis_init(), injected or explicit).
    // Bound as __TIMEBASE__ for the stdlib; the PWM HAL refuses a Timer0 frequency that
    // would reprogram the prescaler under the clock (PyMCU#295). Optional, like Board.
    bool Timebase = false,
    // Library mode: the unit has no entry point. Every top-level function of the entry file is
    // a root and keeps its source name, and nothing is injected around them -- no synthesized
    // main, no HAL init. Without it a file that only defines functions compiles to nothing,
    // because dead-code elimination roots `main` and the functions marked @used, and a library
    // has neither. Used by the natmod emitter, whose entry point is the loader's `mpy_init`.
    bool Library = false,
    // Which CircuitPython/MicroPython compat layer, if any, the project builds against:
    // "circuitpython", "micropython", or "" for neither. The driver already resolves this
    // (`stdlib_flavors` in build.py) and already refuses a project that names two at once,
    // so it is a single build-wide fact, exactly like Board or Arch.
    //
    // Used by CompileTimeEvaluator to fold `sys.implementation.name`,
    // `sys.implementation.version`, `sys.platform` and `os.uname()` the same way __CHIP__ is
    // folded (docs/rfcs/0007): the compiler substitutes the real per-board value directly,
    // it never parses the compat layer's own sys.py/os.py to derive one. Optional, like Board.
    string Stdlib = "",
    // RFC 0008 romfs: "NAME=PATH" pairs the driver resolved for this build. open(NAME)
    // resolves at compile time to a handle over the blob read from PATH. The compiler keys
    // the table by NAME exactly as open() receives it; a name open() never reaches embeds
    // nothing (the blob is only emitted when a handle over it is created).
    List<string>? Embeds = null,
    // Path to a PGO profile JSON (from `pymcu profile --pgo`). Read by the IR
    // generation phase and handed to the optimizer; a file that does not parse
    // or whose block names share nothing with this program is warned about and
    // ignored -- a profile must never fail a build. Optional, like Board.
    string? ProfilePath = null
);