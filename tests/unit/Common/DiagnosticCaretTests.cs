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

using System.IO;
using System.Linq;
using Xunit;
using PyMCU.Common;

namespace PyMCU.UnitTests;

/// <summary>
/// The caret is the one part of a diagnostic that claims to know WHERE in the line the
/// problem is. A reader trusts it: it is an arrow drawn under a specific character. So the
/// rule these tests pin is that the caret is never drawn on a guess. A column the compiler
/// does not know (0 or negative) prints no caret at all, and the reader is left with a line
/// number, which is true, instead of an arrow under an innocent character, which is not.
///
/// The machine-readable header is a different channel with a different contract: the VS Code
/// problem matcher and the JetBrains console filter both require `file:line:column:` with the
/// column present, and they ship independently of the compiler, so an old install would break
/// against a new compiler if the field ever disappeared. The header therefore keeps a column
/// always. Only the human-facing caret is allowed to stay silent.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public class DiagnosticCaretTests
{
    private static string Capture(CompilerError err, string source)
    {
        var buf = new StringWriter();
        var prev = Console.Error;
        Console.SetError(buf);
        try { Diagnostic.Report(err, source.AsSpan(), "test.py"); }
        finally { Console.SetError(prev); }
        return buf.ToString();
    }

    private static string? PointerLine(string output) =>
        output.Split('\n').FirstOrDefault(l => l.Contains('^'));

    // ---- an unknown column draws nothing ------------------------------------------------

    [Fact]
    public void AColumnOfZero_MeansUnknown_AndDrawsNoCaret()
    {
        // 20 error sites in the compiler pass a literal 0 for the column today. Every one of
        // them used to be laundered into a confident caret under column 1 by Math.Max(col, 1).
        var err = new SyntaxError("something is wrong", 1, 0);
        string output = Capture(err, "def foo():");

        Assert.DoesNotContain("^", output);
        Assert.Contains("1 | def foo():", output);
    }

    [Fact]
    public void ANegativeColumn_IsAlsoUnknown_AndDrawsNoCaret()
    {
        var err = new SyntaxError("something is wrong", 1, -3);
        Assert.DoesNotContain("^", Capture(err, "def foo():"));
    }

    [Fact]
    public void AnUnknownColumn_StillPrintsTheHeaderColumnTheProblemMatcherRequires()
    {
        // vscode-pymcu package.json and the JetBrains PyMcuConsoleFilter both match
        // ^(.+):(\d+):(\d+):\s+(error|warning|info):\s+(.+)$ with no optional column.
        var err = new SyntaxError("something is wrong", 1, 0);
        string first = Capture(err, "def foo():").Split('\n')[0];

        Assert.StartsWith("test.py:1:1: error: SyntaxError: something is wrong", first);
    }

    [Fact]
    public void AKnownColumn_StillDrawsItsCaret()
    {
        // The silence is only for the unknown case; a real column must still point.
        var err = new SyntaxError("bad name", 1, 5, 3);
        Assert.Equal("        ^~~", PointerLine(Capture(err, "def foo():"))?.TrimEnd());
    }

    // ---- tabs ---------------------------------------------------------------------------

    [Fact]
    public void ATabBeforeTheColumn_IsCopiedIntoThePadSoTheCaretLandsUnderTheCharacter()
    {
        // A tab is one CHARACTER but eight COLUMNS on a terminal. Padding with spaces counts
        // characters and puts the caret seven columns to the left of the thing it names. The
        // pad has to be built from the source line, copying each tab as a tab, so both lines
        // hit the same tab stops whatever the terminal's tab width happens to be.
        var err = new SyntaxError("bad name", 1, 2, 3);
        string pointer = PointerLine(Capture(err, "\tfoo = 1"))!;

        Assert.Equal("    \t^~~", pointer.TrimEnd());
    }

    [Fact]
    public void MixedTabsAndSpaces_KeepTheirShapeInThePad()
    {
        //  "\t \tx" -- the pad must be "\t \t" so the caret sits under x on any tab width.
        var err = new SyntaxError("bad", 1, 4, 1);
        string pointer = PointerLine(Capture(err, "\t \tx = 1"))!;

        Assert.Equal("    \t \t^", pointer.TrimEnd());
    }

    // ---- out of range -------------------------------------------------------------------

    [Fact]
    public void AColumnPastTheEndOfTheLine_ClampsToJustPastTheLastCharacter()
    {
        // "abc" is 3 characters, so the furthest a caret can honestly point is column 4, the
        // position where a missing token would have gone.
        var err = new SyntaxError("expected ':'", 1, 99);
        string pointer = PointerLine(Capture(err, "abc"))!;

        Assert.Equal("    " + new string(' ', 3) + "^", pointer.TrimEnd());
    }

    [Fact]
    public void AnUnderlineRunningPastTheEndOfTheLine_StopsAtTheLineEnd()
    {
        // Length 50 on a 3-character line drew 50 tildes into empty space.
        var err = new SyntaxError("bad", 1, 2, 50);
        string pointer = PointerLine(Capture(err, "abc"))!;

        Assert.Equal("     ^~", pointer.TrimEnd());
    }

    [Fact]
    public void AnUnderlineThatFitsExactly_IsNotShortened()
    {
        var err = new SyntaxError("bad", 1, 1, 3);
        Assert.Equal("    ^~~", PointerLine(Capture(err, "abc"))?.TrimEnd());
    }
}
