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

/// <summary>
/// The runtime exception types the IR generator predefines, with the codes it raises them
/// under. They are Python builtins and need no import, so `pymcu/exceptions.py` deliberately
/// does not redeclare them.
///
/// Shared rather than written twice: the import check has to know that these names resolve
/// whether or not the module an import names defines them. Two shipped examples say
/// `from pymcu.exceptions import ValueError`, which CPython would reject and this compiler
/// honours, and a second copy of the list would eventually disagree with this one.
///
/// It lives in the SDK rather than in the compiler because the BACKENDS need it too, and the
/// prediction above came true twice while it did not: each backend hand-copied the list into
/// its own `ExnCodeName`, both stopped at 5, and an uncaught ZeroDivisionError therefore
/// printed `E:Exception6` on AVR and on RP2040 (PyMCU#260). The namespace is unchanged, so
/// every existing call site in the compiler still resolves; what changes is that a backend
/// can now derive instead of copy.
/// </summary>
public static class BuiltinExceptionNames
{
    public static readonly IReadOnlyDictionary<string, int> Codes = new Dictionary<string, int>
    {
        ["ValueError"]           = 1,
        ["TypeError"]            = 2,
        ["IndexError"]           = 3,
        ["KeyError"]             = 4,
        ["NotImplementedError"]  = 5,
        ["ZeroDivisionError"]    = 6,

        // 7-10 exist because the CircuitPython libraries need them, not because PyMCU raises
        // them. Nothing in the compiler or the runtime signals these; they are names user code
        // can raise and catch. `ImportError` is the one that unblocks real libraries: almost
        // every Adafruit module opens with `try: from typing import ... except ImportError:`,
        // and PyMCU already tolerates a failing import inside a `try` -- the only thing missing
        // was a name for the handler to catch.
        ["ImportError"]          = 7,
        ["RuntimeError"]         = 8,
        ["OSError"]              = 9,
        ["AttributeError"]       = 10,

        // ArithmeticError is the base CPython puts above OverflowError, ZeroDivisionError
        // and FloatingPointError; drivers raise it directly for a failed computation
        // (adafruit_bmp280's calibration check). Exception codes are flat here -- there is
        // no subclass matching -- so `except ArithmeticError` does not catch a raised
        // ZeroDivisionError the way CPython would.
        ["ArithmeticError"]      = 11,

        // Raised by the arena allocator (lib/src/pymcu/arena.py) when a runtime-sized
        // bytearray() would overflow the static arena. See docs/rfcs/0004-arena-allocator.md.
        ["MemoryError"]          = 12,

        // 13 exists for the same reason 7-10 do: a handler has to name it to compile.
        // adafruit_framebuf's BitmapFont wraps the embedded-font open() in
        // `except OverflowError` (CircuitPython shrinks long fonts); with romfs the open is
        // resolved at compile time and the handler is dead code, but the name must still
        // resolve for the try to lower at all (docs/rfcs/0008-embedded-files.md).
        ["OverflowError"]        = 13,

        // CPython and MicroPython both put TimeoutError UNDER OSError, and drivers lean on
        // that: `except OSError` around an I2C transaction is what catches a timed-out
        // transfer. It is the only builtin with a builtin parent (see Parents below) --
        // the flat table above stays flat for every other type.
        ["TimeoutError"]         = 14,
    };

    /// <summary>
    /// The one built-in subclass edge this model answers: code -> parent code, for the
    /// OSError subtree only. CPython's full hierarchy (ZeroDivisionError under
    /// ArithmeticError, IndexError/KeyError under LookupError, ...) is intentionally NOT
    /// here: matching it would make every `except ArithmeticError` pay comparisons the flat
    /// model never emitted, which breaks the promise that a program that does not touch the
    /// feature keeps byte-identical firmware. User classes get their edge when they are
    /// scanned -- and only while the chain stays inside this subtree, so `except OSError`
    /// catches a raised TimeoutError and nothing else about dispatch changes.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Parents = new Dictionary<string, string>
    {
        ["TimeoutError"] = "OSError",
    };

    /// <summary>
    /// The name a code was raised under, for the unhandled-exception message a backend prints.
    /// Backends MUST call this rather than keeping their own switch: the two that did drifted
    /// (see the type docstring). Unknown codes -- user exception classes, which start at 32 --
    /// have no name here and get the caller's fallback.
    /// </summary>
    public static bool TryGetName(int code, out string name)
    {
        foreach (var kv in Codes)
            if (kv.Value == code) { name = kv.Key; return true; }
        name = "";
        return false;
    }

    public static bool IsBuiltin(string name) => Codes.ContainsKey(name);
}
