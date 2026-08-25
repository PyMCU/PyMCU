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

using PyMCU.Common;

namespace PyMCU.Frontend;

/// <summary>
/// Rejects a top-level function defined twice in one module.
///
/// The compiler only ever noticed this when two definitions survived as far as two IR
/// functions of the same name, which is the entry file and only the entry file: in an
/// imported module an @inline definition and a plain one are filed in different tables, so
/// the build stayed clean and the FIRST definition ran. Python binds the last, so the
/// program that was compiled was neither the one written nor the one CPython runs, and
/// nothing said so.
///
/// Runs per file, right after parsing, so the diagnostic names the file and the line of the
/// definition that has to move.
/// </summary>
public static class DuplicateDefinitionCheck
{
    public static void Check(ProgramNode ast, string filePath)
    {
        var byName = new Dictionary<string, List<FunctionDef>>(StringComparer.Ordinal);

        foreach (var func in ast.Functions)
        {
            if (!byName.TryGetValue(func.Name, out var earlier))
            {
                byName[func.Name] = [func];
                continue;
            }

            foreach (var previous in earlier)
            {
                // Overloads are a property of @inline: the registration that keeps them apart
                // by parameter suffix exists only there. An undecorated function is compiled
                // once as a subroutine, which one name cannot address twice.
                bool bothInline = previous.IsInline && func.IsInline;
                bool distinct = Signature(previous) != Signature(func);
                if (bothInline && distinct) continue;

                throw Duplicate(func, previous, filePath, bothInline);
            }

            earlier.Add(func);
        }
    }

    // The parameter types, which is what an overload is told apart by. An unannotated
    // parameter contributes an empty slot, so two unannotated definitions of the same arity
    // collide -- as they must, since nothing distinguishes them at a call site.
    private static string Signature(FunctionDef func)
        => string.Join(",", func.Params.Select(p => p.Type ?? ""));

    private static CompilerError Duplicate(FunctionDef func, FunctionDef previous,
                                           string filePath, bool bothInline)
    {
        string where = previous.Line > 0 ? $", the first on line {previous.Line}" : "";
        string how = bothInline
            ? "Both are @inline and take the same parameter types, so no call site can tell "
              + "them apart. Give the overloads different parameter types, or rename one"
            : "Overloads are only supported on @inline functions with different parameter "
              + $"types. Rename one, or mark every '{func.Name}' @inline and give them "
              + "different parameter types";

        return new CompilerError("CompileError",
            $"duplicate function definition: '{func.Name}' is defined more than once in "
            + $"'{Path.GetFileName(filePath)}'{where}. Python binds the last definition and "
            + $"PyMCU compiles the first, so the two disagree. {how}",
            func.Line > 0 ? func.Line : 1, 1)
        {
            File = filePath,
        };
    }
}
