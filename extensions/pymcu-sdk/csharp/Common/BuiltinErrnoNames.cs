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
/// The errno codes and symbolic names MicroPython ships in py/mperrno.h -- the set
/// lib/src/pymcu/errno.py exposes as `errno.E*` and `errno.errorcode`, and the names the
/// `[Errno n] NAME` text of print(OSError(n)) is rendered from. MicroPython's values are
/// used rather than the host's, because the compat layers (pymcu-micropython) implement
/// MicroPython, and a portable program must see one table whichever layer resolves the
/// import.
///
/// The table lives here, next to BuiltinExceptionNames, so a backend printing an unhandled
/// OSError can spell the same names the stdlib module defines -- one list, not two that
/// drift.
/// </summary>
public static class BuiltinErrnoNames
{
    public static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [1]   = "EPERM",
        [2]   = "ENOENT",
        [5]   = "EIO",
        [9]   = "EBADF",
        [11]  = "EAGAIN",
        [12]  = "ENOMEM",
        [13]  = "EACCES",
        [17]  = "EEXIST",
        [19]  = "ENODEV",
        [21]  = "EISDIR",
        [22]  = "EINVAL",
        [95]  = "EOPNOTSUPP",
        [98]  = "EADDRINUSE",
        [103] = "ECONNABORTED",
        [104] = "ECONNRESET",
        [105] = "ENOBUFS",
        [107] = "ENOTCONN",
        [110] = "ETIMEDOUT",
        [111] = "ECONNREFUSED",
        [113] = "EHOSTUNREACH",
        [114] = "EALREADY",
        [115] = "EINPROGRESS",
    };

    /// The symbolic name a known code prints, or null for a code outside the table -- which
    /// MicroPython prints as the bare integer.
    public static bool TryGetName(int code, out string name)
    {
        if (Names.TryGetValue(code, out var n)) { name = n; return true; }
        name = "";
        return false;
    }
}
