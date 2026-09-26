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
// polymorphic by design -- the HAL relies on it), overloaded names (inference would
// fight overload-by-type resolution), @extern/@interrupt/@naked. Class methods join
// the RETURN side only (PyMCU#489): their params are never reached by bare-name
// call-site evidence -- `self.<m>(...)` is a member call -- but an unannotated
// `return 300` must land on the FunctionDef exactly as a declared `-> uint16` does.
using System;
using System.Collections.Generic;
using System.Linq;
using PyMCU.Common;

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
        // Class methods are return-inference candidates only (#489). prog.Functions
        // never listed them, so `def _read(self): return 300` kept the void default:
        // `self.v = self._read()` laid the field out as uint8 and the outlined callee
        // truncated its own return. The join mutates the FunctionDef's ReturnType,
        // which is what the scan reads through methodsByName (InferAssignedFieldType)
        // and writes into functionReturnTypes under the Class_method key -- the
        // inferred type is seen precisely as a declared one. No name-count guard is
        // needed for methods: each FunctionDef keeps its own ReturnType and nothing
        // here keys a method by bare name.
        var methodCandidates = new List<FunctionDef>();
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
            foreach (var m in ClassMethods(prog))
            {
                if (m.IsInline || m.IsExtern || m.IsInterrupt || m.IsNaked) continue;
                if (IsInferableReturn(m.ReturnType) && HasValueReturn(m.Body))
                    methodCandidates.Add(m);
            }
        }
        if (candidates.Count == 0 && methodCandidates.Count == 0) return;

        // Known (annotated or already-inferred) return types by bare function name.
        var returnTypes = new Dictionary<string, string>();
        // And their union member lists where declared -- a `return f()` inside
        // another unannotated function contributes the callee's whole member set.
        var memberLists = new Dictionary<string, List<string>>();
        foreach (var prog in programs)
            foreach (var f in prog.Functions)
            {
                if (f.ReturnType.Length > 0) returnTypes[f.Name] = f.ReturnType;
                if (f.ReturnMembers != null) memberLists[f.Name] = f.ReturnMembers;
            }

        for (int pass = 0; pass < Passes; pass++)
        {
            // param evidence: function -> param index -> joined type
            var evidence = new Dictionary<FunctionDef, string?[]>();
            // Per param: whether None can arrive (a `= None` default or a None argument),
            // and whether some argument is a literal that is not a number.
            var noneEvidence = new Dictionary<FunctionDef, (bool none, bool unknown)[]>();
            foreach (var f in candidates)
            {
                var ev = new string?[f.Params.Count];
                var nev = new (bool none, bool unknown)[f.Params.Count];
                for (int i = 0; i < f.Params.Count; i++)
                {
                    if (f.Params[i].Type.Length == 0 && f.Params[i].DefaultValue is IntegerLiteral dl)
                        ev[i] = TypeOfIntValue(dl.Value);
                    if (f.Params[i].DefaultValue is NoneLiteral) nev[i].none = true;
                }
                evidence[f] = ev;
                noneEvidence[f] = nev;
            }
            var byName = candidates.ToDictionary(f => f.Name);

            // Sweep every statement in the program for call sites of the candidates.
            foreach (var prog in programs)
            {
                foreach (var f in prog.Functions)
                    CollectFromBody(f.Body.Statements, ScopeTypes(f), byName, returnTypes, evidence,
                        noneEvidence);
                CollectFromBody(prog.GlobalStatements, ModuleScopeTypes(prog), byName, returnTypes,
                    evidence, noneEvidence);
            }

            // Apply: fill empty param annotations from the joined evidence.
            foreach (var f in candidates)
            {
                var ev = evidence[f];
                var nev = noneEvidence[f];
                for (int i = 0; i < f.Params.Count; i++)
                {
                    if (f.Params[i].Type.Length != 0) continue;
                    // None can arrive and every other argument is an integer: the parameter
                    // is Optional[int], exactly as if it had been written so. Left a plain
                    // integer, the body's `p is None` folded to False for every call -- the
                    // subroutine is shared, so no call site's None could reach the fold --
                    // and the None itself arrived as whatever byte the default lowered to.
                    // The payload is the width the parameter had anyway -- the joined
                    // evidence, or the historical uint8 -- so only the None changes. A
                    // parameter the body treats as a buffer, an object or a callable keeps
                    // the old reading: an integer union would be a lie about it.
                    if (nev[i].none && !nev[i].unknown && f.Params[i].UnionMembers == null
                        && UsedOnlyAsScalar(f, f.Params[i].Name))
                    {
                        string payload = ev[i] ?? "uint8";
                        f.Params[i].Type = payload;
                        f.Params[i].UnionMembers = new List<string> { payload, "None" };
                        continue;
                    }
                    if (ev[i] != null)
                        f.Params[i].Type = ev[i]!;
                }

                // RFC 0009 phase 3 (6.1): the return type is the member list the
                // return statements produce -- one member for a provably uniform
                // body, several for a genuine union (the IRGenerator's resolve pass
                // then decides whether a tag is spent, trims dead-only members, and
                // refuses past the four-member ceiling).
                if (InferUnionReturnMembers(f, returnTypes, memberLists) is { } umembers)
                {
                    f.ReturnType = umembers.Count == 1 ? umembers[0] : WidestMemberName(umembers);
                    returnTypes[f.Name] = f.ReturnType;
                    if (umembers.Count >= 2)
                    {
                        f.ReturnMembers = umembers;
                        f.ReturnMembersInferred = true;
                        memberLists[f.Name] = umembers;
                    }
                }
            }

            // Method returns: the same member collection, but nothing enters
            // returnTypes -- a bare-name entry would alias a module-level function
            // of the same name, and member calls never resolve through that table
            // anyway.
            foreach (var m in methodCandidates)
                if (InferUnionReturnMembers(m, returnTypes, memberLists) is { } mm)
                {
                    m.ReturnType = mm.Count == 1 ? mm[0] : WidestMemberName(mm);
                    if (mm.Count >= 2)
                    {
                        m.ReturnMembers = mm;
                        m.ReturnMembersInferred = true;
                    }
                }
        }
    }

    // Every method FunctionDef a module's classes declare: the classes at the top level
    // of GlobalStatements, and nested classes inside them -- the same universe the scan
    // registers under Class_method keys.
    internal static IEnumerable<FunctionDef> ClassMethods(ProgramNode prog)
    {
        foreach (var s in prog.GlobalStatements)
            foreach (var m in MethodsOf(s))
                yield return m;
    }

    private static IEnumerable<FunctionDef> MethodsOf(Statement st)
    {
        if (st is not ClassDef cls || cls.Body is not Block body) yield break;
        foreach (var member in body.Statements)
        {
            if (member is FunctionDef fn) yield return fn;
            // Nested classes declare methods of their own.
            foreach (var m in MethodsOf(member)) yield return m;
        }
    }

    // Return member list: the distinct representations the value returns produce,
    // in first-appearance order, None always last (RFC 0009 section 6.1). All
    // integer evidence joins into ONE member -- "two paths that return an int share
    // a tag", and the join is what keeps `return 0` / `return -1` a plain int16
    // instead of a union that would force every caller to narrow. bool, float and
    // None are members of their own. One return whose type is unknown -- a member
    // read, a subscript, a string, a call this table does not cover -- gives up and
    // leaves the declaration empty; that is what keeps the RFC 0009 `return None`
    // shape compiling exactly as it did.
    private static List<string>? InferUnionReturnMembers(
        FunctionDef f, Dictionary<string, string> returnTypes,
        Dictionary<string, List<string>> memberLists)
    {
        if (!IsInferableReturn(f.ReturnType) || !HasValueReturn(f.Body)) return null;
        var scope = ScopeTypes(f);
        var members = new List<string>();
        bool sawNone = false;
        foreach (var r in CollectReturns(f.Body.Statements))
        {
            if (r is NoneLiteral) { sawNone = true; continue; }
            var em = ExprMembers(r, scope, returnTypes, memberLists);
            if (em == null) return null;   // any unknown -> give up
            foreach (var m in em)
            {
                if (m == "None") { sawNone = true; continue; }
                MergeMember(members, m);
            }
        }
        if (sawNone || HasBareReturn(f.Body)) members.Add("None");
        return members;
    }

    // Merge one inferred member into the list: int-family members coalesce into a
    // single joined int member at its first-appearance slot; every other kind
    // stays distinct (11.6: the tag keeps uint8 and bool apart even at one byte).
    private static void MergeMember(List<string> members, string m)
    {
        string? n = Normalize(m);
        if (n != null)
        {
            int slot = members.FindIndex(x => Normalize(x) != null);
            if (slot < 0) { members.Add(n); return; }
            members[slot] = Join(members[slot], n);
            return;
        }
        if (m == "bool" || m == "float")
        {
            if (!members.Contains(m)) members.Add(m);
        }
        // Anything else (str, named types) never reaches here -- ExprMembers gives up.
    }

    // The member list an expression can produce -- the union-aware counterpart of
    // StaticTypeOf. Null = unknown (same give-up rule).
    private static List<string>? ExprMembers(
        Expression e, Dictionary<string, string> scope,
        Dictionary<string, string> returnTypes, Dictionary<string, List<string>> memberLists)
    {
        switch (e)
        {
            case NoneLiteral: return new List<string> { "None" };
            case BooleanLiteral: return new List<string> { "bool" };
            case FloatLiteral: return new List<string> { "float" };
            case IntegerLiteral il: return new List<string> { TypeOfIntValue(il.Value) };
            case VariableExpr v:
            {
                if (!scope.TryGetValue(v.Name, out var t)) return null;
                // A union/Optional-annotated local or param contributes its members --
                // but only a union of scalars is a tag domain; Union[ROValueIO,
                // Callable] is a call-site union and says nothing about this return.
                var um = AnnotationText.UnionMembers(t);
                if (um != null)
                    return um.All(m => m == "None" || Normalize(m) != null
                        || m is "bool" or "float") ? new List<string>(um) : null;
                if (Normalize(t) is { } n) return new List<string> { n };
                return t is "bool" or "float" ? new List<string> { t } : null;
            }
            case UnaryExpr { Op: UnaryOp.Negate } un:
            {
                var inner = ExprMembers(un.Operand, scope, returnTypes, memberLists);
                if (inner == null) return null;
                for (int i = 0; i < inner.Count; i++)
                    if (Normalize(inner[i]) != null) inner[i] = Join(inner[i], "int8");
                return inner;
            }
            case UnaryExpr { Op: UnaryOp.BitNot } bn:
                return ExprMembers(bn.Operand, scope, returnTypes, memberLists);
            case UnaryExpr { Op: UnaryOp.Not }:
                return new List<string> { "uint8" };
            case BinaryExpr b:
            {
                if (b.Op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.LessEq
                    or BinaryOp.Greater or BinaryOp.GreaterEq or BinaryOp.And or BinaryOp.Or
                    or BinaryOp.In or BinaryOp.NotIn or BinaryOp.Is or BinaryOp.IsNot)
                    return new List<string> { "uint8" };
                var l = ExprMembers(b.Left, scope, returnTypes, memberLists);
                var r = ExprMembers(b.Right, scope, returnTypes, memberLists);
                if (l == null || r == null) return null;
                // Numeric join: a float on either side makes the result float;
                // otherwise the int members join.
                if (l.Contains("float") || r.Contains("float") || b.Op == BinaryOp.Div)
                    return new List<string> { "float" };
                if (!l.All(m => Normalize(m) != null) || !r.All(m => Normalize(m) != null))
                    return null;
                return new List<string> { l.Concat(r).Aggregate(Join) };
            }
            case TernaryExpr t3:
            {
                var a = ExprMembers(t3.TrueVal, scope, returnTypes, memberLists);
                var c = ExprMembers(t3.FalseVal, scope, returnTypes, memberLists);
                if (a == null || c == null) return null;
                var merged = new List<string>();
                bool none = false;
                foreach (var m in a.Concat(c))
                    if (m == "None") none = true;
                    else MergeMember(merged, m);
                if (none) merged.Add("None");
                return merged;
            }
            case CallExpr c2 when c2.Callee is VariableExpr fn:
            {
                // Width cast: uint16(x) etc.
                if (IntTypes.Contains(fn.Name)) return new List<string> { Normalize(fn.Name)! };
                if (fn.Name == "float") return new List<string> { "float" };
                if (fn.Name == "bool") return new List<string> { "bool" };
                if (fn.Name == "str") return null;
                if (memberLists.TryGetValue(fn.Name, out var ml)) return new List<string>(ml);
                return returnTypes.TryGetValue(fn.Name, out var rt) && Normalize(rt) is { } nrt
                    ? new List<string> { nrt }
                    : null;
            }
            default: return null;
        }
    }

    // Widest member by payload bytes, float winning the four-byte tie -- the same
    // order UnionPayloadType uses in the IRGenerator.
    private static string WidestMemberName(List<string> members)
    {
        static int Rank(string m) => m switch
        {
            "float" or "uint32" or "int32" => 4,
            "uint16" or "int16" or "int" => 2,
            "bool" or "uint8" or "int8" or "char" => 1,
            _ => 0,
        };
        string best = members[0];
        foreach (var m in members)
        {
            if (Rank(m) > Rank(best) ||
                (Rank(m) == Rank(best) && m == "float" && best != "float"))
                best = m;
        }
        return best;
    }

    // ── evidence collection ─────────────────────────────────────────────────────

    private static void CollectFromBody(
        List<Statement> body, Dictionary<string, string> scope,
        Dictionary<string, FunctionDef> byName, Dictionary<string, string> returnTypes,
        Dictionary<FunctionDef, string?[]> evidence,
        Dictionary<FunctionDef, (bool none, bool unknown)[]> noneEvidence)
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
                if (valueExpr is NoneLiteral)
                {
                    noneEvidence[f][index].none = true;
                    continue;
                }
                // A literal that is not a number says what the parameter is: not an integer.
                if (valueExpr is not (IntegerLiteral or BooleanLiteral or VariableExpr or BinaryExpr
                                      or UnaryExpr or CallExpr or MemberAccessExpr or IndexExpr
                                      or TernaryExpr))
                    noneEvidence[f][index].unknown = true;
                string? t = StaticTypeOf(valueExpr, scope, returnTypes);
                if (t != null) ev[index] = ev[index] == null ? t : Join(ev[index]!, t);
            }
        }
    }

    // True when nothing in the body subscripts, iterates, measures, calls or reads a member
    // of <paramref name="name"/>: the uses a number (or None) supports.
    private static bool UsedOnlyAsScalar(FunctionDef f, string name)
    {
        bool Is(Expression? e) => e is VariableExpr v && v.Name == name;
        foreach (var s in WalkStatements(f.Body.Statements))
            if (s is ForStmt fs && Is(fs.Iterable)) return false;
        var bodyStmts = f.Body.Statements;
        foreach (var e in WalkExpressions(bodyStmts))
        {
            switch (e)
            {
                case IndexExpr ix when Is(ix.Target): return false;
                case MemberAccessExpr ma when Is(ma.Object): return false;
                case CallExpr c when Is(c.Callee)
                                     || c.Callee is MemberAccessExpr cm && Is(cm.Object)
                                     || c.Callee is VariableExpr { Name: "len" }
                                        && c.Args.Count == 1 && Is(c.Args[0]):
                    return false;
            }
        }
        return true;
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

    // RFC 0009: a bare `return` carries None exactly like `return None` does, but is
    // not in CollectReturns (which yields the VALUE expressions). Falling off the end
    // is deliberately NOT counted: an unannotated function that only reaches its end
    // keeps the uint8-and-implicit-None it always had -- only an explicit statement
    // is evidence the author meant Optional.
    private static bool HasBareReturn(Block body)
        => WalkStatements(body.Statements).OfType<ReturnStmt>().Any(r => r.Value == null);

    private static IEnumerable<Expression> CollectReturns(List<Statement> body)
        => WalkStatements(body).OfType<ReturnStmt>()
            .Where(r => r.Value != null && r.Value is not Frontend.TupleExpr)
            .Select(r => r.Value!);

    // The shared statement walk: every statement in a body at ANY nesting depth
    // (for/while/if/try/with/match), in source order. DeriveFieldLayout uses it too --
    // a field write inside a loop is the same write for layout purposes (#488).
    internal static IEnumerable<Statement> WalkStatements(IEnumerable<Statement> body)
    {
        foreach (var s in body)
            foreach (var inner in WalkStatement(s))
                yield return inner;
    }

    // Same walk for a body held as a single statement (an arm, a loop body).
    internal static IEnumerable<Statement> WalkStatements(Statement? body)
    {
        if (body == null) yield break;
        foreach (var inner in WalkStatement(body))
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
    internal static IEnumerable<Expression> WalkExpressions(List<Statement> body)
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
