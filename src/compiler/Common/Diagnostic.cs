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

public static class Diagnostic
{
    /// Maps CompilerError type_name to a VS Code severity string.
    private static string SeverityFor(string typeName)
    {
        if (typeName == "Warning") return "warning";
        if (typeName == "Info" || typeName == "Note") return "info";
        return "error";
    }

    /// Emits a machine-readable diagnostic line for VS Code problem matcher,
    /// followed by human-readable context (source line + caret).
    /// Format: file:line:column: severity: ErrorType: message
    public static void Report(CompilerError err, ReadOnlySpan<char> source, string filename)
    {
        int column = Math.Max(err.Column, 1);
        string severity = SeverityFor(err.TypeName);
        bool useColor = !Console.IsErrorRedirected;

        // Machine-readable header (VS Code problem matcher)
        string header = $"{filename}:{err.Line}:{column}: {severity}: {err.TypeName}: {err.Message}";
        if (useColor)
            Console.Error.WriteLine($"\x1b[1;31m{header}\x1b[0m");
        else
            Console.Error.WriteLine(header);

        string lineContent = GetLine(source, err.Line);
        if (string.IsNullOrEmpty(lineContent)) return;

        int lineNumWidth = err.Line.ToString().Length;

        // Context line N-1 (dimmed)
        string prevLine = GetLine(source, err.Line - 1);
        if (!string.IsNullOrEmpty(prevLine))
        {
            string prevFmt = $"{(err.Line - 1).ToString().PadLeft(lineNumWidth)} | {prevLine}";
            Console.Error.WriteLine(useColor ? $"\x1b[2m{prevFmt}\x1b[0m" : prevFmt);
        }

        // Current line
        Console.Error.WriteLine($"{err.Line.ToString().PadLeft(lineNumWidth)} | {lineContent}");

        // Pointer: lineNumWidth + " | " (3 chars) + (column - 1) spaces
        string pointerPad = new string(' ', lineNumWidth + 3 + column - 1);
        string underline = err.Length <= 1
            ? "^"
            : "^" + new string('~', err.Length - 1);
        if (useColor)
            Console.Error.WriteLine($"{pointerPad}\x1b[31m{underline}\x1b[0m");
        else
            Console.Error.WriteLine($"{pointerPad}{underline}");

        // Context line N+1 (dimmed)
        string nextLine = GetLine(source, err.Line + 1);
        if (!string.IsNullOrEmpty(nextLine))
        {
            string nextFmt = $"{(err.Line + 1).ToString().PadLeft(lineNumWidth)} | {nextLine}";
            Console.Error.WriteLine(useColor ? $"\x1b[2m{nextFmt}\x1b[0m" : nextFmt);
        }
    }

    /// Overload for internal compiler errors (no source location).
    ///
    /// An InternalCompilerError is by definition a compiler bug, and its message alone is
    /// often the least informative part of it: "Unable to find the specified file" is what
    /// .NET says both for a source file that is not there and for a Process.Start whose
    /// executable is not there, and the two are not the same problem. The exception TYPE
    /// separates them in one token and costs nothing, so it is always printed; the stack,
    /// which says WHICH call it was, follows under PYMCU_VERBOSE=1 rather than in every
    /// user's face.
    public static void ReportInternal(Exception e, string filename)
    {
        Console.Error.WriteLine(
            $"{filename}:1:1: error: InternalCompilerError: {e.GetType().Name}: {e.Message}");

        if (Environment.GetEnvironmentVariable("PYMCU_VERBOSE") == "1" && e.StackTrace is { } st)
            Console.Error.WriteLine(st);
    }

    /// Overload for a bare message, where no exception was caught.
    public static void ReportInternal(string message, string filename)
    {
        Console.Error.WriteLine($"{filename}:1:1: error: InternalCompilerError: {message}");
    }

    private static string GetLine(ReadOnlySpan<char> src, int targetLine)
    {
        int current = 1;
        int start = 0;
        for (int i = 0; i < src.Length; ++i)
        {
            if (src[i] == (char)10)
            {
                if (current == targetLine)
                    return src.Slice(start, i - start).ToString();
                current++;
                start = i + 1;
            }
        }

        if (current == targetLine)
            return src.Slice(start).ToString();
        return string.Empty;
    }
}