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
/// Simple console logger with support for normal and verbose modes.
/// All output goes to Console.Out (stdout), errors go to Console.Error (stderr).
/// </summary>
public static class Logger
{
    private static bool _isVerbose;

    /// <summary>
    /// Initialize the logger with the verbose mode setting.
    /// Must be called early in the compilation pipeline.
    /// </summary>
    public static void Initialize(bool verbose)
    {
        _isVerbose = verbose;
    }

    /// <summary>
    /// Log a normal message. Always printed regardless of verbose mode.
    /// Format: [ComponentName] Message
    /// </summary>
    public static void Info(string component, string message)
    {
        Console.WriteLine($"[{component}] {message}");
    }

    /// <summary>
    /// Log a verbose/debug message. Only printed when verbose mode is enabled.
    /// Format: [ComponentName] Message
    /// </summary>
    public static void Verbose(string component, string message)
    {
        if (_isVerbose)
        {
            Console.WriteLine($"[{component}] {message}");
        }
    }

    /// <summary>
    /// Log an error message to stderr. Always printed.
    /// Format: [ComponentName] Error: Message
    /// </summary>
    public static void Error(string component, string message)
    {
        Console.Error.WriteLine($"[{component}] Error: {message}");
    }

    /// <summary>
    /// Log a warning message. Always printed.
    /// Format: [ComponentName] Warning: Message
    /// </summary>
    public static void Warning(string component, string message)
    {
        Console.WriteLine($"[{component}] Warning: {message}");
    }
}

