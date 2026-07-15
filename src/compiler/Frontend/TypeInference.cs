// SPDX-License-Identifier: MIT
// Local, conservative type inference for UNANNOTATED parameters and returns of
// outlined (non-@inline) top-level functions.
//
// Motivation: an unannotated `def scale(v, k): return v * k` silently defaulted every
// param and the return to uint8, truncating 16/32-bit arguments (scale(300, 2) -> 88).
// Porting-linter data also ranks untyped params as the #1 raw friction in real driver
// code. This pass fills the blanks from the evidence the program already contains:
//
//   - the parameter's default value literal,
//   - the static type of every call-site argument (literals, annotated variables,
//     casts, calls with known return types, operators over those),
//   - for the return type, the static types of the function's return expressions.
//
// Evidence JOINS by safe integer widening (u8<u16<u32, i8<i16<i32; mixed signedness
// widens to the signed type that can hold both). No evidence leaves the annotation
// empty (the historical uint8 default) so existing code keeps compiling unchanged.
//
// Deliberately OUT of scope: @inline functions (their untyped params are compile-time
// polymorphic by design -- the HAL relies on it), class methods, overloaded names
// (inference would fight overload-by-type resolution), @extern/@interrupt/@naked.
using System;
using System.Collections.Generic;
using System.Linq;

namespace PyMCU.Frontend;

public static class TypeInference
{
    private const int Passes = 2;   // param types feed return types feed other call sites

    // The parser defaults an unannotated def to "void"; with value returns present that
    // default is wrong and inferable. An explicit `-> None` alongside value returns would
    // be a user bug either way.
    private static bool IsInferableReturn(string rt) => rt.Length == 0 || rt == "void";

    public static void InferProgram(ProgramNode main, IEnumerable<ProgramNode> modules)
    {
        var programs = new List<ProgramNode> { main };
        programs.AddRange(modules);

        // Candidate functions per program (top-level, outlined, not overloaded).
        var candidates = new List<FunctionDef>();
        foreach (var prog in programs)
        {
            var counts = prog.Functions.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.Count());
            foreach (var f in prog.Functions)
            {
                if (f.IsInline || f.IsExtern || f.IsInterrupt || f.IsNaked) continue;
                if (counts[f.Name] > 1) continue;   // overload set: types ARE the dispatch
                if (f.Params.Any(p => p.Type.Length == 0)
                    || (IsInferableReturn(f.ReturnType) && HasValueReturn(f.Body)))
                    candidates.Add(f);
            }
        }
        if (candidates.Count == 0) return;

        // Known (annotated or already-inferred) return types by bare function name.
        var returnTypes = new Dictionary<string, string>();
        foreach (var prog in programs)
            foreach (var f in prog.Functions)
                if (f.ReturnType.Length > 0) returnTypes[f.Name] = f.ReturnType;

        for (int pass = 0; pass < Passes; pass++)
        {
            // param evidence: function -> param index -> joined type
            var evidence = new Dictionary<FunctionDef, string?[]>();
            foreach (var f in candidates)
            {
                var ev = new string?[f.Params.Count];
                for (int i = 0; i < f.Params.Count; i++)
                    if (f.Params[i].Type.Length == 0 && f.Params[i].DefaultValue is IntegerLiteral dl)
                        ev[i] = TypeOfIntValue(dl.Value);
                evidence[f] = ev;
            }
            var byName = candidates.ToDictionary(f => f.Name);

            // Sweep every statement in the program for call sites of the candidates.
            foreach (var prog in programs)
            {
                foreach (var f in prog.Functions)
                    CollectFromBody(f.Body.Statements, ScopeTypes(f), byName, returnTypes, evidence);
                CollectFromBody(prog.GlobalStatements, ModuleScopeTypes(prog), byName, returnTypes, evidence);
            }

            // Apply: fill empty param annotations from the joined evidence.
            foreach (var f in candidates)
            {
                var ev = evidence[f];
                for (int i = 0; i < f.Params.Count; i++)
                    if (f.Params[i].Type.Length == 0 && ev[i] != null)
                        f.Params[i].Type = ev[i]!;

                // Return type: join the static types of all value returns, using the
                // (possibly just-inferred) param types as the local scope.
                if (IsInferableReturn(f.ReturnType) && HasValueReturn(f.Body))
                {
                    var scope = ScopeTypes(f);
                    string? rt = null;
                    foreach (var r in CollectReturns(f.Body.Statements))
                    {
                        string? t = StaticTypeOf(r, scope, returnTypes);
                        if (t == null) { rt = null; break; }   // any unknown -> give up
                        rt = rt == null ? t : Join(rt, t);
                    }
                    if (rt != null)
                    {
                        f.ReturnType = rt;
                        returnTypes[f.Name] = rt;
                    }
                }
            }
        }
    }

    // ── evidence collection ─────────────────────────────────────────────────────

    private static void CollectFromBody(
        List<Statement> body, Dictionary<string, string> scope,
        Dictionary<string, FunctionDef> byName, Dictionary<string, string> returnTypes,
        Dictionary<FunctionDef, string?[]> evidence)
    {
        foreach (var e in WalkExpressions(body))
        {
            if (e is not CallExpr call || call.Callee is not VariableExpr callee) continue;
            if (!byName.TryGetValue(callee.Name, out var f)) continue;
            var ev = evidence[f];
            int pos = 0;
            foreach (var arg in call.Args)
            {
                int index;
                Expression valueExpr;
                if (arg is KeywordArgExpr kw)
                {
                    index = f.Params.FindIndex(p => p.Name == kw.Key);
                    valueExpr = kw.Value;
                }
                else
                {
                    index = pos++;
                    valueExpr = arg;
                }
                if (index < 0 || index >= ev.Length || f.Params[index].Type.Length > 0) continue;
                string? t = StaticTypeOf(valueExpr, scope, returnTypes);
                if (t != null) ev[index] = ev[index] == null ? t : Join(ev[index]!, t);
            }
        }
    }

    // Local annotated declarations (params + `x: T = ...`) of a function.
    private static Dictionary<string, string> ScopeTypes(FunctionDef f)
    {
        var scope = new Dictionary<string, string>();
        foreach (var p in f.Params)
            if (p.Type.Length > 0) scope[p.Name] = p.Type;
        foreach (var s in WalkStatements(f.Body.Statements))
        {
            if (s is VarDecl vd && vd.VarType.Length > 0) scope[vd.Name] = vd.VarType;
            else if (s is AnnAssign aa && aa.Annotation.Length > 0) scope[aa.Target] = aa.Annotation;
        }
        return scope;
    }

    private static Dictionary<string, string> ModuleScopeTypes(ProgramNode prog)
    {
        var scope = new Dictionary<string, string>();
        foreach (var s in prog.GlobalStatements)
        {
            if (s is VarDecl vd && vd.VarType.Length > 0) scope[vd.Name] = vd.VarType;
            else if (s is AnnAssign aa && aa.Annotation.Length > 0) scope[aa.Target] = aa.Annotation;
        }
        return scope;
    }

    // ── static expression typing (integers only; null = unknown) ───────────────

    private static readonly HashSet<string> IntTypes = new()
        { "uint8", "int8", "uint16", "int16", "uint32", "int32", "int" };

    private static string? Normalize(string t) => t switch
    {
        "int" => "int16",               // the documented `int` alias
        _ => IntTypes.Contains(t) ? t : null,
    };

    private static string? StaticTypeOf(
        Expression e, Dictionary<string, string> scope, Dictionary<string, string> returnTypes)
    {
        switch (e)
        {
            case IntegerLiteral il: return TypeOfIntValue(il.Value);
            case VariableExpr v:
                return scope.TryGetValue(v.Name, out var vt) ? Normalize(vt) : null;
            case UnaryExpr { Op: UnaryOp.Negate } u:
            {
                string? t = StaticTypeOf(u.Operand, scope, returnTypes);
                return t == null ? null : Join(t, "int8");   // force signedness
            }
            case UnaryExpr { Op: UnaryOp.BitNot } u2:
                return StaticTypeOf(u2.Operand, scope, returnTypes);
            case BinaryExpr b:
            {
                if (b.Op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.LessEq
                    or BinaryOp.Greater or BinaryOp.GreaterEq or BinaryOp.And or BinaryOp.Or
                    or BinaryOp.In or BinaryOp.NotIn or BinaryOp.Is or BinaryOp.IsNot)
                    return "uint8";     // boolean-ish result
                string? l = StaticTypeOf(b.Left, scope, returnTypes);
                string? r = StaticTypeOf(b.Right, scope, returnTypes);
                if (l == null || r == null) return null;
                return Join(l, r);
            }
            case TernaryExpr t3:
            {
                string? a = StaticTypeOf(t3.TrueVal, scope, returnTypes);
                string? c = StaticTypeOf(t3.FalseVal, scope, returnTypes);
                return a != null && c != null ? Join(a, c) : null;
            }
            case CallExpr c2 when c2.Callee is VariableExpr fn:
            {
                // Width cast: uint16(x) etc.
                if (IntTypes.Contains(fn.Name) && fn.Name != "int") return fn.Name;
                return returnTypes.TryGetValue(fn.Name, out var rt) ? Normalize(rt) : null;
            }
            default: return null;
        }
    }

    private static string TypeOfIntValue(int v) => v switch
    {
        < short.MinValue => "int32",
        < sbyte.MinValue => "int16",
        < 0 => "int8",
        <= byte.MaxValue => "uint8",
        <= ushort.MaxValue => "uint16",
        _ => "uint32",
    };

    // Safe integer widening join. Same signedness -> the wider; mixed -> the signed type
    // one rank above the widest unsigned operand (so its full range still fits).
    private static string Join(string a, string b)
    {
        (bool aS, int aR) = Rank(a);
        (bool bS, int bR) = Rank(b);
        if (aS == bS) return Name(aS, Math.Max(aR, bR));
        int uRank = aS ? bR : aR;
        int sRank = aS ? aR : bR;
        return Name(true, Math.Min(2, Math.Max(sRank, uRank + 1)));
    }

    private static (bool Signed, int Rank) Rank(string t) => t switch
    {
        "uint8" => (false, 0), "uint16" => (false, 1), "uint32" => (false, 2),
        "int8" => (true, 0), "int16" or "int" => (true, 1), _ => (true, 2),
    };

    private static string Name(bool signed, int rank) => (signed, rank) switch
    {
        (false, 0) => "uint8", (false, 1) => "uint16", (false, _) => "uint32",
        (true, 0) => "int8", (true, 1) => "int16", (true, _) => "int32",
    };

    // ── AST walking ─────────────────────────────────────────────────────────────

    private static bool HasValueReturn(Block body)
        => CollectReturns(body.Statements).Any();

    private static IEnumerable<Expression> CollectReturns(List<Statement> body)
        => WalkStatements(body).OfType<ReturnStmt>()
            .Where(r => r.Value != null && r.Value is not Frontend.TupleExpr)
            .Select(r => r.Value!);

    private static IEnumerable<Statement> WalkStatements(List<Statement> body)
    {
        foreach (var s in body)
            foreach (var inner in WalkStatement(s))
                yield return inner;
    }

    private static IEnumerable<Statement> WalkStatement(Statement s)
    {
        yield return s;
        switch (s)
        {
            case Block b:
                foreach (var i in WalkStatements(b.Statements)) yield return i;
                break;
            case IfStmt ifs:
                foreach (var i in WalkStatement(ifs.ThenBranch)) yield return i;
                foreach (var (_, eb) in ifs.ElifBranches)
                    foreach (var i in WalkStatement(eb)) yield return i;
                if (ifs.ElseBranch != null)
                    foreach (var i in WalkStatement(ifs.ElseBranch)) yield return i;
                break;
            case WhileStmt w:
                foreach (var i in WalkStatement(w.Body)) yield return i;
                break;
            case ForStmt f:
                foreach (var i in WalkStatement(f.Body)) yield return i;
                break;
            case WithStmt ws:
                foreach (var i in WalkStatement(ws.Body)) yield return i;
                break;
            case TryStmt t:
                foreach (var i in WalkStatements(t.Body)) yield return i;
                foreach (var (_, h) in t.Handlers)
                    foreach (var i in WalkStatements(h)) yield return i;
                if (t.Finally != null)
                    foreach (var i in WalkStatements(t.Finally)) yield return i;
                if (t.ElseBody != null)
                    foreach (var i in WalkStatements(t.ElseBody)) yield return i;
                break;
            case MatchStmt m:
                foreach (var c in m.Branches)
                    if (c.Body != null)
                        foreach (var i in WalkStatement(c.Body)) yield return i;
                break;
        }
    }

    // Every expression appearing in the statements (top-level expressions; sub-expressions
    // are reached via WalkExpression).
    private static IEnumerable<Expression> WalkExpressions(List<Statement> body)
    {
        foreach (var s in WalkStatements(body))
        {
            switch (s)
            {
                case ExprStmt es: foreach (var e in WalkExpression(es.Expr)) yield return e; break;
                case AssignStmt a:
                    foreach (var e in WalkExpression(a.Value)) yield return e;
                    foreach (var e in WalkExpression(a.Target)) yield return e;
                    break;
                case VarDecl vd when vd.Init != null:
                    foreach (var e in WalkExpression(vd.Init)) yield return e; break;
                case AnnAssign aa when aa.Value != null:
                    foreach (var e in WalkExpression(aa.Value)) yield return e; break;
                case AugAssignStmt ag:
                    foreach (var e in WalkExpression(ag.Value)) yield return e; break;
                case ReturnStmt r when r.Value != null:
                    foreach (var e in WalkExpression(r.Value)) yield return e; break;
                case IfStmt ifs:
                    foreach (var e in WalkExpression(ifs.Condition)) yield return e;
                    foreach (var (c, _) in ifs.ElifBranches)
                        foreach (var e in WalkExpression(c)) yield return e;
                    break;
                case WhileStmt w:
                    foreach (var e in WalkExpression(w.Condition)) yield return e; break;
            }
        }
    }

    private static IEnumerable<Expression> WalkExpression(Expression e)
    {
        yield return e;
        switch (e)
        {
            case BinaryExpr b:
                foreach (var i in WalkExpression(b.Left)) yield return i;
                foreach (var i in WalkExpression(b.Right)) yield return i;
                break;
            case UnaryExpr u:
                foreach (var i in WalkExpression(u.Operand)) yield return i;
                break;
            case TernaryExpr t:
                foreach (var i in WalkExpression(t.Condition)) yield return i;
                foreach (var i in WalkExpression(t.TrueVal)) yield return i;
                foreach (var i in WalkExpression(t.FalseVal)) yield return i;
                break;
            case CallExpr c:
                foreach (var a in c.Args)
                {
                    var inner = a is KeywordArgExpr kw ? kw.Value : a;
                    foreach (var i in WalkExpression(inner)) yield return i;
                }
                break;
            case IndexExpr ix:
                foreach (var i in WalkExpression(ix.Target)) yield return i;
                foreach (var i in WalkExpression(ix.Index)) yield return i;
                break;
            case MemberAccessExpr m:
                foreach (var i in WalkExpression(m.Object)) yield return i;
                break;
        }
    }
}
