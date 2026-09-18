/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

using System.Text.RegularExpressions;
using PyMCU.Common;

namespace PyMCU.Frontend;

/// <summary>
/// <c>Name = namedtuple("Name", ("a", "b"))</c> is a compile-time class factory, not a
/// heap type. CircuitPython's collections.namedtuple builds a tuple subclass at run
/// time; there is no heap here, so the assignment is rewritten to the ZCA class a
/// reader would have written by hand -- fields, an inlined <c>__init__</c>,
/// <c>__len__</c>, and <c>__match_args__</c> so a class pattern binds in field
/// order.
///
/// The bound name is the class: <c>UnparseableIRMessage = namedtuple("IRMessage", ...)</c>
/// constructs <c>UnparseableIRMessage</c>, which is how adafruit_irremote spells it.
/// The typename string is not a second type.
///
/// Runs on every module before TypeInference / IR scan, the same slot AsyncTransform
/// occupies. A call that is not a module-level assignment is left alone; the stub in
/// <c>pymcu/collections.py</c> then refuses it.
/// </summary>
public static class NamedtupleTransform
{
    private static readonly Regex FieldName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static void TransformProgram(ProgramNode prog)
    {
        var callNames = new HashSet<string>(StringComparer.Ordinal) { "namedtuple" };
        var moduleNames = new HashSet<string>(StringComparer.Ordinal);
        CollectFactoryNames(prog, callNames, moduleNames);

        for (int i = 0; i < prog.GlobalStatements.Count; i++)
        {
            if (prog.GlobalStatements[i] is not AssignStmt assign) continue;
            if (assign.Target is not VariableExpr target) continue;
            if (assign.Value is not CallExpr call) continue;
            if (!IsNamedtupleFactory(call, callNames, moduleNames)) continue;

            prog.GlobalStatements[i] = SynthesizeClass(target.Name, call, assign);
        }
    }

    private static void CollectFactoryNames(
        ProgramNode prog, HashSet<string> callNames, HashSet<string> moduleNames)
    {
        foreach (var imp in prog.Imports)
        {
            if (!IsCollectionsModule(imp.ModuleName)) continue;
            if (imp.Symbols.Count == 0)
            {
                moduleNames.Add(string.IsNullOrEmpty(imp.ModuleAlias) ? imp.ModuleName : imp.ModuleAlias);
                continue;
            }
            foreach (var sym in imp.Symbols)
            {
                if (sym != "namedtuple") continue;
                callNames.Add(imp.Aliases.TryGetValue(sym, out var alias) ? alias : sym);
            }
        }
    }

    private static bool IsCollectionsModule(string name) =>
        name is "collections" or "ucollections" or "pymcu.collections";

    private static bool IsNamedtupleFactory(
        CallExpr call, HashSet<string> callNames, HashSet<string> moduleNames)
    {
        switch (call.Callee)
        {
            case VariableExpr v:
                return callNames.Contains(v.Name);
            case MemberAccessExpr { Object: VariableExpr obj, Member: "namedtuple" }:
                return moduleNames.Contains(obj.Name);
            default:
                return false;
        }
    }

    private static ClassDef SynthesizeClass(string className, CallExpr call, AssignStmt at)
    {
        var fields = ParseFields(call, at);
        var body = new Block();

        var matchArgs = new AssignStmt(
            new VariableExpr("__match_args__"),
            new TupleExpr(fields.Select(f => (Expression)new StringLiteral(f)).ToList()));
        At(matchArgs, at);
        body.Statements.Add(matchArgs);

        var initParams = new List<Param> { new Param("self", "") };
        var initBody = new Block();
        foreach (var field in fields)
        {
            initParams.Add(new Param(field, "uint8"));
            var store = new AssignStmt(
                new MemberAccessExpr(new VariableExpr("self"), field),
                new VariableExpr(field));
            At(store, at);
            initBody.Statements.Add(store);
        }
        // Same shape the compiler uses for a synthesized no-op constructor: @inline
        // with return type void so `p = Point(3, 5)` is ZCA construction, not a
        // value-returning call, and the implicit empty __init__ is not installed
        // on top of this one.
        var init = new FunctionDef("__init__", initParams, "void", initBody, isInline: true);
        At(init, at);
        body.Statements.Add(init);

        var lenBody = new Block();
        lenBody.Statements.Add(new ReturnStmt(new IntegerLiteral(fields.Count)));
        var len = new FunctionDef(
            "__len__", [new Param("self", "")], "uint8", lenBody, isInline: true);
        At(len, at);
        body.Statements.Add(len);

        var cls = new ClassDef(className, [], body);
        At(cls, at);
        return cls;
    }

    private static List<string> ParseFields(CallExpr call, AssignStmt at)
    {
        if (call.Args.Any(a => a is KeywordArgExpr or StarArgExpr or DoubleStarArgExpr))
            throw new TypeError(
                "namedtuple() takes two positional arguments (typename, field_names); " +
                "rename/defaults/module are not supported",
                at.Line, at.Column, Math.Max(1, at.Length));
        if (call.Args.Count != 2)
            throw new TypeError(
                "namedtuple() takes exactly two arguments: the type name and the field names",
                at.Line, at.Column, Math.Max(1, at.Length));
        if (call.Args[0] is not StringLiteral)
            throw new TypeError(
                "namedtuple() typename must be a string literal",
                at.Line, at.Column, Math.Max(1, at.Length));

        var fields = FieldNamesOf(call.Args[1], at);
        if (fields.Count == 0)
            throw new ValueError(
                "namedtuple() needs at least one field",
                at.Line, at.Column, Math.Max(1, at.Length));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!FieldName.IsMatch(field))
                throw new ValueError(
                    $"namedtuple() field name '{field}' is not a valid identifier",
                    at.Line, at.Column, Math.Max(1, at.Length));
            if (!seen.Add(field))
                throw new ValueError(
                    $"namedtuple() field name '{field}' is duplicated",
                    at.Line, at.Column, Math.Max(1, at.Length));
        }
        return fields;
    }

    private static List<string> FieldNamesOf(Expression spec, AssignStmt at)
    {
        switch (spec)
        {
            case TupleExpr t:
                return LiteralsOf(t.Elements, at);
            case ListExpr l:
                return LiteralsOf(l.Elements, at);
            case StringLiteral s:
                return s.Value
                    .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            default:
                throw new TypeError(
                    "namedtuple() field_names must be a tuple or list of string literals, " +
                    "or a string of space- or comma-separated names",
                    at.Line, at.Column, Math.Max(1, at.Length));
        }
    }

    private static List<string> LiteralsOf(List<Expression> elems, AssignStmt at)
    {
        var names = new List<string>(elems.Count);
        foreach (var e in elems)
        {
            if (e is not StringLiteral s)
                throw new TypeError(
                    "namedtuple() field names must be string literals",
                    at.Line, at.Column, Math.Max(1, at.Length));
            names.Add(s.Value);
        }
        return names;
    }

    private static T At<T>(T node, ASTNode src) where T : ASTNode
    {
        node.Line = src.Line;
        node.Column = src.Column;
        node.Length = src.Length;
        return node;
    }
}
