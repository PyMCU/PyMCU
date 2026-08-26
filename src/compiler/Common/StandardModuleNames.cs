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
/// Names that belong to the Python or MicroPython standard library and that PyMCU does not
/// implement.
///
/// They matter because of the advice they must NOT get. A module that is not found falls
/// through to "if it is a PyMCU library, install it into this project with `pymcu install
/// {name}`", which is sound for a name that really could be a library and useless for one
/// that could never be published as a package: no `pymcu install ustruct` will ever produce
/// `ustruct` (issue #189).
///
/// Only names that are NOT resolvable today belong here. `math`, `time`, `random`, `asyncio`
/// and `collections` are deliberately absent: they resolve to the pymcu stdlib under those
/// exact spellings, and saying they are unavailable would be a new wrong answer.
/// </summary>
public static class StandardModuleNames
{
    public enum Origin
    {
        Python,
        MicroPython,
    }

    private const string NotImplemented = "PyMCU does not implement it.";

    // Only where the reason is definite. A vague guess dressed as a reason is worse than the
    // plain statement, because a reader acts on it.
    private static readonly Dictionary<string, (Origin Origin, string Advice)> Known = new(StringComparer.Ordinal)
    {
        // No heap, so there is no object for these to build or return.
        ["struct"]      = (Origin.Python, "PyMCU has no heap, so there are no packed bytes to hand back; write the fields into a bytearray yourself."),
        ["ustruct"]     = (Origin.MicroPython, "PyMCU has no heap, so there are no packed bytes to hand back; write the fields into a bytearray yourself."),
        ["json"]        = (Origin.Python, "PyMCU has no heap, so there is no parsed object to build."),
        ["ujson"]       = (Origin.MicroPython, "PyMCU has no heap, so there is no parsed object to build."),

        // No operating system underneath.
        ["os"]          = (Origin.Python, "There is no operating system or filesystem on the target."),
        ["uos"]         = (Origin.MicroPython, "There is no operating system or filesystem on the target."),
        ["sys"]         = (Origin.Python, "There is no interpreter to introspect; the chip and its sizes are available through pymcu.chips."),
        ["socket"]      = (Origin.Python, "There is no general socket layer; networking is exposed per part through the HAL."),
        ["usocket"]     = (Origin.MicroPython, "There is no general socket layer; networking is exposed per part through the HAL."),
        ["threading"]   = (Origin.Python, "There are no OS threads; use the asyncio surface, which PyMCU does provide."),

        // Compile-time only in this dialect.
        ["typing"]      = (Origin.Python, "PyMCU reads annotations straight from the source, so nothing needs importing; the width names live in pymcu.types."),
        ["dataclasses"] = (Origin.Python, "Write a plain class with an __init__; PyMCU flattens it to fields at zero cost."),

        ["re"]          = (Origin.Python, NotImplemented),
        ["ure"]         = (Origin.MicroPython, NotImplemented),
        ["itertools"]   = (Origin.Python, NotImplemented),
        ["functools"]   = (Origin.Python, NotImplemented),
        ["logging"]     = (Origin.Python, NotImplemented),
        ["datetime"]    = (Origin.Python, NotImplemented),
        ["hashlib"]     = (Origin.Python, NotImplemented),
        ["uhashlib"]    = (Origin.MicroPython, NotImplemented),
        ["binascii"]    = (Origin.Python, NotImplemented),
        ["ubinascii"]   = (Origin.MicroPython, NotImplemented),
        ["heapq"]       = (Origin.Python, NotImplemented),
        ["uheapq"]      = (Origin.MicroPython, NotImplemented),
        ["io"]          = (Origin.Python, NotImplemented),
        ["uio"]         = (Origin.MicroPython, NotImplemented),
        ["select"]      = (Origin.Python, NotImplemented),
        ["uselect"]     = (Origin.MicroPython, NotImplemented),
        ["errno"]       = (Origin.Python, NotImplemented),
        ["uerrno"]      = (Origin.MicroPython, NotImplemented),
        ["ctypes"]      = (Origin.Python, NotImplemented),
        ["uctypes"]     = (Origin.MicroPython, NotImplemented),
        ["array"]       = (Origin.Python, "Use a bytearray, or a fixed-size list annotated with its element type."),
        ["gc"]          = (Origin.MicroPython, "There is no garbage collector to drive; PyMCU allocates statically."),

        // The u-spellings of modules that DO exist under their plain names, which is a
        // different answer again: the module is here, under the name CPython uses. `utime`
        // is deliberately absent: the micropython layer really does provide it, so it
        // resolves when the layer is present and gets the compat-package message when it is
        // not, and neither path should be intercepted here.
        ["urandom"]     = (Origin.MicroPython, "PyMCU provides it as `random`; `import random` works."),
        ["ucollections"] = (Origin.MicroPython, "PyMCU provides it as `collections`; `import collections` works."),
        ["uasyncio"]    = (Origin.MicroPython, "PyMCU provides it as `asyncio`; `import asyncio` works."),
    };

    public static bool TryDescribe(string moduleName, out Origin origin, out string advice)
    {
        if (Known.TryGetValue(moduleName, out var entry))
        {
            origin = entry.Origin;
            advice = entry.Advice;
            return true;
        }

        origin = Origin.Python;
        advice = "";
        return false;
    }

    public static string Describe(Origin origin)
        => origin == Origin.MicroPython ? "MicroPython standard" : "Python standard";
}
