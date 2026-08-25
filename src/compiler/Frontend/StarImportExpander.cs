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

namespace PyMCU.Frontend;

/// <summary>
/// Turns `from m import *` into the explicit symbol list the rest of the compiler binds.
///
/// The star used to survive as the single literal symbol "*", which matched nothing, so the
/// import brought in no names at all and the failure surfaced one use site at a time as
/// "name ... is not defined", pointing away from the import that caused it.
///
/// The exported set is what the module DEFINES at top level -- functions, classes and
/// module-level variables -- or exactly `__all__` when the module declares one. Names the
/// module itself imported are re-exported only through `__all__`: binding them implicitly
/// would let a star import silently rebind a type intrinsic (`uint8`) that the importing
/// file had already bound to pymcu.types.
/// </summary>
public static class StarImportExpander
{
    public const string Star = "*";

    public static bool IsStar(ImportStmt imp) => imp.Symbols.Count == 1 && imp.Symbols[0] == Star;

    /// <summary>
    /// Replaces the star in <paramref name="imp"/> with the names <paramref name="module"/>
    /// exports. Idempotent: a node whose star has already been expanded is left alone.
    /// </summary>
    public static void Expand(ImportStmt imp, ProgramNode module)
    {
        if (!IsStar(imp)) return;

        imp.Symbols.Clear();
        imp.Symbols.AddRange(ExportedNames(module));
        imp.WasStarImport = true;
    }

    /// <summary>
    /// The names `from module import *` binds, in source order and without duplicates.
    /// </summary>
    public static List<string> ExportedNames(ProgramNode module)
    {
        var declared = DeclaredAll(module);
        if (declared != null) return declared;

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? name)
        {
            // A leading underscore means private, which is what star import honours in Python
            // and what every module here relies on for its internal helpers.
            if (string.IsNullOrEmpty(name) || name[0] == '_') return;
            if (seen.Add(name)) names.Add(name);
        }

        foreach (var fn in module.Functions)
            Add(fn.Name);

        foreach (var stmt in module.GlobalStatements)
        {
            switch (stmt)
            {
                case ClassDef cls: Add(cls.Name); break;
                case FunctionDef fn: Add(fn.Name); break;
                case VarDecl decl: Add(decl.Name); break;
                case AnnAssign ann: Add(ann.Target); break;
                case AssignStmt { Target: VariableExpr v }: Add(v.Name); break;
            }
        }

        return names;
    }

    // `__all__ = ["a", "b"]` at module level. Honoured exactly, including underscore names
    // and names the module imported: an explicit export list is the module saying what its
    // surface is, which is the one case where re-exporting an imported name is deliberate.
    private static List<string>? DeclaredAll(ProgramNode module)
    {
        foreach (var stmt in module.GlobalStatements)
        {
            Expression? value = stmt switch
            {
                AssignStmt { Target: VariableExpr { Name: "__all__" } } a => a.Value,
                AnnAssign { Target: "__all__" } ann => ann.Value,
                VarDecl { Name: "__all__" } d => d.Init,
                _ => null,
            };

            if (value is not ListExpr list) continue;

            var names = new List<string>();
            foreach (var element in list.Elements)
                if (element is StringLiteral s && s.Value.Length > 0)
                    names.Add(s.Value);
            return names;
        }

        return null;
    }
}
