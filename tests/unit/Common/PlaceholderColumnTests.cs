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
using System.Text.RegularExpressions;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#177. A diagnostic must not pass 1 as a stand-in for a column it does not know.
///
/// Column 1 is not a neutral default: it is greater than zero, so Diagnostic.Report treats it
/// as a measurement and DRAWS A CARET under the first character of the line. For an indented
/// statement that character is whitespace, and the arrow claims the error is there. 58 sites
/// did this, and `b: uint8 = a // 0` reported the division by zero with a caret on the indent
/// while the `//` sat at column 18.
///
/// The rule is CompilerError.Unlocated (0) for "not known", which prints no caret while the
/// header keeps its column field for the editor integrations. Passing a node's `.Column` is
/// also correct and is preferred: it is a real column when the parser stamped that node and
/// Unlocated when it did not, so a site written that way starts pointing correctly by itself
/// as stamping spreads, with no further edit.
///
/// This is a source scan rather than a behavioural test because the property is about all 87
/// construction sites, including the ones no test happens to reach. A behavioural test would
/// pin only the diagnostics someone thought to exercise, and the sites that regress are by
/// definition the ones nobody was looking at.
/// </summary>
public class PlaceholderColumnTests
{
    private static readonly string[] ErrorTypes =
    {
        "CompilerError", "SyntaxError", "IndentationError", "LexicalError", "ArchitectureError",
        "ValueError", "TypeError", "RecursionError", "NameError", "IndexError",
    };

    /// Walks up from the test binary to the repository root, identified by the compiler
    /// project sitting where it always does.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "compiler", "PyMCU.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"repository root not found above {AppContext.BaseDirectory}");
    }

    /// The top-level arguments of the call whose '(' is at <paramref name="open"/>.
    private static List<string>? Arguments(string text, int open)
    {
        var args = new List<string>();
        int depth = 0, start = open + 1;
        char quote = '\0';
        bool escaped = false;
        for (int i = open; i < text.Length; ++i)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c is '(' or '[' or '{') { depth++; continue; }
            if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0) { args.Add(text[start..i]); return args; }
                continue;
            }
            if (c == ',' && depth == 1) { args.Add(text[start..i]); start = i + 1; }
        }
        return null;
    }

    [Fact]
    public void NoDiagnosticPassesOneAsAStandInForAnUnknownColumn()
    {
        string root = RepoRoot();
        var pattern = new Regex(@"new (" + string.Join("|", ErrorTypes) + @")\s*\(");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "compiler"), "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in pattern.Matches(text))
            {
                var args = Arguments(text, m.Index + m.Length - 1);
                if (args is null) continue;

                // CompilerError leads with its type name, so the column sits one later.
                int columnIndex = m.Groups[1].Value == "CompilerError" ? 3 : 2;
                if (args.Count <= columnIndex) continue;   // omitted, so Unlocated

                if (string.Join(" ", args[columnIndex].Split(default(char[]?),
                        StringSplitOptions.RemoveEmptyEntries)) != "1") continue;

                int line = text.Take(m.Index).Count(c => c == (char)10) + 1;
                offenders.Add($"{Path.GetRelativePath(root, file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These sites pass 1 as the column, which draws a caret under the first character "
            + "of the line whether or not the error is there. Pass the offending node's "
            + ".Column, or omit the argument so it defaults to CompilerError.Unlocated and no "
            + "caret is drawn:\n  " + string.Join("\n  ", offenders));
    }
}
