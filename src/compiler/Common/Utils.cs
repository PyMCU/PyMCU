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

namespace PyMCU.Common;

public static class Utils
{
    public static string ReadSource(string pathStr)
    {
        if (!File.Exists(pathStr))
        {
            throw new Exception($"File not found: '{pathStr}'\nLooking in: {Path.GetFullPath(pathStr)}");
        }

        try
        {
            return File.ReadAllText(pathStr);
        }
        catch (Exception ex)
        {
            throw new Exception($"Permission denied or locked file: '{pathStr}'. Error: {ex.Message}");
        }
    }
}