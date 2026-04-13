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

public class CompilerError(string typeName, string message, int line, int column)
    : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
    public string TypeName { get; } = typeName;
}

public class SyntaxError(string message, int line, int column) : CompilerError("SyntaxError", message, line, column);

public class IndentationError(string message, int line, int column)
    : CompilerError("IndentationError", message, line, column);

public class LexicalError(string message, int line, int column) : CompilerError("LexicalError", message, line, column);