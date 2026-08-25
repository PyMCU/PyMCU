/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

using System.Text;
using System.Text.Json;

namespace PyMCU.Frontend;

/// <summary>
/// Writes a ProgramNode in the same JSON shape the Python translator emits.
///
/// This is what turns the Python front end into an ORACLE for the C# parser rather than a
/// replacement for it: dump the tree both ways and diff. Comparing firmware only catches a
/// divergence that reaches code generation; comparing trees catches one in a branch nothing
/// calls, in an annotation nobody reads yet, or in a node the IR generator happens to ignore
/// today and will not ignore tomorrow.
///
/// The C# parser is the one that ships -- it is the only one that can run inside a .NET app
/// with no Python available -- so having CPython's grammar as a reference for it is the
/// point of the exercise.
/// </summary>
public static class AstJsonWriter
{
    public static string Write(ProgramNode program)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();

            w.WriteStartArray("imports");
            foreach (var imp in program.Imports) WriteImport(w, imp);
            w.WriteEndArray();

            w.WriteStartArray("functions");
            foreach (var fn in program.Functions) WriteFunction(w, fn);
            w.WriteEndArray();

            w.WriteStartArray("globals");
            foreach (var st in program.GlobalStatements) WriteStatement(w, st);
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteImport(Utf8JsonWriter w, ImportStmt imp)
    {
        w.WriteStartObject();
        w.WriteString("k", "Import");
        w.WriteString("module", imp.ModuleName);
        w.WriteStartArray("symbols");
        foreach (var s in imp.Symbols) w.WriteStringValue(s);
        w.WriteEndArray();
        w.WriteNumber("level", imp.RelativeLevel);
        w.WriteStartObject("aliases");
        foreach (var kv in imp.Aliases.OrderBy(k => k.Key, StringComparer.Ordinal))
            w.WriteString(kv.Key, kv.Value);
        w.WriteEndObject();
        w.WriteString("moduleAlias", imp.ModuleAlias);
        w.WriteNumber("line", imp.Line);
        w.WriteEndObject();
    }

    private static void WriteParam(Utf8JsonWriter w, Param p)
    {
        w.WriteStartObject();
        w.WriteString("name", p.Name);
        w.WriteString("type", p.Type);
        w.WritePropertyName("default");
        WriteExpr(w, p.DefaultValue);
        w.WriteEndObject();
    }

    private static void WriteFunction(Utf8JsonWriter w, FunctionDef fn)
    {
        w.WriteStartObject();
        w.WriteString("k", "Function");
        w.WriteString("name", fn.Name);
        w.WriteStartArray("params");
        foreach (var p in fn.Params) WriteParam(w, p);
        w.WriteEndArray();
        w.WriteString("returnType", fn.ReturnType);
        w.WritePropertyName("body");
        WriteStatement(w, fn.Body);
        w.WriteBoolean("isInline", fn.IsInline);
        w.WriteBoolean("isInterrupt", fn.IsInterrupt);
        w.WriteNumber("vector", fn.InterruptVector);
        w.WriteBoolean("isPropertyGetter", fn.IsPropertyGetter);
        w.WriteBoolean("isPropertySetter", fn.IsPropertySetter);
        w.WriteString("propertyName", fn.PropertyName);
        w.WriteBoolean("isNaked", fn.IsNaked);
        w.WriteBoolean("isExtern", fn.IsExtern);
        w.WriteString("externSymbol", fn.ExternSymbol);
        w.WriteBoolean("isExportC", fn.IsExportC);
        w.WriteBoolean("isOutline", fn.IsOutline);
        w.WriteString("warning", fn.WarningMessage);
        w.WriteBoolean("isPio", fn.IsPioProgram);
        w.WriteStartObject("pioParams");
        foreach (var kv in fn.PioParams.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            w.WritePropertyName(kv.Key);
            WriteExpr(w, kv.Value);
        }
        w.WriteEndObject();
        w.WriteBoolean("isAsync", fn.IsAsync);
        w.WriteNumber("line", fn.Line);
        w.WriteEndObject();
    }

    private static void WriteStatementList(Utf8JsonWriter w, IEnumerable<Statement>? list)
    {
        if (list == null) { w.WriteNullValue(); return; }
        w.WriteStartArray();
        foreach (var s in list) WriteStatement(w, s);
        w.WriteEndArray();
    }

    private static void WriteStatement(Utf8JsonWriter w, Statement? stmt)
    {
        if (stmt == null) { w.WriteNullValue(); return; }

        switch (stmt)
        {
            case Block b:
                w.WriteStartObject();
                w.WriteString("k", "Block");
                w.WriteStartArray("statements");
                foreach (var s in b.Statements) WriteStatement(w, s);
                w.WriteEndArray();
                w.WriteNumber("line", b.Line);
                w.WriteEndObject();
                return;

            case FunctionDef fn: WriteFunction(w, fn); return;
            case ImportStmt imp: WriteImport(w, imp); return;

            case ClassDef c:
                w.WriteStartObject();
                w.WriteString("k", "Class");
                w.WriteString("name", c.Name);
                w.WriteStartArray("bases");
                foreach (var b2 in c.Bases) w.WriteStringValue(b2);
                w.WriteEndArray();
                w.WritePropertyName("body");
                WriteStatement(w, c.Body);
                w.WriteBoolean("isStatic", c.IsStatic);
                w.WriteBoolean("isDataclass", c.IsDataclass);
                w.WriteBoolean("isValue", c.IsValue);
                w.WriteNumber("line", c.Line);
                w.WriteEndObject();
                return;

            case VarDecl v:
                w.WriteStartObject();
                w.WriteString("k", "VarDecl");
                w.WriteString("name", v.Name);
                w.WriteString("varType", v.VarType);
                w.WritePropertyName("init");
                WriteExpr(w, v.Init);
                w.WriteNumber("line", v.Line);
                w.WriteEndObject();
                return;

            case AnnAssign a:
                w.WriteStartObject();
                w.WriteString("k", "AnnAssign");
                w.WriteString("target", a.Target);
                w.WriteString("annotation", a.Annotation);
                w.WritePropertyName("value");
                WriteExpr(w, a.Value);
                w.WriteNumber("line", a.Line);
                w.WriteEndObject();
                return;

            case AssignStmt asg:
                w.WriteStartObject();
                w.WriteString("k", "Assign");
                w.WritePropertyName("target");
                WriteExpr(w, asg.Target);
                w.WritePropertyName("value");
                WriteExpr(w, asg.Value);
                if (asg.AnnotatedType == null) { w.WriteNull("annotatedType"); }
                else w.WriteString("annotatedType", asg.AnnotatedType);
                w.WriteNumber("line", asg.Line);
                w.WriteEndObject();
                return;

            case AugAssignStmt aug:
                w.WriteStartObject();
                w.WriteString("k", "AugAssign");
                w.WritePropertyName("target");
                WriteExpr(w, aug.Target);
                w.WriteString("op", aug.Op.ToString());
                w.WritePropertyName("value");
                WriteExpr(w, aug.Value);
                w.WriteNumber("line", aug.Line);
                w.WriteEndObject();
                return;

            case TupleUnpackStmt tu:
                w.WriteStartObject();
                w.WriteString("k", "TupleUnpack");
                w.WriteStartArray("targets");
                foreach (var t in tu.Targets) w.WriteStringValue(t);
                w.WriteEndArray();
                w.WritePropertyName("value");
                WriteExpr(w, tu.Value);
                w.WriteNumber("starredIndex", tu.StarredIndex);
                w.WriteNumber("line", tu.Line);
                w.WriteEndObject();
                return;

            case ReturnStmt r:
                w.WriteStartObject();
                w.WriteString("k", "Return");
                w.WritePropertyName("value");
                WriteExpr(w, r.Value);
                w.WriteNumber("line", r.Line);
                w.WriteEndObject();
                return;

            case IfStmt i:
                w.WriteStartObject();
                w.WriteString("k", "If");
                w.WritePropertyName("condition");
                WriteExpr(w, i.Condition);
                w.WritePropertyName("then");
                WriteStatement(w, i.ThenBranch);
                w.WriteStartArray("elifs");
                foreach (var (cond, body) in i.ElifBranches)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("condition");
                    WriteExpr(w, cond);
                    w.WritePropertyName("body");
                    WriteStatement(w, body);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WritePropertyName("else");
                WriteStatement(w, i.ElseBranch);
                w.WriteNumber("line", i.Line);
                w.WriteEndObject();
                return;

            case WhileStmt wh:
                w.WriteStartObject();
                w.WriteString("k", "While");
                w.WritePropertyName("condition");
                WriteExpr(w, wh.Condition);
                w.WritePropertyName("body");
                WriteStatement(w, wh.Body);
                w.WriteNumber("line", wh.Line);
                w.WriteEndObject();
                return;

            case ForStmt f:
                w.WriteStartObject();
                w.WriteString("k", "For");
                w.WriteString("varName", f.VarName);
                w.WriteString("var2Name", f.Var2Name);
                w.WritePropertyName("rangeStart");
                WriteExpr(w, f.RangeStart);
                w.WritePropertyName("rangeStop");
                WriteExpr(w, f.RangeStop);
                w.WritePropertyName("rangeStep");
                WriteExpr(w, f.RangeStep);
                w.WritePropertyName("iterable");
                WriteExpr(w, f.Iterable);
                w.WritePropertyName("body");
                WriteStatement(w, f.Body);
                w.WriteNumber("line", f.Line);
                w.WriteEndObject();
                return;

            case WithStmt wi:
                w.WriteStartObject();
                w.WriteString("k", "With");
                w.WritePropertyName("context");
                WriteExpr(w, wi.ContextExpr);
                w.WriteString("asName", wi.AsName);
                w.WritePropertyName("body");
                WriteStatement(w, wi.Body);
                w.WriteNumber("line", wi.Line);
                w.WriteEndObject();
                return;

            case MatchStmt m:
                w.WriteStartObject();
                w.WriteString("k", "Match");
                w.WritePropertyName("target");
                WriteExpr(w, m.Target);
                w.WriteStartArray("branches");
                foreach (var br in m.Branches)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("pattern");
                    WriteExpr(w, br.Pattern);
                    w.WritePropertyName("guard");
                    WriteExpr(w, br.Guard);
                    w.WriteString("capture", br.CaptureName);
                    w.WritePropertyName("body");
                    WriteStatement(w, br.Body);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("line", m.Line);
                w.WriteEndObject();
                return;

            case TryStmt t:
                w.WriteStartObject();
                w.WriteString("k", "Try");
                w.WritePropertyName("body");
                WriteStatementList(w, t.Body);
                w.WriteStartArray("handlers");
                foreach (var (exn, hbody) in t.Handlers)
                {
                    w.WriteStartObject();
                    w.WriteString("exnType", exn);
                    w.WritePropertyName("body");
                    WriteStatementList(w, hbody);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WritePropertyName("finally");
                WriteStatementList(w, t.Finally);
                w.WritePropertyName("else");
                WriteStatementList(w, t.ElseBody);
                w.WriteNumber("line", t.Line);
                w.WriteEndObject();
                return;

            case RaiseStmt r2:
                w.WriteStartObject();
                w.WriteString("k", "Raise");
                w.WriteString("errorType", r2.ErrorType);
                w.WriteString("message", r2.Message);
                if (r2.MessageName == null) w.WriteNull("messageName");
                else w.WriteString("messageName", r2.MessageName);
                w.WriteNumber("line", r2.Line);
                w.WriteEndObject();
                return;

            case AssertStmt asr:
                w.WriteStartObject();
                w.WriteString("k", "Assert");
                w.WritePropertyName("condition");
                WriteExpr(w, asr.Condition);
                w.WriteString("message", asr.Message);
                w.WriteNumber("line", asr.Line);
                w.WriteEndObject();
                return;

            case ExprStmt es:
                w.WriteStartObject();
                w.WriteString("k", "ExprStmt");
                w.WritePropertyName("expr");
                WriteExpr(w, es.Expr);
                w.WriteNumber("line", es.Line);
                w.WriteEndObject();
                return;

            case GlobalStmt g:
                WriteNames(w, "Global", g.Names, g.Line);
                return;

            case NonlocalStmt nl:
                WriteNames(w, "Nonlocal", nl.Names, nl.Line);
                return;

            case BreakStmt: WriteBare(w, "Break", stmt.Line); return;
            case ContinueStmt: WriteBare(w, "Continue", stmt.Line); return;
            case PassStmt: WriteBare(w, "Pass", stmt.Line); return;

            default:
                w.WriteStartObject();
                w.WriteString("k", stmt.GetType().Name);
                w.WriteNumber("line", stmt.Line);
                w.WriteEndObject();
                return;
        }
    }

    private static void WriteNames(Utf8JsonWriter w, string kind, List<string> names, int line)
    {
        w.WriteStartObject();
        w.WriteString("k", kind);
        w.WriteStartArray("names");
        foreach (var n in names) w.WriteStringValue(n);
        w.WriteEndArray();
        w.WriteNumber("line", line);
        w.WriteEndObject();
    }

    private static void WriteBare(Utf8JsonWriter w, string kind, int line)
    {
        w.WriteStartObject();
        w.WriteString("k", kind);
        w.WriteNumber("line", line);
        w.WriteEndObject();
    }

    private static void WriteExpr(Utf8JsonWriter w, Expression? e)
    {
        if (e == null) { w.WriteNullValue(); return; }

        switch (e)
        {
            case IntegerLiteral i:
                Scalar(w, "Int", () => w.WriteNumber("value", i.Value), i.Line); return;
            case FloatLiteral f:
                Scalar(w, "Float", () => w.WriteNumber("value", f.Value), f.Line); return;
            case BooleanLiteral b:
                Scalar(w, "Bool", () => w.WriteBoolean("value", b.Value), b.Line); return;
            case StringLiteral s:
                Scalar(w, "Str", () => w.WriteString("value", s.Value), s.Line); return;
            case NoneLiteral: WriteBareExpr(w, "None", e.Line); return;
            case VariableExpr v:
                Scalar(w, "Var", () => w.WriteString("name", v.Name), v.Line); return;

            case BinaryExpr bin:
                w.WriteStartObject();
                w.WriteString("k", "Binary");
                w.WritePropertyName("left"); WriteExpr(w, bin.Left);
                w.WriteString("op", bin.Op.ToString());
                w.WritePropertyName("right"); WriteExpr(w, bin.Right);
                w.WriteNumber("line", bin.Line);
                w.WriteEndObject();
                return;

            case UnaryExpr u:
                w.WriteStartObject();
                w.WriteString("k", "Unary");
                w.WriteString("op", u.Op.ToString());
                w.WritePropertyName("operand"); WriteExpr(w, u.Operand);
                w.WriteNumber("line", u.Line);
                w.WriteEndObject();
                return;

            case CallExpr c:
                w.WriteStartObject();
                w.WriteString("k", "Call");
                w.WritePropertyName("callee"); WriteExpr(w, c.Callee);
                w.WriteStartArray("args");
                foreach (var a in c.Args) WriteExpr(w, a);
                w.WriteEndArray();
                w.WriteNumber("line", c.Line);
                w.WriteEndObject();
                return;

            case KeywordArgExpr k:
                w.WriteStartObject();
                w.WriteString("k", "Keyword");
                w.WriteString("key", k.Key);
                w.WritePropertyName("value"); WriteExpr(w, k.Value);
                w.WriteNumber("line", k.Line);
                w.WriteEndObject();
                return;

            case MemberAccessExpr m:
                w.WriteStartObject();
                w.WriteString("k", "Member");
                w.WritePropertyName("object"); WriteExpr(w, m.Object);
                w.WriteString("member", m.Member);
                w.WriteNumber("line", m.Line);
                w.WriteEndObject();
                return;

            case IndexExpr ix:
                w.WriteStartObject();
                w.WriteString("k", "Index");
                w.WritePropertyName("target"); WriteExpr(w, ix.Target);
                w.WritePropertyName("index"); WriteExpr(w, ix.Index);
                w.WriteNumber("line", ix.Line);
                w.WriteEndObject();
                return;

            case SliceExpr sl:
                w.WriteStartObject();
                w.WriteString("k", "Slice");
                w.WritePropertyName("start"); WriteExpr(w, sl.Start);
                w.WritePropertyName("stop"); WriteExpr(w, sl.Stop);
                w.WritePropertyName("step"); WriteExpr(w, sl.Step);
                w.WriteNumber("line", sl.Line);
                w.WriteEndObject();
                return;

            case ListExpr l: WriteElements(w, "List", l.Elements, l.Line); return;
            case TupleExpr tp: WriteElements(w, "Tuple", tp.Elements, tp.Line); return;
            case SetExpr st: WriteElements(w, "Set", st.Elements, st.Line); return;

            case DictExpr d:
                w.WriteStartObject();
                w.WriteString("k", "Dict");
                w.WriteStartArray("entries");
                foreach (var (key, val) in d.Entries)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("key"); WriteExpr(w, key);
                    w.WritePropertyName("value"); WriteExpr(w, val);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("line", d.Line);
                w.WriteEndObject();
                return;

            case FStringExpr fs:
                w.WriteStartObject();
                w.WriteString("k", "FString");
                w.WriteStartArray("parts");
                foreach (var p in fs.Parts)
                {
                    w.WriteStartObject();
                    w.WriteBoolean("isExpr", p.IsExpr);
                    if (p.IsExpr) { w.WritePropertyName("expr"); WriteExpr(w, p.Expr); }
                    w.WriteString("text", p.Text);
                    w.WriteString("spec", p.FormatSpec);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("line", fs.Line);
                w.WriteEndObject();
                return;

            case ListCompExpr lc:
                w.WriteStartObject();
                w.WriteString("k", "ListComp");
                w.WritePropertyName("element"); WriteExpr(w, lc.Element);
                w.WriteString("varName", lc.VarName);
                w.WritePropertyName("iterable"); WriteExpr(w, lc.Iterable);
                w.WriteString("var2Name", lc.Var2Name);
                w.WritePropertyName("iterable2"); WriteExpr(w, lc.Iterable2);
                w.WritePropertyName("filter"); WriteExpr(w, lc.Filter);
                w.WriteNumber("line", lc.Line);
                w.WriteEndObject();
                return;

            case TernaryExpr t:
                w.WriteStartObject();
                w.WriteString("k", "Ternary");
                w.WritePropertyName("trueVal"); WriteExpr(w, t.TrueVal);
                w.WritePropertyName("condition"); WriteExpr(w, t.Condition);
                w.WritePropertyName("falseVal"); WriteExpr(w, t.FalseVal);
                w.WriteNumber("line", t.Line);
                w.WriteEndObject();
                return;

            case WalrusExpr wl:
                w.WriteStartObject();
                w.WriteString("k", "Walrus");
                w.WriteString("varName", wl.VarName);
                w.WritePropertyName("value"); WriteExpr(w, wl.Value);
                w.WriteNumber("line", wl.Line);
                w.WriteEndObject();
                return;

            case LambdaExpr lam:
                w.WriteStartObject();
                w.WriteString("k", "Lambda");
                w.WriteStartArray("params");
                foreach (var p in lam.Params) WriteParam(w, p);
                w.WriteEndArray();
                w.WritePropertyName("body"); WriteExpr(w, lam.Body);
                w.WriteNumber("line", lam.Line);
                w.WriteEndObject();
                return;

            case StarArgExpr sa:
                w.WriteStartObject();
                w.WriteString("k", "StarArg");
                w.WritePropertyName("value"); WriteExpr(w, sa.Value);
                w.WriteNumber("line", sa.Line);
                w.WriteEndObject();
                return;

            case AwaitExpr aw:
                w.WriteStartObject();
                w.WriteString("k", "Await");
                w.WritePropertyName("operand"); WriteExpr(w, aw.Operand);
                w.WriteNumber("line", aw.Line);
                w.WriteEndObject();
                return;

            case YieldExpr y:
                w.WriteStartObject();
                w.WriteString("k", "Yield");
                w.WritePropertyName("value"); WriteExpr(w, y.Value);
                w.WriteNumber("line", y.Line);
                w.WriteEndObject();
                return;

            default:
                WriteBareExpr(w, e.GetType().Name, e.Line);
                return;
        }
    }

    private static void Scalar(Utf8JsonWriter w, string kind, Action writeValue, int line)
    {
        w.WriteStartObject();
        w.WriteString("k", kind);
        writeValue();
        w.WriteNumber("line", line);
        w.WriteEndObject();
    }

    private static void WriteBareExpr(Utf8JsonWriter w, string kind, int line)
    {
        w.WriteStartObject();
        w.WriteString("k", kind);
        w.WriteNumber("line", line);
        w.WriteEndObject();
    }

    private static void WriteElements(Utf8JsonWriter w, string kind, List<Expression> elements, int line)
    {
        w.WriteStartObject();
        w.WriteString("k", kind);
        w.WriteStartArray("elements");
        foreach (var el in elements) WriteExpr(w, el);
        w.WriteEndArray();
        w.WriteNumber("line", line);
        w.WriteEndObject();
    }
}
