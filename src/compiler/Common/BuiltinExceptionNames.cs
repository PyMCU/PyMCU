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
    };

    public static bool IsBuiltin(string name) => Codes.ContainsKey(name);
}
