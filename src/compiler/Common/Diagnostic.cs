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

using System.Text;

namespace PyMCU.Common;

/// How a diagnostic leaves the compiler.
public enum ErrorFormat
{
    /// The GCC-style header plus source snippet and caret. What a person reads, and what
    /// every shipped IDE integration parses today with a regular expression.
    Human,

    /// One JSON object per diagnostic, one per line, on stderr. What a machine reads.
    Json,
}

public static class Diagnostic
{
    /// The format every diagnostic of this run is emitted in. Set once from the command line
    /// before the pipeline starts, exactly like <see cref="Logger.Initialize"/>: the reporting
    /// sites are spread across every phase and none of them holds the options.
    public static ErrorFormat Format { get; set; } = ErrorFormat.Human;

    /// Maps CompilerError type_name to a VS Code severity string.
    internal static string SeverityFor(string typeName)
    {
        if (typeName == "Warning") return "warning";
        if (typeName == "Info" || typeName == "Note") return "info";
        return "error";
    }

    /// Emits a machine-readable diagnostic line for VS Code problem matcher,
    /// followed by human-readable context (source line + caret).
    /// Format: file:line:column: severity: ErrorType: message
    ///
    /// The two halves make different promises, so an unknown column is handled differently in
    /// each. The header is read by machines whose regex requires the column field; the caret is
    /// read by a person, who takes an arrow under a character as a claim about that character.
    /// A column the compiler does not know (<see cref="CompilerError.HasColumn"/> false) keeps
    /// the header's field, because dropping it would silently break shipped IDE integrations,
    /// and prints NO caret, because a caret on a guess is worse than no caret at all.
    public static void Report(CompilerError err, ReadOnlySpan<char> source, string filename)
    {
        // An error ends the compilation, so the run it comes from is the last one: the
        // warnings it held come first, in the order they were raised.
        ReleaseWarnings();
        if (Format == ErrorFormat.Json)
        {
            DiagnosticJson.Report(err, source, filename);
            return;
        }

        ReportHuman(err, source, filename, Console.Error, !Console.IsErrorRedirected);
    }

    /// The human rendering, with the destination and the colouring passed in.
    ///
    /// Both are parameters rather than read from the console, because the JSON format carries
    /// this same text in its `rendered` field: a consumer that wants to show exactly what the
    /// terminal shows should not have to re-derive it, and a consumer that shows its own
    /// squiggle drops the field. Capturing it by swapping <c>Console.Error</c> would have made
    /// the two formats race each other in a process that reports twice.
    internal static void ReportHuman(CompilerError err, ReadOnlySpan<char> source, string filename,
                                     TextWriter output, bool useColor)
    {
        bool columnKnown = err.HasColumn;
        string severity = SeverityFor(err.TypeName);

        // Machine-readable header (VS Code problem matcher).
        //
        // The column stays in the line even when it is not known. vscode-pymcu's problem
        // matcher and the JetBrains PyMcuConsoleFilter both match
        // `^(.+):(\d+):(\d+):\s+(error|warning|info):\s+(.+)$` with the column mandatory and no
        // fallback branch, and both ship on their own release cycle, so a column-less header
        // would empty the Problems panel of every extension already installed against an older
        // compiler -- and fail silently, since a non-matching line simply produces no problem.
        // 1 is the conventional stand-in for "somewhere on this line".
        int column = columnKnown ? err.Column : 1;
        string header = $"{filename}:{err.Line}:{column}: {severity}: {err.TypeName}: {err.Message}";
        if (useColor)
            output.WriteLine($"\x1b[1;31m{header}\x1b[0m");
        else
            output.WriteLine(header);

        string lineContent = GetLine(source, err.Line);
        if (string.IsNullOrEmpty(lineContent)) return;

        int lineNumWidth = err.Line.ToString().Length;

        // Context line N-1 (dimmed)
        string prevLine = GetLine(source, err.Line - 1);
        if (!string.IsNullOrEmpty(prevLine))
        {
            string prevFmt = $"{(err.Line - 1).ToString().PadLeft(lineNumWidth)} | {prevLine}";
            output.WriteLine(useColor ? $"\x1b[2m{prevFmt}\x1b[0m" : prevFmt);
        }

        // Current line
        output.WriteLine($"{err.Line.ToString().PadLeft(lineNumWidth)} | {lineContent}");

        // Pointer, drawn only when the column is a measurement rather than a placeholder.
        if (columnKnown)
        {
            // A column past the end of the line points at the position just after the last
            // character: that is where a missing token would have gone, and it is the furthest
            // right the line can honestly be marked.
            int caretColumn = Math.Min(column, lineContent.Length + 1);

            // The underline stops at the end of the line. A token length that runs past it is
            // either a multi-line construct or a stale length, and either way the tildes would
            // be drawn over blank space that has nothing to do with the error.
            int room = Math.Max(1, lineContent.Length - caretColumn + 1);
            int length = Math.Clamp(err.Length, 1, room);

            string pointerPad = CaretPad(lineContent, lineNumWidth + 3, caretColumn);
            string underline = length <= 1 ? "^" : "^" + new string('~', length - 1);
            if (useColor)
                output.WriteLine($"{pointerPad}\x1b[31m{underline}\x1b[0m");
            else
                output.WriteLine($"{pointerPad}{underline}");
        }

        // Context line N+1 (dimmed)
        string nextLine = GetLine(source, err.Line + 1);
        if (!string.IsNullOrEmpty(nextLine))
        {
            string nextFmt = $"{(err.Line + 1).ToString().PadLeft(lineNumWidth)} | {nextLine}";
            output.WriteLine(useColor ? $"\x1b[2m{nextFmt}\x1b[0m" : nextFmt);
        }
    }

    /// A warning, through the same door as every other diagnostic.
    ///
    /// Seven sites in the IR generator used to call `Console.Error.WriteLine` themselves, so
    /// a warning was plain text on stderr whatever `--error-format` said, and `stderr is the
    /// machine` was false for any program that compiles and warns. A float on AVR is enough
    /// to trigger one, so that is not a corner.
    ///
    /// It was invisible in the obvious test because the probes for the JSON format were all
    /// programs that FAIL, and a program that fails never reaches a warning.
    ///
    /// The three prefixes those sites used between them (`[pymcuc] warning:`, `warning:` and
    /// `[Warning]`) become one here. Nothing parses them: none has the `file:line:column:`
    /// shape the IDE problem matchers require, which is the other half of the same defect --
    /// a warning never reached the Problems panel at all.
    ///
    /// <paramref name="text"/> is everything after the prefix, exactly as the site wrote it,
    /// including any `line N:` clause it already carried. <paramref name="line"/> repeats that
    /// number as data for the JSON consumer, and stays 0 where the site does not know one; the
    /// duplication is deliberate, so that adopting this changes no terminal output.
    public static void Warning(string text, int line = 0, string? file = null, string? code = null)
    {
        if (heldWarnings != null)
        {
            heldWarnings.Add(() => EmitWarning(text, line, file, code));
            return;
        }
        EmitWarning(text, line, file, code);
    }

    // Warnings of a compilation run that may be thrown away: the driver runs the compilation
    // again when a run finds an unannotated slot too narrow (WidthSeeds), and each run raises
    // the same warnings. They are held until the driver knows which run is the last.
    private static List<Action>? heldWarnings;

    public static void HoldWarnings() => heldWarnings = new List<Action>();

    public static void DropHeldWarnings() => heldWarnings = null;

    public static void ReleaseWarnings()
    {
        var held = heldWarnings;
        heldWarnings = null;
        if (held == null) return;
        foreach (var w in held) w();
    }

    private static void EmitWarning(string text, int line, string? file, string? code)
    {
        if (Format == ErrorFormat.Json)
        {
            var warn = new CompilerError("Warning", text, line, CompilerError.Unlocated)
                { Code = code, File = file };
            DiagnosticJson.Report(warn, ReadOnlySpan<char>.Empty, file ?? string.Empty);
            return;
        }

        Console.Error.WriteLine($"[pymcuc] warning: {text}");
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
        if (Format == ErrorFormat.Json)
        {
            // A compiler bug must not also break the consumer's parser. Under `--error-format
            // json` every line of stderr is JSON, including this one, and the code says what
            // it is so an IDE can present it as "the compiler crashed" rather than as a defect
            // in the line it happens to name.
            ReportInternalJson($"{e.GetType().Name}: {e.Message}", filename);
            return;
        }

        Console.Error.WriteLine(
            $"{filename}:1:1: error: InternalCompilerError: {e.GetType().Name}: {e.Message}");

        if (Environment.GetEnvironmentVariable("PYMCU_VERBOSE") == "1" && e.StackTrace is { } st)
            Console.Error.WriteLine(st);
    }

    /// Overload for a bare message, where no exception was caught.
    public static void ReportInternal(string message, string filename)
    {
        if (Format == ErrorFormat.Json)
        {
            ReportInternalJson(message, filename);
            return;
        }

        Console.Error.WriteLine($"{filename}:1:1: error: InternalCompilerError: {message}");
    }

    /// An internal error as a diagnostic of its own kind.
    ///
    /// It carries NO span, deliberately. The text form has to write `1:1` because its header
    /// cannot leave the fields out, and that is how a crash of ours ends up underlined on the
    /// first line of somebody's main.py. Here the absence is sayable, so it is said.
    private static void ReportInternalJson(string message, string filename)
    {
        var err = new CompilerError("InternalCompilerError", message, 0, CompilerError.Unlocated)
            { Code = "internal-compiler-error", File = filename };
        DiagnosticJson.Report(err, ReadOnlySpan<char>.Empty, filename);
    }

    /// Builds the blank run that puts the caret under column <paramref name="column"/> of
    /// <paramref name="line"/>, given a gutter of <paramref name="gutterWidth"/> characters.
    ///
    /// The leading part is copied from the source line rather than counted, because a tab is
    /// one character but many columns: an indented line padded with spaces puts the caret
    /// wherever the count landed, which on a default terminal is seven columns left of the
    /// token it names. Copying each tab as a tab makes both lines hit the same tab stops
    /// whatever width the reader's terminal uses, which is the only way to be right without
    /// knowing that width.
    private static string CaretPad(string line, int gutterWidth, int column)
    {
        var sb = new StringBuilder(gutterWidth + column);
        sb.Append(' ', gutterWidth);
        int copy = Math.Min(column - 1, line.Length);
        for (int i = 0; i < copy; ++i)
            sb.Append(line[i] == (char)9 ? (char)9 : ' ');
        // Anything beyond the end of the line has no character to copy the width of.
        sb.Append(' ', Math.Max(0, column - 1 - copy));
        return sb.ToString();
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