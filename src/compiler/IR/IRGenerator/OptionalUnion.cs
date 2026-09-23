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
using PyMCU.Common.Models;
using PyMCU.Frontend;
using AstBinOp = PyMCU.Frontend.BinaryOp;
using AstUnOp = PyMCU.Frontend.UnaryOp;

namespace PyMCU.IR.IRGenerator;

// RFC 0009 phase 1 -- Optional[X] / Union[X, None] as a payload-plus-tag value on real
// subroutines. The tag byte is the union-member index (None is always last); on the wire
// it rides the register after the payload, and in storage it lives in the sibling slot
// "<name>$tag". Everything in this file is compile-time bookkeeping for that byte: which
// functions need one, which locals carry one, and which reads are safe.
public partial class IRGenerator
{
    // ── function-level state ────────────────────────────────────────────────

    // Resolved name -> member list for every function whose return can carry a
    // run-time None. Filled by ResolveOptionalReturns before any body lowers, so a
    // call site can ask it while its own function is still being generated.
    private readonly Dictionary<string, List<string>> functionReturnMembers = new();

    // The FunctionDef's declared/inferred member list, tagged or not -- needed by the
    // resolve pass itself and by diagnostics that name the annotation.
    private readonly Dictionary<string, List<string>> functionDeclaredMembers = new();

    // ── per-value tag state (the current function being lowered) ───────────

    // Qualified payload name -> the Val holding its tag byte right now. Membership
    // means the name is a LIVE run-time optional: a `v is None` test must read the
    // byte. Cleared (re-seated) by assignments, merged at control-flow joins.
    private readonly Dictionary<string, Val> optionalTagSlots = new();

    // Qualified payload name -> its union member list, for diagnostics and the None index.
    private readonly Dictionary<string, List<string>> optionalMembersByName = new();

    // Live-optional names proven to hold ONE member on the path being lowered:
    // qualified payload name -> the member index it provably is (inside
    // `if v is not None:` -- member 0 for the phase-1 [X, None] shape -- and inside
    // `if isinstance(v, int):` / a matching `case` arm for a multi-member union).
    // Payload reads are safe, and they read at the member's width.
    private readonly Dictionary<string, int> narrowedOptionals = new();

    // ── union fields (RFC 0009 phase 3: the field half of the tag) ──────────

    // classKey -> the fields its writes hold BOTH None and a scalar: those fields
    // are tagged unions -- a payload at the widest member's width plus a tag byte,
    // exactly the local Optional shape at field scope. DeriveFieldLayout fills the
    // two evidence sets below; IsUnionField merges them over the MRO (a base ctor's
    // `self.f = None` and a subclass helper's scalar write are one field).
    private readonly Dictionary<string, HashSet<string>> fieldNoneWrites = new();
    private readonly Dictionary<string, HashSet<string>> fieldScalarWrites = new();

    // `cls|field` -> the member list an `self.f: Union[...]`/`Optional[...]`
    // annotation on a write declared; absent for fields the writes alone made
    // unions (their member list grows as the writes expand, None pinned first).
    private readonly Dictionary<string, List<string>> fieldDeclaredUnionMembers = new();

    // Flattened field names whose member list is the declared one -- a write's
    // member must already be in it, not join it.
    private readonly HashSet<string> unionFieldDeclared = new();

    // Names the precompute proved CAN hold a run-time optional somewhere in this
    // function (or module top level). Only such names get the tag write on a
    // non-optional assignment, which is what keeps the tag honest across arms.
    private readonly HashSet<string> optionalCapable = new();

    // >0 while lowering a reader that knows about the tag (is-None operands, `or`
    // left operand, truth tests, return values, the payload copy of `x = v`, the
    // print dispatch). Every other read of an unnarrowed optional is a CompileError.
    private int optionalReadAllowed;

    /// The union-member index of None -- always the last member when present
    /// (RFC 0009 section 4), -1 when the union has no None member.
    private static int NoneIndex(List<string> members) => members.IndexOf("None");

    /// The DataType a member name stores its payload in. `bool` is one byte.
    private static DataType MemberDataType(string member) =>
        member == "bool" ? DataType.UINT8 : DataTypeExtensions.StringToDataType(member);

    /// Whether the member name is an integer-kind scalar (not bool, not float).
    private static bool IsIntMember(string member) =>
        member is "int" or "int8" or "int16" or "int32"
            or "uint8" or "uint16" or "uint32" or "char";

    /// The payload type a union's storage slot carries: the widest member, with a
    /// float member winning a width tie (its register order differs from an int's).
    private static DataType UnionPayloadType(List<string> members)
    {
        DataType widest = DataType.UINT8;
        foreach (var m in members)
        {
            if (m == "None") continue;
            var dt = MemberDataType(m);
            if (dt.SizeOf() > widest.SizeOf()
                || (dt == DataType.FLOAT && widest != DataType.FLOAT && dt.SizeOf() == widest.SizeOf()))
                widest = dt;
        }
        return widest;
    }

    /// The widest member's NAME, where a union field's storage width is recorded
    /// as annotation text (the class field layout's Type). Mirrors UnionPayloadType.
    private static string WidestUnionMemberName(List<string> members)
    {
        string widest = "uint8";
        foreach (var m in members)
        {
            if (m == "None") continue;
            var dt = MemberDataType(m);
            if (dt.SizeOf() > MemberDataType(widest).SizeOf()
                || (dt == DataType.FLOAT && widest != "float"
                    && dt.SizeOf() == MemberDataType(widest).SizeOf()))
                widest = m;
        }
        return widest;
    }

    /// <summary>
    /// The member index a returned/stored value takes in <paramref name="members"/>:
    /// literal kinds pick their Python type's member, a constant integer picks the
    /// first int member that can hold it, a run-time Val matches its DataType
    /// (exact member first, then the first int-family member). Returns null when no
    /// member can represent the value -- the caller turns that into the diagnostic.
    /// </summary>
    private int? MemberIndexFor(Expression? expr, Val? val, List<string> members)
    {
        // Literal evidence first: it carries the author's intended type even where
        // the lowered Val would read as a narrower DataType.
        if (expr is FloatLiteral || val is FloatConstant)
            return members.IndexOf("float") >= 0 ? members.IndexOf("float") : null;
        if (expr is BooleanLiteral)
        {
            int bIdx = members.IndexOf("bool");
            return bIdx >= 0 ? bIdx : FirstIntMemberThatFits(members,
                ((BooleanLiteral)expr!).Value ? 1 : 0);
        }
        if (expr is StringLiteral or FStringExpr) return members.IndexOf("str") >= 0 ? members.IndexOf("str") : null;

        if (val is Constant c)
        {
            long v = c.Value;
            for (int i = 0; i < members.Count; ++i)
                if (IsIntMember(members[i]) && IntMemberFits(members[i], v)) return i;
            return null;
        }

        DataType dt = val != null ? GetValType(val) : DataType.UNKNOWN;
        if (dt == DataType.FLOAT) return members.IndexOf("float") >= 0 ? members.IndexOf("float") : null;
        if (dt is DataType.UINT8 or DataType.INT8 or DataType.UINT16 or DataType.INT16
            or DataType.UINT32 or DataType.INT32)
        {
            // Exact-width member first (`return u16` under Union[uint8, uint16]
            // is the uint16 member), else the first int member that holds it.
            for (int i = 0; i < members.Count; ++i)
                if (MemberDataType(members[i]) == dt && members[i] != "bool") return i;
            for (int i = 0; i < members.Count; ++i)
                if (IsIntMember(members[i]) && MemberDataType(members[i]).SizeOf() >= dt.SizeOf())
                    return i;
            return null;
        }
        if (dt == DataType.FUNCREF)
        {
            for (int i = 0; i < members.Count; ++i)
                if (members[i].StartsWith("Callable")) return i;
            return null;
        }
        return null;
    }

    /// Whether the integer member can hold the constant value.
    private static bool IntMemberFits(string member, long v) => member switch
    {
        "uint8" or "char" => v is >= 0 and <= 0xFF,
        "uint16" => v is >= 0 and <= 0xFFFF,
        "uint32" => v is >= 0 and <= 0xFFFFFFFFL,
        "int8" => v is >= sbyte.MinValue and <= sbyte.MaxValue,
        "int16" or "int" => v is >= short.MinValue and <= short.MaxValue,
        "int32" => v is >= int.MinValue and <= int.MaxValue,
        _ => false,
    };

    private static int FirstIntMemberThatFits(List<string> members, long v)
    {
        for (int i = 0; i < members.Count; ++i)
            if (IsIntMember(members[i]) && IntMemberFits(members[i], v)) return i;
        return -1;
    }

    /// The member indices of <paramref name="members"/> an `isinstance(r, T)`
    /// candidate name matches: an int-family name matches every int member, float
    /// and bool their own, a class name none (a scalar union never holds one).
    private List<int> IsinstanceMemberIndices(Expression typeExpr, List<string> members)
    {
        var names = new List<string>();
        if (typeExpr is TupleExpr tup)
            foreach (var e in tup.Elements)
                if (e is VariableExpr v) names.Add(v.Name);
        if (typeExpr is VariableExpr single) names.Add(single.Name);
        if (names.Count == 0) return new List<int>();

        var hits = new List<int>();
        for (int i = 0; i < members.Count; ++i)
        {
            string m = members[i];
            foreach (var n in names)
            {
                string bare = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n;
                bool hit = bare == m
                    || (bare is "NoneType" && m == "None")
                    || (IsIntMember(bare) && IsIntMember(m))
                    || (bare == "int" && IsIntMember(m));
                if (hit) { hits.Add(i); break; }
            }
        }
        return hits;
    }

    /// The member list of a call target, when it is a runtime-tagged Optional return.
    private List<string>? OptionalMembersOfCallee(string resolvedName)
        => functionReturnMembers.TryGetValue(resolvedName, out var m) ? m : null;

    /// Whether the resolved function name returns a runtime-tagged Optional.
    private bool CalleeIsOptional(string resolvedName) => functionReturnMembers.ContainsKey(resolvedName);

    /// The member list the function currently being lowered returns with a tag,
    /// or null when it is an ordinary (or inline) function.
    private List<string>? CurrentReturnMembers =>
        inlineStack.Count > 0 ? null
        : (functionReturnMembers.TryGetValue(currentFunction, out var m) ? m : null);

    /// The tag slot <paramref name="val"/> carries right now, or null.
    private Val? TagOfVal(Val val) => val switch
    {
        Variable v when optionalTagSlots.TryGetValue(v.Name, out var t) => t,
        Temporary t when optionalTagSlots.TryGetValue(t.Name, out var tg) => tg,
        _ => null,
    };

    /// Every storage-name spelling a source name can resolve to in the current
    /// context -- the inline-prefixed one, the function-qualified one, the
    /// module-global one and the bare name. Tag state is keyed on the name the
    /// emitted Variable carries, so lookups must ask all of them.
    private IEnumerable<string> OptionalNameKeys(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)) yield return currentInlinePrefix + name;
        if (!string.IsNullOrEmpty(currentFunction)) yield return currentFunction + "." + name;
        if (!string.IsNullOrEmpty(currentModulePrefix)) yield return currentModulePrefix + name;
        yield return name;
    }

    /// The key under which a source name is a live optional, when it is one.
    private string? OptionalKeyOf(string name)
    {
        foreach (var k in OptionalNameKeys(name))
            if (optionalTagSlots.ContainsKey(k)) return k;
        return null;
    }

    /// The live-optional key a condition subject resolves to: a bare name for a
    /// tagged local, a member access for a tagged field (its flattened name is
    /// the key).
    private string? OptionalKeyOfExpr(Expression e) => e switch
    {
        VariableExpr ve => OptionalKeyOf(ve.Name),
        MemberAccessExpr ma => FlatOptionalKeyOf(ma),
        _ => null,
    };

    /// Whether the source name is a live optional whose payload is unsafe to read
    /// here (not narrowed on this path).
    private bool IsUnnarrowedOptional(string name)
    {
        string? key = OptionalKeyOf(name);
        return key != null && !narrowedOptionals.ContainsKey(key);
    }

    /// The RFC 0009 section-8 refusal, from the point a payload read would use it.
    private void RefuseIfUnnarrowedOptional(VariableExpr expr)
    {
        if (optionalReadAllowed > 0) return;
        if (OptionalKeyOf(expr.Name) is { } key && !narrowedOptionals.ContainsKey(key))
            throw UserError(
                $"'{expr.Name}' may be None here; narrow it first " +
                $"(`if {expr.Name} is not None:`).", expr);
    }

    /// Storing a still-tagged value somewhere that has no tag byte -- a field, an
    /// array element, a real subroutine's concrete parameter -- drops the tag
    /// silently. Refuse it; a narrowed name is a definite payload and stores fine.
    private void RefuseOptionalPayloadStore(Val v, Expression src)
    {
        if (TagOfVal(v) == null) return;
        if (ValNameOf(v) is { } nm
            && (narrowedOptionals.ContainsKey(nm) || noneValuedNames.Contains(nm))) return;
        string who = DescribeOperand(src) ?? ValNameOf(v) ?? "the value";
        throw UserError(
            $"'{who}' may be None here; narrow it first (`if {who} is not None:`).", src);
    }

    /// Evaluate an expression whose bare-name read is a tag CARRY, not a payload use:
    /// `x = r`, `return r`, an `if r:`/`r or d` operand, an inline parameter bind.
    /// Only the bare variable is exempted -- `x = r + 1` still refuses at the `+`.
    private Val EvalOptionalCarry(Expression e)
    {
        if (e is not VariableExpr) return VisitExpression(e);
        optionalReadAllowed++;
        try { return VisitExpression(e); }
        finally { optionalReadAllowed--; }
    }

    /// The tag Val for a storage name: the existing slot if there is one, else a new
    /// "<name>$tag" byte. A module global's tag is itself a global byte; a local's tag
    /// is a sibling Variable the backend allocates like any other local.
    private Variable TagStorageFor(string storageName)
    {
        if (optionalTagSlots.TryGetValue(storageName, out var existing) && existing is Variable ev)
            return ev;
        string tagName = storageName + "$tag";
        if (mutableGlobals.ContainsKey(storageName))
            mutableGlobals[tagName] = DataType.UINT8;
        return new Variable(tagName, DataType.UINT8);
    }

    /// Record that the storage name now holds a runtime optional whose tag lives in
    /// <paramref name="tagVal"/>.
    private void MarkOptional(string storageName, Val tagVal, List<string> members)
    {
        optionalTagSlots[storageName] = tagVal;
        optionalMembersByName[storageName] = members;
        narrowedOptionals.Remove(storageName);
        noneValuedNames.Remove(storageName);
    }

    /// Record that the storage name holds a definite member (its tag is that
    /// member's index at run time). The tag slot stays -- a name that is optional
    /// on ANY path keeps its byte on every path -- but the read side is safe on
    /// this one, and reads take the member's width.
    private void MarkOptionalDefinite(string storageName, int memberIdx = 0)
    {
        narrowedOptionals[storageName] = memberIdx;
        noneValuedNames.Remove(storageName);
    }

    /// Record that the storage name holds None (tag == None index at run time).
    private void MarkOptionalNone(string storageName)
    {
        narrowedOptionals.Remove(storageName);
        noneValuedNames.Add(storageName);
    }

    // ── the decision-2 gate ─────────────────────────────────────────────────

    /// <summary>
    /// Decide, for every function that COULD return Optional (declared `-> Optional[X]`
    /// or inferred by TypeInference's N=2 rule), whether a run-time None can actually
    /// reach a return. Only those get a tag; a function whose None is provable at
    /// compile time -- a `return None` behind a folded guard -- keeps the exact code
    /// it had before (RFC 0009 decision 2, which is the gate this pass exists for).
    ///
    /// Runs after all scans and TypeInference, before any body lowers, so the answer
    /// is stable no matter which order the callsites and callees are visited in.
    /// </summary>
    private void ResolveOptionalReturns()
    {
        // Seed the declared/inferred member tables and refuse the member shapes the
        // tag cannot carry BEFORE any reachability question is asked.
        var candidates = new List<(string key, string prefix, FunctionDef fn, List<string> members)>();
        foreach (var entry in functionsToCompile)
        {
            var fn = entry.Func;
            if (fn.ReturnMembers == null) continue;
            // Functions whose return value nobody reads stay tag-free whatever their
            // returns look like: main, the module-init bodies and interrupt handlers
            // have no caller to hand a tag to.
            if (fn.Name is "main" or "__module_init" || fn.IsInterrupt || fn.IsNaked) continue;
            string key = (entry.Prefix ?? "") + fn.Name;
            var members = fn.ReturnMembers;

            if (fn.IsExportC || fn.IsExtern)
                throw UserError(
                    $"an exported function cannot return Union[{string.Join(", ", members)}]; " +
                    "a C caller has no tag to read.", fn);

            // The four-member ceiling applies to a declared union as spelled; for an
            // inferred one it applies to the members returns can actually reach --
            // a member that only appears in dead code does not count (6.1).
            ValidateUnionMembers(fn, members, checkCeiling: !fn.ReturnMembersInferred);

            candidates.Add((key, entry.Prefix ?? "", fn, members));
            functionDeclaredMembers[key] = members;
        }

        // The same member shapes are validated on functions that will INLINE:
        // a union-returning body force-inlined out of functionsToCompile (a
        // `return A()` factory, a ZCA method expanded at its call sites) still
        // spells members a tag cannot carry, and refusing at the declaration is
        // the same located diagnostic either way.
        foreach (var (name, ifn) in inlineFunctions)
            if (ifn?.ReturnMembers is { } im)
            {
                if (ifn.IsExportC || ifn.IsExtern)
                    throw UserError(
                        $"an exported function cannot return Union[{string.Join(", ", im)}]; " +
                        "a C caller has no tag to read.", ifn);
                ValidateUnionMembers(ifn, im, checkCeiling: !ifn.ReturnMembersInferred);
            }
        if (candidates.Count == 0) return;

        // The tag exists only for members a return can actually reach at run time
        // (RFC 0009 decision 2, generalized to N members): a union provably decidable
        // at compile time -- every reachable return is the same member -- keeps the
        // plain-type code it had before. `return g()` contributes g's reachable
        // members, and g's set is what this pass is deciding -- close to a fixpoint.
        // A cycle of functions that only ever return each other converges to
        // untagged, which is the correct answer for it.
        var reachableSets = new Dictionary<string, HashSet<int>>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (key, prefix, fn, members) in candidates)
            {
                if (functionReturnMembers.ContainsKey(key)) continue;
                string prevPrefix = currentModulePrefix;
                string prevFn = currentFunction;
                currentModulePrefix = prefix;
                currentFunction = key;
                HashSet<int>? reach = ReachableReturnMembers(fn, members);
                var lines = memberReturnLines;
                currentModulePrefix = prevPrefix;
                currentFunction = prevFn;
                // Null = a return the scan cannot pin to a member; it could be any
                // of them, so the function is tagged with its full member list.
                if (reach == null || reach.Count >= 2)
                {
                    // An inferred union keeps only the members a return can reach --
                    // the tag's domain IS the evidence. The declared list keeps its
                    // spelling (the author asked for those members).
                    var live = members;
                    if (fn.ReturnMembersInferred && reach != null)
                        live = reach.OrderBy(i => i).Select(i => members[i]).ToList();
                    if (fn.ReturnMembersInferred && live.Count > 4)
                    {
                        string detail = string.Join(", ", live.Select(m =>
                            lines.TryGetValue(members.IndexOf(m), out int ln)
                                ? $"{m} (return at line {ln})" : m));
                        throw UserError(
                            $"'{fn.Name}' infers a union of {live.Count} members ({detail}); " +
                            "PyMCU tagged unions carry at most 4 -- a value with that many " +
                            "possible types makes every reader dispatch that many ways. " +
                            "Return one type, or split the function.", fn);
                    }
                    if (fn.ReturnMembersInferred)
                    {
                        fn.ReturnMembers = live;
                        functionDeclaredMembers[key] = live;
                    }
                    functionReturnMembers[key] = live;
                    changed = true;
                }
                else
                {
                    reachableSets[key] = reach;
                }
            }
        }

        // A candidate still undecided reaches at most one member: it is that member
        // (or plain None) to its caller, at zero tag cost -- byte-identical.
        foreach (var (key, _, fn, members) in candidates)
        {
            if (functionReturnMembers.ContainsKey(key)) continue;
            if (reachableSets.TryGetValue(key, out var set) && set.Count == 1
                && set.First() != NoneIndex(members))
                fn.ReturnType = members[set.First()];
            else
                fn.ReturnType = "void";
        }
    }

    /// The members of <paramref name="members"/> a `Union[...]` spelled out, for diagnostics.
    private static string UnionDisplay(List<string> members)
        => "Union[" + string.Join(", ", members) + "]";

    /// <summary>
    /// The member shapes a tagged union cannot carry, refused at the function with
    /// the member list named (RFC 0009 section 6/6.1): more than four members (the
    /// tag is one byte holding the member index -- the ceiling is a design one), a
    /// member that is a ZCA instance (decision 4: instances are storage, not
    /// values), a buffer member (travels as a name, not a payload byte), and any
    /// member name that resolves to nothing.
    /// </summary>
    private void ValidateUnionMembers(FunctionDef fn, List<string> members, bool checkCeiling = true)
        => ValidateUnionMembers($"the return union of '{fn.Name}'", members, fn, checkCeiling);

    /// The member checks a declared union gets wherever it is spelled -- return
    /// annotation or field annotation. <paramref name="owner"/> is the possessive
    /// phrase the diagnostics attach the member to ("the return union of 'f'",
    /// "the union field 'C.x'").
    /// </summary>
    private void ValidateUnionMembers(string owner, List<string> members, ASTNode? site,
        bool checkCeiling = true)
    {
        if (checkCeiling && members.Count > 4)
            throw UserError(
                $"{owner} spells {members.Count} members " +
                $"({UnionDisplay(members)}); PyMCU tagged unions carry at most 4. A value " +
                "with that many possible types makes every reader dispatch that many ways; " +
                "PyMCU keeps one type per value.", site);

        foreach (var m in members)
        {
            if (m == "None") continue;
            if (MemberDataType(m) != DataType.UNKNOWN) continue;
            if (m.StartsWith("Callable") || m == "Callable") continue;
            if (m.StartsWith("list[") || m is "bytearray" or "bytes"
                || m.StartsWith("tuple[") || TupleType.IsTupleType(m) || m.Contains('['))
                throw UserError(
                    $"'{m}' in {owner} is not a value this compiler " +
                    "can carry: buffers travel as names (a reference a caller already holds), " +
                    "not as a payload byte the tag can guard.", site);
            string bare = m.Contains('.') ? m[(m.LastIndexOf('.') + 1)..] : m;
            if (classNames.Contains(m) || classNames.Contains(bare)
                || classFieldLayout.ContainsKey(m) || classFieldLayout.ContainsKey(bare)
                || zcaFactoryClasses.ContainsKey(m) || zcaFactoryClasses.ContainsKey(bare)
                || FindClassKey(bare) != null)
                throw UserError(
                    $"a union of instance types is not supported: '{m}' in {owner} " +
                    "is a class, and instances are storage, not values a tag " +
                    "byte can switch between (RFC 0009 decision 4). Keep the instance and give " +
                    "it a field that says which shape it is.", site);
            if (protocolClasses.Any(k => k == m || k == bare || k.EndsWith("_" + bare)))
                throw UserError(
                    $"a union of instance types is not supported: '{m}' in {owner} " +
                    "is a protocol, and instances are storage, not values a tag " +
                    "byte can switch between (RFC 0009 decision 4).", site);
            throw UserError(
                $"unknown type '{m}' in {owner}; a union member must " +
                "be a scalar type name (int, float, bool, uint8, ...) or None.", site);
        }
    }

    /// <summary>
    /// The set of member indices a `return` of <paramref name="fn"/> can carry at
    /// run time -- None's index included when None is reachable. Null when a return
    /// expression cannot be pinned to a member, which the caller reads as "any of
    /// them" (the function is tagged). Statement-level folding is replayed so a
    /// return on a dead arm contributes nothing (RFC 0009 decision 2 / 6.1).
    /// </summary>
    // The scan-time type scope for the function whose returns are being resolved:
    // params and annotated locals -- the variableTypes table is not populated until
    // the body lowers, which is after this pass.
    private Dictionary<string, string>? scanScope;
    // Member index -> source line of the first return that contributes it, for the
    // five-member refusal (RFC 0009 section 6.1 wants the return paths named).
    private readonly Dictionary<int, int> memberReturnLines = new();

    private HashSet<int>? ReachableReturnMembers(FunctionDef fn, List<string> members)
    {
        var noneLocals = new HashSet<string>();
        var optLocals = new HashSet<string>();
        var hits = new HashSet<int>();
        memberReturnLines.Clear();
        scanScope = new Dictionary<string, string>();
        foreach (var p in fn.Params)
            if (p.Type is { Length: > 0 } pt) scanScope[p.Name] = pt;
        try
        {
            if (!ScanStmtsForUnionMembers(fn.Body.Statements, noneLocals, optLocals, hits, members))
                return null;
        }
        finally { scanScope = null; }
        // Reaching the end of the body is an implicit `return None`.
        if (!AlwaysReturns(fn.Body))
        {
            int endNone = NoneIndex(members);
            if (endNone < 0)
                throw UserError(
                    $"'{fn.Name}' is declared to return {UnionDisplay(members)}, but it can reach " +
                    "the end without returning -- an implicit `return None` -- and None is not a " +
                    "member. Add a return on the remaining path, or add None to the union.", fn);
            hits.Add(endNone);
        }
        return hits;
    }

    /// Statement walk behind <see cref="ReachableReturnMembers"/>: the same arm-folding
    /// and local tracking the Optional scan does, collecting member indices instead
    /// of a yes/no. Returns false on the first return that cannot be pinned.
    private bool ScanStmtsForUnionMembers(List<Statement> stmts,
        HashSet<string> noneLocals, HashSet<string> optLocals,
        HashSet<int> hits, List<string> members)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case ReturnStmt r:
                {
                    if (r.Value == null || r.Value is NoneLiteral)
                    {
                        int ni = NoneIndex(members);
                        if (ni < 0)
                            throw UserError(
                                $"'{currentFunction}' returns None here, but its return union " +
                                $"{UnionDisplay(members)} has no None member -- the tag has no " +
                                "index for it. Add None to the union or return a member value.", r);
                        hits.Add(ni);
                        memberReturnLines.TryAdd(ni, r.Line);
                        break;
                    }
                    if (ExprMaybeRuntimeNone(r.Value, noneLocals, optLocals))
                    {
                        int ni = NoneIndex(members);
                        if (ni < 0)
                            throw UserError(
                                $"'{currentFunction}' can return None here, but its return union " +
                                $"{UnionDisplay(members)} has no None member. Add None to the " +
                                "union or return a member value.", r);
                        hits.Add(ni);
                        memberReturnLines.TryAdd(ni, r.Line);
                        break;
                    }
                    if (ReturnMemberIndexOf(r.Value, noneLocals, optLocals, members) is { } idx)
                    {
                        hits.Add(idx);
                        memberReturnLines.TryAdd(idx, r.Line);
                        break;
                    }
                    return false;   // unpinnable: could be any member
                }

                case AssignStmt a when a.Target is VariableExpr av:
                    TrackOptionalAssign(av.Name, a.Value, noneLocals, optLocals);
                    break;
                case VarDecl vd:
                    if (vd.UnionMembers != null)
                    {
                        optLocals.Add(vd.Name);
                        noneLocals.Remove(vd.Name);
                        if (vd.Init == null || vd.Init is NoneLiteral) { optLocals.Remove(vd.Name); noneLocals.Add(vd.Name); }
                        else if (!ExprMaybeRuntimeNone(vd.Init, noneLocals, optLocals)) { optLocals.Remove(vd.Name); }
                    }
                    else if (vd.Init != null)
                        TrackOptionalAssign(vd.Name, vd.Init, noneLocals, optLocals);
                    break;
                case AnnAssign aa:
                    if (aa.UnionMembers != null)
                    {
                        optLocals.Add(aa.Target);
                        noneLocals.Remove(aa.Target);
                        if (aa.Value == null || aa.Value is NoneLiteral) { optLocals.Remove(aa.Target); noneLocals.Add(aa.Target); }
                        else if (!ExprMaybeRuntimeNone(aa.Value, noneLocals, optLocals)) { optLocals.Remove(aa.Target); }
                    }
                    else if (aa.Value != null)
                        TrackOptionalAssign(aa.Target, aa.Value, noneLocals, optLocals);
                    break;

                case IfStmt ifs:
                    if (!ScanIfForUnionMembers(ifs, noneLocals, optLocals, hits, members)) return false;
                    break;

                case WhileStmt w:
                {
                    var bodyNone = new HashSet<string>(noneLocals);
                    var bodyOpt = new HashSet<string>(optLocals);
                    if (!ScanStmtsForUnionMembers(ArmStatements(w.Body), bodyNone, bodyOpt, hits, members)) return false;
                    noneLocals.IntersectWith(bodyNone);
                    optLocals.UnionWith(bodyOpt);
                    break;
                }
                case ForStmt f:
                {
                    var bodyNone = new HashSet<string>(noneLocals);
                    var bodyOpt = new HashSet<string>(optLocals);
                    if (!ScanStmtsForUnionMembers(ArmStatements(f.Body), bodyNone, bodyOpt, hits, members)) return false;
                    noneLocals.IntersectWith(bodyNone);
                    optLocals.UnionWith(bodyOpt);
                    break;
                }

                case TryStmt t:
                {
                    var tNone = new HashSet<string>(noneLocals);
                    var tOpt = new HashSet<string>(optLocals);
                    if (!ScanStmtsForUnionMembers(t.Body, tNone, tOpt, hits, members)) return false;
                    foreach (var (_, h) in t.Handlers)
                        if (!ScanStmtsForUnionMembers(h, noneLocals, optLocals, hits, members)) return false;
                    if (t.ElseBody != null && !ScanStmtsForUnionMembers(t.ElseBody, noneLocals, optLocals, hits, members)) return false;
                    if (t.Finally != null && !ScanStmtsForUnionMembers(t.Finally, noneLocals, optLocals, hits, members)) return false;
                    optLocals.UnionWith(tOpt);
                    break;
                }

                case WithStmt wi:
                    if (!ScanStmtsForUnionMembers(ArmStatements(wi.Body), noneLocals, optLocals, hits, members)) return false;
                    break;
                case Block b:
                    if (!ScanStmtsForUnionMembers(b.Statements, noneLocals, optLocals, hits, members)) return false;
                    break;
                case MatchStmt m:
                {
                    var armNones = new List<HashSet<string>>();
                    var armOpts = new List<HashSet<string>>();
                    foreach (var br in m.Branches)
                    {
                        var bn = new HashSet<string>(noneLocals);
                        var bo = new HashSet<string>(optLocals);
                        if (br.Body != null && !ScanStmtsForUnionMembers(ArmStatements(br.Body), bn, bo, hits, members))
                            return false;
                        armNones.Add(bn); armOpts.Add(bo);
                    }
                    MergeScanSets(noneLocals, optLocals, armNones, armOpts);
                    break;
                }
            }
        }
        return true;
    }

    /// The `if` arm of <see cref="ScanStmtsForUnionMembers"/>: a condition that folds
    /// statically prunes its untaken side; otherwise all arms scan and merge.
    private bool ScanIfForUnionMembers(IfStmt ifs,
        HashSet<string> noneLocals, HashSet<string> optLocals,
        HashSet<int> hits, List<string> members)
    {
        var conds = new List<Expression?> { ifs.Condition };
        conds.AddRange(ifs.ElifBranches.Select(b => (Expression?)b.Condition));
        var arms = new List<Statement?> { ifs.ThenBranch };
        arms.AddRange(ifs.ElifBranches.Select(b => (Statement?)b.Body));
        arms.Add(ifs.ElseBranch);   // null = the no-else fall-through path

        var armNones = new List<HashSet<string>>();
        var armOpts = new List<HashSet<string>>();
        bool alive = true;   // still looking for the first reachable arm
        for (int i = 0; i < arms.Count; ++i)
        {
            var cond = i < conds.Count ? conds[i] : null;
            var arm = arms[i];
            if (!alive) break;
            if (cond != null && TryFoldStatic(cond, noneLocals, optLocals) is { } folded)
            {
                if (!folded) continue;          // arm provably dead: contributes nothing
                var tn = new HashSet<string>(noneLocals);
                var to = new HashSet<string>(optLocals);
                if (arm != null && !ScanStmtsForUnionMembers(ArmStatements(arm), tn, to, hits, members))
                    return false;
                armNones.Add(tn); armOpts.Add(to);
                alive = false;
                break;
            }
            var an = new HashSet<string>(noneLocals);
            var ao = new HashSet<string>(optLocals);
            if (arm != null && !ScanStmtsForUnionMembers(ArmStatements(arm), an, ao, hits, members))
                return false;
            armNones.Add(an); armOpts.Add(ao);
            if (cond == null) alive = false;    // else arm: no later path exists
        }
        if (alive && ifs.ElseBranch == null && armNones.Count > 0)
        {
            armNones.Add(new HashSet<string>(noneLocals));
            armOpts.Add(new HashSet<string>(optLocals));
        }
        if (alive || armNones.Count > 0)
            MergeScanSets(noneLocals, optLocals, armNones, armOpts);
        return true;
    }

    /// <summary>
    /// The member index a return expression carries, decided from the AST alone
    /// (the value is not lowered yet): literal kinds, tracked None/optional locals,
    /// declared local types, and callee member lists remapped by name. Null when
    /// the expression's member cannot be decided statically.
    /// </summary>
    private int? ReturnMemberIndexOf(Expression e,
        HashSet<string> noneLocals, HashSet<string> optLocals, List<string> members)
    {
        switch (e)
        {
            case IntegerLiteral il:
            {
                int idx = FirstIntMemberThatFits(members, il.Value);
                return idx >= 0 ? idx : null;
            }
            case BooleanLiteral b:
            {
                int bIdx = members.IndexOf("bool");
                if (bIdx >= 0) return bIdx;
                int idx = FirstIntMemberThatFits(members, b.Value ? 1 : 0);
                return idx >= 0 ? idx : null;
            }
            case FloatLiteral: return members.IndexOf("float") >= 0 ? members.IndexOf("float") : (int?)null;
            case StringLiteral or FStringExpr:
                return members.IndexOf("str") >= 0 ? members.IndexOf("str") : (int?)null;
            case VariableExpr v:
            {
                if (noneLocals.Contains(v.Name)) return NoneIndex(members);
                if (optLocals.Contains(v.Name)) return null;   // could be any of its members
                if (scanScope != null && scanScope.TryGetValue(v.Name, out var sct))
                    return MemberIndexFor(v, new Variable(v.Name,
                        DataTypeExtensions.StringToDataType(sct)), members);
                foreach (var k in new[] {
                             currentInlinePrefix + v.Name,
                             currentFunction + "." + v.Name,
                             currentModulePrefix + v.Name, v.Name })
                {
                    if (variableTypes.TryGetValue(k, out var dt))
                        return MemberIndexFor(v, new Variable(k, dt), members);
                }
                return null;
            }
            case UnaryExpr { Op: AstUnOp.Negate } un:
                return ExprCouldBeFloat(un)
                    ? (members.Contains("float") ? null : SignedIntOrNull(members))
                    : SignedIntOrNull(members);
            case UnaryExpr { Op: AstUnOp.Not or AstUnOp.BitNot } un2:
                return ReturnMemberIndexOf(un2.Operand, noneLocals, optLocals, members)
                       ?? SignedIntOrNull(members);
            case BinaryExpr b when b.Op is AstBinOp.Equal or AstBinOp.NotEqual
                or AstBinOp.Less or AstBinOp.LessEq or AstBinOp.Greater or AstBinOp.GreaterEq:
            {
                int bIdx = members.IndexOf("bool");
                if (bIdx >= 0) return bIdx;
                int idx = FirstIntMemberThatFits(members, 1);
                return idx >= 0 ? idx : null;
            }
            case BinaryExpr b when b.Op is AstBinOp.And or AstBinOp.Or:
            {
                // `a or b` yields an operand: the member set is the union of both arms'.
                var l = ReturnMemberIndexOf(b.Left, noneLocals, optLocals, members);
                var r = ReturnMemberIndexOf(b.Right, noneLocals, optLocals, members);
                if (l == null || r == null) return null;
                return l == r ? l : null;   // different members possible -> unpinnable
            }
            case BinaryExpr b2:
                // Any other binary expression is an arithmetic result: an int member
                // when no operand can be float (a float operand or a `/` division
                // makes it unpinnable between the int and float members).
                return ExprCouldBeFloat(b2) ? null : SignedIntOrNull(members);
            case TernaryExpr t:
            {
                var a = ReturnMemberIndexOf(t.TrueVal, noneLocals, optLocals, members);
                var c = ReturnMemberIndexOf(t.FalseVal, noneLocals, optLocals, members);
                if (a == null || c == null) return null;
                return a == c ? a : null;
            }
            case CallExpr c:
            {
                string? key = c.Callee switch
                {
                    VariableExpr cv => ResolveCallee(cv.Name),
                    MemberAccessExpr cm => cm.Member,
                    _ => null,
                };
                if (key == null) return null;
                // Width casts are exact: uint8(x) returns the uint8 member.
                if (MemberDataType(key) != DataType.UNKNOWN)
                    return MemberIndexFor(c, new Temporary("", MemberDataType(key)), members);
                if (key == "bool")
                    return members.IndexOf("bool") >= 0 ? members.IndexOf("bool") : (int?)null;
                if (key == "float")
                    return members.IndexOf("float") >= 0 ? members.IndexOf("float") : (int?)null;
                // A tagged callee contributes its own reachable members, remapped by
                // member name into this function's list.
                List<string>? calleeMembers = functionReturnMembers.TryGetValue(key, out var cm2)
                    ? cm2
                    : functionDeclaredMembers.TryGetValue(key, out var dm) ? dm : null;
                if (calleeMembers == null)
                {
                    var matchKey = functionReturnMembers.Keys
                        .Concat(functionDeclaredMembers.Keys)
                        .FirstOrDefault(k => k.EndsWith("_" + key));
                    if (matchKey != null)
                        calleeMembers = functionReturnMembers.TryGetValue(matchKey, out var mm)
                            ? mm : functionDeclaredMembers[matchKey];
                }
                if (calleeMembers != null)
                {
                    // Any member could arrive; the caller cannot pin one.
                    return null;
                }
                if (functionReturnTypes.TryGetValue(key, out var rt) && rt != null)
                    return MemberIndexFor(c, new Temporary("", DataTypeExtensions.StringToDataType(rt)), members);
                return null;
            }
            default:
                return null;
        }
    }

    /// The first signed int member index as a nullable (null = no int member).
    private static int? SignedIntOrNull(List<string> members)
    {
        for (int i = 0; i < members.Count; ++i)
            if (members[i] is "int" or "int8" or "int16" or "int32") return i;
        for (int i = 0; i < members.Count; ++i)
            if (IsIntMember(members[i])) return i;
        return null;
    }

    /// Whether the scan-time expression can produce a float: a float literal or
    /// float-typed name, a `/` division, or a call to `float(...)` -- recursively
    /// through unary/binary wrappers.
    private bool ExprCouldBeFloat(Expression e)
    {
        switch (e)
        {
            case FloatLiteral: return true;
            case VariableExpr v:
                if (scanScope != null && scanScope.TryGetValue(v.Name, out var sct)
                    && sct == "float") return true;
                foreach (var k in new[] {
                             currentInlinePrefix + v.Name,
                             currentFunction + "." + v.Name,
                             currentModulePrefix + v.Name, v.Name })
                    if (variableTypes.TryGetValue(k, out var dt) && dt == DataType.FLOAT)
                        return true;
                return false;
            case UnaryExpr u: return ExprCouldBeFloat(u.Operand);
            case BinaryExpr b:
                return b.Op == AstBinOp.Div
                    || ExprCouldBeFloat(b.Left) || ExprCouldBeFloat(b.Right);
            case TernaryExpr t: return ExprCouldBeFloat(t.TrueVal) || ExprCouldBeFloat(t.FalseVal);
            case CallExpr c:
                return c.Callee is VariableExpr cf && cf.Name == "float";
            default: return false;
        }
    }

    private static List<Statement> ArmStatements(Statement arm)
        => arm is Block b ? b.Statements : new List<Statement> { arm };

    /// Intersect the provably-None sets and union the maybe-optional sets across the
    /// surviving paths -- a name keeps "is None" only if every path proves it, and
    /// keeps "may be optional" if any path can leave it tagged.
    private static void MergeScanSets(HashSet<string> noneLocals, HashSet<string> optLocals,
        List<HashSet<string>> armNones, List<HashSet<string>> armOpts)
    {
        if (armNones.Count == 0) return;
        var newNone = new HashSet<string>(armNones[0]);
        foreach (var s in armNones.Skip(1)) newNone.IntersectWith(s);
        var newOpt = new HashSet<string>(optLocals);
        foreach (var s in armOpts) newOpt.UnionWith(s);
        noneLocals.Clear(); noneLocals.UnionWith(newNone);
        optLocals.Clear(); optLocals.UnionWith(newOpt);
    }

    /// Assignment bookkeeping for the scan: does this write leave the target provably
    /// None, possibly tagged, or neither?
    private void TrackOptionalAssign(string name, Expression value,
        HashSet<string> noneLocals, HashSet<string> optLocals)
    {
        if (value is NoneLiteral || IsNoneLiteralExpr(value))
        {
            noneLocals.Add(name); optLocals.Remove(name);
        }
        else if (ExprMaybeRuntimeNone(value, noneLocals, optLocals))
        {
            optLocals.Add(name); noneLocals.Remove(name);
        }
        else
        {
            noneLocals.Remove(name); optLocals.Remove(name);
        }
    }

    private static bool IsNoneLiteralExpr(Expression e) => e is NoneLiteral;

    /// Whether <paramref name="e"/> can produce a run-time None under the scan's
    /// current local tracking.
    private bool ExprMaybeRuntimeNone(Expression e,
        HashSet<string> noneLocals, HashSet<string> optLocals)
    {
        switch (e)
        {
            case NoneLiteral: return true;
            case VariableExpr v:
                return noneLocals.Contains(v.Name) || optLocals.Contains(v.Name);
            case CallExpr c:
            {
                string? key = c.Callee switch
                {
                    VariableExpr cv => ResolveCallee(cv.Name),
                    // A method's receiver type is a fact the scan cannot always resolve;
                    // any tagged function whose name ends in the member is enough to say
                    // "maybe" (conservative -- it over-tags, never miscompiles).
                    MemberAccessExpr cm => cm.Member,
                    _ => null,
                };
                if (key == null) return false;
                if (functionReturnMembers.ContainsKey(key)) return true;
                return functionReturnMembers.Keys.Any(k => k.EndsWith("_" + key));
            }
            case TernaryExpr t:
            {
                if (TryFoldStatic(t.Condition, noneLocals, optLocals) is { } ct)
                    return ExprMaybeRuntimeNone(ct ? t.TrueVal : t.FalseVal, noneLocals, optLocals);
                return ExprMaybeRuntimeNone(t.TrueVal, noneLocals, optLocals)
                    || ExprMaybeRuntimeNone(t.FalseVal, noneLocals, optLocals);
            }
            case BinaryExpr { Op: AstBinOp.Or or AstBinOp.And } bo:
                return ExprMaybeRuntimeNone(bo.Left, noneLocals, optLocals)
                    || ExprMaybeRuntimeNone(bo.Right, noneLocals, optLocals);
            case UnaryExpr u:
                return ExprMaybeRuntimeNone(u.Operand, noneLocals, optLocals);
            case MemberAccessExpr m:
                return ExprMaybeRuntimeNone(m.Object, noneLocals, optLocals);
            case IndexExpr ix:
                return ExprMaybeRuntimeNone(ix.Target, noneLocals, optLocals);
            default: return false;
        }
    }

    /// <summary>
    /// Statically evaluate a condition the way the IR generator folds it: literals,
    /// `and`/`or`/`not`, comparisons of folded operands, `is None` against the scan's
    /// provably-None set, and names bound to compile-time constants by the global scan.
    /// Null = cannot decide, which the caller treats as "both arms reachable".
    /// </summary>
    private bool? TryFoldStatic(Expression? e,
        HashSet<string> noneLocals, HashSet<string> optLocals)
    {
        switch (e)
        {
            case null: return null;
            case BooleanLiteral b: return b.Value;
            case NoneLiteral: return false;
            case IntegerLiteral i: return i.Value != 0;
            case FloatLiteral f: return f.Value != 0;
            case StringLiteral s: return s.Value.Length > 0;
            case UnaryExpr { Op: AstUnOp.Not } u:
            {
                var inner = TryFoldStatic(u.Operand, noneLocals, optLocals);
                return inner == null ? null : !inner;
            }
            case BinaryExpr { Op: AstBinOp.And } a:
            {
                var l = TryFoldStatic(a.Left, noneLocals, optLocals);
                if (l == false) return false;
                var r = TryFoldStatic(a.Right, noneLocals, optLocals);
                if (l == true) return r;
                return r == false ? false : null;
            }
            case BinaryExpr { Op: AstBinOp.Or } o:
            {
                var l = TryFoldStatic(o.Left, noneLocals, optLocals);
                if (l == true) return true;
                var r = TryFoldStatic(o.Right, noneLocals, optLocals);
                if (l == false) return r;
                return r == true ? true : null;
            }
            case BinaryExpr { Op: AstBinOp.Is or AstBinOp.IsNot or AstBinOp.Equal or AstBinOp.NotEqual } cmp:
            {
                var (noneSide, other) = cmp.Left is NoneLiteral ? (true, cmp.Right)
                    : cmp.Right is NoneLiteral ? (true, cmp.Left) : (false, (Expression?)null);
                bool negated = cmp.Op is AstBinOp.IsNot or AstBinOp.NotEqual;
                if (noneSide && other is VariableExpr ov)
                {
                    bool? r = noneLocals.Contains(ov.Name) ? true
                        : optLocals.Contains(ov.Name) ? null
                        : false;
                    return r == null ? null : negated ? !r : r;
                }
                if (noneSide && other != null) return negated;   // a non-optional is never None
                if (TryConstValue(cmp.Left, noneLocals, optLocals) is { } lv
                    && TryConstValue(cmp.Right, noneLocals, optLocals) is { } rv)
                {
                    bool eq = lv == rv;
                    return negated ? !eq : eq;
                }
                return null;
            }
            case BinaryExpr { Op: AstBinOp.Less or AstBinOp.LessEq or AstBinOp.Greater or AstBinOp.GreaterEq } rel:
            {
                if (TryConstValue(rel.Left, noneLocals, optLocals) is { } lv
                    && TryConstValue(rel.Right, noneLocals, optLocals) is { } rv)
                    return rel.Op switch
                    {
                        AstBinOp.Less => lv < rv,
                        AstBinOp.LessEq => lv <= rv,
                        AstBinOp.Greater => lv > rv,
                        _ => lv >= rv,
                    };
                return null;
            }
            case VariableExpr v:
            {
                if (noneLocals.Contains(v.Name)) return false;
                if (optLocals.Contains(v.Name)) return null;
                if (TryConstValue(v, noneLocals, optLocals) is { } cv) return cv != 0;
                if (strConstantVariables.TryGetValue(v.Name, out var sv)
                    || strConstantVariables.TryGetValue(currentModulePrefix + v.Name, out sv))
                    return sv.Length > 0;
                return null;
            }
            default: return null;
        }
    }

    /// A folded integer value for the scan evaluator: literals and names the module
    /// scan already proved constant.
    private long? TryConstValue(Expression e, HashSet<string> noneLocals, HashSet<string> optLocals)
    {
        switch (e)
        {
            case IntegerLiteral i: return i.Value;
            case BooleanLiteral bl: return bl.Value ? 1 : 0;
            case UnaryExpr { Op: AstUnOp.Negate } n:
                return TryConstValue(n.Operand, noneLocals, optLocals) is { } nv ? -nv : null;
            case UnaryExpr { Op: AstUnOp.Not } un:
                return TryFoldStatic(un.Operand, noneLocals, optLocals) is { } ub ? (ub ? 0 : 1) : null;
            case BinaryExpr bx:
            {
                var l = TryConstValue(bx.Left, noneLocals, optLocals);
                var r = TryConstValue(bx.Right, noneLocals, optLocals);
                if (l == null || r == null) return null;
                return bx.Op switch
                {
                    AstBinOp.Add => l + r, AstBinOp.Sub => l - r, AstBinOp.Mul => l * r,
                    AstBinOp.BitAnd => l & r, AstBinOp.BitOr => l | r, AstBinOp.BitXor => l ^ r,
                    AstBinOp.LShift => l << (int)r, AstBinOp.RShift => l >> (int)r,
                    AstBinOp.Mod => r == 0 ? null : l % r,
                    AstBinOp.Div or AstBinOp.FloorDiv => r == 0 ? null : l / r,
                    _ => null,
                };
            }
            case VariableExpr v:
            {
                foreach (var k in new[] { currentModulePrefix + v.Name, v.Name })
                {
                    if (constantVariables.TryGetValue(k, out int cv)) return cv;
                    if (globals.TryGetValue(k, out var sym) && !sym.IsMemoryAddress) return sym.Value;
                }
                return null;
            }
            default: return null;
        }
    }

    // ── narrowing state across branches ───────────────────────────────────
    //
    // optionalTagSlots and narrowedOptionals ride BranchState (BranchState.cs): the
    // tag storage joins by union -- whichever arm minted the byte, it exists past
    // the merge -- and the member narrowing joins all-agree like every other map
    // there. noneValuedNames is in the same snapshot, so optional names keep the
    // shared membership rule.

    /// The optional name a condition decides, and whether the condition being TRUE
    /// proves it None (`v is None`, `v == None`, `None is v`) or not-None
    /// (`v is not None`, `v != None`). Null when the condition is not a None test
    /// on a live optional.
    private (string key, bool trueMeansNone)? OptionalCondSubject(Expression? cond)
    {
        if (cond is not BinaryExpr { Op: AstBinOp.Is or AstBinOp.IsNot or AstBinOp.Equal or AstBinOp.NotEqual } cmp)
            return null;
        Expression? side = cmp.Left is NoneLiteral ? cmp.Right
            : cmp.Right is NoneLiteral ? cmp.Left : null;
        if (side == null) return null;
        if (OptionalKeyOfExpr(side) is not { } key) return null;
        bool trueMeansNone = cmp.Op is AstBinOp.Is or AstBinOp.Equal;
        return (key, trueMeansNone);
    }

    /// The name an `if v:`-style truth test decides: truthy means the tag says payload,
    /// so the taken arm narrows. Null when the operand is not a live optional.
    private string? OptionalTruthSubject(Expression? cond)
    {
        if (cond == null) return null;
        return OptionalKeyOfExpr(cond);
    }

    /// The tag Val, member list and None member-index of a live optional the
    /// expression reads, when the expression is a bare unnarrowed optional name.
    /// Narrowed and provable-None names answer through the ordinary
    /// truthiness/None folds instead. noneIdx is -1 for a union without None.
    private (Val tag, List<string> members, int noneIdx, string key)? LiveOptionalTag(Expression? e)
    {
        if (e == null) return null;
        if (OptionalKeyOfExpr(e) is not { } key) return null;
        if (narrowedOptionals.ContainsKey(key) || noneValuedNames.Contains(key)) return null;
        if (!optionalTagSlots.TryGetValue(key, out var tag)) return null;
        if (!optionalMembersByName.TryGetValue(key, out var members) || members.Count == 0)
            return null;
        return (tag, members, NoneIndex(members), key);
    }

    /// The union member list an arm's value contributes: `["None"]` for a provable
    /// None, the narrowed member alone for a name narrowed on that arm, the
    /// source's own member list for a live (unnarrowed-on-that-arm) optional, or
    /// the payload's type name alone for a definite value.
    private List<string> UnionMembersOf(Val v, Expression src, BranchState? arm)
    {
        if (src is NoneLiteral || v is NoneVal || IsNoneValued(src))
            return new List<string> { "None" };
        if (TagOfVal(v) != null && ValNameOf(v) is { } nm)
        {
            if (arm?.NarrowedOptionals.TryGetValue(nm, out var armIdx) ?? false)
            {
                // The arm proved one member: contribute THAT member's declared name,
                // not the val's storage type -- `int` and `int16` are the same width
                // and only the member spelling keeps the joined list deduplicated.
                if (optionalMembersByName.TryGetValue(nm, out var nm2)
                    && armIdx >= 0 && armIdx < nm2.Count)
                    return new List<string> { nm2[armIdx] };
                return new List<string> { TypeNameFor(v) };
            }
            if (!noneValuedNames.Contains(nm))
            {
                if (optionalMembersByName.TryGetValue(nm, out var m)) return new List<string>(m);
                return new List<string> { TypeNameFor(v), "None" };
            }
        }
        return new List<string> { TypeNameFor(v) };
    }

    /// <summary>
    /// The tag an arm's value writes into a joined result: the source's own tag
    /// byte when it is a live optional whose member list matches the result's,
    /// the result's None index for a provable None, the member index for a
    /// definite (or narrowed) value. When the source's member ORDER differs from
    /// the result's the tag is remapped member-by-member -- a copied tag byte
    /// would name the wrong member under the other order.
    /// </summary>
    private Val ArmTagFor(Val v, Expression src, BranchState? arm, List<string> dstMembers)
    {
        int noneIdx = NoneIndex(dstMembers);
        if (src is NoneLiteral || v is NoneVal || IsNoneValued(src))
            return new Constant(noneIdx);
        if (TagOfVal(v) is { } t && ValNameOf(v) is { } nm)
        {
            if (arm?.NarrowedOptionals.TryGetValue(nm, out var armIdx) ?? false)
            {
                if (optionalMembersByName.TryGetValue(nm, out var srcM2)
                    && armIdx >= 0 && armIdx < srcM2.Count)
                    return new Constant(dstMembers.IndexOf(srcM2[armIdx]));
                return new Constant(MemberIndexFor(src, v, dstMembers) ?? 0);
            }
            if (!noneValuedNames.Contains(nm))
            {
                var srcMembers = optionalMembersByName.TryGetValue(nm, out var sm)
                    ? sm : null;
                if (srcMembers != null && !srcMembers.SequenceEqual(dstMembers))
                    return EmitTagRemap(t, srcMembers, dstMembers, src);
                return t;
            }
        }
        return new Constant(MemberIndexFor(src, v, dstMembers) ?? 0);
    }

    /// <summary>
    /// A run-time tag remapped from <paramref name="srcMembers"/>' order to
    /// <paramref name="dstMembers"/>'s: a select chain over the source's members.
    /// A source member with no image in the destination is a compile-time lie the
    /// union could carry -- refuse the write rather than let a tag point nowhere.
    /// </summary>
    private Val EmitTagRemap(Val srcTag, List<string> srcMembers, List<string> dstMembers, Expression? src)
    {
        var remap = new List<(int srcIdx, int dstIdx)>();
        for (int i = 0; i < srcMembers.Count; ++i)
        {
            int dstIdx = dstMembers.IndexOf(srcMembers[i]);
            if (dstIdx < 0)
                throw UserError(
                    $"a member of the source union has no place here: '{srcMembers[i]}' is not " +
                    $"a member of {UnionDisplay(dstMembers)}. Widen the target's union to " +
                    "include it.", src);
            remap.Add((i, dstIdx));
        }
        Temporary remapped = MakeTemp(DataType.UINT8);
        foreach (var (si, di) in remap)
        {
            string skip = MakeLabel();
            Emit(new JumpIfNotEqual(srcTag, new Constant(si), skip));
            Emit(new Copy(new Constant(di), remapped));
            Emit(new Label(skip));
        }
        return remapped;
    }

    /// Union-merge two member lists preserving order, with None always last.
    private static List<string> UnionMerge(List<string> a, List<string> b)
    {
        var members = new List<string>();
        foreach (var m in a.Concat(b))
            if (m != "None" && !members.Contains(m)) members.Add(m);
        if (a.Contains("None") || b.Contains("None")) members.Add("None");
        return members;
    }

    private static string? ValNameOf(Val v) => v switch
    {
        Variable vv => vv.Name,
        Temporary tt => tt.Name,
        _ => null,
    };

    private string TypeNameFor(Val v) => DataTypeToSuffixStr(GetValType(v));

    /// Emit the conditional jump for an optional truth test: jump when the value is
    /// truthy (a payload tag AND nonzero payload) or falsy (None tag OR zero
    /// payload), per <paramref name="jumpOnTruthy"/>. A union with several real
    /// members dispatches per member -- each reads the payload at ITS width.
    private void EmitOptionalTruthJump(Val tag, Val payload, List<string> members,
        string target, bool jumpOnTruthy)
    {
        int noneIdx = NoneIndex(members);
        var real = Enumerable.Range(0, members.Count).Where(i => i != noneIdx).ToList();
        if (real.Count == 1 && noneIdx >= 0)
        {
            // The phase-1 [X, None] shape: the two-jump form, byte-identical.
            if (jumpOnTruthy)
            {
                string skip = MakeLabel();
                Emit(new JumpIfEqual(tag, new Constant(noneIdx), skip));
                Emit(new JumpIfNotZero(payload, target));
                Emit(new Label(skip));
            }
            else
            {
                Emit(new JumpIfEqual(tag, new Constant(noneIdx), target));
                Emit(new JumpIfZero(payload, target));
            }
            return;
        }
        if (jumpOnTruthy)
        {
            // truthy = (tag == i AND payload-as-member-i != 0) for some real member i.
            foreach (var i in real)
            {
                string skip = MakeLabel();
                Emit(new JumpIfNotEqual(tag, new Constant(i), skip));
                Emit(new JumpIfNotZero(MemberRead(payload, i, members), target));
                Emit(new Label(skip));
            }
            return;
        }
        // falsy = (tag == None) OR (tag == i AND payload-as-member-i == 0).
        if (noneIdx >= 0)
            Emit(new JumpIfEqual(tag, new Constant(noneIdx), target));
        string done = MakeLabel();
        foreach (var i in real)
        {
            string skip = MakeLabel();
            Emit(new JumpIfNotEqual(tag, new Constant(i), skip));
            Emit(new JumpIfZero(MemberRead(payload, i, members), target));
            Emit(new Jump(done));
            Emit(new Label(skip));
        }
        Emit(new Label(done));
    }

    /// The payload Val retyped to member <paramref name="idx"/>'s width: narrowed
    /// reads take the member's size from the low payload bytes (RFC 0009 section 6).
    private static Val MemberRead(Val payload, int idx, List<string> members)
    {
        var dt = MemberDataType(members[idx]);
        return payload switch
        {
            Variable v => new Variable(v.Name, dt),
            Temporary t => new Temporary(t.Name, dt),
            _ => payload,
        };
    }

    /// <summary>
    /// The destination a payload write into a union slot must use: a definite
    /// member writes its OWN width into the slot's low bytes -- a widest-typed
    /// `Copy` would convert an int member into the float member's representation
    /// under the int tag (Copy on a FLOAT destination calls __floatsisf). A live
    /// union source moves its whole payload raw at the widest width; a None write
    /// emits no payload copy at all.
    /// </summary>
    private Val UnionPayloadStoreTarget(Variable target, Val src, Expression? srcExpr)
    {
        if (!optionalMembersByName.TryGetValue(target.Name, out var m)) return target;
        if (ValNameOf(src) is { } sn && narrowedOptionals.TryGetValue(sn, out var ni)
            && optionalMembersByName.TryGetValue(sn, out var nm2) && ni >= 0 && ni < nm2.Count)
        {
            int tIdx = m.IndexOf(nm2[ni]);
            if (tIdx >= 0) return MemberRead(target, tIdx, m);
        }
        if (TagOfVal(src) != null) return target;
        if (srcExpr is NoneLiteral || src is NoneVal
            || (srcExpr != null && IsNoneValued(srcExpr))) return target;
        int? idx = MemberIndexFor(srcExpr, src, m);
        return idx is { } i && i >= 0 && i < m.Count && m[i] != "None"
            ? MemberRead(target, i, m) : target;
    }

    /// Apply the narrowing a condition implies to the current path's state.
    /// `whenTrue` = the arm where the condition held; `whenFalse` = the path where
    /// it did not (the else arm, and the state every later arm inherits).
    private void ApplyOptionalCondEffect(Expression? cond, bool whenTrue)
    {
        switch (cond)
        {
            case null: return;
            case BinaryExpr { Op: AstBinOp.And } a when whenTrue:
                // Both operands held: both of their true-effects apply.
                ApplyOptionalCondEffect(a.Left, true);
                ApplyOptionalCondEffect(a.Right, true);
                return;
            case BinaryExpr { Op: AstBinOp.Or } o when !whenTrue:
                // Neither operand held: both of their false-effects apply.
                ApplyOptionalCondEffect(o.Left, false);
                ApplyOptionalCondEffect(o.Right, false);
                return;
            case UnaryExpr { Op: AstUnOp.Not } n:
                ApplyOptionalCondEffect(n.Operand, !whenTrue);
                return;
            default:
                break;
        }
        if (IsinstanceCondSubject(cond) is { } isa)
        {
            // `isinstance(v, T)` narrows the member set to the candidates T names
            // (true arm) or to everything else (false arm). Only a single-member
            // result narrows the payload read: a two-member remainder stays
            // ambiguous and keeps the refusal.
            var armSet = whenTrue
                ? isa.match
                : Enumerable.Range(0, isa.members.Count).Where(i => !isa.match.Contains(i)).ToList();
            if (armSet.Count == 1)
            {
                int idx = armSet[0];
                if (idx == NoneIndex(isa.members))
                {
                    narrowedOptionals.Remove(isa.key);
                    noneValuedNames.Add(isa.key);
                }
                else
                {
                    narrowedOptionals[isa.key] = idx;
                    noneValuedNames.Remove(isa.key);
                }
            }
            return;
        }
        if (OptionalCondSubject(cond) is { } subj)
        {
            bool proveNone = subj.trueMeansNone == whenTrue;
            if (proveNone)
            {
                narrowedOptionals.Remove(subj.key);
                noneValuedNames.Add(subj.key);
            }
            else if (optionalMembersByName.TryGetValue(subj.key, out var sm))
            {
                // `v is not None` on an [X, None] narrows to X; on a union with two
                // or more real members the remainder is still ambiguous and the
                // payload read keeps refusing -- the name is only known not-None.
                var nonNone = Enumerable.Range(0, sm.Count)
                    .Where(i => i != NoneIndex(sm)).ToList();
                if (nonNone.Count == 1)
                {
                    narrowedOptionals[subj.key] = nonNone[0];
                    noneValuedNames.Remove(subj.key);
                }
            }
            return;
        }
        // `if v:` narrows on the taken arm when the union has exactly one real
        // member: truthy means the tag said payload. With two or more real members
        // the tag is still ambiguous, same as the fall-through on every arm.
        if (whenTrue && OptionalTruthSubject(cond) is { } truthKey
            && optionalMembersByName.TryGetValue(truthKey, out var tm))
        {
            var nonNone = Enumerable.Range(0, tm.Count)
                .Where(i => i != NoneIndex(tm)).ToList();
            if (nonNone.Count == 1)
                narrowedOptionals[truthKey] = nonNone[0];
        }
    }

    /// The live optional name and member list an `isinstance(v, T)` test decides,
    /// with the member indices T names. Null when the call is not an isinstance on
    /// a live optional, or T names no member (the comparison folds to Constant
    /// instead and this path never runs).
    private (string key, List<string> members, List<int> match)? IsinstanceCondSubject(Expression? cond)
    {
        if (cond is not CallExpr { Args.Count: 2 } ic) return null;
        if (ic.Callee is not VariableExpr { Name: "isinstance" }) return null;
        if (OptionalKeyOfExpr(ic.Args[0]) is not { } key) return null;
        if (!optionalMembersByName.TryGetValue(key, out var members)) return null;
        var match = IsinstanceMemberIndices(ic.Args[1], members);
        if (match == null || match.Count == 0) return null;
        return (key, members, match);
    }

    // ── capable-name precompute ─────────────────────────────────────────────

    /// <summary>
    /// The names in <paramref name="stmts"/> that can ever hold a runtime optional:
    /// declared `Optional[T]`, assigned a tagged call result, or copied from another
    /// capable name (closed to a fixpoint). Only capable names get the tag write on a
    /// non-optional assignment -- without the precompute, `v = 5` on the arm that runs
    /// before `v = read()` was ever lowered would leave the tag byte from the other
    /// path stale at the join.
    /// </summary>
    private void CollectOptionalCapable(List<Statement> stmts, HashSet<string> capable)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in TypeInference.WalkStatements(stmts))
            {
                string? name = s switch
                {
                    AssignStmt a when a.Target is VariableExpr av => av.Name,
                    VarDecl vd => vd.Name,
                    AnnAssign aa => aa.Target,
                    _ => null,
                };
                if (name == null || capable.Contains(name)) continue;

                bool isCapable = s switch
                {
                    VarDecl vd => vd.UnionMembers != null
                        || (vd.Init != null && ExprPossiblyOptional(vd.Init, capable)),
                    AnnAssign aa => aa.UnionMembers != null
                        || (aa.Value != null && ExprPossiblyOptional(aa.Value, capable)),
                    AssignStmt a => ExprPossiblyOptional(a.Value, capable),
                    _ => false,
                };
                if (isCapable) { capable.Add(name); changed = true; }
            }
        }
    }

    /// Whether <paramref name="e"/> can evaluate to a runtime-optional value, for the
    /// capable-name precompute.
    private bool ExprPossiblyOptional(Expression e, HashSet<string> capable)
    {
        switch (e)
        {
            case CallExpr c:
            {
                string? key = c.Callee switch
                {
                    VariableExpr cv => ResolveCallee(cv.Name),
                    MemberAccessExpr cm => cm.Member,
                    _ => null,
                };
                if (key == null) return false;
                if (CalleeIsOptional(key)) return true;
                if (functionReturnMembers.Keys.Any(k => k.EndsWith("_" + key))) return true;
                // An @inline callee with declared Optional members also yields a tagged
                // result (the expansion carries a tag temp, not a wire byte).
                if (c.Callee is VariableExpr iv && inlineFunctions.TryGetValue(
                        ResolveCallee(iv.Name), out var ifn) && ifn?.ReturnMembers != null)
                    return true;
                if (c.Callee is MemberAccessExpr im)
                    foreach (var kv in inlineFunctions)
                        if (kv.Key.EndsWith("_" + im.Member) && kv.Value?.ReturnMembers != null)
                            return true;
                return false;
            }
            case VariableExpr v: return capable.Contains(v.Name);
            case TernaryExpr t:
                return ExprPossiblyOptional(t.TrueVal, capable)
                    || ExprPossiblyOptional(t.FalseVal, capable);
            case BinaryExpr { Op: AstBinOp.Or or AstBinOp.And } bo:
                return ExprPossiblyOptional(bo.Left, capable)
                    || ExprPossiblyOptional(bo.Right, capable);
            case UnaryExpr u: return ExprPossiblyOptional(u.Operand, capable);
            default: return false;
        }
    }

    // ── emission helpers ────────────────────────────────────────────────────

    /// Whether the storage name is allowed to carry a tag at all in this function.
    private bool IsOptionalCapableName(string storageName, string sourceName)
        => optionalCapable.Contains(storageName)
           || optionalCapable.Contains(sourceName)
           || optionalTagSlots.ContainsKey(storageName);

    // ── union fields ────────────────────────────────────────────────────────

    /// Whether <paramref name="cls"/> has <paramref name="field"/> as a union
    /// field: some write stores None and some write stores a scalar, anywhere up
    /// the class's MRO (each link's own writes count -- a subclass method's write
    /// and the base ctor's write land on the same flattened name). A declared
    /// `Union[...]`/`Optional[...]` annotation on the field answers the member
    /// list outright.
    private bool IsUnionField(string cls, string field, out List<string>? declared)
    {
        declared = null;
        bool none = false, scalar = false;
        for (string? c = cls; c != null;)
        {
            if (fieldDeclaredUnionMembers.TryGetValue(c + "|" + field, out declared))
                return true;
            if (fieldNoneWrites.TryGetValue(c, out var ns) && ns.Contains(field)) none = true;
            if (fieldScalarWrites.TryGetValue(c, out var ss) && ss.Contains(field)) scalar = true;
            if (!classBasePrefixes.TryGetValue(c, out var parent) || string.IsNullOrEmpty(parent))
                break;
            c = parent.EndsWith("_") ? parent[..^1] : parent;
        }
        return none && scalar;
    }

    /// The flattened-name tag bookkeeping for a union field, created on first touch:
    /// the payload Variable's widest-member type, the tag byte sibling and the
    /// member list (declared, or seeded [None] and grown by each write's member).
    private bool EnsureUnionField(string flat, string cls, string field, out List<string> members)
    {
        members = null!;
        if (!IsUnionField(cls, field, out var declared)) return false;
        if (!optionalMembersByName.TryGetValue(flat, out members))
        {
            members = declared != null ? new List<string>(declared) : new List<string> { "None" };
            optionalMembersByName[flat] = members;
            variableTypes[flat] = UnionPayloadType(members);
            // A union field is always runtime storage: never a compile-time fold.
            constantVariables.Remove(flat);
            killedConstants.Add(flat);
            // The payload global must be registered BEFORE TagStorageFor runs -- the
            // tag is itself a global byte only when the payload's name is one.
            if (moduleInstanceMutableFields.Contains(flat))
                mutableGlobals[flat] = variableTypes[flat];
            optionalTagSlots[flat] = TagStorageFor(flat);
            if (declared != null) unionFieldDeclared.Add(flat);
        }
        return true;
    }

    /// Whether this member-assign target resolves to a union field -- the
    /// RefuseOptionalPayloadStore exemption asks before the flattened path runs.
    private bool IsUnionFieldTarget(MemberAccessExpr m)
    {
        if (m.Object is not VariableExpr ov) return false;
        // A module-level instance keys `instanceClasses` under its BARE name, so
        // both the function-scoped and bare spellings have to answer here.
        string pfx = !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." : "");
        foreach (var cand in new[] { pfx + ov.Name, ov.Name })
        {
            string b = cand;
            for (int d = 0; d < 20 && variableAliases.TryGetValue(b, out var a); d++) b = a;
            if (instanceClasses.TryGetValue(b, out var cls) && cls != null
                && IsUnionField(cls, m.Member, out _))
                return true;
        }
        return false;
    }

    /// The member list key a tagged field carries, resolved without emitting -- the
    /// same flattened name the read path hands back. A cond test can be the field's
    /// first touch, so the bookkeeping is minted here when the class resolves.
    private string? FlatOptionalKeyOf(MemberAccessExpr ma)
    {
        if (ma.Object is not VariableExpr ov) return null;
        string pfx = !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." : "");
        foreach (var cand in new[] { pfx + ov.Name, ov.Name })
        {
            string b = cand;
            for (int d = 0; d < 20 && variableAliases.TryGetValue(b, out var a); d++) b = a;
            string flat = b + "_" + ma.Member;
            if (optionalMembersByName.ContainsKey(flat)) return flat;
            if (instanceClasses.TryGetValue(b, out var cls) && cls != null
                && EnsureUnionField(flat, cls, ma.Member, out _))
                return flat;
        }
        return null;
    }

    /// The member-name a plain stored Val joins the field's list as.
    private static string? UnionMemberNameFor(Val v, Expression? expr)
    {
        if (expr is FloatLiteral || v is FloatConstant) return "float";
        if (expr is BooleanLiteral) return "bool";
        // A folded scalar (a setter param bound to `Pull.UP`, say) still names an
        // int member -- the same smallest-fit convention TypeOfIntValue uses.
        if (v is Constant cv) return cv.Value switch
        {
            < short.MinValue => "int32", < sbyte.MinValue => "int16", < 0 => "int8",
            <= byte.MaxValue => "uint8", <= ushort.MaxValue => "uint16",
            _ => "uint32",
        };
        return (v != null ? v switch
        {
            Variable vv => vv.Type, Temporary tv => tv.Type, _ => DataType.UNKNOWN,
        } : DataType.UNKNOWN) switch
        {
            DataType.FLOAT => "float",
            DataType.INT8 => "int8", DataType.INT16 => "int16", DataType.INT32 => "int32",
            DataType.UINT8 => "uint8", DataType.UINT16 => "uint16",
            DataType.UINT32 => "uint32",
            _ => null,
        };
    }

    /// `self.f = v` where f is a union field: payload at the member's own width plus
    /// the tag write. A None write writes only the tag. A live-union source forwards
    /// (remapping) its tag and stores its payload bytes at its own width.
    private void EmitUnionFieldStore(string flat, string field, Expression? srcExpr,
        Val value, ASTNode loc)
    {
        var members = optionalMembersByName[flat];
        var tagVar = (Variable)optionalTagSlots[flat];

        bool noneWrite = srcExpr is NoneLiteral || (srcExpr != null && IsNoneValued(srcExpr))
            || (value is NoneVal && srcExpr is not CallExpr);
        if (!noneWrite)
        {
            // The value's kind must be a member: a live union contributes its whole
            // list, a plain value its own type name, and anything else (an instance,
            // a buffer) is a member a scalar payload cannot hold -- decision 4.
            if (TagOfVal(value) is { }
                && ValNameOf(value) is { } srcNm
                && optionalMembersByName.TryGetValue(srcNm, out var srcMembers))
            {
                foreach (var m in srcMembers)
                    if (!members.Contains(m))
                    {
                        if (unionFieldDeclared.Contains(flat))
                            throw UserError(
                                $"a member of the source union has no place here: '{m}' is " +
                                $"not a member of field '{field}'s {UnionDisplay(members)}. " +
                                "Widen the annotation to include it.", loc);
                        members.Add(m);
                    }
            }
            else if (MemberIndexFor(srcExpr, value, members) == null)
            {
                string? mn = UnionMemberNameFor(value, srcExpr);
                if (mn == null || value is ArrayBase or MemoryAddress
                    || !string.IsNullOrEmpty(GetValClass(value)))
                    throw UserError(
                        $"field '{field}' already holds None -- a tagged union member -- and " +
                        "this value is not a scalar the payload can hold (an instance or a "
                        + "buffer has no member slot; RFC 0009 decision 4)", loc);
                if (unionFieldDeclared.Contains(flat))
                    throw UserError(
                        $"field '{field}' is declared {UnionDisplay(members)} -- this write's " +
                        $"'{mn}' is not a member of it. Widen the annotation or keep the "
                        + "stored types within it.", loc);
                members.Add(mn);
            }
            if (members.Count > 4)
                throw UserError(
                    $"field '{field}' would need {members.Count} members -- a tagged union "
                    + "carries at most 4 (RFC 0009 section 6.1)", loc);
            variableTypes[flat] = UnionPayloadType(members);
            if (moduleInstanceMutableFields.Contains(flat))
                mutableGlobals[flat] = UnionPayloadType(members);
        }

        if (noneWrite)
        {
            Emit(new Copy(new Constant(NoneIndex(members) >= 0 ? NoneIndex(members) : 0), tagVar));
            MarkOptionalNone(flat);
            strConstantVariables.Remove(flat);
            return;
        }

        // Payload at the member's own width; a still-tagged source stores the bytes
        // it already carries (the runtime member is its tag's, not this write's).
        DataType payTy;
        if (TagOfVal(value) != null && ValNameOf(value) is { } pv
            && !narrowedOptionals.ContainsKey(pv) && !noneValuedNames.Contains(pv))
            payTy = GetValType(value);
        else
        {
            int? idx = MemberIndexFor(srcExpr, value, members);
            payTy = idx is { } i2 ? MemberDataType(members[i2]) : GetValType(value);
        }
        Emit(new Copy(value, new Variable(flat, payTy)));
        EmitOptionalTagWrite(new Variable(flat, payTy), srcExpr, value);
    }

    /// <summary>
    /// Emit the tag write that accompanies a payload write to a capable name, and
    /// update the read-side bookkeeping. The payload copy is emitted by the caller;
    /// this emits ONLY the tag half. <paramref name="valueExpr"/> is the source
    /// expression when there is one (so `x = None` is recognised from its literal,
    /// the same rule noneValuedNames follows), <paramref name="value"/> the Val it
    /// lowered to.
    /// </summary>
    private void EmitOptionalTagWrite(Variable target, Expression? valueExpr, Val value)
    {
        Variable tagVar = TagStorageFor(target.Name);

        // The member list the target carries: its own declared union when it has
        // one, else the source's list (so `x = tagged_call()` inherits the callee's
        // member order), else the phase-1 default.
        List<string> members;
        if (optionalMembersByName.TryGetValue(target.Name, out var declared))
            members = declared;
        else if (ValNameOf(value) is { } srcNm
                 && optionalMembersByName.TryGetValue(srcNm, out var srcM))
            members = srcM;
        else
            members = new List<string> { "uint8", "None" };
        int noneIdx = NoneIndex(members);

        int? srcNarrowedIdx = value is Variable sv2 && narrowedOptionals.TryGetValue(sv2.Name, out var si)
                              ? si
                              : value is Temporary st2 && narrowedOptionals.TryGetValue(st2.Name, out var si2)
                                  ? si2 : null;
        // A NoneVal value alone does NOT mean None -- a constructor call yields one too.
        // The SOURCE expression has to say None, the same rule noneValuedNames follows.
        if (valueExpr is NoneLiteral
            || (valueExpr != null && IsNoneValued(valueExpr)))
        {
            if (noneIdx < 0)
                throw UserError(
                    $"None is not a member of {UnionDisplay(members)} -- assigning it to " +
                    $"'{target.Name.Split('.').Last()}' would write a tag with no meaning. " +
                    "Add None to the union.", valueExpr);
            Emit(new Copy(new Constant(noneIdx), tagVar));
            optionalTagSlots[target.Name] = tagVar;
            MarkOptionalNone(target.Name);
        }
        else if (TagOfVal(value) is { } srcTag)
        {
            if (srcNarrowedIdx is { } sIdx
                && optionalMembersByName.TryGetValue(ValNameOf(value)!, out var narrowM)
                && sIdx >= 0 && sIdx < narrowM.Count)
            {
                // A narrowed source has a definite member: the tag is that member's
                // index in the TARGET's list, not the runtime byte.
                Emit(new Copy(new Constant(members.IndexOf(narrowM[sIdx])), tagVar));
            }
            else if (ValNameOf(value) is { } rNm
                     && optionalMembersByName.TryGetValue(rNm, out var srcList)
                     && !srcList.SequenceEqual(members))
            {
                Emit(new Copy(EmitTagRemap(srcTag, srcList, members, valueExpr), tagVar));
            }
            else
            {
                Emit(new Copy(srcTag, tagVar));
            }
            optionalTagSlots[target.Name] = tagVar;
            narrowedOptionals.Remove(target.Name);
            noneValuedNames.Remove(target.Name);
            if (srcNarrowedIdx is { } nIdx
                && optionalMembersByName.TryGetValue(ValNameOf(value)!, out var nm2)
                && nIdx >= 0 && nIdx < nm2.Count)
            {
                int tIdx = members.IndexOf(nm2[nIdx]);
                if (tIdx >= 0) narrowedOptionals[target.Name] = tIdx;
            }
        }
        else
        {
            Emit(new Copy(new Constant(MemberIndexFor(valueExpr, value, members) ?? 0), tagVar));
            optionalTagSlots[target.Name] = tagVar;
            MarkOptionalDefinite(target.Name, MemberIndexFor(valueExpr, value, members) ?? 0);
        }
        optionalMembersByName.TryAdd(target.Name, members);
    }

    /// RFC 0009: when an @inline argument is a live optional, the parameter it binds to
    /// carries the same tag byte, so `param is None` inside the expansion reads it --
    /// the inline version of the wire tag a real subroutine's return carries.
    private void CarryOptionalTagToParam(string paramName, Val argVal)
    {
        string argName = argVal is Variable av ? av.Name
            : argVal is Temporary at ? at.Name : "";
        optionalTagSlots.Remove(paramName);
        optionalMembersByName.Remove(paramName);
        narrowedOptionals.Remove(paramName);
        if (argName.Length > 0 && TagOfVal(argVal) is { } srcTag)
        {
            optionalTagSlots[paramName] = srcTag;
            if (optionalMembersByName.TryGetValue(argName, out var am))
                optionalMembersByName[paramName] = am;
            if (narrowedOptionals.TryGetValue(argName, out var nIdx)) narrowedOptionals[paramName] = nIdx;
        }
    }

    /// The None member index of the union <paramref name="v"/> is a value of -- 1 for
    /// the phase-1 two-member shape when nothing more precise is recorded.
    private int NoneIndexOfVal(Val v)
    {
        string? n = v is Variable vv ? vv.Name : v is Temporary tv ? tv.Name : null;
        return n != null && optionalMembersByName.TryGetValue(n, out var m) ? NoneIndex(m) : 1;
    }

    /// Whether the callee of <paramref name="c"/> yields a live optional -- a tagged
    /// real subroutine, or an @inline function declared `-> Optional[X]`.
    private bool CalleeMayReturnOptional(CallExpr c)
    {
        if (c.Callee is VariableExpr cv)
        {
            string resolved = ResolveCallee(cv.Name);
            if (functionReturnMembers.ContainsKey(resolved)) return true;
            if (inlineFunctions.TryGetValue(resolved, out var fn) && fn?.ReturnMembers != null)
                return true;
            return functionReturnMembers.Keys.Any(k => k.EndsWith("_" + cv.Name));
        }
        if (c.Callee is MemberAccessExpr cm)
        {
            foreach (var kv in functionReturnMembers)
                if (kv.Key.EndsWith("_" + cm.Member)) return true;
            foreach (var kv in inlineFunctions)
                if (kv.Key.EndsWith("_" + cm.Member) && kv.Value?.ReturnMembers != null) return true;
        }
        return false;
    }

    /// Whether the expression can hand back a runtime-optional value: a provable None,
    /// a live optional name, or a call whose callee is tagged. Used before the operand
    /// is evaluated, so it answers from the AST alone.
    private bool ExprMayBeOptional(Expression? e) => e switch
    {
        null => false,
        NoneLiteral => true,
        VariableExpr ve => OptionalKeyOf(ve.Name) != null || IsNoneValued(ve),
        CallExpr c => CalleeMayReturnOptional(c),
        TernaryExpr t => ExprMayBeOptional(t.TrueVal) || ExprMayBeOptional(t.FalseVal),
        BinaryExpr { Op: AstBinOp.Or or AstBinOp.And } b
            => ExprMayBeOptional(b.Left) || ExprMayBeOptional(b.Right),
        _ => false,
    };

    /// RFC 0009 section 5: `v is None` on a live optional is a tag compare -- the tag
    /// byte against the None member index (always the last). A name provably None or
    /// provably narrowed keeps the compile-time answer the caller's fold gives.
    /// Returns null when neither side is a runtime-tagged value.
    private Val? TryEmitOptionalNoneTest(BinaryExpr expr)
    {
        if (expr.Op is not (AstBinOp.Equal or AstBinOp.NotEqual or AstBinOp.Is or AstBinOp.IsNot))
            return null;
        Expression? optSide = expr.Left is NoneLiteral ? expr.Right
            : expr.Right is NoneLiteral ? expr.Left : null;
        if (optSide == null || IsNoneValued(optSide)) return null;
        bool mightBeTagged = optSide switch
        {
            VariableExpr v => OptionalKeyOf(v.Name) != null,
            MemberAccessExpr => true,   // the flattened field name needs the eval
            CallExpr c => CalleeMayReturnOptional(c),
            _ => false,
        };
        if (!mightBeTagged) return null;

        // The operand's payload is not read here -- only its tag -- so the unsafe-read
        // refusal does not apply to the bare-name form (the narrowing operation itself).
        Val optV;
        if (optSide is VariableExpr)
        {
            optionalReadAllowed++;
            try { optV = VisitExpression(optSide); } finally { optionalReadAllowed--; }
        }
        else
            optV = VisitExpression(optSide);
        if (TagOfVal(optV) is not { } liveTag) return null;

        string? vName = optV is Variable vv ? vv.Name
            : optV is Temporary tv ? tv.Name : null;
        bool isIs = expr.Op is AstBinOp.Is or AstBinOp.Equal;
        if (vName != null && narrowedOptionals.ContainsKey(vName))
            return new Constant(isIs ? 0 : 1);
        int noneIdx = NoneIndexOfVal(optV);
        if (noneIdx < 0)
            // The union has no None member: the tag can never say None.
            return new Constant(isIs ? 0 : 1);
        var t = MakeTemp(DataType.UINT8);
        Emit(new Binary(isIs ? BinaryOp.Equal : BinaryOp.NotEqual,
            liveTag, new Constant(noneIdx), t));
        return t;
    }

    /// Emit a Call that carries the callee's tag byte when it has one (RFC 0009). The
    /// tag lands in a fresh temp sibling to <paramref name="dst"/> and the dst is
    /// recorded as a live optional so the copy/assign paths propagate it.
    private void EmitMaybeTaggedCall(string callee, List<Val> args, Val dst)
    {
        if (dst is not NoneVal && functionReturnMembers.TryGetValue(callee, out var members))
        {
            Temporary tagDst = MakeTemp(DataType.UINT8);
            Emit(new Call(callee, args, dst, tagDst));
            MarkOptional(dst is Variable dv ? dv.Name : ((Temporary)dst).Name, tagDst, members);
            return;
        }
        Emit(new Call(callee, args, dst));
    }

    /// The member index an `-> Optional[X]`/union @inline callee's `return` writes
    /// into the expansion's tag temp.
    private Val InlineReturnTagVal(InlineContext ctx, Expression? expr, Val val)
    {
        var members = (inlineFunctions.TryGetValue(ctx.CalleeName, out var fn)
            && fn?.ReturnMembers is { } m) ? m : new List<string> { "uint8", "None" };
        return ReturnTagVal(expr, val, members);
    }

    /// <summary>
    /// Shared tag arithmetic for the two return paths: None reports the None index
    /// (a union without None has no legal `return None` -- the resolve pass already
    /// refused it), a live optional forwards or remaps its runtime tag, a narrowed
    /// or plain value reports its member index.
    /// </summary>
    private Val ReturnTagVal(Expression? expr, Val val, List<string> members)
    {
        int noneIdx = NoneIndex(members);
        if (expr == null || expr is NoneLiteral || val is NoneVal)
            return new Constant(noneIdx >= 0 ? noneIdx : 0);
        if (expr is VariableExpr ve)
        {
            foreach (var k in OptionalNameKeys(ve.Name))
                if (narrowedOptionals.TryGetValue(k, out var nIdx)
                    && optionalMembersByName.TryGetValue(k, out var nm)
                    && nIdx >= 0 && nIdx < nm.Count)
                    return new Constant(members.IndexOf(nm[nIdx]));
            if (TagOfVal(val) is { } srcTag)
            {
                if (ValNameOf(val) is { } nm2
                    && optionalMembersByName.TryGetValue(nm2, out var srcM)
                    && !srcM.SequenceEqual(members))
                    return EmitTagRemap(srcTag, srcM, members, expr);
                return srcTag;
            }
            if (IsNoneValued(ve)) return new Constant(noneIdx >= 0 ? noneIdx : 0);
            return new Constant(MemberIndexFor(expr, val, members) ?? 0);
        }
        if (TagOfVal(val) is { } t)
        {
            if (ValNameOf(val) is { } nm3
                && optionalMembersByName.TryGetValue(nm3, out var srcM2)
                && !srcM2.SequenceEqual(members))
                return EmitTagRemap(t, srcM2, members, expr);
            return t;
        }
        return new Constant(MemberIndexFor(expr, val, members) ?? 0);
    }

    /// The Tag operand a `Return` instruction needs for this return's value.
    private Val? TagForReturn(Expression? expr, Val val)
    {
        if (CurrentReturnMembers is not { } members) return null;
        return ReturnTagVal(expr, val, members);
    }
}
