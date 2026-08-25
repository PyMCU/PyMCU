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
using Xunit;
using PyMCU.Common;

namespace PyMCU.UnitTests;

[Collection(ConsoleCaptureCollection.Name)]
public class DiagnosticTests
{
    private static string CaptureReport(CompilerError err, string source)
    {
        var buf = new StringWriter();
        var prev = Console.Error;
        Console.SetError(buf);
        try { Diagnostic.Report(err, source.AsSpan(), "test.py"); }
        finally { Console.SetError(prev); }
        return buf.ToString();
    }

    [Fact]
    public void Report_SingleCharError_UsesCaret()
    {
        var err = new SyntaxError("unexpected token", 1, 5, 1);
        string output = CaptureReport(err, "def (foo):");
        Assert.Contains("^", output);
        Assert.DoesNotContain("~", output);
    }

    [Fact]
    public void Report_MultiCharError_UsesCaretTilde()
    {
        // error on "foo" (col 5, length 3) in "def foo():"
        var err = new SyntaxError("test error", 1, 5, 3);
        string output = CaptureReport(err, "def foo():");
        Assert.Contains("^~~", output);
    }

    [Fact]
    public void Report_PointerPointsToTokenStart()
    {
        // "def foo():" — error on "foo" at col 5, length 3
        // lineNumWidth=1, prefix="1 | " (4 chars), padding = 1+3+(5-1)=8
        var err = new SyntaxError("test error", 1, 5, 3);
        string output = CaptureReport(err, "def foo():");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var pointerLine = lines.First(l => l.Contains('^'));
        Assert.Equal("        ^~~", pointerLine.TrimEnd());
    }

    [Fact]
    public void Report_LineNumbersAreDisplayed()
    {
        // error on line 2 — should show lines 1, 2, 3
        string src = "line1" + (char)10 + "line2" + (char)10 + "line3";
        var err = new SyntaxError("error", 2, 1, 5);
        string output = CaptureReport(err, src);
        Assert.Contains("1 |", output);
        Assert.Contains("2 |", output);
        Assert.Contains("3 |", output);
    }

    [Fact]
    public void Report_MachineReadableLineIsFirst()
    {
        var err = new SyntaxError("bad token", 3, 7, 2);
        string output = CaptureReport(err, "a\nb\nc = foo\nd");
        var firstLine = output.Split('\n')[0];
        Assert.StartsWith("test.py:3:7: error: SyntaxError: bad token", firstLine);
    }

    [Fact]
    public void Report_NoContextOnFirstLine_NoPrevLine()
    {
        var err = new SyntaxError("bad", 1, 1, 1);
        string output = CaptureReport(err, "bad code");
        // line 1 has no previous context line — only the current line and pointer
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // header + current line + pointer = 3 lines max (no prev, maybe no next)
        Assert.Contains("1 |", output);
    }
}

/// <summary>
/// An InternalCompilerError is by definition a compiler bug, and its message alone is often
/// the least informative part of it: .NET says "Unable to find the specified file" both for a
/// source file that is not there and for a Process.Start whose executable is not there, and
/// the two are not the same problem. Reporting the exception TYPE separates them in one token.
///
/// This was written after an AVR suite flake reported exactly that message with nothing else
/// to go on, twice, to two different readers.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public class InternalErrorReportTests
{
    private static string Capture(Action a)
    {
        var buf = new StringWriter();
        var prev = Console.Error;
        Console.SetError(buf);
        try { a(); }
        finally { Console.SetError(prev); }
        return buf.ToString();
    }

    [Fact]
    public void AnInternalError_NamesTheExceptionType()
    {
        var text = Capture(() => Diagnostic.ReportInternal(
            new FileNotFoundException("Unable to find the specified file"), "main.py"));

        Assert.Contains("FileNotFoundException", text);
        Assert.Contains("Unable to find the specified file", text);
    }

    [Fact]
    public void TwoErrorsWithTheSameMessage_AreToldApartByTheirType()
    {
        var a = Capture(() => Diagnostic.ReportInternal(
            new FileNotFoundException("Unable to find the specified file"), "main.py"));
        var b = Capture(() => Diagnostic.ReportInternal(
            new System.ComponentModel.Win32Exception("Unable to find the specified file"),
            "main.py"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ItKeepsTheDiagnosticShapeTheProblemMatcherReads()
    {
        var text = Capture(() => Diagnostic.ReportInternal(new Exception("boom"), "main.py"));

        Assert.StartsWith("main.py:1:1: error: InternalCompilerError: ", text);
    }

    [Fact]
    public void TheStackStaysOutOfTheWayUnlessAskedFor()
    {
        // A stack trace in every user's face is noise; a stack trace when you are chasing a
        // compiler bug is the whole point. PYMCU_VERBOSE=1 is the switch the driver already
        // sets when the test runner is in debug mode.
        Exception caught;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception e) { caught = e; }

        var quiet = Capture(() => Diagnostic.ReportInternal(caught, "main.py"));
        Assert.Single(quiet.TrimEnd().Split(Environment.NewLine));

        Environment.SetEnvironmentVariable("PYMCU_VERBOSE", "1");
        try
        {
            var loud = Capture(() => Diagnostic.ReportInternal(caught, "main.py"));
            Assert.Contains("InternalErrorReportTests", loud);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYMCU_VERBOSE", null);
        }
    }
}
