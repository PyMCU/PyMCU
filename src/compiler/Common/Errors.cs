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

// The `column = 0` default is CompilerError.Unlocated, spelled as a literal because a primary
// constructor's parameter default cannot name a member of the type it is declaring.
public class CompilerError(string typeName, string message, int line, int column = 0, int length = 1)
    : Exception(message)
{
    /// The column of an error whose position within the line is not known. Most diagnostics
    /// are raised well after parsing, from a phase that holds the statement's line and nothing
    /// finer, and there is no honest answer for them to give. Saying so is the point: a caret
    /// is an arrow drawn under one character, a reader takes it as a claim about THAT
    /// character, and the compiler that draws it under an innocent one has told a lie that
    /// costs more than the silence would have. Diagnostic.Report prints no caret for this.
    ///
    /// Prefer passing a real column wherever a token or an AST node is in hand. Never pass 1
    /// to mean "no idea": that is the value this constant exists to replace.
    public const int Unlocated = 0;

    public int Line { get; } = line;
    public int Column { get; } = column;
    public int Length { get; } = length;
    public string TypeName { get; } = typeName;

    /// True when <see cref="Column"/> is a measurement rather than a placeholder.
    public bool HasColumn => Column > 0;

    /// The file the error is IN, when that is not the entry file. Diagnostics are printed
    /// against the entry file by default, which is right for everything the entry file
    /// contains and wrong for an import that lives in another module: the reader was sent
    /// to a line of main.py that never mentioned the name in the message.
    public string? File { get; init; }

    /// True when this error chose its own file and line and they must not be filled in for it.
    ///
    /// `File == null` used to carry that meaning by implication, and it carried two others at
    /// the same time: `CompilerPhaseBase` read it as "the entry file" and
    /// `DependencyGraphBuilder` read it as "this error has no location of its own". One null,
    /// three readings, and a site that deliberately reports somewhere other than where it is
    /// raised had no way to say so except by leaving the null alone and hoping. This property
    /// says it. Issue #230.
    public bool LocationIsFinal { get; init; }
}

public class SyntaxError(string message, int line, int column = CompilerError.Unlocated, int length = 1)
    : CompilerError("SyntaxError", message, line, column, length);

public class IndentationError(string message, int line, int column = CompilerError.Unlocated, int length = 1)
    : CompilerError("IndentationError", message, line, column, length);

public class LexicalError(string message, int line, int column = CompilerError.Unlocated, int length = 1)
    : CompilerError("LexicalError", message, line, column, length);

public class ArchitectureError(string message, int line, int column = CompilerError.Unlocated, int length = 1)
    : CompilerError("CompileError", message, line, column, length);

public class ValueError(string message, int line, int column = CompilerError.Unlocated, int length = 1)
    : CompilerError("ValueError", message, line, column, length);

public class TypeError(string message, int line, int column = CompilerError.Unlocated, int length = 1)
    : CompilerError("TypeError", message, line, column, length);

public class RecursionError(string message, int line, int column = CompilerError.Unlocated, int length = 1)
    : CompilerError("RecursionError", message, line, column, length);

public class NameError(string message, int line, int column = CompilerError.Unlocated, int length = 1)
    : CompilerError("NameError", message, line, column, length);

public class IndexError(string message, int line, int column = CompilerError.Unlocated, int length = 1)
    : CompilerError("IndexError", message, line, column, length);