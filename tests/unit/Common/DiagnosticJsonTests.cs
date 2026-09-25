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
using System.Text.Json;
using PyMCU.Common;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// What `--error-format json` promises a consumer that is not a person.
///
/// Asserted by PARSING the line rather than by matching its text, which is the whole point of
/// the format: a test that did a substring match would pass on output no JSON reader accepts,
/// and that is the exact failure the format exists to remove from the plugins.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public class DiagnosticJsonTests
{
    private static JsonElement Emit(CompilerError err, string source, string file = "test.py")
    {
        var buf = new StringWriter();
        var prevErr = Console.Error;
        var prevFormat = Diagnostic.Format;
        Console.SetError(buf);
        Diagnostic.Format = ErrorFormat.Json;
        try { Diagnostic.Report(err, source.AsSpan(), file); }
        finally { Console.SetError(prevErr); Diagnostic.Format = prevFormat; }

        // One object, one line. A consumer reads this stream line by line while the compiler
        // is still running, so a pretty-printed object spanning several lines would break it
        // even though it parses.
        string[] lines = buf.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }

    [Fact]
    public void Span_EndColumnIsExclusiveAndCoversTheToken()
    {
        // "def foo():" -- the error is on `foo`, column 5, three characters long. The text
        // format says this with tildes under the name and nothing an editor can read; here it
        // is a range, and [5, 8) is exactly `foo`.
        var err = new SyntaxError("test error", 1, 5, 3);
        var json = Emit(err, "def foo():");
        var span = json.GetProperty("span");

        Assert.Equal(1, span.GetProperty("line").GetInt32());
        Assert.Equal(5, span.GetProperty("column").GetInt32());
        Assert.Equal(1, span.GetProperty("end_line").GetInt32());
        Assert.Equal(8, span.GetProperty("end_column").GetInt32());
    }

    [Fact]
    public void Span_OmitsTheColumnTheCompilerDoesNotKnow()
    {
        // The one thing the text header CANNOT say. There the column field is mandatory, so an
        // unlocated diagnostic prints 1 and an editor cannot tell it from a measured column 1;
        // a caret is withheld for exactly that reason and the header cannot withhold anything.
        // Here the field is simply absent, and the consumer underlines the whole line.
        var err = new CompilerError("CompileError", "somewhere on this line", 2);
        var json = Emit(err, "a = 1\nb = 2\n");
        var span = json.GetProperty("span");

        Assert.Equal(2, span.GetProperty("line").GetInt32());
        Assert.False(span.TryGetProperty("column", out _));
        Assert.False(span.TryGetProperty("end_column", out _));
    }

    [Fact]
    public void Code_IsNullUntilASiteClaimsOne()
    {
        var json = Emit(new SyntaxError("test error", 1, 5, 3), "def foo():");
        Assert.Equal(JsonValueKind.Null, json.GetProperty("code").ValueKind);
    }

    [Fact]
    public void Fix_CarriesTheReplacementAndItsApplicability()
    {
        var err = new CompilerError("CompileError", "480 does not fit in 'us'", 1, 14, 3)
        {
            Code = "literal-too-wide-for-parameter",
            Fixes =
            [
                new SuggestedFix("narrow it on purpose", "", 1, 14, 3, "uint8(480)",
                                 FixApplicability.MaybeIncorrect),
            ],
        };
        var json = Emit(err, "    return f(480)");

        Assert.Equal("literal-too-wide-for-parameter", json.GetProperty("code").GetString());
        var fix = Assert.Single(json.GetProperty("fixes").EnumerateArray().ToList());
        Assert.Equal("maybe-incorrect", fix.GetProperty("applicability").GetString());

        var edit = Assert.Single(fix.GetProperty("edits").EnumerateArray().ToList());
        Assert.Equal("uint8(480)", edit.GetProperty("replacement").GetString());
        // An empty file on the edit means "the file the diagnostic is reported against". The
        // raising site holds the node; the path is resolved a phase later.
        Assert.Equal("test.py", edit.GetProperty("file").GetString());
        Assert.Equal(14, edit.GetProperty("column").GetInt32());
        Assert.Equal(17, edit.GetProperty("end_column").GetInt32());
    }

    [Fact]
    public void Related_CarriesTheSecondSiteAsDataRatherThanAsProse()
    {
        // The fact a message states as "(module guard at adc/__init__.py:36)". As prose it is
        // unreachable to an editor and it is what the driver's line-citation regular expression
        // exists to renumber; as data it is a second squiggle in a second file.
        var err = new CompilerError("CompileError", "the radio needs a Pico W", 6, 11, 5)
        {
            Code = "module-guard",
            Related = [new RelatedSpan("/p/radio.py", 2, 1, 5, "the module guard that refuses it")],
        };
        var json = Emit(err, "x = 1\n");

        var rel = Assert.Single(json.GetProperty("related").EnumerateArray().ToList());
        Assert.Equal("/p/radio.py", rel.GetProperty("file").GetString());
        Assert.Equal(2, rel.GetProperty("line").GetInt32());
        Assert.Equal(6, rel.GetProperty("end_column").GetInt32());
        Assert.Equal("the module guard that refuses it", rel.GetProperty("label").GetString());
    }

    [Fact]
    public void Rendered_CarriesTheTerminalTextWithoutEscapeCodes()
    {
        var json = Emit(new SyntaxError("test error", 1, 5, 3), "def foo():");
        string rendered = json.GetProperty("rendered").GetString()!;

        Assert.Contains("test.py:1:5: error: SyntaxError: test error", rendered);
        Assert.Contains("^~~", rendered);
        // The escape introducer, spelled by code point: `\x` in C# consumes up to four hex
        // digits, so writing it inline swallows whatever follows and the assertion silently
        // searches for something else.
        Assert.False(rendered.Contains((char)27), "rendered carries an ANSI escape: " + string.Join(",", rendered.Take(12).Select(c => (int)c)));
    }

    [Fact]
    public void InternalError_IsJsonToo_AndBlamesNoLineOfTheUsersFile()
    {
        // A compiler bug must not also break the consumer's parser: under `--error-format
        // json` EVERY line of stderr is JSON, including this one. And it carries no span. The
        // text form has to write `1:1` because its header cannot leave the fields out, which
        // is how a crash of ours ends up underlined on the first line of somebody's main.py.
        var buf = new StringWriter();
        var prevErr = Console.Error;
        var prevFormat = Diagnostic.Format;
        Console.SetError(buf);
        Diagnostic.Format = ErrorFormat.Json;
        try { Diagnostic.ReportInternal("something gave way", "main.py"); }
        finally { Console.SetError(prevErr); Diagnostic.Format = prevFormat; }

        var json = JsonDocument.Parse(buf.ToString().Trim()).RootElement;
        Assert.Equal("internal-compiler-error", json.GetProperty("code").GetString());
        Assert.Equal("InternalCompilerError", json.GetProperty("type").GetString());
        var span = json.GetProperty("span");
        Assert.Equal("main.py", span.GetProperty("file").GetString());
        Assert.Equal(0, span.GetProperty("line").GetInt32());
        Assert.False(span.TryGetProperty("column", out _));
    }

    [Fact]
    public void HumanFormat_IsUnchangedWhenJsonIsNotAskedFor()
    {
        // The regression this whole change has to avoid. Both shipped plugins parse the text
        // header with a mandatory column and no fallback branch, and a non-matching line
        // produces no problem at all rather than a visible failure.
        var buf = new StringWriter();
        var prevErr = Console.Error;
        var prevFormat = Diagnostic.Format;
        Console.SetError(buf);
        Diagnostic.Format = ErrorFormat.Human;
        try { Diagnostic.Report(new SyntaxError("test error", 1, 5, 3), "def foo():".AsSpan(), "test.py"); }
        finally { Console.SetError(prevErr); Diagnostic.Format = prevFormat; }

        string[] lines = buf.ToString().Split('\n');
        Assert.Equal("test.py:1:5: error: SyntaxError: test error", lines[0].TrimEnd());
        Assert.DoesNotContain("{\"version\"", buf.ToString());
    }
}
