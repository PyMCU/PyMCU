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

/// Multi-value return annotations. Both spellings
///   def divmod8(a: uint8, b: uint8) -> (uint8, uint8):
///   def divmod8(a: uint8, b: uint8) -> tuple[uint8, uint8]:
/// are recorded by the parser as the textual type "tuple[uint8,uint8]"; these helpers
/// are the single place that reads that shape back.
public static class TupleType
{
    private const string Prefix = "tuple[";

    public static bool IsTupleType(string? typeStr) =>
        typeStr != null && typeStr.StartsWith(Prefix, StringComparison.Ordinal) && typeStr.EndsWith("]", StringComparison.Ordinal);

    /// The element types of a tuple return annotation, in order. Empty for a non-tuple type.
    /// Splits on top-level commas only, so nested subscripts (const[uint8], ptr[uint16]) survive.
    public static List<string> ElementTypes(string? typeStr)
    {
        var elements = new List<string>();
        if (!IsTupleType(typeStr)) return elements;

        string inner = typeStr!.Substring(Prefix.Length, typeStr.Length - Prefix.Length - 1);
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; ++i)
        {
            char c = inner[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                elements.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }

        if (start < inner.Length) elements.Add(inner[start..].Trim());
        return elements;
    }

    /// Renders the annotation the way the user wrote it, for diagnostics.
    public static string Describe(string typeStr) => "(" + string.Join(", ", ElementTypes(typeStr)) + ")";
}
