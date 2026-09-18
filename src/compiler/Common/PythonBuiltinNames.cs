// -----------------------------------------------------------------------------
// PyMCU Compiler
// Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
//
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------------

namespace PyMCU.Common;

/// <summary>
/// Every name in CPython's builtins namespace.
///
/// Two callers need the same answer and must not keep two lists that drift apart. The IR
/// generator uses it to rule out "typo, or a missing import?", since a builtin is always in
/// scope and neither branch of that suggestion can be the answer. The imported-name check uses
/// it to stay silent on `from m import print`: the question that check asks is whether the name
/// will RESOLVE, not whether the module defines it, and a builtin resolves. That spelling is
/// deliberate and widespread here, `from pymcu.hal.console import print` being the way a
/// program says which sink print writes to.
///
/// <see cref="RepresentedTypes"/> is the subset that ARE types and that this compiler
/// stores -- a width, a buffer, or a compile-time view. <c>memoryview()</c> as a call and
/// <c>-&gt; memoryview</c> as an annotation are the same name; an annotation check that
/// only consulted the scalar-width list treated the builtin as unknown. A builtin that is
/// a function (<c>print</c>, <c>len</c>) is not a type. A builtin that is a type without
/// a representation here (<c>complex</c>, <c>dict</c>) stays refused, by name.
/// </summary>
public static class PythonBuiltinNames
{
    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        "abs", "aiter", "all", "anext", "any", "ascii", "bin", "bool", "breakpoint", "bytearray",
        "bytes", "callable", "chr", "classmethod", "compile", "complex", "delattr", "dict", "dir",
        "divmod", "enumerate", "eval", "exec", "exit", "filter", "float", "format", "frozenset",
        "getattr", "globals", "hasattr", "hash", "help", "hex", "id", "input", "int",
        "isinstance", "issubclass", "iter", "len", "list", "locals", "map", "max", "memoryview",
        "min", "next", "object", "oct", "open", "ord", "pow", "print", "property", "quit",
        "range", "repr", "reversed", "round", "set", "setattr", "slice", "sorted",
        "staticmethod", "str", "sum", "super", "tuple", "type", "vars", "zip",
    };

    /// <summary>
    /// CPython builtin names that are types and that PyMCU has a representation for as a
    /// bare annotation. <c>list</c> and <c>tuple</c> are types too, but only as the head of
    /// a bracketed form, so they are not in this set.
    /// </summary>
    public static readonly HashSet<string> RepresentedTypes = new(StringComparer.Ordinal)
    {
        "int", "bool", "float", "str", "bytes", "bytearray", "memoryview", "object",
    };

    public static bool IsBuiltin(string name) => All.Contains(name);

    public static bool IsRepresentedType(string name) => RepresentedTypes.Contains(name);
}
