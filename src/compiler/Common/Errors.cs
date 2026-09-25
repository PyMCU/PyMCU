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

    /// A stable identifier for THIS refusal, in kebab case, or null where the site has not
    /// been given one yet.
    ///
    /// The message text is not one. It is edited whenever it reads badly, and every edit
    /// breaks whatever was keyed on it: an editor's "never show me this again", a docs anchor,
    /// a project-wide suppression, an issue that quotes it. A code separates the two promises
    /// -- the sentence may improve freely, the identifier may not change -- and it is the only
    /// field an IDE can offer "suppress" or "explain" against.
    ///
    /// Null is the honest default. A code invented per call site as the sites are touched
    /// would collide and drift; the ones that carry a code carry it because somebody decided
    /// what it names.
    public string? Code { get; init; }

    /// The other places the reader has to look, each with what it contributes.
    ///
    /// Some refusals already name a second site inside their sentence -- "module guard at
    /// adc/__init__.py:36", "already 3 for PD6 at line 43" -- and a sentence is where that
    /// fact goes to die for a machine: the driver has a regular expression whose only job is
    /// to renumber line citations inside message text (core/compiler.py), and an editor cannot
    /// make either one clickable. Carried as data, the same fact is a second squiggle.
    public IReadOnlyList<RelatedSpan> Related { get; init; } = [];

    /// The edits the message already proposes in prose, as edits.
    ///
    /// Our refusals routinely end in "or write `uint8(480)` if narrowing it to 224 is what you
    /// meant". That sentence IS a quick fix; today it exists only as text that the reader
    /// retypes. Carried as an edit, an IDE offers it as one keystroke.
    ///
    /// Applicability travels with it, because it is what keeps a wrong fix from being applied
    /// silently. PyMCU#280 measured that one in three user-facing refusals leads somewhere
    /// worse when followed literally, so a route this compiler is not sure about must arrive
    /// marked `maybe-incorrect` and never be applied by a "fix all".
    public IReadOnlyList<SuggestedFix> Fixes { get; init; } = [];
}

/// A second location a diagnostic points at, with the sentence fragment that says why.
///
/// `File` is a path that can be opened, not a display label: a related span whose file cannot
/// be resolved is a squiggle an editor cannot place.
public sealed record RelatedSpan(string File, int Line, int Column, int Length, string Label);

/// One proposed rewrite, as the text to put in place of a range.
///
/// `MaybeIncorrect` is the default and not `MachineApplicable`, so a site that does not think
/// about applicability gets the answer that cannot silently corrupt a program.
public sealed record SuggestedFix(
    string Label,
    string File,
    int Line,
    int Column,
    int Length,
    string Replacement,
    FixApplicability Applicability = FixApplicability.MaybeIncorrect);

/// How far an editor may go with a <see cref="SuggestedFix"/>, in rustc's three grades.
public enum FixApplicability
{
    /// Safe to apply without asking, including in a "fix all in file".
    MachineApplicable,

    /// Offer it, apply it only on an explicit choice. The compiler believes it, and PyMCU#280
    /// is the measurement of how often that belief is wrong.
    MaybeIncorrect,

    /// The replacement contains something the reader has to fill in.
    HasPlaceholders,
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
/// Filesystem failures on a path the compiler was TOLD to write: the output assembly and the
/// `--emit-ir` file.
///
/// Every one of them used to surface as `InternalCompilerError: IOException: <.NET's words>`,
/// which is wrong twice over. It is not an internal error -- nothing in the compiler is
/// broken, the environment refused the write -- and the header names the SOURCE file, so the
/// one fact the reader needs, WHICH path could not be written, appears nowhere in the
/// diagnostic. That is how #498 hid: two test suites in parallel handed pymcuc the same
/// output path, the loser of the race reported a compiler crash on the user's main.py, and
/// the failure looked like a miscompilation of whatever test happened to lose.
///
/// A read of an INPUT file must not come through here. The message asserts the path is an
/// output, and a guard drawn wide enough to catch a runtime fragment the backend reads would
/// make that assertion a lie -- so each guard wraps the call that opens or writes the named
/// file and nothing else. See <see cref="Guard"/> for what that leaves out.
public static class OutputFile
{
    /// Runs <paramref name="write"/>, reporting a filesystem refusal of <paramref name="path"/>
    /// as a user diagnostic that names the path and says which of the two things failed.
    ///
    /// The location is line 0, column <see cref="CompilerError.Unlocated"/>: no line of the
    /// source is responsible for this and none is pointed at. `PythonAstReader` reports a
    /// python that will not start the same way.
    ///
    /// What this does NOT cover: a buffer that fills mid-codegen and flushes early. The write
    /// syscall then happens inside the backend, outside this guard, and a disk that fills at
    /// that exact moment is still reported as an internal error. Widening the guard to the
    /// whole of codegen would put every IOException the backend can raise -- reading a runtime
    /// `.S`, say -- under a message that swears the output file was at fault, which trades a
    /// rare wrong label for a common one.
    public static void Guard(string path, string action, Action write)
    {
        try
        {
            write();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                  or NotSupportedException or ArgumentException)
        {
            throw new CompilerError("OSError",
                $"cannot {action} the output file '{path}': {e.Message}", 0, CompilerError.Unlocated);
        }
    }
}
