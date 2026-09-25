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

/// Emits a diagnostic as one JSON object on one line of stderr, for a reader that is not a
/// person.
///
/// WHY NOT the text format. The header an editor parses today is
/// `file:line:column: severity: TypeName: message`, and both shipped plugins read it with the
/// same regular expression, `^(.+):(\d+):(\d+):\s+(error|warning|info):\s+(.+)$`. That line
/// can carry one position and nothing else, so four things the compiler already knows do not
/// survive it: the END of the range (the caret's tildes are drawn from `Length`, which the
/// header does not print), the second site some refusals name inside their sentence, the fix
/// the message spells out in prose, and any identifier for the refusal that is stable when the
/// wording changes. The driver's `_remap_diagnostics` is what that costs on the other side:
/// three regular expressions in core/compiler.py whose whole job is to renumber the header,
/// the snippet gutter and the line citations INSIDE the message text, because the compiler was
/// handed a staged file and the reader has a different one.
///
/// JSON Lines, not one array. The compiler stops at its first error today, so an array would
/// be a list of one -- but the format has to survive the day it does not, and an array cannot
/// be written incrementally by a process that may still fail. One object per line is what
/// rustc emits and what a reader can consume as it arrives.
///
/// Written by hand rather than through a serializer. `pymcuc` is published NativeAOT, where
/// reflection-based serialization is trimmed away and fails at runtime rather than at build
/// time; the shape here is fixed and small enough that a StringBuilder is the honest tool.
internal static class DiagnosticJson
{
    /// The schema version. A consumer reads this FIRST and refuses a major it does not know.
    /// Fields may be added within a version; a field never changes meaning within one.
    private const int SchemaVersion = 1;

    internal static void Report(CompilerError err, ReadOnlySpan<char> source, string filename)
    {
        var rendered = new StringWriter();
        Diagnostic.ReportHuman(err, source, filename, rendered, useColor: false);

        var sb = new StringBuilder(512);
        sb.Append("{\"version\":").Append(SchemaVersion);
        sb.Append(",\"severity\":");
        Str(sb, Diagnostic.SeverityFor(err.TypeName));
        sb.Append(",\"type\":");
        Str(sb, err.TypeName);
        sb.Append(",\"code\":");
        if (err.Code is null) sb.Append("null"); else Str(sb, err.Code);
        sb.Append(",\"message\":");
        Str(sb, err.Message);

        // The primary span. `column` is omitted rather than defaulted when the compiler does
        // not know it, which is the one thing the text header cannot say: there it has to
        // print 1, and an editor cannot tell a measured column 1 from a placeholder. A
        // consumer that gets no column underlines the whole line.
        sb.Append(",\"span\":{\"file\":");
        Str(sb, filename);
        sb.Append(",\"line\":").Append(err.Line);
        if (err.HasColumn)
        {
            sb.Append(",\"column\":").Append(err.Column);
            // End is exclusive and on the same line, because that is what a `Length` measured
            // from one token can honestly describe. A multi-line construct reports the line it
            // starts on; inventing an end line from a length would place the squiggle over
            // text nobody measured.
            sb.Append(",\"end_line\":").Append(err.Line);
            sb.Append(",\"end_column\":").Append(err.Column + Math.Max(1, err.Length));
        }
        sb.Append('}');

        sb.Append(",\"related\":[");
        for (int i = 0; i < err.Related.Count; ++i)
        {
            if (i > 0) sb.Append(',');
            var r = err.Related[i];
            sb.Append("{\"file\":");
            Str(sb, r.File);
            sb.Append(",\"line\":").Append(r.Line);
            sb.Append(",\"column\":").Append(r.Column);
            sb.Append(",\"end_line\":").Append(r.Line);
            sb.Append(",\"end_column\":").Append(r.Column + Math.Max(1, r.Length));
            sb.Append(",\"label\":");
            Str(sb, r.Label);
            sb.Append('}');
        }
        sb.Append(']');

        sb.Append(",\"fixes\":[");
        for (int i = 0; i < err.Fixes.Count; ++i)
        {
            if (i > 0) sb.Append(',');
            var f = err.Fixes[i];
            sb.Append("{\"label\":");
            Str(sb, f.Label);
            sb.Append(",\"applicability\":");
            Str(sb, ApplicabilityName(f.Applicability));
            // An empty File means "the file this diagnostic is reported against". The raising
            // site holds the node and its line, and usually does NOT hold the path the
            // diagnostic will end up printed against: that is resolved a phase later, in
            // CompilerPhaseBase, from `File` and the entry file. Filling it in here is the only
            // place both facts are in hand.
            sb.Append(",\"edits\":[{\"file\":");
            Str(sb, string.IsNullOrEmpty(f.File) ? filename : f.File);
            sb.Append(",\"line\":").Append(f.Line);
            sb.Append(",\"column\":").Append(f.Column);
            sb.Append(",\"end_line\":").Append(f.Line);
            sb.Append(",\"end_column\":").Append(f.Column + Math.Max(0, f.Length));
            sb.Append(",\"replacement\":");
            Str(sb, f.Replacement);
            sb.Append("}]}");
        }
        sb.Append(']');

        // The terminal rendering, verbatim minus the escape codes. An editor that shows its
        // own squiggle ignores it; a tool piping to a log keeps the output people recognise
        // without re-deriving the caret arithmetic, which has its own two corner cases (tabs,
        // and a column past the end of the line).
        sb.Append(",\"rendered\":");
        Str(sb, rendered.ToString());
        sb.Append('}');

        Console.Error.WriteLine(sb.ToString());
    }

    private static string ApplicabilityName(FixApplicability a) => a switch
    {
        FixApplicability.MachineApplicable => "machine-applicable",
        FixApplicability.HasPlaceholders => "has-placeholders",
        _ => "maybe-incorrect",
    };

    /// A JSON string literal. Control characters are escaped as \u00XX rather than dropped:
    /// a message can quote a source line, and a source line can contain a tab.
    private static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
