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

    // Live-optional names proven non-None on the path being lowered (inside
    // `if v is not None:` and past `if v is None: return`). Payload reads are safe.
    private readonly HashSet<string> narrowedOptionals = new();

    // Names the precompute proved CAN hold a run-time optional somewhere in this
    // function (or module top level). Only such names get the tag write on a
    // non-optional assignment, which is what keeps the tag honest across arms.
    private readonly HashSet<string> optionalCapable = new();

    // >0 while lowering a reader that knows about the tag (is-None operands, `or`
    // left operand, truth tests, return values, the payload copy of `x = v`, the
    // print dispatch). Every other read of an unnarrowed optional is a CompileError.
    private int optionalReadAllowed;

    /// The union-member index of None -- always the last member (RFC 0009 section 4).
    private static int NoneIndex(List<string> members) => members.Count - 1;

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

    /// Whether the source name is a live optional whose payload is unsafe to read
    /// here (not narrowed on this path).
    private bool IsUnnarrowedOptional(string name)
    {
        string? key = OptionalKeyOf(name);
        return key != null && !narrowedOptionals.Contains(key);
    }

    /// The RFC 0009 section-8 refusal, from the point a payload read would use it.
    private void RefuseIfUnnarrowedOptional(VariableExpr expr)
    {
        if (optionalReadAllowed > 0) return;
        if (OptionalKeyOf(expr.Name) is { } key && !narrowedOptionals.Contains(key))
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
            && (narrowedOptionals.Contains(nm) || noneValuedNames.Contains(nm))) return;
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

    /// Record that the storage name holds a definite payload (tag == 0 at run time).
    /// The tag slot stays -- a name that is optional on ANY path keeps its byte on
    /// every path -- but the read side is safe on this one.
    private void MarkOptionalDefinite(string storageName)
    {
        narrowedOptionals.Add(storageName);
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
        // Seed the declared/inferred member tables and refuse the member shapes phase
        // 1 cannot carry BEFORE any reachability question is asked.
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
                    $"an exported function cannot return Optional[{members[0]}]; a C caller " +
                    "has no tag to read.", fn);

            // members is [payload, "None"] -- two members by construction here: the
            // annotation check refused Union[int, float, None] before it got this far.
            string payload = members[0];
            if (DataTypeExtensions.StringToDataType(payload) == DataType.UNKNOWN)
            {
                if (payload.StartsWith("list[") || payload is "bytearray" or "bytes" or "str"
                    || payload.StartsWith("tuple[") || TupleType.IsTupleType(payload))
                    throw UserError(
                        $"Optional[{payload}] is not a value this compiler can carry: buffers " +
                        "travel as names (a reference a caller already holds), not as a " +
                        "payload byte the tag can guard.", fn);
                throw UserError(
                    $"Optional[{payload}] is not supported: an instance is not a value that " +
                    "can be present or absent in storage -- it is storage. Keep the instance " +
                    "and give it a field that says whether it is valid.", fn);
            }

            candidates.Add((key, entry.Prefix ?? "", fn, members));
            functionDeclaredMembers[key] = members;
        }
        if (candidates.Count == 0) return;

        // `return g()` is a None-source when g is tagged, and g's tag-ness is what this
        // pass is deciding -- close over it to a fixpoint. A cycle of functions that only
        // ever return each other has no None source, so it converges to untagged, which
        // is the correct answer for it.
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
                bool canNone = CanReachRuntimeNone(fn);
                currentModulePrefix = prevPrefix;
                currentFunction = prevFn;
                if (canNone)
                {
                    functionReturnMembers[key] = members;
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="fn"/> can reach a return carrying a run-time None --
    /// a reachable `return None`, falling off the end, or `return <expr>` where the
    /// expression can hold a run-time None. Statement-level folding is replayed so a
    /// `return None` on a dead arm does not count (RFC 0009 decision 2).
    /// </summary>
    private bool CanReachRuntimeNone(FunctionDef fn)
    {
        var noneLocals = new HashSet<string>();
        var optLocals = new HashSet<string>();
        bool hit = ScanStmtsForRuntimeNone(fn.Body.Statements, noneLocals, optLocals);
        // Reaching the end of the body is an implicit `return None`.
        return hit || !AlwaysReturns(fn.Body);
    }

    /// Statement walk behind <see cref="CanReachRuntimeNone"/>. Tracks which locals
    /// provably hold None (noneLocals) and which may hold a run-time optional
    /// (optLocals), and returns true the moment a return can carry None.
    private bool ScanStmtsForRuntimeNone(List<Statement> stmts,
        HashSet<string> noneLocals, HashSet<string> optLocals)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case ReturnStmt r:
                    if (r.Value == null || r.Value is NoneLiteral) return true;
                    if (ExprMaybeRuntimeNone(r.Value, noneLocals, optLocals)) return true;
                    break;

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
                    if (ScanIfForRuntimeNone(ifs, noneLocals, optLocals)) return true;
                    break;

                case WhileStmt w:
                {
                    // The body may run zero or more times: scan it for returns, then
                    // merge conservatively -- a name is still provably-None only if it
                    // was before AND stays so through one iteration.
                    var bodyNone = new HashSet<string>(noneLocals);
                    var bodyOpt = new HashSet<string>(optLocals);
                    if (ScanStmtsForRuntimeNone(ArmStatements(w.Body), bodyNone, bodyOpt)) return true;
                    noneLocals.IntersectWith(bodyNone);
                    optLocals.UnionWith(bodyOpt);
                    break;
                }
                case ForStmt f:
                {
                    var bodyNone = new HashSet<string>(noneLocals);
                    var bodyOpt = new HashSet<string>(optLocals);
                    if (ScanStmtsForRuntimeNone(ArmStatements(f.Body), bodyNone, bodyOpt)) return true;
                    noneLocals.IntersectWith(bodyNone);
                    optLocals.UnionWith(bodyOpt);
                    break;
                }

                case TryStmt t:
                {
                    var tNone = new HashSet<string>(noneLocals);
                    var tOpt = new HashSet<string>(optLocals);
                    if (ScanStmtsForRuntimeNone(t.Body, tNone, tOpt)) return true;
                    foreach (var (_, h) in t.Handlers)
                        if (ScanStmtsForRuntimeNone(h, noneLocals, optLocals)) return true;
                    if (t.ElseBody != null && ScanStmtsForRuntimeNone(t.ElseBody, noneLocals, optLocals)) return true;
                    if (t.Finally != null && ScanStmtsForRuntimeNone(t.Finally, noneLocals, optLocals)) return true;
                    // The body's own effects merge conservatively into the through-state.
                    optLocals.UnionWith(tOpt);
                    break;
                }

                case WithStmt wi:
                    if (ScanStmtsForRuntimeNone(ArmStatements(wi.Body), noneLocals, optLocals)) return true;
                    break;
                case Block b:
                    if (ScanStmtsForRuntimeNone(b.Statements, noneLocals, optLocals)) return true;
                    break;
                case MatchStmt m:
                {
                    var armNones = new List<HashSet<string>>();
                    var armOpts = new List<HashSet<string>>();
                    foreach (var br in m.Branches)
                    {
                        var bn = new HashSet<string>(noneLocals);
                        var bo = new HashSet<string>(optLocals);
                        if (br.Body != null && ScanStmtsForRuntimeNone(ArmStatements(br.Body), bn, bo))
                            return true;
                        armNones.Add(bn); armOpts.Add(bo);
                    }
                    MergeScanSets(noneLocals, optLocals, armNones, armOpts);
                    break;
                }
            }
        }
        return false;
    }

    /// The `if` arm of <see cref="ScanStmtsForRuntimeNone"/>: a condition that folds
    /// statically prunes its untaken side; otherwise all arms scan and merge.
    private bool ScanIfForRuntimeNone(IfStmt ifs,
        HashSet<string> noneLocals, HashSet<string> optLocals)
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
                // Provably true: no later arm runs.
                var tn = new HashSet<string>(noneLocals);
                var to = new HashSet<string>(optLocals);
                if (arm != null && ScanStmtsForRuntimeNone(ArmStatements(arm), tn, to)) return true;
                armNones.Add(tn); armOpts.Add(to);
                alive = false;
                break;
            }
            var an = new HashSet<string>(noneLocals);
            var ao = new HashSet<string>(optLocals);
            if (arm != null && ScanStmtsForRuntimeNone(ArmStatements(arm), an, ao)) return true;
            armNones.Add(an); armOpts.Add(ao);
            if (cond == null) alive = false;    // else arm: no later path exists
        }
        // If every arm was folded dead the whole `if` vanishes -- the implicit path is
        // the pre-if state, which is already in noneLocals/optLocals.
        if (alive && ifs.ElseBranch == null && armNones.Count > 0)
        {
            armNones.Add(new HashSet<string>(noneLocals));
            armOpts.Add(new HashSet<string>(optLocals));
        }
        if (alive || armNones.Count > 0)
            MergeScanSets(noneLocals, optLocals, armNones, armOpts);
        return false;
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

    /// The three pieces of optional state a branch snapshot carries. noneValuedNames is
    /// shared with the pre-existing compile-time tracking, so only the entries an
    /// optional name owns travel; a plain `x = None` keeps the loose semantics it has
    /// always had.
    private sealed class OptionalSnap
    {
        public Dictionary<string, Val> Slots = new();
        public HashSet<string> Narrowed = new();
        public HashSet<string> None = new();
    }

    /// Whether the qualified name can carry a runtime optional somewhere in this
    /// function -- the keys noneValued-restore is allowed to touch.
    private bool NameIsOptionalish(string q) =>
        optionalTagSlots.ContainsKey(q) || optionalMembersByName.ContainsKey(q)
        || optionalCapable.Contains(q) || optionalCapable.Contains(SourcePartOf(q));

    /// The source-level spelling of a qualified storage name.
    private string SourcePartOf(string q)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix) && q.StartsWith(currentInlinePrefix, StringComparison.Ordinal))
            return q[currentInlinePrefix.Length..];
        if (!string.IsNullOrEmpty(currentFunction) && q.StartsWith(currentFunction + ".", StringComparison.Ordinal))
            return q[(currentFunction.Length + 1)..];
        if (!string.IsNullOrEmpty(currentModulePrefix) && q.StartsWith(currentModulePrefix, StringComparison.Ordinal))
            return q[currentModulePrefix.Length..];
        return q;
    }

    private OptionalSnap SnapOptionalState()
    {
        var s = new OptionalSnap
        {
            Slots = new Dictionary<string, Val>(optionalTagSlots),
            Narrowed = new HashSet<string>(narrowedOptionals),
        };
        foreach (var n in noneValuedNames)
            if (NameIsOptionalish(n)) s.None.Add(n);
        return s;
    }

    private void RestoreOptionalState(OptionalSnap s)
    {
        optionalTagSlots.Clear();
        foreach (var kv in s.Slots) optionalTagSlots[kv.Key] = kv.Value;
        narrowedOptionals.Clear();
        narrowedOptionals.UnionWith(s.Narrowed);
        foreach (var n in noneValuedNames.Where(NameIsOptionalish).ToList())
            noneValuedNames.Remove(n);
        noneValuedNames.UnionWith(s.None);
    }

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
        if (side is not VariableExpr ve) return null;
        if (OptionalKeyOf(ve.Name) is not { } key) return null;
        bool trueMeansNone = cmp.Op is AstBinOp.Is or AstBinOp.Equal;
        return (key, trueMeansNone);
    }

    /// The name an `if v:`-style truth test decides: truthy means the tag says payload,
    /// so the taken arm narrows. Null when the operand is not a live optional.
    private string? OptionalTruthSubject(Expression? cond)
    {
        if (cond is not VariableExpr ve) return null;
        return OptionalKeyOf(ve.Name);
    }

    /// The tag Val and None member-index of a live optional the expression reads, when
    /// the expression is a bare unnarrowed optional name. Narrowed and provable-None
    /// names answer through the ordinary truthiness/None folds instead.
    private (Val tag, int noneIdx)? LiveOptionalTag(Expression? e)
    {
        if (e is not VariableExpr ve) return null;
        if (OptionalKeyOf(ve.Name) is not { } key) return null;
        if (narrowedOptionals.Contains(key) || noneValuedNames.Contains(key)) return null;
        if (!optionalTagSlots.TryGetValue(key, out var tag)) return null;
        if (!optionalMembersByName.TryGetValue(key, out var members) || members.Count == 0)
            return null;
        return (tag, members.Count - 1);   // None is always the last member (RFC 0009)
    }

    /// The union member list an arm's value contributes: `["None"]` for a provable
    /// None, the source's own member list for a live (unnarrowed-on-that-arm)
    /// optional, or the payload's type name alone for a definite value.
    private List<string> UnionMembersOf(Val v, Expression src, OptionalSnap? arm)
    {
        if (src is NoneLiteral || v is NoneVal || IsNoneValued(src))
            return new List<string> { "None" };
        if (TagOfVal(v) != null && ValNameOf(v) is { } nm
            && !(arm?.Narrowed.Contains(nm) ?? false)
            && !noneValuedNames.Contains(nm))
        {
            if (optionalMembersByName.TryGetValue(nm, out var m)) return new List<string>(m);
            return new List<string> { TypeNameFor(v), "None" };
        }
        return new List<string> { TypeNameFor(v) };
    }

    /// The tag an arm writes into a joined result: the source's own tag byte when it
    /// is a live optional, the result's None index for a provable None, else 0. Phase-1
    /// unions all share the [X, None] member order, so a copied tag keeps its meaning.
    private Val ArmTagFor(Val v, Expression src, OptionalSnap? arm, int noneIdx)
    {
        if (src is NoneLiteral || v is NoneVal || IsNoneValued(src))
            return new Constant(noneIdx);
        if (TagOfVal(v) is { } t && ValNameOf(v) is { } nm
            && !(arm?.Narrowed.Contains(nm) ?? false))
            return t;
        return new Constant(0);
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
    /// truthy (payload tag AND nonzero payload) or falsy (None tag OR zero payload),
    /// per <paramref name="jumpOnTruthy"/>.
    private void EmitOptionalTruthJump(Val tag, Val payload, int noneIdx, string target, bool jumpOnTruthy)
    {
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
        if (OptionalCondSubject(cond) is { } subj)
        {
            bool proveNone = subj.trueMeansNone == whenTrue;
            if (proveNone)
            {
                narrowedOptionals.Remove(subj.key);
                noneValuedNames.Add(subj.key);
            }
            else
            {
                narrowedOptionals.Add(subj.key);
                noneValuedNames.Remove(subj.key);
            }
            return;
        }
        // `if v:` narrows on the taken arm: truthy means the tag said payload. The
        // fall-through proves nothing (None or a falsy payload), same as `if not v:`.
        if (whenTrue && OptionalTruthSubject(cond) is { } truthKey)
            narrowedOptionals.Add(truthKey);
    }

    /// Merge the per-arm end states into the join. <paramref name="armEnds"/> holds a
    /// null for an arm that never reaches the end (it returned or raised), and
    /// <paramref name="fallthrough"/> is the state past a chain with no else. A name
    /// stays narrowed only when EVERY surviving path narrows it; a tag slot stays when
    /// ANY path can leave one.
    private void JoinOptionalState(List<OptionalSnap?> armEnds, OptionalSnap? fallthrough)
    {
        var paths = new List<OptionalSnap>();
        foreach (var e in armEnds) if (e != null) paths.Add(e);
        if (fallthrough != null) paths.Add(fallthrough);
        if (paths.Count == 0) return;   // every arm leaves: nothing joins

        var narrowed = new HashSet<string>(paths[0].Narrowed);
        var none = new HashSet<string>(paths[0].None);
        foreach (var p in paths.Skip(1))
        {
            narrowed.IntersectWith(p.Narrowed);
            none.IntersectWith(p.None);
        }
        var slots = new Dictionary<string, Val>();
        foreach (var p in paths)
            foreach (var kv in p.Slots)
                slots.TryAdd(kv.Key, kv.Value);

        optionalTagSlots.Clear();
        foreach (var kv in slots) optionalTagSlots[kv.Key] = kv.Value;
        narrowedOptionals.Clear();
        narrowedOptionals.UnionWith(narrowed);
        foreach (var n in noneValuedNames.Where(NameIsOptionalish).ToList())
            noneValuedNames.Remove(n);
        noneValuedNames.UnionWith(none);
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
        List<string> members = optionalMembersByName.TryGetValue(target.Name, out var m)
            ? m : new List<string> { "uint8", "None" };

        bool srcNarrowed = value is Variable sv && narrowedOptionals.Contains(sv.Name)
                           || value is Temporary st && narrowedOptionals.Contains(st.Name);
        // A NoneVal value alone does NOT mean None -- a constructor call yields one too.
        // The SOURCE expression has to say None, the same rule noneValuedNames follows.
        if (valueExpr is NoneLiteral
            || (valueExpr != null && IsNoneValued(valueExpr)))
        {
            Emit(new Copy(new Constant(NoneIndex(members)), tagVar));
            optionalTagSlots[target.Name] = tagVar;
            MarkOptionalNone(target.Name);
        }
        else if (TagOfVal(value) is { } srcTag)
        {
            Emit(new Copy(srcTag, tagVar));
            optionalTagSlots[target.Name] = tagVar;
            narrowedOptionals.Remove(target.Name);
            noneValuedNames.Remove(target.Name);
            if (srcNarrowed) narrowedOptionals.Add(target.Name);
        }
        else
        {
            Emit(new Copy(new Constant(0), tagVar));
            optionalTagSlots[target.Name] = tagVar;
            MarkOptionalDefinite(target.Name);
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
            if (narrowedOptionals.Contains(argName)) narrowedOptionals.Add(paramName);
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
        if (vName != null && narrowedOptionals.Contains(vName))
            return new Constant(isIs ? 0 : 1);
        var t = MakeTemp(DataType.UINT8);
        Emit(new Binary(isIs ? BinaryOp.Equal : BinaryOp.NotEqual,
            liveTag, new Constant(NoneIndexOfVal(optV)), t));
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

    /// The member index an `-> Optional[X]` @inline callee's `return` writes into the
    /// expansion's tag temp.
    private Val InlineReturnTagVal(InlineContext ctx, Expression? expr, Val val)
    {
        int noneIdx = (inlineFunctions.TryGetValue(ctx.CalleeName, out var fn)
            && fn?.ReturnMembers is { } m) ? m.Count - 1 : 1;
        return ReturnTagVal(expr, val, noneIdx);
    }

    /// Shared tag arithmetic for the two return paths: None or a bare `return` reports
    /// the last member, a narrowed name reports member 0, a live optional forwards its
    /// runtime tag, and anything else is member 0.
    private Val ReturnTagVal(Expression? expr, Val val, int noneIdx)
    {
        if (expr == null || expr is NoneLiteral || val is NoneVal) return new Constant(noneIdx);
        if (expr is VariableExpr ve)
        {
            if (narrowedOptionals.Contains(ve.Name)) return new Constant(0);
            if (TagOfVal(val) is { } srcTag) return srcTag;
            if (IsNoneValued(ve)) return new Constant(noneIdx);
            return new Constant(0);
        }
        if (TagOfVal(val) is { } t) return t;
        return new Constant(0);
    }

    /// The Tag operand a `Return` instruction needs for this return's value.
    private Val? TagForReturn(Expression? expr, Val val)
    {
        if (CurrentReturnMembers is not { } members) return null;
        return ReturnTagVal(expr, val, NoneIndex(members));
    }
}
