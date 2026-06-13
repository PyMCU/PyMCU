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

public class CompilerError(string typeName, string message, int line, int column, int length = 1)
    : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
    public int Length { get; } = length;
    public string TypeName { get; } = typeName;
}

public class SyntaxError(string message, int line, int column, int length = 1)
    : CompilerError("SyntaxError", message, line, column, length);

public class IndentationError(string message, int line, int column, int length = 1)
    : CompilerError("IndentationError", message, line, column, length);

public class LexicalError(string message, int line, int column, int length = 1)
    : CompilerError("LexicalError", message, line, column, length);

public class ArchitectureError(string message, int line, int column, int length = 1)
    : CompilerError("CompileError", message, line, column, length);

public class ValueError(string message, int line, int column, int length = 1)
    : CompilerError("ValueError", message, line, column, length);

public class TypeError(string message, int line, int column, int length = 1)
    : CompilerError("TypeError", message, line, column, length);

public class RecursionError(string message, int line, int column, int length = 1)
    : CompilerError("RecursionError", message, line, column, length);

public class NameError(string message, int line, int column, int length = 1)
    : CompilerError("NameError", message, line, column, length);