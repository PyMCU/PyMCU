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

using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    // Resolves the fixed-array size of `name` by following the variableAliases
    // chain from the current inline prefix. This lets a `for x in param` loop
    // find the array length when a (possibly uninitialised) runtime array is
    // passed into an @inline function -- the size is registered under the
    // caller's qualified name, not the parameter name.
    private int ResolveAliasedArraySize(string name, out string baseKey)
    {
        baseKey = "";
        string key = currentInlinePrefix + name;
        // A name bound outside an expansion -- `x = f()` where f returned its local buffer
        // -- is aliased under the function-qualified spelling (`main.x`), which the bare
        // starting key would never find.
        if (!variableAliases.ContainsKey(key) && !arraySizes.ContainsKey(key)
            && !string.IsNullOrEmpty(currentFunction))
            key = currentFunction + "." + name;
        for (int d = 0; d < 20; d++)
        {
            if (variableAliases.TryGetValue(key, out var nxt)) key = nxt;
            else break;
            if (TryResolveArrayStorageKey(key, out var stored)) { baseKey = stored; return LogicalArrayLen(stored, arraySizes[stored]); }
        }
        return -1;
    }

    // A resolved alias endpoint can name a CLASS attribute or module-level array by its
    // class-canonical spelling (`Sensor__BUFFER`, `mod_TCS34725__BUFFER`) while the storage
    // was registered under the init function that ran the class body -- `main.<attr>` for
    // the entry module, `<mod>__module_init.<attr>` for an imported one. Without this
    // normalization an `enumerate(buffer)` over a `_BUFFER = bytearray(8)` class attribute
    // passed through `i2c.write(self._BUFFER)` missed every size lookup (PyMCU#454 follow-up:
    // adafruit_tcs34725's writeto). A key already carrying a `.` is function-qualified and
    // needs no second home.
    private bool TryResolveArrayStorageKey(string key, out string storageKey)
    {
        if (arraySizes.ContainsKey(key)) { storageKey = key; return true; }
        int dot = key.LastIndexOf('.');
        if (dot < 0)
        {
            if (arraySizes.ContainsKey("main." + key)) { storageKey = "main." + key; return true; }
            foreach (var modName in modules.Keys)
            {
                string mp = modName.Replace('.', '_') + "_";
                if (!key.StartsWith(mp, StringComparison.Ordinal)) continue;
                string initKey = mp + "__module_init." + key.Substring(mp.Length);
                if (arraySizes.ContainsKey(initKey)) { storageKey = initKey; return true; }
            }
        }
        else
        {
            // The mirror image of the prefixing above: a module-level array declared with a
            // subscripted annotation (`buf: uint8[2]`) is registered BARE by ScanGlobals and
            // every instruction that touches it spells it bare too, but a name resolved inside
            // a function or an inline expansion arrives function-qualified (`main.buf`). The
            // unannotated `bytearray(N)` spelling registers BOTH `main.cfg` and `cfg`, so it
            // already hit the exact match above. (PyMCU#258)
            string bareSuffix = key[(dot + 1)..];
            if (arraySizes.ContainsKey(bareSuffix)) { storageKey = bareSuffix; return true; }
        }
        storageKey = key;
        return false;
    }

    /// <summary>
    /// A <c>memoryview(buf)[k:]</c> window's loads and stores go to the
    /// underlying array at <c>index + offset</c>. A name that is not a
    /// view is returned unchanged.
    /// </summary>
    private void RemapArrayAccess(string name, Val idx,
        out string storage, out Val mappedIdx, out int totalSize, out DataType elemDt)
    {
        string n = name;
        for (int hop = 0; hop < 20; hop++)
        {
            if (!variableAliases.TryGetValue(n, out var next) || next == null || next == n)
                break;
            n = next;
        }

        int off = 0;
        storage = n;
        for (int hop = 0; hop < 8 && arrayViewBase.TryGetValue(storage, out var next); hop++)
        {
            off += arrayViewOffset.TryGetValue(storage, out var o) ? o : 0;
            storage = next;
        }

        if (!arraySizes.ContainsKey(storage) && TryResolveArrayStorageKey(storage, out var sk))
            storage = sk;
        if (arraySizes.TryGetValue(storage, out totalSize))
            totalSize = LogicalArrayLen(storage, totalSize);
        else
            totalSize = arraySizes.TryGetValue(n, out var vs) ? LogicalArrayLen(n, vs) : 0;
        elemDt = arrayElemTypes.TryGetValue(storage, out var edt)
            ? edt
            : (arrayElemTypes.TryGetValue(n, out var vedt) ? vedt : DataType.UINT8);

        if (off == 0) { mappedIdx = idx; return; }
        if (idx is Constant c)
        {
            mappedIdx = new Constant(c.Value + off);
            return;
        }
        Temporary t = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, idx, new Constant(off), t));
        mappedIdx = t;
    }

    // A subscripted name the current function does not claim resolves at MODULE scope -- in
    // Python `cfg[i] = v` mutates the module global with no `global` statement needed, the
    // declaration only rebinds the name itself. Module arrays canonicalize to the BARE name
    // (`cfg`), which is where ScanGlobals files them and where the replayed declaration now
    // lands (PyMCU#460); an imported module may instead file its storage under the init
    // function (`<mod>___module_init.cfg`), so that spelling is probed first when the caller
    // belongs to that module. `main.<suffix>` is last: after #460 it can only be a local of
    // `main` itself, never module storage -- kept only as a fallback for shapes not yet
    // canonicalized.
    private string ModuleScopeArrayName(string fnQualified)
    {
        int dot = fnQualified.LastIndexOf('.');
        string suffix = dot >= 0 ? fnQualified[(dot + 1)..] : fnQualified;
        foreach (var modName in modules.Keys)
        {
            string mp = modName.Replace('.', '_') + "_";
            if (!fnQualified.StartsWith(mp, StringComparison.Ordinal)) continue;
            string initKey = mp + "__module_init." + suffix;
            if (arraySizes.ContainsKey(initKey)) return initKey;
        }
        if (arraySizes.ContainsKey(suffix)) return suffix;
        if (arraySizes.ContainsKey("main." + suffix)) return "main." + suffix;
        return fnQualified;
    }

    // The name at the end of `name`'s alias chain, spelled as the current scope would write
    // it. A parameter handed through stacked @inline expansions aliases another parameter
    // (`pulses` -> `inline1.send.pulses` -> `main.signal`), so the terminal -- not the
    // parameter's local spelling -- is where the value lives and the name write counts and
    // flash-table keys are filed under (PyMCU#258).
    private string TerminalAliasOf(string name)
    {
        string term = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);
        for (int d = 0; d < 20 && variableAliases.TryGetValue(term, out var nxt); ++d)
            term = nxt;
        return term;
    }

    // The module prefixes (`mod_`) the current compile context belongs to: a bare name
    // inside `mod`'s code means that module's global, and no other module's. Used to probe
    // the spellings module-level bindings are filed under -- `mod_<name>` by the scan and
    // `mod___module_init.<name>` by the init-function lowering.
    private IEnumerable<string> OwningModulePrefixes()
    {
        foreach (var modName in modules.Keys)
        {
            string mp = modName.Replace('.', '_') + "_";
            if ((currentModulePrefix ?? "").StartsWith(mp, StringComparison.Ordinal)
                || (currentFunction ?? "").StartsWith(mp, StringComparison.Ordinal))
                yield return mp;
        }
    }

    // `for c in <const[str]>` unrolls at or below this length (each char a compile-time
    // constant); longer strings emit a runtime loop over a flash table instead so a heavy
    // body is not duplicated per character.
    private const int StringForLoopUnrollLimit = 8;

    // A named list/tuple unrolls up to this length; past it the loop stays a loop.
    internal const int ConstSequenceUnrollLimit = 8;



    // True if `s` has a break/continue that targets the *enclosing* loop — i.e. one not nested
    // inside its own for/while (which owns its break/continue). A compile-time-unrolled loop
    // must, only when this holds, bracket each iteration with a continue label and share a break
    // label so those statements have somewhere to jump; when it does not, the plain unroll is
    // kept so per-iteration constant folding is not split across label boundaries.
    private static bool LoopBodyHasBreakOrContinue(Statement? s, bool breakOnly = false)
    {
        switch (s)
        {
            case null: return false;
            case BreakStmt: return true;
            // A continue restarts THIS loop; it never reaches the statement after it.
            // The label-bracketing callers need it counted, the `while True:` end-of-
            // function check does not (a continue-only while-True still cannot exit).
            case ContinueStmt: return !breakOnly;
            case ForStmt:
            case WhileStmt: return false;            // nested loop owns its break/continue
            case Block b: return b.Statements.Any(x => LoopBodyHasBreakOrContinue(x, breakOnly));
            case IfStmt i:
                return LoopBodyHasBreakOrContinue(i.ThenBranch, breakOnly)
                       || i.ElifBranches.Any(e => LoopBodyHasBreakOrContinue(e.Body, breakOnly))
                       || LoopBodyHasBreakOrContinue(i.ElseBranch, breakOnly);
            case MatchStmt m: return m.Branches.Any(br => LoopBodyHasBreakOrContinue(br.Body, breakOnly));
            case WithStmt w: return LoopBodyHasBreakOrContinue(w.Body, breakOnly);
            case TryStmt t:
                return t.Body.Any(x => LoopBodyHasBreakOrContinue(x, breakOnly))
                       || t.Handlers.Any(h => h.Handler.Any(x => LoopBodyHasBreakOrContinue(x, breakOnly)))
                       || (t.Finally?.Any(x => LoopBodyHasBreakOrContinue(x, breakOnly)) ?? false);
            default: return false;
        }
    }

    // ------------------------------------------------------------------
    // The unroll policy.
    //
    // A compile-time `for` -- range() with folded bounds, a fixed-size sequence,
    // or enumerate()/reversed()/zip() over one -- copies its body once per
    // element only when the trip count is at most ConstSequenceUnrollLimit AND
    // the body is small. UnrolledLoopBodyIsCheap is the "small" half:
    //
    //   * no loop nested in the body -- a loop is already a counter, and
    //     multiplying it is what produced eight copies of the nested Life
    //     program's inner fill_rect;
    //   * no call that expands an inline body bigger than
    //     ConstSequenceUnrollBodyLimit nodes -- `display.show()` pulls in the
    //     whole byte-at-a-time I2C walk, and N copies of that is N programs,
    //     not a loop.
    //
    // Anything else lowers to the run-time counter loop the same source would
    // have written with `while`: `for __u in range(n): v = seq[__u]; <body>`.
    // That fallback needs a sequence the counter can subscript, which is every
    // fixed array (contiguous SRAM, or a const table the subscript lowering
    // materialises into flash) and every all-integer constant sequence (interned
    // the same way). Elements that exist only as compile-time bindings with no
    // storage form -- instances, string chunks -- still unroll.
    // ------------------------------------------------------------------

    /// <summary>The largest inline-expanded call body, in AST nodes (roughly one IR
    /// instruction each), that a loop body may repeat per element and still
    /// unroll.</summary>
    internal const int ConstSequenceUnrollBodyLimit = 24;

    /// <summary>
    /// The "body is small" half of the unroll policy: true when the body holds no
    /// loop of its own and its whole expansion -- its own nodes plus every inline
    /// callee it reaches -- is within <see cref="ConstSequenceUnrollBodyLimit"/>.
    /// </summary>
    private bool UnrolledLoopBodyIsCheap(Statement? body) =>
        CheapBodyShape(body)
        && ExpandedNodes(body, null, null, new HashSet<FunctionDef>())
            <= ConstSequenceUnrollBodyLimit;

    // Walk one loop body; false as soon as a loop or a comprehension (lowered as
    // a loop) appears inside it. Nested def/class bodies are not descended into:
    // they do not expand into the unroll.
    private static bool CheapBodyShape(Statement? s)
    {
        foreach (var st in TypeInference.WalkStatements(s))
        {
            bool cheap = st switch
            {
                ForStmt or WhileStmt => false,
                IfStmt i => CheapExprShape(i.Condition)
                    && i.ElifBranches.All(e => CheapExprShape(e.Condition)),
                MatchStmt m => CheapExprShape(m.Target)
                    && m.Branches.All(br => CheapExprShape(br.Guard)),
                WithStmt w => CheapExprShape(w.ContextExpr),
                TryStmt => true,
                AssignStmt a => CheapExprShape(a.Target) && CheapExprShape(a.Value),
                AnnAssign a => CheapExprShape(a.Value),
                AugAssignStmt a => CheapExprShape(a.Target) && CheapExprShape(a.Value),
                ExprStmt e => CheapExprShape(e.Expr),
                ReturnStmt r => CheapExprShape(r.Value),
                TupleUnpackStmt t => CheapExprShape(t.Value),
                AssertStmt a => CheapExprShape(a.Condition),
                RaiseStmt r => CheapExprShape(r.MessageExpr),
                VarDecl v => CheapExprShape(v.Init),
                _ => true,   // break/continue/pass/global/nonlocal/import/def/class
            };
            if (!cheap) return false;
        }
        return true;
    }

    private static bool CheapExprShape(Expression? e)
    {
        switch (e)
        {
            case null: return true;
            case CallExpr c:
                return CheapExprShape(c.Callee) && c.Args.All(CheapExprShape);
            case ListCompExpr or GeneratorExpr: return false;   // lowered as a loop
            case BinaryExpr b: return CheapExprShape(b.Left) && CheapExprShape(b.Right);
            case UnaryExpr u: return CheapExprShape(u.Operand);
            case TernaryExpr t:
                return CheapExprShape(t.Condition)
                    && CheapExprShape(t.TrueVal) && CheapExprShape(t.FalseVal);
            case MemberAccessExpr m: return CheapExprShape(m.Object);
            case IndexExpr i: return CheapExprShape(i.Target) && CheapExprShape(i.Index);
            case SliceExpr s:
                return CheapExprShape(s.Start) && CheapExprShape(s.Stop)
                    && CheapExprShape(s.Step);
            case ListExpr l: return l.Elements.All(CheapExprShape);
            case TupleExpr t: return t.Elements.All(CheapExprShape);
            case SetExpr s: return s.Elements.All(CheapExprShape);
            case DictExpr d: return d.Entries.All(kv => CheapExprShape(kv.Key) && CheapExprShape(kv.Value));
            case FStringExpr f: return f.Parts.All(p => !p.IsExpr || CheapExprShape(p.Expr));
            case WalrusExpr w: return CheapExprShape(w.Value);
            case StarArgExpr s: return CheapExprShape(s.Value);
            case DoubleStarArgExpr d: return CheapExprShape(d.Value);
            case KeywordArgExpr k: return CheapExprShape(k.Value);
            case LambdaExpr l: return CheapExprShape(l.Body);
            case AwaitExpr a: return CheapExprShape(a.Operand);
            case YieldExpr y: return CheapExprShape(y.Value);
            default: return true;   // literals and bare names
        }
    }

    /// <summary>
    /// Maps a callable's lookup names to the ARGUMENT positions that must bind a
    /// compile-time constant: `Pin(pin_id: const[uint8], ...)` records position 0
    /// under "Pin" (from its `__init__`) and under the member name. Built once per
    /// program, on first use -- every scan table is populated by then.
    /// </summary>
    private Dictionary<string, HashSet<int>>? constArgPositions;

    private Dictionary<string, HashSet<int>> ConstArgPositions()
    {
        if (constArgPositions != null) return constArgPositions;
        var map = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        void Note(string? key, int pos)
        {
            if (string.IsNullOrEmpty(key) || pos < 0) return;
            if (!map.TryGetValue(key!, out var s)) map[key!] = s = new HashSet<int>();
            s.Add(pos);
        }

        void NoteFunc(string? key, FunctionDef f)
        {
            int self = f.Params.Count > 0 && f.Params[0].Name is "self" or "cls" ? 1 : 0;
            for (int j = self; j < f.Params.Count; j++)
            {
                if (!IsConstType(f.Params[j].Type ?? "")) continue;
                int pos = j - self;
                Note(f.Name, pos);
                Note(key, pos);
                // A constructor answers to the class name at the call site --
                // `Pin(n)` reaches `Pin.__init__` -- so record it there too.
                if (f.Name == "__init__" && key != null
                    && key.EndsWith("___init__", StringComparison.Ordinal))
                {
                    string cls = key[..^9];
                    Note(cls, pos);
                    int us = cls.LastIndexOf('_');
                    if (us >= 0) Note(cls[(us + 1)..], pos);
                }
            }
        }

        foreach (var fe in functionsToCompile)
            NoteFunc((fe.Prefix ?? "") + fe.Func.Name, fe.Func);
        foreach (var kv in inlineFunctions) if (kv.Value != null) NoteFunc(kv.Key, kv.Value);
        foreach (var kv in methodAstByName) NoteFunc(kv.Key, kv.Value);
        foreach (var kv in instanceMethodDefs) NoteFunc(kv.Key, kv.Value);
        foreach (var kv in zcaHandlerAstNodes) NoteFunc(kv.Key, kv.Value.Func);
        constArgPositions = map;
        return map;
    }

    // True when the loop variable -- or a name the body copies it into -- reaches a
    // position only a compile-time constant can fill: a `const[...]` call argument
    // (`Pin(n, Pin.OUT)` needs `n` unrolled to bind), the index of storage that has
    // no run-time form (`GPIOR0[n]`), or -- transitively -- a callee's parameter that
    // itself indexes a compile-time table (`for ch in "Cb-": d.show(ch)` unrolls
    // because `self.chars[ch]` folds per character; a counter would pay a compare
    // chain per character instead). The counter-loop fallback lowers the variable
    // to a run-time value, which these positions refuse or pessimize -- so the loop
    // must unroll whatever the body costs.
    private bool LoopVarNeedsConst(ForStmt stmt)
    {
        var names = new HashSet<string> { stmt.VarName };
        if (!string.IsNullOrEmpty(stmt.Var2Name)) names.Add(stmt.Var2Name!);
        if (stmt.Body == null) return false;

        var nodes = AstNodes(stmt.Body, descendIntoFunctions: true).ToList();
        GrowNamesThroughCopies(nodes, names);

        var positions = ConstArgPositions();
        var visiting = new HashSet<(FunctionDef, int)>();
        foreach (var node in nodes)
        {
            if (node is CallExpr call)
            {
                string? key = call.Callee switch
                {
                    VariableExpr ve => ve.Name,
                    MemberAccessExpr ma => ma.Member,
                    _ => null,
                };
                if (key != null && positions.TryGetValue(key, out var posSet))
                    for (int i = 0; i < call.Args.Count; i++)
                    {
                        if (!MentionsAny(call.Args[i], names)) continue;
                        if (posSet.Contains(i)) return true;
                        // A keyword/star argument names its parameter rather than its
                        // position, so position matching cannot see it; any const
                        // position on the callee is suspect.
                        if (call.Args[i] is KeywordArgExpr or StarArgExpr or DoubleStarArgExpr)
                            return true;
                    }
                if (ResolveCallFunc(call, null) is { } resolved)
                    for (int i = 0; i < call.Args.Count; i++)
                        if (MentionsAny(call.Args[i], names)
                            && CalleeArgPos(call.Args[i], i, resolved.Func) is { } ap
                            && CalleeParamNeedsConst(resolved.Func, ap, resolved.RecvKey, visiting))
                            return true;
            }
            else if (node is IndexExpr ix
                     && MentionsAny(ix.Index, names)
                     && ConstTableBase(ix.Target, null))
            {
                return true;
            }
            else if (node is BinaryExpr { Op: Frontend.BinaryOp.In or Frontend.BinaryOp.NotIn } bx
                     && MentionsAny(bx.Left, names)
                     && ConstTableBase(bx.Right, null))
            {
                return true;
            }
        }
        return false;
    }

    // A value a tracked name flows through carries the same requirement:
    // `p = n; Pin(p)` folds `p` to the constant only when the loop unrolls.
    private static void GrowNamesThroughCopies(List<ASTNode> nodes, HashSet<string> names)
    {
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case AssignStmt { Target: VariableExpr tv, Value: { } vv } when MentionsAny(vv, names):
                        grew |= names.Add(tv.Name); break;
                    case AnnAssign { Value: { } av } aa when MentionsAny(av, names):
                        grew |= names.Add(aa.Target); break;
                    case VarDecl { Init: { } iv } vd when MentionsAny(iv, names):
                        grew |= names.Add(vd.Name); break;
                    case TupleUnpackStmt { Value: { } tv } tu when MentionsAny(tv, names):
                        foreach (var t in tu.Targets) grew |= names.Add(t);
                        break;
                }
            }
        }
    }

    private static bool MentionsAny(Expression? e, HashSet<string> names) =>
        e != null && AstNodes(e, descendIntoFunctions: true)
            .OfType<VariableExpr>().Any(v => names.Contains(v.Name));

    // The base of a subscript or `in` test that a run-time key cannot reach
    // cheaply: a dict/set literal binding (`self.chars`, a named table) whose
    // run-time form is a per-key compare chain, or a name with no subscriptable
    // storage at all. `recvKey` is the instance a scanned method body's `self`
    // stands for.
    private bool ConstTableBase(Expression t, string? recvKey)
    {
        switch (t)
        {
            case VariableExpr v:
                if (dictLiteralBindings.ContainsKey(ResolveNameKey(v.Name))
                    || setLiteralBindings.ContainsKey(ResolveNameKey(v.Name)))
                    return true;
                return !HasSubscriptableStorage(v.Name);
            case MemberAccessExpr { Object: VariableExpr ov } ma:
                string owner = ov.Name == "self" && recvKey != null
                    ? recvKey
                    : ResolveNameKey(ov.Name);
                return dictLiteralBindings.ContainsKey(owner + "_" + ma.Member)
                    || setLiteralBindings.ContainsKey(owner + "_" + ma.Member);
            default:
                return false;
        }
    }

    // The FunctionDef a call reaches, best-effort, for the needs-const scan only:
    // `inst.m(...)` through instanceClasses and the MRO walk the emitter uses,
    // `self.m(...)` through the receiver key the scan is running under, a bare
    // `f(...)` through the function tables. Null for anything else -- an
    // unresolved callee simply does not force an unroll.
    private (FunctionDef Func, string? RecvKey)? ResolveCallFunc(CallExpr call, string? selfKey)
    {
        string? key = null;
        string? recvKey = null;
        if (call.Callee is MemberAccessExpr { Object: VariableExpr recv } mem)
        {
            string? cls = null;
            if (recv.Name == "self")
            {
                recvKey = selfKey;
                if (recvKey != null) instanceClasses.TryGetValue(recvKey, out cls);
                cls ??= methodInstanceTypes.TryGetValue(currentFunction, out var mc) ? mc : null;
            }
            else
            {
                string rk = recv.Name;
                if (!instanceClasses.TryGetValue(rk, out cls) || cls == null)
                {
                    if (AliasedInstanceName(rk) is { } al
                        && instanceClasses.TryGetValue(al, out cls))
                        rk = al;
                }
                if (cls != null) recvKey = rk;
            }
            if (cls == null) return null;
            key = ResolveMROMethod(cls, mem.Member) + "_" + mem.Member;
        }
        else if (call.Callee is VariableExpr ve)
        {
            key = ve.Name;
        }
        if (key == null) return null;
        if (!inlineFunctions.TryGetValue(key, out var f)
            && !instanceMethodDefs.TryGetValue(key, out f)
            && !methodAstByName.TryGetValue(key, out f))
            return null;
        return f == null ? null : (f, recvKey);
    }

    // The callee parameter index a call argument binds: its position among the
    // user arguments, or the position of the parameter a keyword names. Null for
    // a *args/** splat, which binds a shape this scan cannot name.
    private static int? CalleeArgPos(Expression arg, int pos, FunctionDef func)
    {
        if (arg is not KeywordArgExpr kw) return arg is StarArgExpr or DoubleStarArgExpr ? null : pos;
        int self = func.Params.Count > 0 && IsReceiverParamName(func.Params[0].Name) ? 1 : 0;
        for (int j = self; j < func.Params.Count; j++)
            if (func.Params[j].Name == kw.Key) return j - self;
        return null;
    }

    // True when the parameter at `argPos` of `func` is used inside the function
    // body in a position only a compile-time constant fills: indexing a
    // compile-time table (`self.chars[ch]`, `ch in self.chars`), a const[...]
    // argument onward, or another callee's parameter that needs the same. The
    // `visiting` set bounds the recursion through mutually recursive callees.
    private bool CalleeParamNeedsConst(FunctionDef func, int argPos, string? recvKey,
                                     HashSet<(FunctionDef, int)> visiting)
    {
        int self = func.Params.Count > 0 && IsReceiverParamName(func.Params[0].Name) ? 1 : 0;
        int pi = argPos + self;
        if (pi >= func.Params.Count) return false;
        if (!visiting.Add((func, argPos))) return false;
        try
        {
            var nodes = AstNodes(func.Body, descendIntoFunctions: true).ToList();
            var names = new HashSet<string> { func.Params[pi].Name };
            GrowNamesThroughCopies(nodes, names);

            var positions = ConstArgPositions();
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case IndexExpr ix when MentionsAny(ix.Index, names)
                                          && ConstTableBase(ix.Target, recvKey):
                        return true;
                    case BinaryExpr { Op: Frontend.BinaryOp.In or Frontend.BinaryOp.NotIn } bx
                        when MentionsAny(bx.Left, names) && ConstTableBase(bx.Right, recvKey):
                        return true;
                    case CallExpr call:
                    {
                        string? key = call.Callee switch
                        {
                            VariableExpr ve => ve.Name,
                            MemberAccessExpr ma => ma.Member,
                            _ => null,
                        };
                        if (key != null && positions.TryGetValue(key, out var posSet))
                            for (int i = 0; i < call.Args.Count; i++)
                            {
                                if (!MentionsAny(call.Args[i], names)) continue;
                                if (posSet.Contains(i)) return true;
                                if (call.Args[i] is KeywordArgExpr or StarArgExpr or DoubleStarArgExpr)
                                    return true;
                            }
                        if (ResolveCallFunc(call, recvKey) is { } next)
                            for (int i = 0; i < call.Args.Count; i++)
                                if (MentionsAny(call.Args[i], names)
                                    && CalleeArgPos(call.Args[i], i, next.Func) is { } ap
                                    && CalleeParamNeedsConst(next.Func, ap, next.RecvKey, visiting))
                                    return true;
                        break;
                    }
                }
            }
            return false;
        }
        finally
        {
            visiting.Remove((func, argPos));
        }
    }

    // Counts the nodes one inline body lowers to, descending into the inline
    // calls it makes. `selfClass` is the class the body being counted belongs to,
    // so `self.` calls inside it resolve against that class rather than the
    // caller's context; `expanding` is the chain of callees already open, so a
    // recursive pair cannot loop the count forever.
    private int ExpandedNodes(Statement? body, string? selfClass, string? fnKey,
        HashSet<FunctionDef> expanding)
    {
        string? savedPrefix = currentModulePrefix;
        if (fnKey != null && functionModulePrefix.TryGetValue(fnKey, out var mp))
            currentModulePrefix = mp;
        int nodes = 0;
        try
        {
            // Per-statement count for the shared walk: each statement adds what the
            // old recursion counted for that node; the walk supplies the children.
            void Stmt(Statement s)
            {
                switch (s)
                {
                    case IfStmt i:
                        nodes++; Expr(i.Condition);
                        foreach (var (c, _) in i.ElifBranches) Expr(c);
                        return;
                    case WhileStmt w: nodes++; Expr(w.Condition); return;
                    case ForStmt f:
                        nodes++;
                        if (f.Iterable != null) Expr(f.Iterable);
                        if (f.RangeStart != null) Expr(f.RangeStart);
                        if (f.RangeStop != null) Expr(f.RangeStop);
                        if (f.RangeStep != null) Expr(f.RangeStep);
                        return;
                    case MatchStmt m:
                        nodes++; Expr(m.Target);
                        foreach (var br in m.Branches) Expr(br.Guard);
                        return;
                    case WithStmt w: nodes++; Expr(w.ContextExpr); return;
                    case TryStmt: nodes++; return;
                    case AssignStmt a: nodes++; Expr(a.Target); Expr(a.Value); return;
                    case AnnAssign a: nodes++; Expr(a.Value); return;
                    case AugAssignStmt a: nodes++; Expr(a.Target); Expr(a.Value); return;
                    case ExprStmt e: nodes++; Expr(e.Expr); return;
                    case ReturnStmt r: nodes++; Expr(r.Value); return;
                    case TupleUnpackStmt t: nodes++; Expr(t.Value); return;
                    case AssertStmt a: nodes++; Expr(a.Condition); return;
                    case RaiseStmt r: nodes++; Expr(r.MessageExpr); return;
                    case VarDecl v: nodes++; Expr(v.Init); return;
                    case Block: return;
                    default: nodes++; return;
                }
            }
            void Expr(Expression? e)
            {
                switch (e)
                {
                    case null: return;
                    case CallExpr c:
                        nodes++; Expr(c.Callee);
                        foreach (var a in c.Args) Expr(a);
                        foreach (var (fn, cls, key) in ResolveInlineCallees(c, selfClass))
                            if (expanding.Add(fn))
                            {
                                nodes += ExpandedNodes(fn.Body, cls, key, expanding);
                                expanding.Remove(fn);
                            }
                        return;
                    case BinaryExpr b: nodes++; Expr(b.Left); Expr(b.Right); return;
                    case UnaryExpr u: nodes++; Expr(u.Operand); return;
                    case TernaryExpr t: nodes++; Expr(t.Condition); Expr(t.TrueVal); Expr(t.FalseVal); return;
                    case MemberAccessExpr m: nodes++; Expr(m.Object); return;
                    case IndexExpr i: nodes++; Expr(i.Target); Expr(i.Index); return;
                    case SliceExpr s: nodes++; Expr(s.Start); Expr(s.Stop); Expr(s.Step); return;
                    case ListExpr l: nodes++; foreach (var x in l.Elements) Expr(x); return;
                    case TupleExpr t: nodes++; foreach (var x in t.Elements) Expr(x); return;
                    case SetExpr s: nodes++; foreach (var x in s.Elements) Expr(x); return;
                    case DictExpr d: nodes++; foreach (var (k, v) in d.Entries) { Expr(k); Expr(v); } return;
                    case FStringExpr f: nodes++; foreach (var p in f.Parts) if (p.IsExpr) Expr(p.Expr); return;
                    case ListCompExpr lc: nodes++; Expr(lc.Element); Expr(lc.Iterable); Expr(lc.Iterable2); Expr(lc.Filter); return;
                    case GeneratorExpr g: nodes++; Expr(g.Element); Expr(g.Iterable); Expr(g.Iterable2); Expr(g.Filter); return;
                    case WalrusExpr w: nodes++; Expr(w.Value); return;
                    case StarArgExpr s: nodes++; Expr(s.Value); return;
                    case DoubleStarArgExpr d: nodes++; Expr(d.Value); return;
                    case KeywordArgExpr k: nodes++; Expr(k.Value); return;
                    case LambdaExpr l: nodes++; Expr(l.Body); return;
                    case AwaitExpr a: nodes++; Expr(a.Operand); return;
                    case YieldExpr y: nodes++; Expr(y.Value); return;
                    default: nodes++; return;
                }
            }
            foreach (var s in TypeInference.WalkStatements(body)) Stmt(s);
            return nodes;
        }
        finally { currentModulePrefix = savedPrefix; }
    }

    /// <summary>
    /// The inline function(s) a call site expands to, for the unroll policy's
    /// cost check: bare names through ResolveCallee, `obj.m(...)` and
    /// `self.field.m(...)` through the class maps, `mod.f(...)` through the
    /// module alias table. Yields (body, class `self` binds inside it, mangled
    /// key); overloads yield every variant and the caller takes the largest.
    /// </summary>
    private IEnumerable<(FunctionDef Fn, string? SelfClass, string Key)> ResolveInlineCallees(
        CallExpr call, string? selfClass)
    {
        if (call.Callee is VariableExpr ve)
        {
            string key = ResolveCallee(ve.Name);
            if (inlineFunctions.TryGetValue(key, out var f) && f != null)
                yield return (f, null, key);
            foreach (var kv in inlineFunctions)
                if (kv.Value != null && kv.Key.StartsWith(key + "___", StringComparison.Ordinal))
                    yield return (kv.Value, null, kv.Key);
            yield break;
        }

        if (call.Callee is not MemberAccessExpr mem) yield break;

        string? recvCls = null;
        switch (mem.Object)
        {
            case VariableExpr { Name: "self" } when selfClass != null:
                recvCls = selfClass;
                break;
            case VariableExpr ov:
                recvCls = InstanceClassOfName(ov.Name);
                // `mod.f(...)`: f may still be an inline function re-exported
                // through the module the name names.
                TryImportedAlias(ov.Name, out var modAlias);
                if (recvCls == null && (modules.ContainsKey(ov.Name) || modAlias != null))
                {
                    string modKey = (modAlias ?? ov.Name).Replace('.', '_') + "_" + mem.Member;
                    if (inlineFunctions.TryGetValue(modKey, out var mf) && mf != null)
                        yield return (mf, null, modKey);
                    foreach (var kv in inlineFunctions)
                        if (kv.Value != null && kv.Key.StartsWith(modKey + "___", StringComparison.Ordinal))
                            yield return (kv.Value, null, kv.Key);
                    yield break;
                }
                break;
            case MemberAccessExpr { Object: VariableExpr { Name: "self" }, Member: var fld }
                when selfClass != null:
                // `self.dev.write(...)` inside a callee being measured: the field's
                // class is filed under the class that declared it, so walk the MRO.
                for (string? anc = selfClass; anc != null && recvCls == null;)
                {
                    if (fieldClasses.TryGetValue(anc + "|" + fld, out var fc))
                        recvCls = ResolveConcreteClass(fc) ?? fc;
                    else if (classBasePrefixes.TryGetValue(anc, out var pp) && !string.IsNullOrEmpty(pp))
                        anc = pp.EndsWith("_") ? pp[..^1] : pp;
                    else
                        anc = null;
                }
                break;
        }
        if (recvCls == null) yield break;

        string defCls = ResolveMROMethod(recvCls, mem.Member);
        string mkey = defCls + "_" + mem.Member;
        if (inlineFunctions.TryGetValue(mkey, out var mm) && mm != null)
            yield return (mm, defCls, mkey);
        foreach (var kv in inlineFunctions)
            if (kv.Value != null && kv.Key.StartsWith(mkey + "___", StringComparison.Ordinal))
                yield return (kv.Value, defCls, kv.Key);
    }

    /// <summary>
    /// True when <paramref name="base"/> names one storage a run-time subscript can
    /// read: contiguous SRAM, a flash table already interned, or a constant table
    /// the subscript lowering materialises into flash on first indexed access
    /// (which it only can when the name is never written again).
    /// </summary>
    private bool HasSubscriptableStorage(string @base)
    {
        if (arraysWithVariableIndex.Contains(@base) || moduleSramArrays.Contains(@base)
            || flashArrays.Contains(@base))
            return true;
        if (!ctArrayConstElements.ContainsKey(@base)) return false;
        int dot = @base.LastIndexOf('.');
        string bare = dot >= 0 ? @base[(dot + 1)..] : @base;
        // Same veto the flash-table materialisation applies: no writes beyond the
        // annotated declaration that created the values.
        return nameWriteCounts.GetValueOrDefault(bare)
            <= (annAssignValueNames.Contains(bare) ? 1 : 0);
    }

    /// <summary>
    /// Lowers the loop as the run-time counter loop the unroll policy asks for:
    /// `for __u in range(start, stop, step): v = seq[__u]; <body>` -- for
    /// enumerate, the index name IS the counter and `v = seq[i]` binds the value.
    /// <paramref name="seqExpr"/> is what the body's subscript resolves through:
    /// the array's storage name, a materialised table, or the original iterable.
    /// </summary>
    private void EmitIndexedCounterLoop(ForStmt stmt, Expression seqExpr,
        int start, int stop, int step)
    {
        var body = new Block();
        string counter;
        if (!string.IsNullOrEmpty(stmt.Var2Name))
        {
            counter = stmt.VarName;
            body.Statements.Add(new AssignStmt(new VariableExpr(stmt.Var2Name),
                new IndexExpr(seqExpr, new VariableExpr(counter))));
        }
        else
        {
            counter = "__uci" + (++sliceLoopId);
            // The counter is compared against stop as well as assigned start, so its
            // type spans both bounds -- a descending range's negative stop needs int8.
            variableTypes[QualifyLoopVar(counter)] =
                NarrowestTypeFor(Math.Min(start, stop), Math.Max(start, stop));
            body.Statements.Add(new AssignStmt(new VariableExpr(stmt.VarName),
                new IndexExpr(seqExpr, new VariableExpr(counter))));
        }
        if (stmt.Body is Block ob) body.Statements.AddRange(ob.Statements);
        else body.Statements.Add(stmt.Body);
        var syn = new ForStmt(counter, new IntegerLiteral(start), new IntegerLiteral(stop),
            step == 1 ? null : new IntegerLiteral(step), body) { Line = stmt.Line };
        if (loopVarReadAfter.Contains(stmt)) loopVarReadAfter.Add(syn);
        VisitStatement(syn);
    }

    /// <summary>
    /// True when an element of <paramref name="base"/> is a ZCA instance: those exist
    /// only as per-element compile-time bindings (`base__0` ...), so a counter loop
    /// cannot read them and they still unroll whatever the policy says about the body.
    /// </summary>
    private bool HasInstanceElements(string @base) =>
        instanceClasses.ContainsKey(@base + "__0")
        || instanceClasses.Keys.Any(k => k.StartsWith(@base + "__0.", StringComparison.Ordinal));

    /// <summary>
    /// The unroll policy's fallback for a compile-time sequence of numbers -- a list
    /// literal, a name bound to one, a `self` field, a parameter's literal, or the same
    /// walked by reversed(): when the policy declines the unroll (more than
    /// <see cref="ConstSequenceUnrollLimit"/> elements, or a body it rejects), the
    /// values are materialised into a flash table and walked as a counter loop, the
    /// same read `tab[i]` performs. False when the policy accepts the unroll, when the
    /// loop unpacks pairs, or when an element is not a number -- instances and string
    /// chunks have no storage form and still unroll.
    /// </summary>
    private bool TryConstSeqCounterLoop(ForStmt stmt, IReadOnlyList<Expression> elems,
        string writtenName, bool reverse = false)
    {
        if (!string.IsNullOrEmpty(stmt.Var2Name)) return false;
        if (LoopVarNeedsConst(stmt)) return false;
        if (elems.Count <= ConstSequenceUnrollLimit && UnrolledLoopBodyIsCheap(stmt.Body))
            return false;
        if (ConstValuesOf(elems) is not { } vals) return false;
        if (reverse) vals.Reverse();
        // writtenName tolerates the one annotated declaration that created the
        // sequence, the same veto the array materialisation applies.
        int decls = annAssignValueNames.Contains(writtenName) ? 1 : 0;
        if (TryMaterialiseConstTableFromValues("__forseq_" + (++sliceLoopId), writtenName,
                vals, bindings: decls) is not { } tab) return false;
        EmitIndexedCounterLoop(stmt, new VariableExpr(tab), 0, vals.Count, 1);
        return true;
    }

    // Emits one unrolled-iteration body. When `breakLabel` is non-empty (the body uses
    // break/continue), a fresh continue label brackets the iteration and a shared break label
    // is active, so continue lands at the end of this iteration and break exits the loop.
    // Evaluate a list/tuple element to a compile-time integer constant. Accepts
    // integer / boolean literals and a unary-minus on an integer literal.
    private static bool TryEvalConstElement(Expression e, out int value)
    {
        switch (e)
        {
            case IntegerLiteral il: value = il.Value; return true;
            case BooleanLiteral bl: value = bl.Value ? 1 : 0; return true;
            case UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral n }: value = -n.Value; return true;
            default: value = 0; return false;
        }
    }

    // An element the literal-only check above misses but the general constant evaluator
    // still folds: a name bound to a compile-time constant (`MODE_SLEEP = const(0)`, then
    // `(MODE_SLEEP, MODE_FORCE)` -- adafruit_bmp280's mode table). The caller stores the
    // folded literal, so every consumer downstream sees a number, not the name.
    private bool TryFoldConstElement(Expression e, out int value)
    {
        if (TryEvalConstElement(e, out value)) return true;
        bool savedFold = foldLocalConstants;
        foldLocalConstants = true;
        try { value = EvaluateConstantExpr(e); return true; }
        catch { value = 0; return false; }
        finally { foldLocalConstants = savedFold; }
    }

    /// <summary>
    /// Binds one unrolled element to the loop variable, and says whether it could. A number
    /// binds as it always has; a STRING binds as a string constant, which is what a `const`
    /// parameter needs and what `case "PD6"` matches on.
    ///
    /// Only integers were accepted, so the CircuitPython idiom for a row of pins --
    /// `for pin in (board.D2, board.D3, board.D4)` -- was refused as "elements must be
    /// compile-time integer constants" for elements that are compile-time constants, and every
    /// guide with more than one pin had to be written out one call per pin (#308).
    /// </summary>
    private bool BindUnrolledElement(string key, Expression elem)
    {
        // A string element -- a literal, or a name/member bound to one text -- must be
        // asked about BEFORE the general fold: the fold reduces a string to its interned
        // id, an integer, and `for name in ["PD2", "PD3"]: print(name)` printed the ids
        // where the names were meant (oracle probes 009/010). The string check takes
        // literals and bound names only, so an integer element can never be mistaken for
        // one -- `256` in a tuple stays 256 even once "END" has interned as id 256
        // (the genexp-all-tuple fixture), which a value-first check would not survive.
        if (TryEvalConstStrElement(elem, out var text))
        {
            strConstantVariables[key] = text;
            // A one-character string is its own character code in expression position and an
            // interned id through a name. The unrolled name has to be indistinguishable from the
            // literal it stands for, which is the state the read path expects.
            if (text.Length == 1) constantVariables[key] = text[0];
            else constantVariables.Remove(key);
            return true;
        }

        // Names, field reads, arithmetic and constant ternaries fold the same way a
        // bound sequence already does (TryFoldConstElement). Literal-only evaluation
        // refused adafruit_ssd1306's `for cmd in (SET_DISP, 0x10 if self.page_addressing
        // else 0x00, self.height - 1, ...)`.
        if (TryFoldConstElement(elem, out int iv))
        {
            constantVariables[key] = iv;
            strConstantVariables.Remove(key);
            return true;
        }

        // `for pin in (reset_dio, enable_dio, ...)`: an element that names an INSTANCE is not
        // a constant but is still a compile-time answer -- the loop variable is another name
        // for that object, so alias it (with its class, for method dispatch) rather than
        // refusing the tuple (adafruit_character_lcd's pin-setup loop).
        if (elem is VariableExpr instVe)
        {
            string instKey = ResolveNameKey(instVe.Name);
            if (instanceClasses.TryGetValue(instKey, out var instCls) && instCls != null)
            {
                variableAliases[key] = instKey;
                instanceClasses[key] = instCls;
                return true;
            }
        }

        // A nested tuple/list unrolled as the loop variable: `for value_tuple in tuples`
        // then `name, value, string, delay = value_tuple` (Adafruit CV.add_values).
        if (elem is TupleExpr te)
        {
            constSequenceBindings[key] = te.Elements;
            return true;
        }
        if (elem is ListExpr le)
        {
            constSequenceBindings[key] = le.Elements;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The compile-time TEXT of an element that is not a number: a string literal, a name
    /// bound to one, or a module or class constant such as `board.D2`. Emits nothing -- an
    /// element that turns out to need code is not a constant, and anything it wrote is undone.
    /// </summary>
    private bool TryEvalConstStrElement(Expression e, out string text)
    {
        text = "";
        if (e is StringLiteral sl) { text = sl.Value; return true; }
        if (e is not VariableExpr && e is not MemberAccessExpr) return false;

        int before = currentInstructions.Count;
        Val v;
        try
        {
            v = VisitExpression(e);
        }
        catch (PyMCU.Common.CompilerError)
        {
            if (currentInstructions.Count > before)
                currentInstructions.RemoveRange(before, currentInstructions.Count - before);
            return false;
        }

        if (currentInstructions.Count > before)
        {
            currentInstructions.RemoveRange(before, currentInstructions.Count - before);
            return false;
        }

        if (v is not Constant c) return false;
        if (!string.IsNullOrEmpty(c.Text)) { text = c.Text!; return true; }
        // Interned ids start at 256, so a value in that range is a string and nothing else.
        if (c.Value >= 256 && stringIdToStr.TryGetValue(c.Value, out var interned))
        {
            text = interned;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Binds a compile-time string to an unrolled variable's key, leaving the name in the
    /// same state `s = "..."` leaves it in: the TEXT in strConstantVariables (what len(),
    /// s.split(), enumerate(s) and `case "..."` read) plus the number the same literal
    /// lowers to in constantVariables -- the char code for one character, the interned id
    /// for a longer one. Used by `for chunk in s.split()`, by enumerate() over a compile-time
    /// string, and by generator expressions whose elements are strings.
    /// </summary>
    private void BindUnrolledString(string key, string text)
    {
        strConstantVariables[key] = text;
        if (VisitExpression(new StringLiteral(text)) is Constant sc)
            constantVariables[key] = sc.Value;
        else
            constantVariables.Remove(key);
    }

    /// <summary>
    /// Drops every binding an unrolled element may have left on a variable's key, so the
    /// next iteration -- and the code after the loop or reduction -- never reads a stale
    /// answer. variableTypes is deliberately kept: a runtime element materialized a slot
    /// under the name, and Python's scoping leaves the loop variable bound after the loop.
    /// </summary>
    private void UnbindUnrolledVar(string key)
    {
        constantVariables.Remove(key);
        strConstantVariables.Remove(key);
        floatConstantVariables.Remove(key);
        constSequenceBindings.Remove(key);
        variableAliases.Remove(key);
        instanceClasses.Remove(key);
    }

    /// <summary>
    /// `s.split(sep[, maxsplit])` where s is a compile-time string: the pieces, as
    /// compile-time strings, in the order a `for` over the call unrolls them. There is no
    /// list to return -- the chunks exist only as a per-iteration binding -- so a caller
    /// that is not a `for` or `enumerate` iterable never reaches this. Empties are kept,
    /// which is Python's rule: "a\n".split("\n") is ["a", ""], and "".split("\n") is [""].
    ///
    /// The receiver's text and the separator must both be known when the program is
    /// compiled; anything else is refused with the reason named, because the alternative --
    /// a runtime split -- needs a heap to hold the pieces.
    /// </summary>
    private List<string> CompileTimeSplit(CallExpr call, ASTNode at)
    {
        var ma = (MemberAccessExpr)call.Callee;
        if (StaticStringOf(ma.Object) is not { } text)
        {
            if (ma.Object is ListExpr)
                throw UserError(
                    "bytes.split() is not supported: only str.split() unrolls at compile "
                    + "time, over a string whose text the compiler knows.", call);
            throw UserError(
                "str.split() is supported only where it unrolls at compile time -- the "
                + "iterable of a `for` or of enumerate() -- and only on a string whose text "
                + "is known when the program is compiled: a literal, a module constant, or a "
                + "parameter bound to one. This receiver is none of those; a runtime split "
                + "would need a heap to hold the pieces.", ma.Object);
        }
        if (call.Args.Count == 0)
            throw UserError(
                "str.split() with no separator splits on runs of whitespace, which is not "
                + "supported -- pass a compile-time separator, like s.split(\"\\n\").", call);
        if (call.Args.Count > 2)
            throw UserError("str.split() takes at most sep and maxsplit.", ArgAt(call, 2));
        if (StaticStringOf(call.Args[0]) is not { } sep)
            throw UserError(
                "str.split() needs a compile-time string separator -- a literal or a name "
                + "bound to one. A runtime separator would make the chunk count a runtime "
                + "property, and the unrolled loop needs it fixed.", call.Args[0]);
        if (sep.Length == 0)
            throw UserError(
                "str.split() separator cannot be empty -- a ValueError in Python too.",
                call.Args[0]);
        int? maxsplit = null;
        if (call.Args.Count >= 2)
        {
            if (call.Args[1] is KeywordArgExpr msk)
            {
                if (msk.Key != "maxsplit")
                    throw UserError(
                        $"str.split() takes no keyword argument '{msk.Key}=' -- only 'maxsplit'.",
                        call.Args[1]);
                if (!TryFoldConstElement(msk.Value, out int msv))
                    throw UserError("str.split() maxsplit must be a compile-time integer.",
                        msk.Value);
                maxsplit = msv;
            }
            else if (TryFoldConstElement(call.Args[1], out int ms))
                maxsplit = ms;
            else
                throw UserError("str.split() maxsplit must be a compile-time integer.",
                    call.Args[1]);
        }
        var parts = maxsplit is int ms2 && ms2 >= 0
            ? text.Split(new[] { sep }, ms2 + 1, StringSplitOptions.None)
            : text.Split(new[] { sep }, StringSplitOptions.None);
        return new List<string>(parts);
    }

    // The compile-time array a bare name denotes: its base key and length, or a negative length
    // when the name is not one. The probe order is inline expansion, enclosing function, bare
    // name, then the alias chain -- the same order every other lookup on this path uses.
    private void ResolveForBase(string name, out string baseKey, out int size)
    {
        baseKey = "";
        size = -1;
        // Except that a frame binding the name ends the search: past it, the enclosing and
        // bare spellings are the module's array of the same name (ShadowingFrameKey).
        if (FrameArrayStorage(name, out bool frameBinds) is { } frameBase
            && arraySizes.TryGetValue(frameBase, out int fs))
        {
            size = LogicalArrayLen(frameBase, fs);
            baseKey = frameBase;
            return;
        }
        if (frameBinds) return;
        if (!string.IsNullOrEmpty(currentInlinePrefix))
        {
            string key = currentInlinePrefix + name;
            if (arraySizes.TryGetValue(key, out int s)) { size = LogicalArrayLen(key, s); baseKey = key; }
        }
        if (size < 0 && !string.IsNullOrEmpty(currentFunction))
        {
            string key = currentFunction + "." + name;
            if (arraySizes.TryGetValue(key, out int s)) { size = LogicalArrayLen(key, s); baseKey = key; }
        }
        if (size < 0 && arraySizes.TryGetValue(name, out int s2)) { size = LogicalArrayLen(name, s2); baseKey = name; }
        if (size < 0)
        {
            int s3 = ResolveAliasedArraySize(name, out var b3);
            if (s3 > 0) { size = s3; baseKey = b3; }
        }
    }

    // Unrolls `for v in <compile-time array>` over `base__0` .. `base__(size-1)`. The elements
    // are scalars, ZCA instances, or an SRAM-resident array read with an indexed load.
    private void EmitSequenceUnroll(ForStmt stmt, string forBase, int forSize,
        Expression seqExpr)
    {
        // The unroll policy: when the elements live in storage a run-time subscript
        // can read, a sequence past the trip cap -- or a body the policy rejects --
        // walks them as the counter loop `for __u in range(n): v = seq[__u]` instead
        // of multiplying the body. Elements with no storage form (ZCA instances
        // bound per element) still unroll.
        if (HasSubscriptableStorage(forBase) && !HasInstanceElements(forBase)
            && !LoopVarNeedsConst(stmt)
            && (forSize > ConstSequenceUnrollLimit || !UnrolledLoopBodyIsCheap(stmt.Body)))
        {
            EmitIndexedCounterLoop(stmt, seqExpr, 0, forSize, 1);
            return;
        }

        // Qualify the loop variable the same way ResolveBinding does for a bare name,
        // so the loop body's references (e.g. a `pin.direction = ...` property setter)
        // resolve to the same key the loop binds -- including the currentFunction prefix
        // when iterating inside a def. Without this, ZCA per-element state registered on
        // the loop var is invisible to the body inside a function.
        string forVarKey = QualifyLoopVar(stmt.VarName);
        DataType elemDt2 = arrayElemTypes.TryGetValue(forBase, out var dt3) ? dt3 : DataType.UINT8;
        variableTypes[forVarKey] = LoopVarStorageType(forVarKey, elemDt2);
        // An SRAM-resident array (runtime-indexed or module-level) has no per-element
        // arr__k vars — its elements live in memory and must be read with an indexed
        // load, exactly as the enumerate path does. Without this a `for v in arr` over
        // such an array read 0 from the missing element vars.
        bool forSram = arraysWithVariableIndex.Contains(forBase) || moduleSramArrays.Contains(forBase);

        // Only bracket iterations with labels when the body actually uses break/continue
        // (else keep the plain unroll so constant folding is not split by labels).
        bool forBrk = LoopBodyHasBreakOrContinue(stmt.Body);
        string forBreakLabel = forBrk ? MakeLabel() : "";

        for (int fk = 0; fk < forSize; fk++)
        {
            string forContLabel = forBrk ? MakeLabel() : "";
            if (forBrk)
                loopStack.Add(new LoopLabels { ContinueLabel = forContLabel, BreakLabel = forBreakLabel, FinallyDepth = finallyStack.Count });

            string elemKey2 = forBase + "__" + fk;
            bool isZca = instanceClasses.ContainsKey(elemKey2) ||
                         instanceClasses.Keys.Any(x => x.StartsWith(elemKey2 + "."));
            if (forSram)
            {
                Temporary tmp = MakeTemp(elemDt2);
                Emit(new ArrayLoad(forBase, new Constant(fk), tmp, elemDt2, forSize));
                Emit(new Copy(tmp, new Variable(forVarKey, elemDt2)));
            }
            else if (isZca)
            {
                // A hoisted `(a, b)` aliases `__ctseqN__k` to the NAMED instance, so the
                // loop variable must name `a` -- copying fields wrote a discarded slot
                // and `pin.direction = 1` left the original at 0. An array element that
                // IS the instance (`leds__0` from a list comp) still needs the field
                // copies: its pin id lives in a run-time slot the setter reads.
                if (variableAliases.ContainsKey(elemKey2))
                    BindLoopVarToInstance(elemKey2, forVarKey);
                else
                    BindInstanceForIteration(elemKey2, forVarKey);
            }
            else if (constantVariables.TryGetValue(elemKey2, out int cv2))
                constantVariables[forVarKey] = cv2;
            else
                Emit(new Copy(new Variable(elemKey2, elemDt2), new Variable(forVarKey, elemDt2)));

            VisitStatement(stmt.Body);
            _seqTerminated = false;

            if (forBrk)
            {
                loopStack.RemoveAt(loopStack.Count - 1);
                Emit(new Label(forContLabel));   // continue lands here: end of this iteration
            }
            CleanCtState(forVarKey);
            constantVariables.Remove(forVarKey);
        }
        if (forBrk) Emit(new Label(forBreakLabel));
    }

    /// <summary>
    /// The elements of a name bound to a short all-constant list/tuple, following aliases the
    /// way the parameter lookup does. Null when the name is not such a binding.
    /// </summary>
    /// <summary>
    /// The elements a `range(...)` or a `reversed(...)` stands for, when every bound folds and
    /// the receiver is a sequence the compiler can already see (PyMCU#363). Null when it is
    /// neither, or when a bound is only known at run time.
    ///
    /// This is what lets a range be given a NAME. `range()` is not a value on this target and
    /// stays refused as one; a name whose every use is `for x in name` or `reversed(name)` is
    /// not a value either, it is a compile-time sequence, which the compiler already has a
    /// representation for.
    /// </summary>
    private List<Expression>? ConstSequenceFromRange(Expression value)
    {
        if (value is not CallExpr call || call.Callee is not VariableExpr callee) return null;

        if (callee.Name == "reversed" && call.Args.Count == 1)
        {
            var inner = call.Args[0] switch
            {
                VariableExpr ve => ResolveConstSequence(ve.Name),
                ListExpr le => le.Elements,
                var other => ConstSequenceFromRange(other),
            };
            if (inner == null) return null;
            var flipped = new List<Expression>(inner);
            flipped.Reverse();
            return flipped;
        }

        if (callee.Name != "range" || call.Args.Count is < 1 or > 3) return null;

        var bounds = new List<int>();
        foreach (var a in call.Args)
        {
            if (!TryRangeBound(a, out int b)) return null;
            bounds.Add(b);
        }
        int start = call.Args.Count == 1 ? 0 : bounds[0];
        int stop = call.Args.Count == 1 ? bounds[0] : bounds[1];
        int step = call.Args.Count == 3 ? bounds[2] : 1;
        if (step == 0) return null;

        var elems = new List<Expression>();
        for (int i = start; step > 0 ? i < stop : i > stop; i += step)
        {
            elems.Add(new IntegerLiteral(i) { Line = value.Line });
            // A range whose element count runs away is not a sequence anyone meant to unroll;
            // the caller's limit rejects it, and this keeps the expansion bounded meanwhile.
            if (elems.Count > ConstSequenceUnrollLimit) break;
        }
        return elems;
    }

    /// <summary>
    /// One bound of a range that is being given a name, as a compile-time number.
    ///
    /// The cheap evaluator answers for a literal and for a name it is already tracking. A bound
    /// that is a FIELD is the case the register drivers write -- `range(self.register_width, 0,
    /// -1)` -- and it only becomes a number once the instance is folded, which is what lowering
    /// the expression does. Lowering a bound that turns out not to fold can leave a load behind,
    /// which is harmless: the caller then declines the binding and the assignment is refused, so
    /// nothing reaches a backend.
    /// </summary>
    private bool TryRangeBound(Expression e, out int value)
    {
        if (TryEvalConstElement(e, out value)) return true;
        if (VisitExpression(e) is Constant c) { value = c.Value; return true; }
        value = 0;
        return false;
    }

    /// <summary>
    /// The key a name is registered under when it was bound from a `range(...)`, or null. Same
    /// candidate order as ResolveConstSequence, so the two answer about the same binding.
    /// </summary>
    private string? RangeBoundKeyOf(string name)
    {
        string?[] candidates =
        {
            !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name : null,
            !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : null,
            !string.IsNullOrEmpty(currentModulePrefix) ? currentModulePrefix + name : null,
            name,
        };
        foreach (var candidate in candidates)
            if (candidate != null && rangeBoundSequences.Contains(candidate)) return name;
        return null;
    }

    private List<Expression>? ResolveConstSequence(string name)
    {
        // A frame that binds the name owns it: `len(buf)` in `def f(buf: bytes)` answered
        // with the length of a module-level `buf = b"..."` (ShadowingFrameKey). The alias
        // walk below still follows what the frame's binding is bound to.
        string? frameKey = ShadowingFrameKey(name);
        var candidates = frameKey != null ? new List<string?> { frameKey } : new List<string?>
        {
            !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name : null,
            !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : null,
            !string.IsNullOrEmpty(currentModulePrefix) ? currentModulePrefix + name : null,
            name,
        };

        // A name written bare inside an imported module's function means that module's
        // global, which the lowering files under the synthesized `__module_init`
        // (`mod___module_init.MODES`) -- a spelling none of the scope prefixes above
        // produces. Only the module(s) the current context belongs to are probed: another
        // module's global of the same name is not visible here.
        if (frameKey == null)
            foreach (var mp in OwningModulePrefixes())
                candidates.Add(mp + "__module_init." + name);

        foreach (var candidate in candidates)
        {
            if (candidate == null) continue;
            string? key = candidate;
            for (int depth = 0; depth < 20 && key != null; depth++)
            {
                if (constSequenceBindings.TryGetValue(key, out var elements)) return elements;
                // A bare chase terminal can still name a module-level sequence: an
                // inline parameter aliases to the caller's `g1`, while the binding is
                // filed under `main.g1` -- the same split TryResolveArrayStorageKey
                // normalises for arrays. Ask at each hop, not only of the starting
                // name, so `self.f = p` keeps the sequence it was handed.
                if (!key.Contains('.'))
                {
                    if (constSequenceBindings.TryGetValue("main." + key, out var mainElems))
                        return mainElems;
                    foreach (var mp in OwningModulePrefixes())
                        if (constSequenceBindings.TryGetValue(mp + "__module_init." + key, out var modElems))
                            return modElems;
                }
                if (!variableAliases.TryGetValue(key, out key)) break;
            }
        }

        return null;
    }

    // The (start, stop, step) of a plain `for x in range(...)` when all three are known at
    // compile time, whatever shape they were written in, or null when one of them is decided
    // at run time. The trip count is the caller's business.
    //
    // Only a literal or a NAME folded before, so an EXPRESSION that is a constant -- which is
    // what `range(total_us // 60000000)` is after the fold -- was lowered as a run-time counter
    // loop over a 32-bit bound: 1814 bytes on a 380-byte program (PyMCU#326). The evaluator
    // throws on anything whose value is not fixed, which is the answer wanted, and emits
    // nothing, so asking it speculatively costs nothing either.
    private (int Start, int Stop, int Step)? RangeFoldedBounds(ForStmt stmt)
    {
        int? Bound(Expression? e, int whenAbsent)
        {
            if (e == null) return whenAbsent;
            if (e is IntegerLiteral il) return il.Value;
            if (e is UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral n }) return -n.Value;
            // The general constant evaluator knows the unrolled loop variables, the
            // inline-expansion bindings and the module's own constants -- so `WIDTH = 4` then
            // `range(WIDTH)` folds exactly like `range(4)`, and so does `range(WIDTH // 2)`.
            // Asked here with locals in scope, which is what a bound computed into one needs.
            bool savedFold = foldLocalConstants;
            foldLocalConstants = true;
            try { return EvaluateConstantExpr(e); }
            catch { return null; }
            finally { foldLocalConstants = savedFold; }
        }

        if (Bound(stmt.RangeStart, 0) is not { } start) return null;
        if (Bound(stmt.RangeStop, 0) is not { } stop) return null;
        if (Bound(stmt.RangeStep, 1) is not { } step || step == 0) return null;

        return (start, stop, step);
    }

    // The same bounds, but only when the trip count is at most
    // <see cref="ConstSequenceUnrollLimit"/> -- the loops the unroller takes.
    private (int Start, int Stop, int Step)? RangeUnrollBounds(ForStmt stmt)
    {
        if (RangeFoldedBounds(stmt) is not { } b) return null;
        long trips = RangeTripCount(b.Start, b.Stop, b.Step);
        if (trips <= 0 || trips > ConstSequenceUnrollLimit) return null;
        return b;
    }

    // How many values range(start, stop, step) visits, for a non-zero step. Every reading of a
    // range -- the unroller, the comprehension expanders, reversed(range()) -- goes through
    // this one formula so they agree on the length.
    internal static long RangeTripCount(long start, long stop, long step)
        => step > 0
            ? (stop > start ? (stop - start + step - 1) / step : 0)
            : (stop < start ? (start - stop - step - 1) / -step : 0);

    // The program's own annotation for a loop variable, or null when it has none. Same
    // three-key lookup as InferExprType (Core.cs), but "absent" is not "uint8", and a type an
    // earlier range loop over the same name inferred is not a declaration (see
    // rangeInferredCounterKeys).
    private DataType? DeclaredLoopVarType(string bareName)
    {
        foreach (var start in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + bareName,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + bareName,
            bareName,
        })
        {
            if (start == null) continue;
            var key = start;
            for (var i = 0; i < 20; ++i)
            {
                if (rangeInferredCounterKeys.Contains(key)) return null;
                if (variableTypes.TryGetValue(key, out var t)) return IsIntegerType(t) ? t : null;
                if (variableAliases.TryGetValue(key, out var alias)) key = alias;
                else break;
            }
        }
        return null;
    }

    // The key a loop variable is stored under: the same qualification the body uses to read
    // it, which is the general rule for any name a statement binds (QualifyBoundName).
    // `for j in ...` at module level writes the global `j` when one exists; minting `main.j`
    // split the name, so the counter advanced one slot while `pulses[j]` kept reading the
    // untouched global and a `p[j] = ...` store always hit element 0.
    private string QualifyLoopVar(string bareName) => QualifyBoundName(bareName);

    // The type a possibly-global loop variable is stored at.
    private DataType LoopVarStorageType(string key, DataType dt) => BoundNameStorageType(key, dt);

    private (long Lo, long Hi) OperandRange(Val v)
        => v is Constant or Temporary or Variable ? ValRange(v) : RangeOfType(GetValType(v));

    // Every value the counter of range(start, stop, step) holds, including the one it stops
    // on. That last value is start + trips*step, which passes stop by up to |step| - 1: with a
    // unit step the counter never passes stop and the bounds alone decide; otherwise the
    // overshoot is exact when everything is constant, and one extra step wide when a bound is
    // only known at run time. stop is included even when the loop is empty, so both sides of
    // the exit test share one signedness (range(0, -5) compares signed, not by luck).
    private (long Lo, long Hi) CounterValueRange(Val startVal, Val stopVal, Val stepVal)
    {
        var (sLo, sHi) = OperandRange(startVal);
        var (eLo, eHi) = OperandRange(stopVal);
        long lo = Math.Min(sLo, eLo), hi = Math.Max(sHi, eHi);
        if (startVal is Constant sc && stopVal is Constant ec && stepVal is Constant kc && kc.Value != 0)
        {
            long last = sc.Value + RangeTripCount(sc.Value, ec.Value, kc.Value) * kc.Value;
            return (Math.Min(lo, last), Math.Max(hi, last));
        }
        var (kLo, kHi) = OperandRange(stepVal);
        if (kHi > 1) hi = Math.Max(hi, eHi + kHi - 1);
        if (kLo < -1) lo = Math.Min(lo, eLo + kLo + 1);
        return (lo, hi);
    }

    // `for v in reversed(range(a, b, s))` as the range it is: from the last value visited down
    // to a, by -s. Constant bounds give constants; runtime bounds are supported for a unit step
    // (`range(b - 1, a - 1, -1)`), and any other step is refused naming the spelling to use.
    private ForStmt ReversedRangeFor(ForStmt stmt, CallExpr range)
    {
        var args = range.Args;
        if (args.Count is < 1 or > 3) throw UserError("range() takes 1 to 3 arguments", range.Callee);
        Expression a = args.Count >= 2 ? args[0] : new IntegerLiteral(0) { Line = stmt.Line };
        Expression b = args.Count >= 2 ? args[1] : args[0];
        Expression s = args.Count == 3 ? args[2] : new IntegerLiteral(1) { Line = stmt.Line };
        int? Const(Expression e) { try { return EvaluateConstantExpr(e); } catch (Exception) { return null; } }

        ForStmt rewritten;
        if (Const(s) is not { } cs)
            throw UserError("reversed(range()) needs a compile-time constant step", args[2]);
        if (cs == 0) throw UserError("for-in range() step cannot be zero.", args[2]);
        if (Const(a) is { } ca && Const(b) is { } cb)
        {
            long trips = RangeTripCount(ca, cb, cs);
            int last = (int)(ca + (trips - 1) * cs);
            rewritten = trips > 0
                ? new ForStmt(stmt.VarName, new IntegerLiteral(last), new IntegerLiteral(ca - cs),
                              new IntegerLiteral(-cs), stmt.Body)
                : new ForStmt(stmt.VarName, new IntegerLiteral(ca), new IntegerLiteral(ca),
                              new IntegerLiteral(1), stmt.Body);
        }
        else if (cs is 1 or -1)
        {
            Expression Shift(Expression e, int by) =>
                new BinaryExpr(e, by < 0 ? Frontend.BinaryOp.Sub : Frontend.BinaryOp.Add,
                               new IntegerLiteral(Math.Abs(by))) { Line = stmt.Line };
            rewritten = new ForStmt(stmt.VarName, Shift(b, -cs), Shift(a, -cs), new IntegerLiteral(-cs), stmt.Body);
        }
        else
            throw UserError(
                "reversed(range()) with a step other than 1 or -1 needs compile-time constant " +
                "bounds; write the descending range directly, e.g. range(last, start - step, -step)",
                args[2]);
        rewritten.Line = stmt.Line;
        if (loopVarReadAfter.Contains(stmt)) loopVarReadAfter.Add(rewritten);
        return rewritten;
    }

    // `x in range(a, b, s)` as the comparisons it stands for: `a <= x and x < b` (descending:
    // `b < x and x <= a`), and for a step past 1 `(x - a) % s == 0` as well. Operands that
    // are not a name or a literal are bound to a local first, so each is evaluated once.
    private Expression RangeMembershipAst(Expression x, CallExpr range, bool negate, Expression at)
    {
        var args = range.Args;
        if (args.Count is < 1 or > 3) throw UserError("range() takes 1 to 3 arguments", range.Callee);
        Expression a = args.Count >= 2 ? args[0] : new IntegerLiteral(0) { Line = at.Line };
        Expression b = args.Count >= 2 ? args[1] : args[0];
        int step = 1;
        if (args.Count == 3)
        {
            try { step = EvaluateConstantExpr(args[2]); }
            catch (Exception) { throw UserError("'in range(...)' needs a compile-time constant step", args[2]); }
            if (step == 0) throw UserError("range() step cannot be zero.", args[2]);
        }

        Expression Pinned(Expression e)
        {
            if (e is IntegerLiteral or VariableExpr or UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral })
                return e;
            string name = "__in_range_" + (_inRangeCounter++);
            VisitStatement(new AssignStmt(new VariableExpr(name) { Line = at.Line }, e) { Line = at.Line });
            return new VariableExpr(name) { Line = at.Line };
        }
        x = Pinned(x); a = Pinned(a); b = Pinned(b);

        Expression Bin(Expression l, Frontend.BinaryOp op, Expression r) => new BinaryExpr(l, op, r) { Line = at.Line };
        Expression cond = step > 0
            ? Bin(Bin(a, Frontend.BinaryOp.LessEq, x), Frontend.BinaryOp.And, Bin(x, Frontend.BinaryOp.Less, b))
            : Bin(Bin(b, Frontend.BinaryOp.Less, x), Frontend.BinaryOp.And, Bin(x, Frontend.BinaryOp.LessEq, a));
        if (step is not (1 or -1))
            cond = Bin(cond, Frontend.BinaryOp.And,
                Bin(Bin(Bin(x, Frontend.BinaryOp.Sub, a), Frontend.BinaryOp.Mod, new IntegerLiteral(step)),
                    Frontend.BinaryOp.Equal, new IntegerLiteral(0)));
        return negate ? new UnaryExpr(Frontend.UnaryOp.Not, cond) { Line = at.Line } : cond;
    }

    private int _inRangeCounter;

    // The type of a range() counter: the program's annotation when there is one, otherwise the
    // narrowest integer type that holds every value the counter takes. `for i in range(N)` with
    // N up to 255 stays the 8-bit loop it always was; range(300), a uint16 stop variable, a
    // descending range past 127 or a step that overshoots widen exactly as far as they need.
    private DataType RangeCounterType(ForStmt stmt, string varName, Val startVal, Val stopVal, Val stepVal)
    {
        foreach (var (v, node) in new[]
                 {
                     (startVal, stmt.RangeStart ?? stmt.RangeStop!),
                     (stopVal, stmt.RangeStop!),
                     (stepVal, stmt.RangeStep ?? stmt.RangeStop!),
                 })
            if (!IsIntegerType(GetValType(v)))
                throw UserError("range() bounds must be integers", node);

        var (lo, hi) = CounterValueRange(startVal, stopVal, stepVal);
        bool exact = startVal is Constant && stopVal is Constant && stepVal is Constant;
        return ChooseCounterType(stmt, varName, lo, hi, exact);
    }

    // The declared type when the loop variable has one (refused when `exact` constant bounds
    // do not fit it), otherwise the narrowest type for [lo, hi], remembered as inferred.
    private DataType ChooseCounterType(ForStmt stmt, string varName, long lo, long hi, bool exact)
    {
        DataType inferred = NarrowestTypeFor(lo, hi);
        if (DeclaredLoopVarType(stmt.VarName) is { } declared)
        {
            var (dLo, dHi) = RangeOfType(declared);
            if (exact && (lo < dLo || hi > dHi))
                throw UserError(
                    $"loop variable '{stmt.VarName}' is declared {declared.ToString().ToLowerInvariant()} " +
                    $"but range(...) reaches {(hi > dHi ? hi : lo)}; declare it " +
                    $"{inferred.ToString().ToLowerInvariant()} or drop the annotation", stmt.RangeStop!);
            return declared;
        }
        rangeInferredCounterKeys.Add(varName);
        return inferred;
    }

    private void EmitUnrolledIteration(Statement body, string breakLabel)
    {
        if (breakLabel.Length == 0) { VisitStatement(body); return; }

        // A `continue` or a `break` gives this iteration MORE THAN ONE WAY IN AND OUT, and a
        // local is only known-constant between its assignment and the next write ON EVERY PATH
        // THAT REACHES THE READ. An unrolled body with either of those has paths the linear
        // lowering did not walk: control arrives at the next iteration from a `continue` in the
        // middle of this one, carrying whatever that path left. Folding from the linear state
        // then answers with a value no run of the program holds -- measured on
        // fixtures/continue-break, whose two accumulators came out 100, 100 where the program
        // computes 70, 30.
        //
        // An unrolled body WITHOUT them keeps its folds, which is where the win is: every
        // iteration is lowered on its own and its state is exactly what reaches it.
        InvalidateConstantsAssignedIn(body);

        string cont = MakeLabel();
        loopStack.Add(new LoopLabels { ContinueLabel = cont, BreakLabel = breakLabel, FinallyDepth = finallyStack.Count });
        VisitStatement(body);
        loopStack.RemoveAt(loopStack.Count - 1);
        Emit(new Label(cont));
        // Each unrolled iteration is a fresh sequence: a `continue`/`break` that
        // terminated this body's tail must not dead-skip the next iteration.
        _seqTerminated = false;
    }

    private void VisitFor(ForStmt stmt)
    {
        // The loop rebinds its variable, so whatever the name held before the loop stops being
        // true inside it. The unroller puts a value back per iteration; a run-time loop does
        // not, and must not answer with the one from before.
        ForgetLocalConstant(stmt.VarName);
        if (!string.IsNullOrEmpty(stmt.Var2Name)) ForgetLocalConstant(stmt.Var2Name);

        // The loop variable (and enumerate's index) is a binding even when no type is filed for
        // it -- a range loop and a runtime-bounded slice both bind a name that never enters
        // variableTypes. Recorded before any lowering decision, because `for i in range(...)`
        // carries no Iterable at all and would otherwise miss the shape below.
        {
            boundNames.Add(QualifyLoopVar(stmt.VarName));
            if (!string.IsNullOrEmpty(stmt.Var2Name))
                boundNames.Add(QualifyLoopVar(stmt.Var2Name));
        }

        if (stmt.Iterable != null)
        {
            var iter = stmt.Iterable;
            // Qualify like the body resolves variable references (func-scoped names get the
            // `func.` prefix when not inline-expanded), so a constant the unrolled loop binds to
            // the loop variable is found when the body reads it. The inline-only prefix left a
            // top-level loop variable bare while the body read "func.<name>".
            string varKey = QualifyLoopVar(stmt.VarName);

            // A generator expression is not an iterable this dispatch can lower -- there is
            // no iterator for `for` to draw from. The five reductions unwrap theirs before
            // `for` is ever asked, so say where the construct does work.
            if (iter is GeneratorExpr)
                throw UserError(GenExpWhere + " -- as a `for` iterable it would have to be "
                    + "a value, and it is not one.", iter);

            // A compile-time 2-D grid iterates ROWS: `for row in g` binds a row
            // view per iteration (lowered to a row-index loop), and
            // `for x in g[y]` / `for x in r` walk one row's elements.
            if (iter is IndexExpr forRowIx && ResolveGridKey(forRowIx.Target) != null)
            {
                if (forRowIx.Index is SliceExpr)
                    throw UserError(
                        "a 2-D grid cannot be sliced -- g[a:b] would have to be a window " +
                        "of rows, and rows are views, not values. Write the loop.",
                        forRowIx.Index);
                if (forRowIx.Index is TupleExpr)
                    throw UserError(TwoIndexSubscriptRefusal, forRowIx.Index);
                EmitGridRowElemLoop(stmt, forRowIx.Index, ResolveGridKey(forRowIx.Target)!);
                return;
            }
            if (iter is VariableExpr forRowVe && ResolveRowRef(forRowVe) is { } forRowRef)
            {
                // The block scan already approved this loop over `r`; the
                // element loads it synthesizes (`x = r[ci]`) are new nodes that
                // were never in Uses, so the per-node gate lifts for the body.
                var savedUses = forRowRef.Uses;
                forRowRef.Uses = null;
                try
                {
                    EmitIndexedCounterLoop(stmt, iter, 0, gridDims[forRowRef.GridKey].W, 1);
                }
                finally
                {
                    forRowRef.Uses = savedUses;
                }
                return;
            }
            if (ResolveGridKey(iter) is { } forGridKey)
            {
                EmitGridRowLoop(stmt, forGridKey);
                return;
            }

            string? GetStr(Expression e)
            {
                if (e is StringLiteral lit) return lit.Value;
                if (e is not VariableExpr varE) return null;
                var key = currentInlinePrefix + varE.Name;
                for (var depth = 0; depth < 20; depth++)
                {
                    if (key != null && strConstantVariables.TryGetValue(key, out var s)) return s;
                    if (key != null && variableAliases.TryGetValue(key, out var alias)) key = alias;
                    else break;
                }

                return null;
            }

            if (GetStr(iter) is string strOpt)
            {
                // Short strings unroll (each char a compile-time constant) — smallest code
                // and preserves bodies that need a constant loop variable. Longer strings
                // -- or a body the unroll policy rejects -- emit a RUNTIME loop that reads
                // each byte from a flash table, so the body is generated ONCE instead of N
                // times. This keeps idiomatic `for c in s` from exploding when the body is
                // heavy (e.g. an I2C/SPI write per char).
                if ((strOpt.Length > StringForLoopUnrollLimit || !UnrolledLoopBodyIsCheap(stmt.Body))
                    && !LoopVarNeedsConst(stmt))
                {
                    string sFlash = InternStringAsFlash(strOpt);
                    var sCharVar = new Variable(varKey, DataType.UINT8);
                    var sIdxVar = new Variable(varKey + "__si", DataType.UINT8);

                    constantVariables.Remove(varKey);
                    variableTypes[varKey] = DataType.UINT8;

                    Emit(new Copy(new Constant(0), sIdxVar));
                    string sStart = MakeLabel();
                    string sCont = MakeLabel();
                    string sEnd = MakeLabel();
                    // A RUN-TIME loop: the body is lowered once and runs many times, so nothing
                    // it can write may be folded from the value it holds on the way in. The
                    // range and while paths have always done this; these two did not, and it
                    // went unnoticed until reads of locals began to fold (#331) -- an
                    // accumulator then read its starting value on every pass and
                    // `for v in x: total = total + v` answered 0.
                    var sStrBefore = new Dictionary<string, string?>(strConstantVariables);
                    var sLoopSnap = TakeBranchState();
                    InvalidateConstantsAssignedIn(stmt.Body);

                    // continue advances the index then re-tests (else the loop spins on one char).
                    loopStack.Add(new LoopLabels { ContinueLabel = sCont, BreakLabel = sEnd, FinallyDepth = finallyStack.Count });

                    Emit(new Label(sStart));
                    Emit(new JumpIfGreaterOrEqual(sIdxVar, new Constant(strOpt.Length), sEnd));
                    Emit(new ArrayLoadFlash(sFlash, sIdxVar, sCharVar));

                    VisitStatement(stmt.Body);

                    Emit(new Label(sCont));
                    Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, sIdxVar, new Constant(1)));
                    Emit(new Jump(sStart));
                    Emit(new Label(sEnd));
                    loopStack.RemoveAt(loopStack.Count - 1);
                    JoinLoopState(sLoopSnap, TakeBranchState(), sStrBefore);
                    return;
                }

                string strBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (char c in strOpt)
                {
                    constantVariables[varKey] = (int)c;
                    // The loop variable IS a one-character string in Python: recording its
                    // text is what lets `char in ":;"` (adafruit_ht16k33's _push) answer
                    // as the substring test it is, rather than an integer membership
                    // question a string literal cannot take.
                    strConstantVariables[varKey] = c.ToString();
                    EmitUnrolledIteration(stmt.Body, strBrk);
                }
                if (strBrk.Length > 0) Emit(new Label(strBrk));

                constantVariables.Remove(varKey);
                strConstantVariables.Remove(varKey);
                return;
            }

            // RFC 0008: `for name in os.listdir(dir)` unrolls over the embedded-file
            // table -- each iteration binds the loop variable to one file's name as a
            // compile-time string, which is what `open(name)` inside the body reads.
            if (iter is CallExpr lsCall && IsOsFsCall(lsCall, "listdir"))
            {
                string lsBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (var el in OsListdirExprs(lsCall))
                {
                    // Bind the TEXT, not the interned id: TryFoldConstElement would file
                    // the name's string id into constantVariables and `print(name)` would
                    // emit the number.
                    if (el is StringLiteral lsName)
                    {
                        strConstantVariables[varKey] = lsName.Value;
                        constantVariables.Remove(varKey);
                        floatConstantVariables.Remove(varKey);
                        EmitUnrolledIteration(stmt.Body, lsBrk);
                        strConstantVariables.Remove(varKey);
                    }
                }
                if (lsBrk.Length > 0) Emit(new Label(lsBrk));
                return;
            }

            // A parameter bound to a bytes/list literal argument (e.g. the `buf` of
            // uart.write(b"Hi")) iterates exactly like a direct list literal.
            ListExpr? GetListParam(Expression e)
            {
                if (e is not VariableExpr varE) return null;
                return ResolveListLiteralParam(varE.Name);
            }

            if (GetListParam(iter) is ListExpr boundList)
            {
                // The caller's literal has no local name to veto on -- the elements
                // were just read, so the materialised table holds what they are now.
                if (TryConstSeqCounterLoop(stmt, boundList.Elements, "")) return;
                string lpBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (var elem in boundList.Elements)
                {
                    if (elem is IntegerLiteral il)
                    {
                        constantVariables[varKey] = il.Value;
                        EmitUnrolledIteration(stmt.Body, lpBrk);
                    }
                    else if (BindUnrolledElement(varKey, elem))
                    {
                        EmitUnrolledIteration(stmt.Body, lpBrk);
                        constSequenceBindings.Remove(varKey);
                        strConstantVariables.Remove(varKey);
                        constantVariables.Remove(varKey);
                        floatConstantVariables.Remove(varKey);
                    }
                    // Deliberately unlocated. `elem` is the CALLER's literal, reached by
                    // resolving the parameter, while this diagnostic is reported against the
                    // callee's file and line. ASTNode carries no file, so pointing at the
                    // element would state main.py's column against helper.py's line: a location
                    // that does not exist. Measured, not assumed. Same rule at the two other
                    // resolved-by-name sites below (the dict/set walk and enumerate()).
                    else throw UserError("for-in list iterable elements must be compile-time integer constants.");
                }
                if (lpBrk.Length > 0) Emit(new Label(lpBrk));

                constantVariables.Remove(varKey);
                return;
            }

            // The same unrolling when the sequence was bound to a name first. `for p in pins:`
            // is the shape every "declare the pins, then walk them" program has, and without
            // this it fell through to a run-time loop whose variable is not a constant -- so
            // Pin(p) rejected it while `for p in (11, 12, 13):` compiled.
            if (iter is VariableExpr seqVar && ResolveConstSequence(seqVar.Name) is { } boundSeq)
            {
                if (TryConstSeqCounterLoop(stmt, boundSeq, seqVar.Name)) return;
                string sqBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (var elem in boundSeq)
                {
                    if (TryEvalConstElement(elem, out int sv))
                    {
                        constantVariables[varKey] = sv;
                        EmitUnrolledIteration(stmt.Body, sqBrk);
                    }
                    // Strings, instances and nested sequences bind the same way the
                    // parameter-literal branch above binds them: `for a in alarms`
                    // over `*alarms` carries the call site's elements here.
                    else if (BindUnrolledElement(varKey, elem))
                    {
                        EmitUnrolledIteration(stmt.Body, sqBrk);
                        constSequenceBindings.Remove(varKey);
                        strConstantVariables.Remove(varKey);
                        constantVariables.Remove(varKey);
                        floatConstantVariables.Remove(varKey);
                    }
                    else throw UserError("for-in over a named sequence needs compile-time integer elements.");
                }
                if (sqBrk.Length > 0) Emit(new Label(sqBrk));

                constantVariables.Remove(varKey);
                return;
            }

            // `for v in self._levels:` — the same unroll when the sequence of NUMBERS was stored
            // in a field. Runs after the instance-sequence path above, so a field holding pins
            // binds instances and a field holding numbers binds constants.
            if (iter is MemberAccessExpr && ResolveConstSequenceExpr(iter) is { } memConstSeq)
            {
                if (TryConstSeqCounterLoop(stmt, memConstSeq, ((MemberAccessExpr)iter).Member))
                    return;
                string memBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (var elem in memConstSeq)
                {
                    if (!TryEvalConstElement(elem, out int mv))
                        throw UserError("for-in over a sequence held in a field needs compile-time integer elements.");
                    constantVariables[varKey] = mv;
                    EmitUnrolledIteration(stmt.Body, memBrk);
                }
                if (memBrk.Length > 0) Emit(new Label(memBrk));

                constantVariables.Remove(varKey);
                return;
            }

            if (iter is ListExpr or TupleExpr)
            {
                var elems = iter is ListExpr le ? le.Elements : ((TupleExpr)iter).Elements;
                string llBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";

                // `for pin in (reset_dio, enable_dio, ...)`: each element is an already-built
                // ZCA instance (adafruit_character_lcd). The same hoist a list argument already
                // uses, then the same unroll `for p in self._pins` already uses. A pair unpack
                // stays on the path below -- those elements are pairs, not instances.
                if (string.IsNullOrEmpty(stmt.Var2Name))
                {
                    var asList = iter as ListExpr ?? new ListExpr(elems) { Line = iter.Line };
                    if (IsInstanceSequenceLiteral(asList))
                    {
                        string seqBase = HoistInstanceSequence(asList);
                        EmitSequenceUnroll(stmt, seqBase, elems.Count, asList);
                        return;
                    }
                }

                // All-number elements have a storage form the counter loop can read;
                // pairs and instances do not, and unpack/keep unrolling as before.
                if (TryConstSeqCounterLoop(stmt, elems, "")) return;

                // `for a, b in [(1, 2), (3, 4)]`. The unrolling is the same one the single-target
                // form does; what the two-name form needs is the second key bound alongside the
                // first, from the element's second component. Qualified the same way varKey is,
                // so the body finds it under whatever name it reads.
                string? varKey2 = string.IsNullOrEmpty(stmt.Var2Name) ? null
                    : QualifyLoopVar(stmt.Var2Name);

                foreach (var elem in elems)
                {
                    if (varKey2 != null)
                    {
                        // Refuse by naming what the element IS and how many names it carries,
                        // rather than repeating "must be constants" at a program whose elements
                        // are all constants.
                        if (elem is not (ListExpr or TupleExpr))
                            throw UserError(
                                $"'for {stmt.VarName}, {stmt.Var2Name} in ...' unpacks two names from each " +
                                "element, so every element has to be a pair like (1, 2). This one is not a " +
                                $"pair, so there is nothing to give {stmt.Var2Name}.", elem);

                        var parts = elem is ListExpr pl ? pl.Elements : ((TupleExpr)elem).Elements;
                        if (parts.Count != 2)
                            throw UserError(
                                $"'for {stmt.VarName}, {stmt.Var2Name} in ...' unpacks two names, and this " +
                                $"element has {parts.Count}. Every element has to carry exactly two values.",
                                elem);

                        // Both are evaluated before the check so the diagnostic can point at
                        // WHICH of the two is not a constant. Short-circuiting the || would
                        // leave the second unevaluated and the caret with nothing to choose
                        // between.
                        bool pairOk0 = BindUnrolledElement(varKey, parts[0]);
                        bool pairOk1 = BindUnrolledElement(varKey2, parts[1]);
                        if (!pairOk0 || !pairOk1)
                            throw UserError(
                                "for-in over a list of pairs unrolls at compile time, so both values in " +
                                "each pair have to be constants -- a number, or a string such as a board " +
                                "pin name. Read the run-time value inside the body instead.",
                                pairOk0 ? parts[1] : parts[0]);

                        EmitUnrolledIteration(stmt.Body, llBrk);
                        continue;
                    }

                    if (BindUnrolledElement(varKey, elem))
                    {
                        EmitUnrolledIteration(stmt.Body, llBrk);
                    }
                    // A tuple element with a single loop name is the shape that used to be
                    // reported as a non-constant element while every element was a constant.
                    // What is missing is a name to unpack it into, which is what it says now.
                    else if (elem is ListExpr or TupleExpr)
                        throw UserError(
                            $"each element here is a pair, and '{stmt.VarName}' is one name, so there is " +
                            $"nowhere to put the second value. Write 'for {stmt.VarName}, second in ...' to " +
                            "unpack both.", elem);
                    else throw UserError(
                        "for-in list/tuple iterable elements must be compile-time constants -- a number, "
                        + "a string such as a board pin name, or a name bound to an instance.", elem);
                }
                if (llBrk.Length > 0) Emit(new Label(llBrk));

                constantVariables.Remove(varKey);
                strConstantVariables.Remove(varKey);
                variableAliases.Remove(varKey);
                instanceClasses.Remove(varKey);
                if (varKey2 != null)
                {
                    constantVariables.Remove(varKey2);
                    strConstantVariables.Remove(varKey2);
                    variableAliases.Remove(varKey2);
                    instanceClasses.Remove(varKey2);
                }
                return;
            }

            // Iterating a dict or a set. Both bind a compile-time table and no storage, so
            // there is no run-time sequence to walk -- and none is needed: the entries are
            // known here, so the loop unrolls over them exactly as a constant list literal
            // already does, and the loop variable is a compile-time constant inside the body
            // the same way (issue #200). `d[k]` in the body then folds to that entry's value.
            //
            // The order is the order written. For a dict that is CPython's order too; for a
            // set CPython iterates in hash order, and a compile-time membership table has no
            // hashes, so source order is the only answer that is stable across builds.
            {
                // The element as the program wrote it, for a message that shows the list to
                // write instead. Anything else is shown as `...`: a wrong rendering in advice
                // is worse than an honest gap in it.
                static string SourceOfElement(Expression e) => e switch
                {
                    IntegerLiteral il => il.Value.ToString(),
                    StringLiteral sl2 => "\"" + sl2.Value + "\"",
                    BooleanLiteral bl2 => bl2.Value ? "True" : "False",
                    UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral ng } => "-" + ng.Value,
                    _ => "...",
                };

                DictExpr? DictOf(Expression e) =>
                    e as DictExpr
                    ?? (e is VariableExpr dv && TryGetDictBinding(dv.Name, out var bd) ? bd : null);
                SetExpr? SetOf(Expression e) =>
                    e as SetExpr
                    ?? (e is VariableExpr sv && TryGetSetBinding(sv.Name, out var bs) ? bs : null);

                List<Expression>? dsElems = null;                       // one name per element
                List<(Expression Key, Expression Value)>? dsPairs = null;  // two, from items()
                string dsWhat = "";

                // True when the entries came from a literal written at the `for`. A name
                // resolved to a binding may have been written in another module, and a node
                // carries no file, so blaming one of those entries would put this file's line
                // against that file's column. See the note at the list-parameter site above.
                bool dsWrittenHere = iter is DictExpr or SetExpr;

                if (DictOf(iter) is { } dIter)
                {
                    dsElems = dIter.Entries.Select(en => en.Key).ToList();
                    dsWhat = "dict";
                }
                else if (SetOf(iter) is { } sIter)
                {
                    // Not unrolled, and the order is why. CPython walks a set in hash-table
                    // slot order: {30, 5} gives 5 then 30, and {70, 7} gives 70 then 7, which
                    // is neither the order written nor sorted. A compile-time membership table
                    // has no hashes and no table, so any order chosen here would silently
                    // disagree with the host on some literal, which is worse than not walking
                    // it at all. A dict IS unrolled, because its order is insertion order and
                    // that is exactly what the source says.
                    throw UserError(
                        $"a set is not iterated here: CPython walks it in hash order, and a "
                        + "compile-time membership table has no hashes to reproduce it with, so "
                        + "the order would differ from the one your program prints on the host. "
                        + $"Write the values as a list to walk them in order (`for {stmt.VarName} "
                        + $"in [{string.Join(", ", sIter.Elements.Take(3).Select(SourceOfElement))}"
                        + (sIter.Elements.Count > 3 ? ", ..." : "") + "]`), or keep the set and "
                        + $"ask it what it holds (`{stmt.VarName} in ...`), which is what it is for.",
                        iter);
                }
                else if (iter is CallExpr { Callee: MemberAccessExpr dsMa } dsCall
                         && dsCall.Args.Count == 0 && DictOf(dsMa.Object) is { } mDict)
                {
                    switch (dsMa.Member)
                    {
                        case "keys":
                            dsElems = mDict.Entries.Select(en => en.Key).ToList();
                            dsWhat = "dict";
                            break;
                        case "values":
                            dsElems = mDict.Entries.Select(en => en.Value).ToList();
                            dsWhat = "dict";
                            break;
                        case "items":
                            dsPairs = mDict.Entries.Select(en => (en.Key, en.Value)).ToList();
                            dsWhat = "dict";
                            break;
                    }
                }

                if (dsElems != null || dsPairs != null)
                {
                    string dsWhich = dsPairs != null ? "items()" : dsWhat;
                    string? dsKey2 = string.IsNullOrEmpty(stmt.Var2Name) ? null
                        : QualifyLoopVar(stmt.Var2Name);

                    // Binds one entry to one loop name. A string key or value is bound as a
                    // string constant, which is what makes `d[k]` fold for the string-keyed
                    // dict this reads best on.
                    void BindOne(string key, Expression e, string role)
                    {
                        if (TryEvalConstElement(e, out int iv)) { constantVariables[key] = iv; return; }
                        if (e is StringLiteral sl)
                        {
                            strConstantVariables[key] = sl.Value;
                            // A one-character string is its own character code in expression
                            // position, and an interned id when read back through a name. The
                            // unrolled name has to be indistinguishable from the literal it
                            // stands for, so it is bound in both maps, which is the state the
                            // read path already expects for such a name.
                            if (sl.Value.Length == 1) constantVariables[key] = sl.Value[0];
                            return;
                        }
                        throw UserError(
                            $"a {dsWhat} is iterated by unrolling it at compile time, so every {role} has "
                            + $"to be a constant, and this one is not. Read the run-time value inside the "
                            + "body instead.",
                            dsWrittenHere ? e : null);
                    }
                    void Unbind(string key)
                    {
                        constantVariables.Remove(key);
                        strConstantVariables.Remove(key);
                    }

                    if (dsPairs != null && dsKey2 == null)
                        throw UserError(
                            $"'{stmt.VarName}' is one name and items() gives a key and a value, so there "
                            + $"is nowhere to put the value. Write 'for {stmt.VarName}, value in ...', or "
                            + "iterate the dict itself for the keys alone.");
                    if (dsPairs == null && dsKey2 != null)
                        throw UserError(
                            $"iterating a {dsWhich} gives one value at a time, and this unpacks two names. "
                            + (dsWhat == "dict"
                                ? "Write 'for k, v in d.items()' for both, or one name for the keys."
                                : "Write one name."));

                    string dsBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                    if (dsPairs != null)
                        foreach (var (kE, vE) in dsPairs)
                        {
                            BindOne(varKey, kE, "key");
                            BindOne(dsKey2!, vE, "value");
                            EmitUnrolledIteration(stmt.Body, dsBrk);
                        }
                    else
                        foreach (var elem in dsElems!)
                        {
                            BindOne(varKey, elem, dsWhat == "dict" ? "key" : "element");
                            EmitUnrolledIteration(stmt.Body, dsBrk);
                        }
                    if (dsBrk.Length > 0) Emit(new Label(dsBrk));

                    Unbind(varKey);
                    if (dsKey2 != null) Unbind(dsKey2);
                    return;
                }
            }

            // `for chunk in s.split(sep)`: a compile-time string split into compile-time
            // strings, unrolled one iteration per chunk (adafruit_framebuf's text() does
            // `for chunk in string.split("\n")` on its `string` parameter). There is no
            // runtime list to hold the pieces -- the chunks exist only as the loop
            // variable's per-iteration binding.
            if (iter is CallExpr { Callee: MemberAccessExpr { Member: "split" } } forSplit)
            {
                var chunks = CompileTimeSplit(forSplit, iter);
                string spBrk = LoopBodyHasBreakOrContinue(stmt.Body) ? MakeLabel() : "";
                foreach (var chunk in chunks)
                {
                    BindUnrolledString(varKey, chunk);
                    EmitUnrolledIteration(stmt.Body, spBrk);
                }
                if (spBrk.Length > 0) Emit(new Label(spBrk));
                UnbindUnrolledVar(varKey);
                return;
            }

            if (iter is CallExpr call && call.Callee is VariableExpr calleeVar)
            {
                // `for i in range(*t)` reaches here as a call node, not through
                // EmitBuiltinCall -- splice the compile-time sequence the same way, so the
                // iterable below sees the elements the call would have been written with.
                if (call.Args.Any(a => a is StarArgExpr or DoubleStarArgExpr))
                {
                    call = new CallExpr(call.Callee, SpliceVariadicArgs(call.Args)) { Line = call.Line };
                    iter = call;
                }

                // The same check the expression dispatch runs. enumerate(), zip() and range()
                // reach the compiler as the ITERABLE of a `for` and never pass through
                // EmitBuiltinCall, so without this line their keywords fall through to the
                // lowerings below and are reported as something about the argument.
                call = CheckBuiltinKeywords(call, calleeVar.Name);
                iter = call;

                // A generator expression nested inside enumerate()/zip()/reversed() is the
                // same refusal as the value-position one -- the unrolling happens only at
                // the five reductions, where the iterable's length is the question asked.
                if (call.Args.Any(a => a is GeneratorExpr))
                    throw UserError(GenExpWhere + ".",
                        call.Args.First(a => a is GeneratorExpr));

                if (calleeVar.Name == "range")
                {
                    // `for i in range(*t)` is the same loop `for i in range(a, b, s)`
                    // lowers -- hand it the spliced arguments and let the canonical
                    // range path apply the unroll policy: short and cheap unrolls,
                    // anything else runs as the counter loop. Before this it unrolled
                    // unconditionally, with no trip cap and no break/continue labels.
                    if (call.Args.Count is < 1 or > 3)
                        throw UserError("range() takes 1 to 3 arguments", call.Callee);
                    var starRange = new ForStmt(stmt.VarName,
                        call.Args.Count >= 2 ? call.Args[0] : null,
                        call.Args.Count >= 2 ? call.Args[1] : call.Args[0],
                        call.Args.Count == 3 ? call.Args[2] : null,
                        stmt.Body) { Line = stmt.Line };
                    if (loopVarReadAfter.Contains(stmt)) loopVarReadAfter.Add(starRange);
                    VisitFor(starRange);
                    return;
                }
                else if (calleeVar.Name == "enumerate" && !string.IsNullOrEmpty(stmt.Var2Name) && call.Args.Count == 1)
                {
                    string idxKey = currentInlinePrefix + stmt.VarName;
                    string valKey = currentInlinePrefix + stmt.Var2Name;
                    Expression inner = call.Args[0];
                    int idx = 0;

                    // enumerate() over a 2-D grid iterates (index, row): the
                    // index is an ordinary counter and the row a view pinned to
                    // it -- the same lowering `for row in g` gets.
                    if (ResolveGridKey(inner) is { } enumGridKey)
                    {
                        EmitGridRowLoop(stmt, enumGridKey);
                        return;
                    }

                    // enumerate() over a list [..] / tuple (..) literal, or an inline
                    // parameter bound to such a literal, of compile-time constants.
                    Expression enumInner = inner;
                    if (enumInner is VariableExpr epv && ResolveListLiteralParam(epv.Name) is ListExpr eBound)
                        enumInner = eBound;
                    // Only the literal spelling was written at this `for`. The other two the
                    // switch below accepts reach a list through a NAME -- an @inline parameter
                    // bound to the caller's literal, or a module-level sequence -- and those
                    // elements belong to whichever file wrote them. See the note at the
                    // list-parameter site above.
                    bool enumWrittenHere = inner is ListExpr or TupleExpr;
                    var seqElems = enumInner switch
                    {
                        ListExpr le  => le.Elements,
                        TupleExpr te => te.Elements,
                        // A name bound to a short constant sequence enumerates like the literal
                        // it stands for; `for i, p in enumerate(pins)` is the same program as
                        // enumerating the list written at the call.
                        VariableExpr ev2 => ResolveConstSequence(ev2.Name),
                        _ => null,
                    };
                    if (seqElems != null)
                    {
                        // The unroll policy applies here too: a body it rejects walks
                        // the values as a counter loop over the elements materialised
                        // into a flash table, the same read `tab[i]` performs.
                        if (!UnrolledLoopBodyIsCheap(stmt.Body)
                            && !LoopVarNeedsConst(stmt)
                            && ConstValuesOf(seqElems) is { } enumVals
                            && TryMaterialiseConstTableFromValues(
                                "__enum_" + (++sliceLoopId), "", enumVals) is { } enumTab)
                        {
                            EmitIndexedCounterLoop(stmt, new VariableExpr(enumTab),
                                0, enumVals.Count, 1);
                            return;
                        }
                        foreach (var elem in seqElems)
                        {
                            // TryEvalElemConst, not the literal-only TryEvalConstElement:
                            // `bytes([register & 0xFF])` with `register` a folded parameter
                            // IS a compile-time element -- the general evaluator folds what
                            // the literal switch cannot see.
                            if (TryEvalElemConst(elem, out int ev))
                            {
                                constantVariables[idxKey] = idx++;
                                constantVariables[valKey] = ev;
                                VisitStatement(stmt.Body);
                            }
                            else
                                throw UserError(
                                    "enumerate() list/tuple elements must be compile-time integer constants.",
                                    enumWrittenHere ? elem : null);
                        }

                        constantVariables.Remove(idxKey);
                        constantVariables.Remove(valKey);
                        return;
                    }

                    // enumerate() over a run-time heap list: the loop IS a counter
                    // loop -- `for i, v in enumerate(lst)` is `for i in range(len(lst)):
                    // v = lst[i]` with the count read from the object header, the same
                    // lowering `for v in lst` runs plus the index name alongside.
                    if (inner is VariableExpr lstIterVe
                        && ResolveListVarQualified(lstIterVe.Name) is { Length: > 0 } lstIterQ)
                    {
                        DataType lstElemDt = listVarElemTypes[lstIterQ];
                        if (lstElemDt == DataType.UNKNOWN)
                            throw UserError(
                                $"cannot infer the element type of '{lstIterVe.Name}' yet; " +
                                "its first append must precede iteration, or declare it " +
                                "like `x: list[uint8] = []`", stmt);
                        Variable lstIterPtr = new Variable(lstIterQ, DataType.GC_REF);

                        string lstIdxQ = QualifyLoopVar(stmt.VarName);
                        string lstValQ = QualifyLoopVar(stmt.Var2Name);
                        DataType lstIdxDt = LoopVarStorageType(lstIdxQ, DataType.UINT8);
                        DataType lstValDt = LoopVarStorageType(lstValQ, lstElemDt);
                        Variable lstIdxVar = new Variable(lstIdxQ, lstIdxDt);
                        Variable lstValVar = new Variable(lstValQ, lstValDt);
                        variableTypes[lstIdxQ] = lstIdxDt;
                        variableTypes[lstValQ] = lstValDt;
                        // `for _, pb in enumerate(bins)` on a list[list[T]]: the value
                        // var is an inner list (GC_REF) -- file its element type so
                        // `pb[0]` resolves inside the body.
                        if (lstElemDt == DataType.GC_REF
                            && listInnerElemTypes.TryGetValue(lstIterQ, out var lstInnerElem))
                            listVarElemTypes[lstValQ] = lstInnerElem;

                        // A run-time loop: nothing the body writes may fold from the
                        // value it held on the way in, same rule `for v in lst` keeps.
                        constantVariables.Remove(idxKey);
                        constantVariables.Remove(valKey);
                        var lstStrBefore = new Dictionary<string, string?>(strConstantVariables);
                        var lstLoopSnap = TakeBranchState();
                        InvalidateConstantsAssignedIn(stmt.Body);

                        Temporary lstLen = MakeTemp(DataType.UINT8);
                        Emit(new LoadIndirect(lstIterPtr, lstLen));
                        Emit(new Copy(new Constant(0), lstIdxVar));

                        string lstLoopStart = MakeLabel();
                        string lstLoopCont = MakeLabel();
                        string lstLoopEnd = MakeLabel();
                        loopStack.Add(new LoopLabels { ContinueLabel = lstLoopCont,
                            BreakLabel = lstLoopEnd, FinallyDepth = finallyStack.Count });

                        Emit(new Label(lstLoopStart));
                        Temporary lstCmp = MakeTemp(DataType.UINT8);
                        Emit(new Binary(PyMCU.IR.BinaryOp.GreaterEqual, lstIdxVar, lstLen, lstCmp));
                        Emit(new JumpIfNotZero(lstCmp, lstLoopEnd));

                        Temporary lstElemAddr = EmitElemAddr(lstIterPtr, lstIdxVar, lstElemDt.SizeOf());
                        Temporary lstElemTmp = MakeTemp(lstElemDt);
                        Emit(new LoadIndirect(lstElemAddr, lstElemTmp, lstElemDt));
                        Emit(new Copy(lstElemTmp, lstValVar));

                        VisitStatement(stmt.Body);

                        Emit(new Label(lstLoopCont));
                        Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, lstIdxVar, new Constant(1)));
                        Emit(new Jump(lstLoopStart));
                        Emit(new Label(lstLoopEnd));
                        loopStack.RemoveAt(loopStack.Count - 1);
                        JoinLoopState(lstLoopSnap, TakeBranchState(), lstStrBefore);
                        return;
                    }

                    // enumerate() over a compile-time sequence of INSTANCES held in a field:
                    // `for i, _ in enumerate(self.i2c_device)` (the HT16K33 shape -- one
                    // wrapped I2CDevice, or a list literal of them). The elements live as
                    // `base__k` instance keys, so the value var binds to each element slot
                    // exactly as `for x in self.seq` already does, and the index is the
                    // ordinary constant the counter loop above also produces.
                    if (inner is MemberAccessExpr
                        && TryResolveInstanceSequence(inner, out var enSeqBase, out int enSeqN))
                    {
                        string enQVal = QualifyLoopVar(stmt.Var2Name);
                        bool enSeqBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                        string enSeqBreakLabel = enSeqBrk ? MakeLabel() : "";
                        for (int k = 0; k < enSeqN; ++k)
                        {
                            string enSeqCont = enSeqBrk ? MakeLabel() : "";
                            if (enSeqBrk)
                                loopStack.Add(new LoopLabels { ContinueLabel = enSeqCont,
                                    BreakLabel = enSeqBreakLabel, FinallyDepth = finallyStack.Count });
                            constantVariables[idxKey] = k;
                            BindInstanceForIteration(enSeqBase + "__" + k, enQVal);
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                            if (enSeqBrk) { loopStack.RemoveAt(loopStack.Count - 1); Emit(new Label(enSeqCont)); }
                            CleanCtState(enQVal);
                            constantVariables.Remove(enQVal);
                        }
                        if (enSeqBrk) Emit(new Label(enSeqBreakLabel));
                        constantVariables.Remove(idxKey);
                        return;
                    }

                    // enumerate() over a compile-time string: (index, char) pairs. At or
                    // below the unroll cap each iteration binds the same one-character
                    // string constant `for c in s` makes; past it the loop runs at run
                    // time over the interned flash copy, the same lowering `for c in s`
                    // itself takes past its own cap -- except the counter is the user's
                    // index variable and `char` is a run-time uint8 char code, which is
                    // what ord(char) and font-table arithmetic consume either way.
                    if (StaticStringOf(inner) is { } enumStr)
                    {
                        if (enumStr.Length > ConstSequenceUnrollLimit)
                        {
                            string enumFlash = InternStringAsFlash(enumStr);
                            string enumIdxQ = QualifyLoopVar(stmt.VarName);
                            string enumValQ = QualifyLoopVar(stmt.Var2Name);
                            DataType enumIdxType = NarrowestTypeFor(0, enumStr.Length);
                            variableTypes[enumIdxQ] = enumIdxType;
                            variableTypes[enumValQ] = DataType.UINT8;
                            // The loop rebinds both names at run time, so a constant or a
                            // text either held on the way in is not what the body sees.
                            constantVariables.Remove(idxKey);
                            constantVariables.Remove(valKey);
                            strConstantVariables.Remove(valKey);
                            constantVariables.Remove(enumIdxQ);
                            constantVariables.Remove(enumValQ);
                            strConstantVariables.Remove(enumValQ);
                            var enumStrIdx = new Variable(enumIdxQ, enumIdxType);
                            var enumStrChar = new Variable(enumValQ, DataType.UINT8);

                            Emit(new Copy(new Constant(0), enumStrIdx));
                            string enumStart = MakeLabel();
                            string enumCont = MakeLabel();
                            string enumEnd = MakeLabel();
                            // A RUN-TIME loop, lowered once and run many times: nothing the
                            // body writes may fold from the value it held on the way in.
                            var enumStrBefore = new Dictionary<string, string?>(strConstantVariables);
                            var enumLoopSnap = TakeBranchState();
                            InvalidateConstantsAssignedIn(stmt.Body);
                            loopStack.Add(new LoopLabels { ContinueLabel = enumCont, BreakLabel = enumEnd, FinallyDepth = finallyStack.Count });
                            Emit(new Label(enumStart));
                            Emit(new JumpIfGreaterOrEqual(enumStrIdx, new Constant(enumStr.Length), enumEnd));
                            Emit(new ArrayLoadFlash(enumFlash, enumStrIdx, enumStrChar));
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                            Emit(new Label(enumCont));
                            Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, enumStrIdx, new Constant(1)));
                            Emit(new Jump(enumStart));
                            Emit(new Label(enumEnd));
                            loopStack.RemoveAt(loopStack.Count - 1);
                            JoinLoopState(enumLoopSnap, TakeBranchState(), enumStrBefore);
                            return;
                        }
                        for (int k = 0; k < enumStr.Length; k++)
                        {
                            constantVariables[idxKey] = k;
                            BindUnrolledString(valKey, enumStr[k].ToString());
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                        }
                        constantVariables.Remove(idxKey);
                        strConstantVariables.Remove(valKey);
                        constantVariables.Remove(valKey);
                        return;
                    }

                    // enumerate() over a compile-time split: the chunks the `for` over
                    // `s.split(sep)` unrolls, with an index next to each.
                    if (inner is CallExpr { Callee: MemberAccessExpr { Member: "split" } } enumSplit)
                    {
                        var eChunks = CompileTimeSplit(enumSplit, inner);
                        for (int k = 0; k < eChunks.Count; k++)
                        {
                            constantVariables[idxKey] = k;
                            BindUnrolledString(valKey, eChunks[k]);
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                        }
                        constantVariables.Remove(idxKey);
                        strConstantVariables.Remove(valKey);
                        constantVariables.Remove(valKey);
                        return;
                    }

                    // enumerate(range(...)) is the range loop itself with an index alongside:
                    // the plain range lowering runs the loop (unrolled when short and constant,
                    // a counter otherwise) and keeps the index for it. Before this only
                    // constant bounds were accepted, and they unrolled with no cap (PyMCU#288).
                    if (inner is CallExpr { Callee: VariableExpr { Name: "range" } } rcall)
                    {
                        var rargs = rcall.Args;
                        if (rargs.Count is < 1 or > 3)
                            throw UserError("range() takes 1 to 3 arguments", rcall.Callee);
                        var plain = new ForStmt(stmt.Var2Name,
                            rargs.Count >= 2 ? rargs[0] : null,
                            rargs.Count >= 2 ? rargs[1] : rargs[0],
                            rargs.Count == 3 ? rargs[2] : null,
                            stmt.Body) { Var2Name = stmt.VarName, Line = stmt.Line };
                        if (loopVarReadAfter.Contains(stmt)) loopVarReadAfter.Add(plain);
                        enumerateIndexFor[plain] = stmt.VarName;
                        VisitFor(plain);
                        return;
                    }

                    if (inner is VariableExpr or MemberAccessExpr)
                    {
                        string @base = "";
                        int arrSize = -1;
                        // A class-level fixed array reached through the receiver --
                        // `for i, b in enumerate(self._BUFFER):` (PyMCU#442).
                        if (inner is MemberAccessExpr enMem
                            && ResolveMemberArrayName(enMem) is { } enArr
                            && arraySizes.TryGetValue(enArr, out int enArrSz))
                        {
                            arrSize = LogicalArrayLen(enArr, enArrSz);
                            @base = enArr;
                        }
                        else if (inner is VariableExpr vE)
                        {
                            ResolveForBase(vE.Name, out @base, out arrSize);
                        }

                        if (arrSize > 0)
                        {
                            DataType elemDt = arrayElemTypes.TryGetValue(@base, out var dt) ? dt : DataType.UINT8;
                            bool useSram = arraysWithVariableIndex.Contains(@base) || moduleSramArrays.Contains(@base);

                            // A subscriptable sequence past the unroll limit -- or a body
                            // the unroll policy rejects -- iterates as a counter loop, the
                            // same rewrite `for b in buf[0:n]` takes:
                            // `for i in range(n): b = arr[i]; <body>`. Its elements are
                            // runtime data either way, so unrolling only multiplies the
                            // body by the array size -- a 513-byte framebuffer write did
                            // not fit in flash where the counter loop is a few instructions.
                            if (HasSubscriptableStorage(@base)
                                && !LoopVarNeedsConst(stmt)
                                && (arrSize > ConstSequenceUnrollLimit
                                    || !UnrolledLoopBodyIsCheap(stmt.Body)))
                            {
                                string rtIdx = QualifyLoopVar(stmt.VarName);
                                variableTypes[rtIdx] = NarrowestTypeFor(0, arrSize);
                                var rtBody = new Block();
                                rtBody.Statements.Add(new AssignStmt(
                                    new VariableExpr(stmt.Var2Name),
                                    new IndexExpr(inner, new VariableExpr(stmt.VarName))));
                                if (stmt.Body is Block enOb) rtBody.Statements.AddRange(enOb.Statements);
                                else rtBody.Statements.Add(stmt.Body);
                                VisitStatement(new ForStmt(stmt.VarName,
                                    new IntegerLiteral(0), new IntegerLiteral(arrSize), null, rtBody));
                                return;
                            }

                            string qualifiedVal = QualifyLoopVar(stmt.Var2Name);
                            variableTypes[qualifiedVal] = LoopVarStorageType(qualifiedVal, elemDt);
                            bool enBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                            string enBreakLabel = enBrk ? MakeLabel() : "";
                            for (int k = 0; k < arrSize; ++k)
                            {
                                string enContLabel = enBrk ? MakeLabel() : "";
                                if (enBrk)
                                    loopStack.Add(new LoopLabels { ContinueLabel = enContLabel, BreakLabel = enBreakLabel, FinallyDepth = finallyStack.Count });
                                constantVariables[idxKey] = k;
                                if (useSram)
                                {
                                    var synIndex = new IntegerLiteral(k);
                                    var synIdxExpr = new IndexExpr(inner, synIndex);
                                    Val elemVal = VisitIndex(synIdxExpr);
                                    var valVar = new Variable(qualifiedVal, elemDt);
                                    Emit(new Copy(elemVal, valVar));
                                }
                                else
                                {
                                    string elemKey = @base + "__" + k;
                                    bool elemIsZca = instanceClasses.ContainsKey(elemKey) ||
                                                     instanceClasses.Keys.Any(x => x.StartsWith(elemKey + "."));
                                    if (elemIsZca)
                                    {
                                        // Bind to the function-qualified value-var name the loop body
                                        // resolves to (qualifiedVal), not the inline-only valKey, so a
                                        // `pin.value = ...` setter inside a def sees the ZCA state.
                                        BindInstanceForIteration(elemKey, qualifiedVal);
                                    }
                                    else if (constantVariables.TryGetValue(elemKey, out int cv))
                                    {
                                        constantVariables[qualifiedVal] = cv;
                                    }
                                    else
                                    {
                                        var srcVar = new Variable(elemKey, elemDt);
                                        var valVar = new Variable(qualifiedVal, elemDt);
                                        Emit(new Copy(srcVar, valVar));
                                    }
                                }

                                VisitStatement(stmt.Body);
                                _seqTerminated = false;
                                if (enBrk) { loopStack.RemoveAt(loopStack.Count - 1); Emit(new Label(enContLabel)); }
                                CleanCtState(qualifiedVal);
                                constantVariables.Remove(qualifiedVal);
                            }
                            if (enBrk) Emit(new Label(enBreakLabel));

                            constantVariables.Remove(idxKey);
                            return;
                        }
                    }

                    throw UserError(
                        "enumerate() argument must be a constant list/tuple literal, a compile-time "
                        + "string, a compile-time s.split(sep), range(N), or a fixed-size array.",
                        ArgAt(call, 0));
                }
                else if (calleeVar.Name == "zip" && !string.IsNullOrEmpty(stmt.Var2Name) && call.Args.Count == 2)
                {
                    string key1 = currentInlinePrefix + stmt.VarName;
                    string key2 = currentInlinePrefix + stmt.Var2Name;
                    Expression arg0 = call.Args[0];
                    Expression arg1 = call.Args[1];

                    // zip(a, b) over two fixed arrays whose elements may be runtime values:
                    // iterate element-wise, binding each loop variable to the element (Copy from
                    // arr__k, or ArrayLoad for SRAM arrays). The all-constant fast paths below
                    // still apply to list literals / function-reference lists.
                    (string Base, int Size, DataType Elem)? ResolveArr(Expression e)
                    {
                        if (e is not VariableExpr ve) return null;
                        ResolveForBase(ve.Name, out var b2, out int sz2);
                        if (b2.Length > 0) return (b2, sz2, arrayElemTypes.TryGetValue(b2, out var dt2) ? dt2 : DataType.UINT8);
                        return null;
                    }

                    // A zip side is anything `for x in ...` already walks: a fixed array by
                    // name, a compile-time sequence of instances held in a FIELD, or a ROW of a
                    // rectangular dict selected at run time. Each answers how many elements it
                    // has and how to bind the loop variable to element k; the unroll below does
                    // not care which it is. Before this, only the first was resolved, so
                    // `zip(self.segments, pattern)` -- the shape every segment driver has -- was
                    // refused as not being a constant array (PyMCU#337).
                    (int Len, Action<string, int> Bind)? ResolveSide(Expression e)
                    {
                        if (e is MemberAccessExpr && TryResolveInstanceSequence(e, out var isb, out int isn))
                            return (isn, (qk, k) => BindInstanceForIteration(isb + "__" + k, qk));

                        // A list of constants reached by name, through a parameter, or through
                        // a field. `zip(self.segments, ROW)` is the same walk with the values
                        // known, and a row selected by a CONSTANT key lands here too.
                        if (ResolveConstSequenceExpr(e) is { Count: > 0 } cseq)
                            return (cseq.Count, (qk, k) =>
                            {
                                if (TryEvalConstElement(cseq[k], out int cv)) constantVariables[qk] = cv;
                                else Emit(new Copy(VisitExpression(cseq[k]), new Variable(qk, DataType.UINT8)));
                                variableTypes[qk] = DataType.UINT8;
                            });

                        if (e is VariableExpr rve && ResolveRowView(rve.Name) is { } rv)
                            return (rv.Width, (qk, k) =>
                            {
                                Val off = EmitRowOffset(rv, k);
                                Val v = EmitFlashArrayRead(rv.Table, off, rv.Width);
                                Emit(new Copy(v, new Variable(qk, DataType.UINT8)));
                                variableTypes[qk] = DataType.UINT8;
                            });

                        if (ResolveArr(e) is not { } arr) return null;
                        bool sram = arraysWithVariableIndex.Contains(arr.Base) || moduleSramArrays.Contains(arr.Base);
                        return (arr.Size, (qk, k) =>
                        {
                            string ek = arr.Base + "__" + k;
                            if (instanceClasses.ContainsKey(ek)) { BindInstanceForIteration(ek, qk); return; }
                            variableTypes[qk] = arr.Elem;
                            if (sram)
                            {
                                Temporary tmp = MakeTemp(arr.Elem);
                                Emit(new ArrayLoad(arr.Base, new Constant(k), tmp, arr.Elem, arr.Size));
                                Emit(new Copy(tmp, new Variable(qk, arr.Elem)));
                            }
                            else if (constantVariables.TryGetValue(ek, out int cv)) constantVariables[qk] = cv;
                            else Emit(new Copy(new Variable(ek, arr.Elem), new Variable(qk, arr.Elem)));
                        });
                    }

                    if (ResolveSide(arg0) is { } side0 && ResolveSide(arg1) is { } side1)
                    {
                        string qk1 = !string.IsNullOrEmpty(currentInlinePrefix)
                            ? key1 : QualifyLoopVar(stmt.VarName);
                        string qk2 = !string.IsNullOrEmpty(currentInlinePrefix)
                            ? key2 : QualifyLoopVar(stmt.Var2Name);
                        int zlen = Math.Min(side0.Len, side1.Len);
                        bool zbrk = LoopBodyHasBreakOrContinue(stmt.Body);
                        string zBreak = zbrk ? MakeLabel() : "";

                        for (int k = 0; k < zlen; ++k)
                        {
                            string zCont = zbrk ? MakeLabel() : "";
                            if (zbrk) loopStack.Add(new LoopLabels { ContinueLabel = zCont, BreakLabel = zBreak, FinallyDepth = finallyStack.Count });
                            side0.Bind(qk1, k);
                            side1.Bind(qk2, k);
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                            if (zbrk) { loopStack.RemoveAt(loopStack.Count - 1); Emit(new Label(zCont)); }
                            CleanCtState(qk1);
                            CleanCtState(qk2);
                            constantVariables.Remove(qk1);
                            constantVariables.Remove(qk2);
                        }
                        if (zbrk) Emit(new Label(zBreak));
                        return;
                    }

                    List<int> CollectInts(Expression e)
                    {
                        if (e is ListExpr le2)
                        {
                            var vals = new List<int>();
                            foreach (var elem in le2.Elements)
                            {
                                if (elem is IntegerLiteral il)
                                {
                                    vals.Add(il.Value);
                                }
                                else
                                {
                                    // Try resolving as a compile-time constant expression
                                    // (e.g. enum member like Priority.IDLE, or BooleanLiteral).
                                    Val resolved = VisitExpression(elem);
                                    if (resolved is Constant rc)
                                        vals.Add(rc.Value);
                                    else
                                        throw UserError(
                                            "zip() list elements must be compile-time integer constants.", elem);
                                }
                            }

                            return vals;
                        }

                        if (e is VariableExpr v)
                        {
                            ResolveForBase(v.Name, out string @base, out int arrSize);

                            if (arrSize > 0)
                            {
                                var vals = new List<int>();
                                for (int k = 0; k < arrSize; ++k)
                                {
                                    string elemKey = @base + "__" + k;
                                    if (constantVariables.TryGetValue(elemKey, out int cv)) vals.Add(cv);
                                    else
                                        // The element has no node of its own: it is a slot of an
                                        // array, and what the reader wrote is the array's name.
                                        throw UserError(
                                            "zip() array elements must be compile-time integer constants.", v);
                                }

                                return vals;
                            }
                        }

                        throw UserError(
                            "zip() arguments must be constant list literals or constant arrays.", e);
                    }

                    // Try to interpret a list expression as a list of function references.
                    // Returns null if any element is not a known function name.
                    List<string>? TryCollectFuncRefs(Expression e)
                    {
                        if (e is not ListExpr le3) return null;
                        var names = new List<string>();
                        foreach (var elem in le3.Elements)
                        {
                            if (elem is VariableExpr ve)
                            {
                                string resolved = ResolveCallee(ve.Name);
                                if (functionParams.ContainsKey(resolved) || functionReturnTypes.ContainsKey(resolved)
                                    || inlineFunctions.ContainsKey(resolved))
                                {
                                    names.Add(resolved);
                                    continue;
                                }
                            }
                            return null;
                        }
                        return names;
                    }

                    List<string>? funcRefs0 = TryCollectFuncRefs(arg0);
                    List<string>? funcRefs1 = TryCollectFuncRefs(arg1);

                    if (funcRefs0 != null)
                    {
                        // First list is function references; second must be integer constants.
                        var vals1 = CollectInts(arg1);
                        int len = Math.Min(funcRefs0.Count, vals1.Count);
                        for (int k = 0; k < len; ++k)
                        {
                            loopFunctionAliases[key1] = funcRefs0[k];
                            constantVariables[key2] = vals1[k];
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                        }
                        loopFunctionAliases.Remove(key1);
                        constantVariables.Remove(key2);
                    }
                    else if (funcRefs1 != null)
                    {
                        // First list is integer constants; second is function references.
                        var vals0 = CollectInts(arg0);
                        int len = Math.Min(vals0.Count, funcRefs1.Count);
                        for (int k = 0; k < len; ++k)
                        {
                            constantVariables[key1] = vals0[k];
                            loopFunctionAliases[key2] = funcRefs1[k];
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                        }
                        constantVariables.Remove(key1);
                        loopFunctionAliases.Remove(key2);
                    }
                    else
                    {
                        // Both lists are integer constants (original behaviour).
                        var vals0 = CollectInts(arg0);
                        var vals1 = CollectInts(arg1);
                        int len = Math.Min(vals0.Count, vals1.Count);
                        for (int k = 0; k < len; ++k)
                        {
                            constantVariables[key1] = vals0[k];
                            constantVariables[key2] = vals1[k];
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                        }

                        constantVariables.Remove(key1);
                        constantVariables.Remove(key2);
                    }
                    return;
                }
                else if (calleeVar.Name == "reversed" && call.Args.Count == 1)
                {
                    string valKey = currentInlinePrefix + stmt.VarName;
                    Expression inner = call.Args[0];

                    // reversed(range(...)) is the same range walked from its last value down,
                    // so it goes through the one range lowering and unrolls, sizes its counter
                    // and keeps its variable exactly like a range written that way (PyMCU#288).
                    if (inner is CallExpr { Callee: VariableExpr { Name: "range" } } rrange)
                    {
                        VisitFor(ReversedRangeFor(stmt, rrange));
                        return;
                    }

                    // `reversed(order)` where the name is bound to a compile-time sequence, which
                    // is what `order = range(...)` binds it to (#363). Without this the message
                    // offered reversed() as a supported form and refused the name it was given.
                    if (inner is VariableExpr rve && ResolveConstSequence(rve.Name) is { } rseq)
                    {
                        // Reversed values materialised in walk order read the same
                        // elements the descending index would.
                        if (TryConstSeqCounterLoop(stmt, rseq, rve.Name, reverse: true)) return;
                        for (int k = rseq.Count - 1; k >= 0; --k)
                        {
                            if (!TryEvalConstElement(rseq[k], out int rv))
                                throw UserError(
                                    $"reversed({rve.Name}): element {k} is not a compile-time constant.",
                                    rseq[k]);
                            constantVariables[valKey] = rv;
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                        }

                        constantVariables.Remove(valKey);
                        return;
                    }

                    if (inner is ListExpr le3)
                    {
                        if (TryConstSeqCounterLoop(stmt, le3.Elements, "", reverse: true)) return;
                        for (int k = le3.Elements.Count - 1; k >= 0; --k)
                        {
                            if (le3.Elements[k] is IntegerLiteral il) constantVariables[valKey] = il.Value;
                            else
                                throw UserError(
                                    "reversed() list elements must be compile-time integer constants.",
                                    le3.Elements[k]);
                            VisitStatement(stmt.Body);
                            _seqTerminated = false;
                        }

                        constantVariables.Remove(valKey);
                        return;
                    }

                    // `reversed(self._gpio)` -- the two forms the bare-name branches cover,
                    // reached through a field: a compile-time sequence bound to the field
                    // (the `self.f = buf` setter write in adafruit_74hc595), or the
                    // member-held fixed array `self.f = bytearray(n)` laid out.
                    if (inner is MemberAccessExpr rMem)
                    {
                        if (ResolveConstSequenceExpr(rMem) is { } rMemSeq)
                        {
                            if (TryConstSeqCounterLoop(stmt, rMemSeq, rMem.Member, reverse: true)) return;
                            for (int k = rMemSeq.Count - 1; k >= 0; --k)
                            {
                                if (!TryEvalConstElement(rMemSeq[k], out int rmv))
                                    throw UserError(
                                        $"reversed(self.{rMem.Member}): element {k} is not a compile-time constant.",
                                        rMemSeq[k]);
                                constantVariables[valKey] = rmv;
                                VisitStatement(stmt.Body);
                            }
                            constantVariables.Remove(valKey);
                            return;
                        }

                        if (ResolveMemberArrayName(rMem) is { } rMemArr
                            && arraySizes.TryGetValue(rMemArr, out int rMemSz) && rMemSz > 0)
                        {
                            if (HasSubscriptableStorage(rMemArr) && !HasInstanceElements(rMemArr)
                                && !LoopVarNeedsConst(stmt)
                                && (rMemSz > ConstSequenceUnrollLimit
                                    || !UnrolledLoopBodyIsCheap(stmt.Body)))
                            {
                                EmitIndexedCounterLoop(stmt, rMem, rMemSz - 1, -1, -1);
                                return;
                            }
                            DataType rMemDt = arrayElemTypes.TryGetValue(rMemArr, out var rmdt) ? rmdt : DataType.UINT8;
                            bool rmUseSram = arraysWithVariableIndex.Contains(rMemArr) || moduleSramArrays.Contains(rMemArr);
                            variableTypes[valKey] = rMemDt;
                            bool rmBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                            string rmBreakLabel = rmBrk ? MakeLabel() : "";
                            for (int k = rMemSz - 1; k >= 0; --k)
                            {
                                string rmContLabel = rmBrk ? MakeLabel() : "";
                                if (rmBrk)
                                    loopStack.Add(new LoopLabels { ContinueLabel = rmContLabel, BreakLabel = rmBreakLabel, FinallyDepth = finallyStack.Count });
                                string rmElemKey = rMemArr + "__" + k;
                                if (rmUseSram)
                                {
                                    Val rmElemVal = VisitIndex(new IndexExpr(rMem, new IntegerLiteral(k)));
                                    Emit(new Copy(rmElemVal, new Variable(valKey, rMemDt)));
                                }
                                else if (constantVariables.TryGetValue(rmElemKey, out int rmCv))
                                    constantVariables[valKey] = rmCv;
                                else
                                    Emit(new Copy(new Variable(rmElemKey, rMemDt), new Variable(valKey, rMemDt)));
                                VisitStatement(stmt.Body);
                                if (rmBrk) { loopStack.RemoveAt(loopStack.Count - 1); Emit(new Label(rmContLabel)); }
                                CleanCtState(valKey);
                                constantVariables.Remove(valKey);
                            }
                            if (rmBrk) Emit(new Label(rmBreakLabel));
                            return;
                        }
                    }

                    if (inner is VariableExpr v)
                    {
                        ResolveForBase(v.Name, out string @base, out int arrSize);

                        if (arrSize > 0)
                        {
                            // The unroll policy: a subscriptable array the policy declines
                            // walks descending as `for __u in range(n-1, -1, -1)` -- the
                            // same elements the unroll would have read backwards.
                            if (HasSubscriptableStorage(@base) && !HasInstanceElements(@base)
                                && !LoopVarNeedsConst(stmt)
                                && (arrSize > ConstSequenceUnrollLimit
                                    || !UnrolledLoopBodyIsCheap(stmt.Body)))
                            {
                                EmitIndexedCounterLoop(stmt, new VariableExpr(v.Name),
                                    arrSize - 1, -1, -1);
                                return;
                            }

                            DataType elemDt = arrayElemTypes.TryGetValue(@base, out var edt) ? edt : DataType.UINT8;
                            // Use the fully-qualified key so the optimizer's copy-propagation
                            // maps "main.v" correctly when the body resolves the loop variable.
                            string qValKey = !string.IsNullOrEmpty(currentInlinePrefix)
                                ? valKey
                                : QualifyLoopVar(stmt.VarName);
                            variableTypes[qValKey] = LoopVarStorageType(qValKey, elemDt);
                            bool rvBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                            string rvBreakLabel = rvBrk ? MakeLabel() : "";
                            for (int k = arrSize - 1; k >= 0; --k)
                            {
                                string rvContLabel = rvBrk ? MakeLabel() : "";
                                if (rvBrk)
                                    loopStack.Add(new LoopLabels { ContinueLabel = rvContLabel, BreakLabel = rvBreakLabel, FinallyDepth = finallyStack.Count });
                                string elemKey = @base + "__" + k;
                                if (instanceClasses.ContainsKey(elemKey) ||
                                    instanceClasses.Keys.Any(x => x.StartsWith(elemKey + ".")))
                                    BindInstanceForIteration(elemKey, qValKey);
                                else
                                {
                                    // The element is what `v[k]` reads. A copy of the flattened
                                    // `v__k` slot read storage no store writes -- the array lives
                                    // in SRAM -- and every element came out 0 (the forward walk
                                    // already reads through ArrayLoad).
                                    Val rElem = VisitIndex(new IndexExpr(new VariableExpr(v.Name) { Line = v.Line },
                                        new IntegerLiteral(k)) { Line = v.Line });
                                    if (rElem is Constant rc) constantVariables[valKey] = rc.Value;
                                    else Emit(new Copy(rElem, new Variable(qValKey, elemDt)));
                                }
                                VisitStatement(stmt.Body);
                                _seqTerminated = false;
                                if (rvBrk) { loopStack.RemoveAt(loopStack.Count - 1); Emit(new Label(rvContLabel)); }
                                CleanCtState(qValKey);
                                constantVariables.Remove(valKey);
                            }
                            if (rvBrk) Emit(new Label(rvBreakLabel));
                            return;
                        }
                    }

                    throw UserError(
                        "reversed() argument must be a constant list literal or a constant array.", inner);
                }
            }

            // for v in x: where x is a list[T] variable → runtime loop with heap load per iteration
            if (iter is VariableExpr listVarExpr)
            {
                string listQ = ResolveListVarQualified(listVarExpr.Name);
                if (!string.IsNullOrEmpty(listQ))
                {
                    DataType elemDt = listVarElemTypes[listQ];
                    // A promoted `x = []` iterated before its first append has
                    // no element type for the loop variable -- refuse rather
                    // than emit a 0-width load.
                    if (elemDt == DataType.UNKNOWN)
                        throw UserError(
                            $"cannot infer the element type of '{listVarExpr.Name}' yet; " +
                            "its first append must precede iteration, or declare it " +
                            "like `x: list[uint8] = []`", stmt);
                    Variable listPtr = new Variable(listQ, DataType.GC_REF);

                    // load length
                    Temporary listLen = MakeTemp(DataType.UINT8);
                    Emit(new LoadIndirect(listPtr, listLen));

                    // loop index
                    string idxVarName = string.IsNullOrEmpty(currentInlinePrefix)
                        ? (string.IsNullOrEmpty(currentFunction)
                            ? "__list_i" + tempCounter
                            : currentFunction + ".__list_i" + tempCounter)
                        : currentInlinePrefix + "__list_i" + tempCounter;
                    tempCounter++;
                    Variable idxVar = new Variable(idxVarName, DataType.UINT8);
                    variableTypes[idxVarName] = DataType.UINT8;
                    Emit(new Copy(new Constant(0), idxVar));

                    // loop variable (the element)
                    string elemVarName = QualifyLoopVar(stmt.VarName);
                    DataType elemVarDt = LoopVarStorageType(elemVarName, elemDt);
                    Variable elemVar = new Variable(elemVarName, elemVarDt);
                    variableTypes[elemVarName] = elemVarDt;
                    // `for kb in bins` on a list[list[T]]: the loop variable is an
                    // inner list (GC_REF), and kb[i]/len(kb) resolve through the
                    // same tables a declared `kb: list[T]` would file.
                    if (elemDt == DataType.GC_REF
                        && listInnerElemTypes.TryGetValue(listQ, out var loopInnerElem))
                        listVarElemTypes[elemVarName] = loopInnerElem;

                    string loopStart = MakeLabel();
                    string loopCont = MakeLabel();
                    string loopEnd = MakeLabel();
                    // A RUN-TIME loop: the body is lowered once and runs many times, so nothing
                    // it can write may be folded from the value it holds on the way in. The
                    // range and while paths have always done this; these two did not, and it
                    // went unnoticed until reads of locals began to fold (#331) -- an
                    // accumulator then read its starting value on every pass and
                    // `for v in x: total = total + v` answered 0.
                    var listStrBefore = new Dictionary<string, string?>(strConstantVariables);
                    var listLoopSnap = TakeBranchState();
                    InvalidateConstantsAssignedIn(stmt.Body);

                    // continue advances the index then re-tests (else the loop spins on one elem).
                    loopStack.Add(new LoopLabels { ContinueLabel = loopCont, BreakLabel = loopEnd, FinallyDepth = finallyStack.Count });

                    Emit(new Label(loopStart));
                    Temporary cmpTmp = MakeTemp(DataType.UINT8);
                    Emit(new Binary(PyMCU.IR.BinaryOp.GreaterEqual, idxVar, listLen, cmpTmp));
                    Emit(new JumpIfNotZero(cmpTmp, loopEnd));

                    Temporary elemAddr = EmitElemAddr(listPtr, idxVar, elemDt.SizeOf());
                    Temporary elemTmp = MakeTemp(elemDt);
                    Emit(new LoadIndirect(elemAddr, elemTmp, elemDt));
                    Emit(new Copy(elemTmp, elemVar));

                    VisitStatement(stmt.Body);

                    Emit(new Label(loopCont));
                    Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, idxVar, new Constant(1)));
                    Emit(new Jump(loopStart));
                    Emit(new Label(loopEnd));
                    loopStack.RemoveAt(loopStack.Count - 1);
                    JoinLoopState(listLoopSnap, TakeBranchState(), listStrBefore);
                    return;
                }
            }

            // for v in ct_array: — unroll over compile-time array (scalars or ZCA instances).
            // A MemberAccessExpr reaches the same shape through a field: `for p in self._pins:`
            // is how a driver that was handed a list of pins walks them.
            if (iter is VariableExpr or MemberAccessExpr)
            {
                string forBase = "";
                int forSize = -1;
                if (iter is MemberAccessExpr)
                {
                    if (TryResolveInstanceSequence(iter, out var memSeqBase, out int memSeqCount))
                    { forSize = memSeqCount; forBase = memSeqBase; }
                    // A class-level fixed array reached through the receiver --
                    // `for b in self._BUFFER:` (PyMCU#442). Indexed access already resolves
                    // it through the same helper; the iterable only had to ask.
                    else if (ResolveMemberArrayName((MemberAccessExpr)iter) is { } forMemArr
                             && arraySizes.TryGetValue(forMemArr, out int forMemArrSize))
                    { forSize = LogicalArrayLen(forMemArr, forMemArrSize); forBase = forMemArr; }
                }
                if (forSize < 0 && iter is VariableExpr forVarExpr2)
                    ResolveForBase(forVarExpr2.Name, out forBase, out forSize);

                if (forSize > 0)
                {
                    EmitSequenceUnroll(stmt, forBase, forSize, iter);
                    return;
                }
            }

            // for v in arr[lo:hi:step]: — unroll over a fixed-array slice (constant bounds).
            if (iter is IndexExpr { Target: VariableExpr sliceVar, Index: SliceExpr slc })
            {
                ResolveForBase(sliceVar.Name, out string slBase, out int slSize);

                if (slSize > 0)
                {
                    // Runtime bounds (`for b in buf[0:n]` with n known only at runtime): no
                    // allocation is needed to ITERATE a slice, so rewrite to the range loop
                    // the bounds describe -- `for __i in range(lo, hi): v = arr[__i]; <body>`.
                    // The range machinery provides break/continue/else; ScanStmt already
                    // marked the array variable-indexed (any slice subscript does), so the
                    // per-iteration read is a runtime ArrayLoad. Step must still be constant:
                    // a runtime stride has no clear termination against a fixed array.
                    bool slConstBounds = true;
                    try
                    {
                        if (slc.Start != null) EvaluateConstantExpr(slc.Start);
                        if (slc.Stop != null) EvaluateConstantExpr(slc.Stop);
                    }
                    catch (Exception) { slConstBounds = false; }

                    if (!slConstBounds)
                    {
                        if (slc.Step != null)
                            throw UserError(
                                "for-in over a slice with runtime bounds does not take a step; " +
                                "iterate range() with the stride explicitly",
                                slc.Step);
                        string slIdx = "__slci" + (++sliceLoopId);
                        // The index walks a fixed array, so it is as wide as the array's size
                        // needs and no wider: filed as declared, so the range lowering does not
                        // size it from a bound like `n + 1`, which promotes past a byte.
                        variableTypes[QualifyLoopVar(slIdx)] = NarrowestTypeFor(0, slSize);
                        var slBody = new Block();
                        slBody.Statements.Add(new AssignStmt(
                            new VariableExpr(stmt.VarName),
                            new IndexExpr(new VariableExpr(sliceVar.Name), new VariableExpr(slIdx))));
                        if (stmt.Body is Block slOb) slBody.Statements.AddRange(slOb.Statements);
                        else slBody.Statements.Add(stmt.Body);
                        VisitStatement(new ForStmt(slIdx,
                            slc.Start ?? new IntegerLiteral(0),
                            slc.Stop ?? new IntegerLiteral(slSize),
                            null, slBody));
                        return;
                    }

                    int start = slc.Start != null ? EvaluateConstantExpr(slc.Start) : 0;
                    int stop = slc.Stop != null ? EvaluateConstantExpr(slc.Stop) : slSize;
                    int step = slc.Step != null ? EvaluateConstantExpr(slc.Step) : 1;
                    if (step == 0) throw UserError("for-in slice step cannot be zero.", slc.Step);
                    if (start < 0) start += slSize;
                    if (stop < 0) stop += slSize;
                    start = Math.Max(0, Math.Min(start, slSize));
                    stop = Math.Max(0, Math.Min(stop, slSize));

                    // The unroll policy: constant bounds used to unroll however long
                    // the slice was. A slice the policy declines walks the same indices
                    // as the counter loop the runtime-bounds branch above rewrites to.
                    int slTrips = step > 0
                        ? Math.Max(0, (stop - start + step - 1) / step)
                        : Math.Max(0, (start - stop + (-step) - 1) / -step);
                    if ((slTrips > ConstSequenceUnrollLimit
                            || !UnrolledLoopBodyIsCheap(stmt.Body))
                        && !LoopVarNeedsConst(stmt)
                        && HasSubscriptableStorage(slBase) && !HasInstanceElements(slBase))
                    {
                        EmitIndexedCounterLoop(stmt, new VariableExpr(sliceVar.Name),
                            start, stop, step);
                        return;
                    }

                    DataType slElem = arrayElemTypes.TryGetValue(slBase, out var sdt) ? sdt : DataType.UINT8;
                    bool slSram = arraysWithVariableIndex.Contains(slBase) || moduleSramArrays.Contains(slBase);
                    string slKey = QualifyLoopVar(stmt.VarName);
                    variableTypes[slKey] = LoopVarStorageType(slKey, slElem);

                    bool slBrk = LoopBodyHasBreakOrContinue(stmt.Body);
                    string slBreakLabel = slBrk ? MakeLabel() : "";

                    for (int i = start; step > 0 ? i < stop : i > stop; i += step)
                    {
                        string slContLabel = slBrk ? MakeLabel() : "";
                        if (slBrk)
                            loopStack.Add(new LoopLabels { ContinueLabel = slContLabel, BreakLabel = slBreakLabel, FinallyDepth = finallyStack.Count });

                        string elemKey = slBase + "__" + i;
                        if (slSram)
                        {
                            Temporary tmp = MakeTemp(slElem);
                            Emit(new ArrayLoad(slBase, new Constant(i), tmp, slElem, slSize));
                            Emit(new Copy(tmp, new Variable(slKey, slElem)));
                        }
                        else if (constantVariables.TryGetValue(elemKey, out int cv))
                            constantVariables[slKey] = cv;
                        else
                            Emit(new Copy(new Variable(elemKey, slElem), new Variable(slKey, slElem)));

                        VisitStatement(stmt.Body);
                        _seqTerminated = false;

                        if (slBrk)
                        {
                            loopStack.RemoveAt(loopStack.Count - 1);
                            Emit(new Label(slContLabel));
                        }
                        constantVariables.Remove(slKey);
                    }
                    if (slBrk) Emit(new Label(slBreakLabel));

                    return;
                }
            }

            // for v in lst[lo:hi:step]: a heap-list slice needs no copy to be iterated --
            // rewrite to the index loop the bounds describe,
            // `for __i in range(lo, hi, step): v = lst[__i]; <body>`, and let the range
            // machinery take it from there (runtime bounds included). An omitted stop is
            // len(lst), which lowers through the heap header the same call site uses.
            if (iter is IndexExpr { Target: VariableExpr lstSliceVar, Index: SliceExpr lstSlc }
                && !string.IsNullOrEmpty(ResolveListVarQualified(lstSliceVar.Name)))
            {
                string lstIdx = "__slci" + (++sliceLoopId);
                var lstBody = new Block();
                lstBody.Statements.Add(new AssignStmt(
                    new VariableExpr(stmt.VarName),
                    new IndexExpr(new VariableExpr(lstSliceVar.Name), new VariableExpr(lstIdx))));
                if (stmt.Body is Block lstOb) lstBody.Statements.AddRange(lstOb.Statements);
                else lstBody.Statements.Add(stmt.Body);
                VisitStatement(new ForStmt(lstIdx,
                    lstSlc.Start ?? new IntegerLiteral(0),
                    lstSlc.Stop ?? new CallExpr(new VariableExpr("len"),
                        new List<Expression> { new VariableExpr(lstSliceVar.Name) }),
                    lstSlc.Step, lstBody));
                return;
            }

            // CPython's OLD iteration protocol is __getitem__(0), __getitem__(1), ... until
            // IndexError. PyMCU cannot stop on the exception, for the same reason it cannot run
            // __iter__/__next__ below, but it does not need to: when __len__ is a compile-time
            // constant the trip count is known, so this rewrites to `for __sqi in range(0, N):
            // v = obj[__sqi]` and the range machinery unrolls it into the code the author would
            // have written by hand. Each obj[__sqi] dispatches __getitem__ the way a direct
            // subscript already does.
            if (stmt.Iterable is VariableExpr sqVe
                && TryResolveInstanceMethodAst(sqVe.Name, "__getitem__") != null
                && InstanceClassOfName(sqVe.Name) is { } sqCls
                && DunderConstLen(sqCls) is { } sqLen)
            {
                if (sqLen < 0)
                    throw UserError(
                        $"'{sqVe.Name}' has a negative __len__ ({sqLen}), so the loop has no trip count.",
                        sqVe);
                if (sqLen > 0)
                {
                    string sqIdx = "__sqi" + (++sliceLoopId);
                    var sqBody = new Block();
                    sqBody.Statements.Add(new AssignStmt(
                        new VariableExpr(stmt.VarName),
                        new IndexExpr(new VariableExpr(sqVe.Name), new VariableExpr(sqIdx))));
                    if (stmt.Body is Block sqOb) sqBody.Statements.AddRange(sqOb.Statements);
                    else sqBody.Statements.Add(stmt.Body);
                    VisitStatement(new ForStmt(sqIdx,
                        new IntegerLiteral(0), new IntegerLiteral(sqLen), null, sqBody));
                }
                return;
            }

            // An instance of a class defining __iter__/__next__ is the one shape worth naming
            // separately: it looks like it should work, and the generic list would leave the
            // author guessing why their iterator protocol is ignored.
            if (stmt.Iterable is VariableExpr itVe
                && TryResolveInstanceMethodAst(itVe.Name, "__next__") != null)
                throw UserError(
                    $"'{itVe.Name}' defines __iter__/__next__, but PyMCU does not run the iterator " +
                    "protocol: there is no exception to stop on, so the loop could never end. " +
                    "Write the loop explicitly (`while <cond>: v = obj.next()`), or iterate a " +
                    "range/fixed array instead. A `yield` generator function IS supported.", itVe);

            // `for b in f(...)`: a call whose result is a fixed-size buffer runs ONCE, then
            // the loop walks the returned storage exactly like a named array -- the same
            // spelling with the assignment inlined (adafruit_bmp280's register-read loop).
            if (iter is CallExpr
                && VisitExpression(iter) is Variable callRet
                && TryResolveArrayStorageKey(callRet.Name, out var callBase)
                && arraySizes.TryGetValue(callBase, out int callSize) && callSize > 0)
            {
                EmitSequenceUnroll(stmt, callBase, LogicalArrayLen(callBase, callSize), new VariableExpr(callRet.Name));
                return;
            }

            // __getitem__ without a compile-time __len__: the sequence protocol is the right
            // shape, but the trip count is only known at run time and there is no IndexError to
            // stop on, so unrolling is not available. Name that rather than listing the forms
            // this object is not.
            if (stmt.Iterable is VariableExpr sqBadVe
                && TryResolveInstanceMethodAst(sqBadVe.Name, "__getitem__") != null)
                throw UserError(
                    $"'{sqBadVe.Name}' defines __getitem__, but " +
                    (TryResolveInstanceMethodAst(sqBadVe.Name, "__len__") == null
                        ? "no __len__, so the loop has no trip count"
                        : "its __len__ is not a compile-time constant, so the trip count is only " +
                          "known at run time") +
                    " and PyMCU has no IndexError to stop on. Give the class a __len__ with a " +
                    "constant return (`def __len__(self) -> uint8: return 4`) to iterate it " +
                    $"directly, or write the loop over the length you have (`for i in range(n): " +
                    $"v = {sqBadVe.Name}[i]`).", sqBadVe);

            // `for b in buf` over an arena-allocated bytearray (neopixel_write's frame
            // loop, handed `self._post_brightness_buffer`): the region's byte length is a
            // run-time value, so this is a real counter loop -- `b` is read one byte at a
            // time through arena.read8, not unrolled. The offset is materialised into a
            // local first, for the same reason the indexed read does (PyMCU#415): an
            // inline parameter re-resolved inside read8's own expansion answers a name
            // from the wrong scope.
            if (stmt.Iterable is VariableExpr arenaItVe
                && TryResolveArenaBuffer(arenaItVe.Name, out string arenaItQ)
                && arenaBufferLenVar.TryGetValue(arenaItQ, out string? arenaItLen))
            {
                string arenaItMod = ResolveArenaModuleAlias(stmt.Iterable);
                string offLocal = $"__arena_itr_off_{arenaFieldTempId++}";
                string lenLocal = $"__arena_itr_len_{arenaFieldTempId++}";
                string idxLocal = $"__arena_itr_i_{arenaFieldTempId++}";
                string offQ = currentInlinePrefix + offLocal;
                string lenQ = currentInlinePrefix + lenLocal;

                variableTypes[offQ] = DataType.UINT16;
                Emit(new Copy(VisitExpression(stmt.Iterable), new Variable(offQ, DataType.UINT16)));
                variableTypes[lenQ] = DataType.UINT16;
                Emit(new Copy(new Variable(arenaItLen, DataType.UINT16),
                    new Variable(lenQ, DataType.UINT16)));

                var arenaBody = new Block();
                arenaBody.Statements.Add(new AssignStmt(
                    new VariableExpr(stmt.VarName),
                    new CallExpr(
                        new MemberAccessExpr(new VariableExpr(arenaItMod), "read8"),
                        new List<Expression>
                        {
                            new BinaryExpr(new VariableExpr(offLocal),
                                Frontend.BinaryOp.Add, new VariableExpr(idxLocal))
                        })));
                if (stmt.Body is Block arenaOuter) arenaBody.Statements.AddRange(arenaOuter.Statements);
                else arenaBody.Statements.Add(stmt.Body);
                VisitStatement(new ForStmt(idxLocal,
                    new IntegerLiteral(0), new VariableExpr(lenLocal), null, arenaBody)
                    { Line = stmt.Line });
                return;
            }

            // `for p in self.pin_mapping.analog_pins`: the field holds a class OBJECT --
            // the iterable is a different compile-time tuple per candidate, and a `for`
            // has no tag dispatch to unroll through. With ONE candidate the sequence
            // already resolved above; more than one is refused here by name.
            if (stmt.Iterable is MemberAccessExpr coForOuter
                && coForOuter.Object is MemberAccessExpr coForInner
                && ClassObjectFieldClasses(coForInner) is { } coForCands)
            {
                throw UserError(
                    $"for-in over '{coForOuter.Member}' of class-object field "
                    + $"'{coForInner.Member}': the field can hold "
                    + string.Join(", ", coForCands.Select(ShortClassName))
                    + ", whose tuples differ, and a for-in has no tag dispatch -- "
                    + "test membership (`in`), index (`.index()`) or a constant subscript",
                    stmt.Iterable);
            }

            throw UserError(
                "for-in loop iterable must be a compile-time string constant, a constant list literal [v0, v1, ...], range(N), enumerate(list/range), zip(a, b), reversed(iterable), or a fixed-array slice arr[lo:hi]. Use 'const[str]' type annotation for string parameters.",
                stmt.Iterable);
        }

        // `for p in range(11, 14)` over CONSTANT bounds and a short trip count unrolls, the way
        // a short constant list already does. The parser files the plain form's bounds in
        // RangeStart/Stop/Step and leaves Iterable null, so the unrolling above -- which only
        // ever sees range() as an ITERABLE expression, the shape enumerate() and zip() build --
        // never ran for it. The loop variable therefore never qualified where a compile-time
        // constant is required, and `Pin(p, Pin.OUT)` rejected the range spelling while
        // `pins = [11, 12, 13]` then `for p in pins:` compiled to the same three pins.
        //
        // The trip-count cap is the sequence limit: unrolling copies the body no more times
        // than writing the same values as a list literal already copies it. Past it the loop
        // stays a loop, and a body that needs a constant loop variable says so as before.
        //
        // Emitted before the run-time lowering starts, because the checks below evaluate the
        // bound expressions and that is not free of side effects.
        // A range that is empty at compile time runs nothing. It used to be lowered as a
        // counter loop that immediately exits, which is a comparison, a jump and a counter of
        // whatever width the bound needed -- and the bound itself, recomputed at run time
        // (#326). Python never binds the loop variable for an empty range; the run-time
        // lowering left it at start, so a read after the loop keeps that.
        if (RangeFoldedBounds(stmt) is { } emptyBounds
            && RangeTripCount(emptyBounds.Start, emptyBounds.Stop, emptyBounds.Step) <= 0)
        {
            if (loopVarReadAfter.Contains(stmt))
            {
                string emptyKey = QualifyLoopVar(stmt.VarName);
                DataType emptyType = ChooseCounterType(stmt, emptyKey,
                    emptyBounds.Start, emptyBounds.Start, exact: true);
                variableTypes[emptyKey] = emptyType;
                Emit(new Copy(new Constant(emptyBounds.Start), new Variable(emptyKey, emptyType)));
            }
            return;
        }

        if (RangeUnrollBounds(stmt) is { } unroll
            && (UnrolledLoopBodyIsCheap(stmt.Body) || LoopVarNeedsConst(stmt)))
        {
            (int unrollStart, int unrollStop, int unrollStep) = unroll;
            string unrollKey = QualifyLoopVar(stmt.VarName);
            bool unrollBreaks = LoopBodyHasBreakOrContinue(stmt.Body);
            string unrollBrk = unrollBreaks ? MakeLabel() : "";

            // Python leaves the loop variable at the last value visited. The body reads it as
            // a constant per iteration, but anything after the loop reads a real variable, so
            // the value has to be stored too -- once at the end when the loop always runs to
            // completion, or per iteration when a break decides where it stops. Unread, the
            // stores are dead and go with the rest. Before this the binding was dropped on
            // exit and `for i in range(3): ...` then `print(i)` printed 0.
            int unrollLast = (int)(unrollStart + (RangeTripCount(unrollStart, unrollStop, unrollStep) - 1) * unrollStep);
            DataType unrollType = ChooseCounterType(stmt, unrollKey,
                Math.Min(unrollStart, unrollLast), Math.Max(unrollStart, unrollLast), exact: true);
            variableTypes[unrollKey] = unrollType;
            var unrollVar = new Variable(unrollKey, unrollType);
            bool unrollReadAfter = loopVarReadAfter.Contains(stmt);

            // enumerate(range(...)): the index walks 0, 1, 2 ... next to the value.
            string? unrollIdxKey = null;
            Variable? unrollIdxVar = null;
            long unrollTrips = RangeTripCount(unrollStart, unrollStop, unrollStep);
            if (enumerateIndexFor.TryGetValue(stmt, out var unrollIdxName))
            {
                unrollIdxKey = QualifyLoopVar(unrollIdxName);
                var idxType = NarrowestTypeFor(0, unrollTrips);
                variableTypes[unrollIdxKey] = idxType;
                unrollIdxVar = new Variable(unrollIdxKey, idxType);
            }

            int unrollIdx = 0;
            for (int i = unrollStart; unrollStep > 0 ? i < unrollStop : i > unrollStop; i += unrollStep, unrollIdx++)
            {
                constantVariables[unrollKey] = i;
                if (unrollBreaks && unrollReadAfter) Emit(new Copy(new Constant(i), unrollVar));
                if (unrollIdxKey != null)
                {
                    constantVariables[unrollIdxKey] = unrollIdx;
                    if (unrollBreaks && unrollReadAfter) Emit(new Copy(new Constant(unrollIdx), unrollIdxVar!));
                }
                EmitUnrolledIteration(stmt.Body, unrollBrk);
            }
            if (unrollBrk.Length > 0) Emit(new Label(unrollBrk));

            constantVariables.Remove(unrollKey);
            if (unrollIdxKey != null) constantVariables.Remove(unrollIdxKey);
            if (!unrollBreaks && unrollReadAfter)
            {
                Emit(new Copy(new Constant(unrollLast), unrollVar));
                if (unrollIdxKey != null) Emit(new Copy(new Constant((int)unrollTrips - 1), unrollIdxVar!));
            }
            return;
        }

        Val startVal = stmt.RangeStart != null ? VisitExpression(stmt.RangeStart) : new Constant(0);
        Val stopVal = VisitExpression(stmt.RangeStop!);
        Val stepVal = stmt.RangeStep != null ? VisitExpression(stmt.RangeStep) : new Constant(1);

        // A zero step never advances the loop variable (Python raises ValueError). The
        // compile-time-unrolled path above already rejects this; mirror it for the runtime
        // loop, where a literal-zero step would otherwise emit an infinite loop.
        if (stepVal is Constant stepZero && stepZero.Value == 0)
            throw UserError("for-in range() step cannot be zero.", stmt.RangeStep);

        // Qualify the loop variable the same way the body resolves a variable reference:
        // function-scoped names get the `func.` prefix when not inline-expanded. Using the
        // inline-only prefix left a top-level loop variable bare ("i") while the body read it
        // as "func.i", so the counter and the body's reads were different registers — using `i`
        // in the body read 0 (e.g. `for i in range(n): acc += i` produced 0).
        string varName = QualifyLoopVar(stmt.VarName);
        // The counter was an unconditional UINT8 here, whatever the bounds said: range(300) ran
        // 44 times, range(0, 256) never ran, a descending range from 200 never ran, and a
        // uint16 stop variable or a uint16 annotation on the loop variable were both ignored.
        // Filed in variableTypes before the body is visited, so the body's reads and the
        // storage allocator see the same width the loop compares and steps.
        DataType counterType = LoopVarStorageType(varName,
            RangeCounterType(stmt, varName, startVal, stopVal, stepVal));
        constantVariables.Remove(varName);
        variableTypes[varName] = counterType;
        var loopVar = new Variable(varName, counterType);
        Emit(new Copy(startVal, loopVar));
        // The exit fix-up below compares the counter with where it started; a start that is
        // not a constant is kept in a temporary of its own, since the body may rewrite the
        // variable it was read from.
        bool readAfter = loopVarReadAfter.Contains(stmt);

        // enumerate(range(...)): an index that starts at 0 and steps by 1 with the counter.
        Variable? enumIdxVar = null;
        if (enumerateIndexFor.TryGetValue(stmt, out var idxName))
        {
            string idxKey = QualifyLoopVar(idxName);
            var (cLo, cHi) = CounterValueRange(startVal, stopVal, stepVal);
            var idxType = LoopVarStorageType(idxKey, NarrowestTypeFor(0, cHi - cLo + 1));
            variableTypes[idxKey] = idxType;
            enumIdxVar = new Variable(idxKey, idxType);
            Emit(new Copy(new Constant(0), enumIdxVar));
        }

        Val startKeep = startVal;
        if (readAfter && startVal is not Constant)
        {
            var keep = MakeTemp(counterType);
            Emit(new Copy(startVal, keep));
            startKeep = keep;
        }

        string startLabel = MakeLabel();
        string contLabel = MakeLabel();
        string endLabel = MakeLabel();
        // Where the exit test lands. A `break` lands on endLabel with the counter already at
        // the value Python leaves; the exit test lands one step past it, so when the value is
        // read after the loop that path gets a label of its own and a step back (below).
        string exitLabel = readAfter ? MakeLabel() : endLabel;
        // `continue` must run the step before re-testing, otherwise the loop variable never
        // advances and the loop spins forever — so the continue target is the step, not the
        // condition check at the top.
        loopStack.Add(new LoopLabels { ContinueLabel = contLabel, BreakLabel = endLabel, FinallyDepth = finallyStack.Count });

        // Same rule as a `while`: the body is emitted once and runs many times, so nothing it
        // can write may be folded from the value it holds on the way in. Without this an
        // accumulator read its starting value on every pass, and `for i in range(4): total =
        // total + i` came out 3 instead of 6.
        var strBeforeLoop = new Dictionary<string, string?>(strConstantVariables);
        var loopSnap = TakeBranchState();
        InvalidateConstantsAssignedIn(stmt.Body);

        Emit(new Label(startLabel));
        // A negative step counts down, so the loop ends when the variable drops to or below
        // stop (Python's range(hi, lo, -1)); a positive step ends at/above stop. The previous
        // unconditional `>= stop` test made any negative-step runtime range exit immediately.
        if (stepVal is Constant stepC && stepC.Value < 0)
            Emit(new JumpIfLessOrEqual(loopVar, stopVal, exitLabel));
        else if (stepVal is not Constant && GetValType(stepVal).IsSigned())
        {
            // A step held in a signed variable is decided at run time, so the direction of
            // the exit test is too: step < 0 counts down to stop, anything else counts up. An
            // unsigned step cannot be negative and keeps the single compare below. Before
            // this the ascending test was used for every non-constant step, so range(10, 0,
            // step) with step = -2 exited before its first iteration.
            string negLabel = MakeLabel();
            string bodyLabel = MakeLabel();
            Emit(new JumpIfLessThan(stepVal, new Constant(0), negLabel));
            Emit(new JumpIfGreaterOrEqual(loopVar, stopVal, exitLabel));
            Emit(new Jump(bodyLabel));
            Emit(new Label(negLabel));
            Emit(new JumpIfLessOrEqual(loopVar, stopVal, exitLabel));
            Emit(new Label(bodyLabel));
        }
        else
            Emit(new JumpIfGreaterOrEqual(loopVar, stopVal, exitLabel));

        VisitStatement(stmt.Body);

        Emit(new Label(contLabel));
        Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, loopVar, stepVal));
        if (enumIdxVar != null) Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, enumIdxVar, new Constant(1)));
        Emit(new Jump(startLabel));

        // Python leaves the loop variable at the last value visited; the exit test fires one
        // step past it. When something after the loop reads the name, step it back on that
        // path -- unless the loop never ran, in which case the counter still holds `start`
        // and stays there (Python would have left the name as it was). A `break` skips this:
        // it lands on endLabel with the value it broke at. Nothing is emitted for a variable
        // no one reads afterwards, which is the common case and stays free.
        if (readAfter)
        {
            Emit(new Label(exitLabel));
            Emit(new JumpIfEqual(loopVar, startKeep, endLabel));
            Emit(new AugAssign(PyMCU.IR.BinaryOp.Sub, loopVar, stepVal));
            if (enumIdxVar != null) Emit(new AugAssign(PyMCU.IR.BinaryOp.Sub, enumIdxVar, new Constant(1)));
        }
        Emit(new Label(endLabel));
        loopStack.RemoveAt(loopStack.Count - 1);

        // A range whose bounds are decided at run time can run zero times: a name the
        // body rewrote holds either value at the exit, so the merge is the pre-loop
        // state joined with the body's (see JoinLoopState).
        JoinLoopState(loopSnap, TakeBranchState(), strBeforeLoop);
    }

    private int _withManagerCounter;

    private void VisitWith(WithStmt stmt)
    {
        // `with C(x) as v:` -- give the manager a name of its own and carry on as if the
        // program had written `m = C(x)` first. Without this the statement fell through to
        // "just run the body": nothing constructed the manager, nothing bound v, and the
        // program was rejected for using a name that is assigned right there in its header.
        if (stmt.ContextExpr is not VariableExpr && !string.IsNullOrEmpty(stmt.AsName))
        {
            string managerName = "__with_manager_" + (_withManagerCounter++);
            VisitStatement(new AssignStmt(new VariableExpr(managerName), stmt.ContextExpr));
            stmt = new WithStmt(new VariableExpr(managerName), stmt.AsName, stmt.Body);
        }

        if (stmt.ContextExpr is VariableExpr varExpr)
        {
            string objName = varExpr.Name;

            if (!string.IsNullOrEmpty(stmt.AsName))
            {
                // The AS-name is a LOCAL of whichever body the `with` is written in, qualified
                // the same way every other local is: by the inline frame when one is active
                // (a `with` inside a force-inlined method, as bmp280's `with self._i2c as
                // i2c:` inside `_read_register` is), else by the enclosing function, else bare
                // at true module level. `currentFunction` alone missed the inline case: the
                // alias was written under "main.bus" while the call inside the SAME inlined
                // body read "self"'s inline-qualified "bus", so nothing found the alias and
                // the receiver fell to a plain, classless local (#390's method-call half).
                string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + stmt.AsName
                    : (!string.IsNullOrEmpty(currentFunction)
                        ? currentFunction + "." + stmt.AsName
                        : stmt.AsName);
                // NOT `currentFunction + "." + objName`: a module-level `with g as h:` runs
                // inside the synthesized/explicit main as module init, but every LATER
                // reference to `g` resolves it as a module global under its bare name (the
                // same reason SlotInstanceKey exists for construction) -- so aliasing `h` to
                // the qualified "main.g" pointed the alias at a name nothing else ever wrote,
                // and `h.state` read whatever storage happened to exist under it (zero) while
                // `__enter__`'s own body kept writing the field the bare name owns (#390).
                string qualifiedObj = SlotInstanceKey(objName);
                variableAliases[qualified] = qualifiedObj;
            }

            var enterCallee = new MemberAccessExpr(new VariableExpr(objName), "__enter__");
            var enterCall = new CallExpr(enterCallee, new List<Expression>());
            Val entered = VisitExpression(enterCall);

            // `with obj as v`: v is what __enter__ RETURNED. The alias above already covers the
            // usual `return self` (the value IS the instance, and aliasing keeps its class), but
            // a context manager handing back something else -- a value, a different object --
            // would otherwise leave v bound to the manager itself.
            if (!string.IsNullOrEmpty(stmt.AsName))
            {
                // Same qualification as above -- kept in step because this second write is
                // what stays live once the block below decides whether __enter__ returned
                // something else entirely.
                string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + stmt.AsName
                    : (!string.IsNullOrEmpty(currentFunction)
                        ? currentFunction + "." + stmt.AsName
                        : stmt.AsName);
                string qualifiedObj = SlotInstanceKey(objName);
                // A ZCA `__enter__` whose body is `return self` hands back the instance, but the
                // instance has no single runtime value to hand back: the expansion yields a
                // temporary that stands for nothing. Reading the AST settles it -- when the
                // method returns bare self, v IS obj, and the alias must stay. Without this the
                // alias was dropped and v read whatever the temporary happened to hold, which
                // was every field zero.
                bool entersSelf = entered is NoneVal
                                  || (entered is Variable ev && (ev.Name == qualifiedObj || ev.Name == objName))
                                  || (TryResolveInstanceMethodAst(objName, "__enter__") is { } enterDef
                                      && MethodReturnsBareSelf(enterDef));
                if (!entersSelf)
                {
                    // `__enter__` handed back another OBJECT (`return self.spi`, the
                    // bus_device.SPIDevice shape): the bound name must take the returned
                    // instance's storage and class, not a scalar copy of a field that
                    // carries none -- without it `spi.write_readinto(...)` resolves to a
                    // free function nothing emits (#454).
                    string? enteredInst = ResolveClassCarryingName(entered);
                    if (enteredInst != null
                        && instanceClasses.TryGetValue(enteredInst, out var enteredCls)
                        && enteredCls != null)
                    {
                        variableAliases[qualified] = enteredInst;
                        instanceClasses[qualified] = enteredCls;
                    }
                    else
                    {
                        variableAliases.Remove(qualified);
                        DataType et = entered switch
                        {
                            Variable v3 => v3.Type,
                            Temporary t3 => t3.Type,
                            _ => DataType.UINT8,
                        };
                        variableTypes[qualified] = et;
                        Emit(new Copy(entered, new Variable(qualified, et)));
                    }
                }
            }

            // CPython always calls __exit__(exc_type, exc_value, traceback); PyMCU's own HAL
            // declares it as __exit__(self) alone. Pass exactly as many placeholders as the
            // resolved method declares, so both spellings work instead of the CPython one
            // reporting a missing argument.
            var exitArgs = new List<Expression>();
            if (TryResolveInstanceMethodAst(objName, "__exit__") is { } exitDef)
                for (int i = 1; i < exitDef.Params.Count; i++) exitArgs.Add(new IntegerLiteral(0));
            var exitCall = new CallExpr(new MemberAccessExpr(new VariableExpr(objName), "__exit__"), exitArgs);

            // The body can leave early: `return` inside `with` (bmp280's `_read_register`
            // hands back `result` from inside `with self._i2c`) jumps to the expansion's
            // exit label, and `break`/`continue` jump to a loop's. Emitting __exit__ only
            // after the body leaves those paths locked -- i2c_device's `try_lock` stayed
            // held and the next `with` spun forever. Pending it like a `finally` runs it
            // on every escape, innermost first, and the normal path still runs it below.
            finallyStack.Add(new List<Statement> { new ExprStmt(exitCall) });
            VisitStatement(stmt.Body);
            finallyStack.RemoveAt(finallyStack.Count - 1);
            VisitExpression(exitCall);
        }
        else
        {
            VisitStatement(stmt.Body);
        }
    }

    /// <summary>
    /// A condition folded to true or false at compile time, or null when it does not fold.
    ///
    /// Local to `assert` on purpose. EvaluateConstantExpr folds arithmetic and bitwise
    /// operators and nothing else, so a comparison and a `BooleanLiteral` both throw out of
    /// it; teaching IT to fold comparisons would change every caller that asks it for an
    /// array size or an address, which is far wider than an assert needs.
    /// </summary>
    private bool? FoldAssertCondition(Expression? cond)
    {
        switch (cond)
        {
            case null: return null;
            case BooleanLiteral b: return b.Value;

            case UnaryExpr { Op: Frontend.UnaryOp.Not } u:
                return FoldAssertCondition(u.Operand) is { } inner ? !inner : null;

            case BinaryExpr { Op: Frontend.BinaryOp.And } a:
                // Short-circuit: one false operand decides the whole thing even when the
                // other does not fold, which is what makes `assert 0 and f()` answerable.
                if (FoldAssertCondition(a.Left) is false || FoldAssertCondition(a.Right) is false) return false;
                return FoldAssertCondition(a.Left) is true && FoldAssertCondition(a.Right) is true ? true : null;

            case BinaryExpr { Op: Frontend.BinaryOp.Or } o:
                if (FoldAssertCondition(o.Left) is true || FoldAssertCondition(o.Right) is true) return true;
                return FoldAssertCondition(o.Left) is false && FoldAssertCondition(o.Right) is false ? false : null;

            case BinaryExpr cmp when cmp.Op is Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual
                                          or Frontend.BinaryOp.Less or Frontend.BinaryOp.Greater
                                          or Frontend.BinaryOp.LessEq or Frontend.BinaryOp.GreaterEq:
            {
                if (TryEvalElemConst(cmp.Left, out int l) && TryEvalElemConst(cmp.Right, out int r))
                    return cmp.Op switch
                    {
                        Frontend.BinaryOp.Equal     => l == r,
                        Frontend.BinaryOp.NotEqual  => l != r,
                        Frontend.BinaryOp.Less      => l < r,
                        Frontend.BinaryOp.Greater   => l > r,
                        Frontend.BinaryOp.LessEq    => l <= r,
                        _                           => l >= r,
                    };
                return null;
            }

            default:
                return TryEvalElemConst(cond, out int v) ? v != 0 : null;
        }
    }

    // `assert` fired only for the literal `0`. Every other spelling of a false assertion was
    // dropped: this method evaluated the condition and the catch swallowed everything that was
    // not already an AssertionError. EvaluateConstantExpr folds an integer literal, so `0`
    // arrived, but it folds neither a comparison nor a BooleanLiteral, so `assert 1 == 2` and
    // `assert False` both threw, were both swallowed, and lowered to nothing. The one row that
    // agreed with CPython was the one nobody writes.
    //
    // Refusing a statically false assert wherever it stands, rather than only on a path always
    // reached, is the policy `assert 0` already shipped: it was refused inside a run-time `if`
    // and inside an `else` too. Keeping that and making the other spellings match it does mean
    // `assert False` cannot serve as an unreachable-branch marker, and it could not when
    // spelled `assert 0` either. `raise` is the construct that survives to run time.
    //
    // A condition that does NOT fold still emits no code. That is deliberate and measured:
    // `AssertionError` is not a defined name in PyMCU, so a run-time check has nothing to
    // raise, and the nearest equivalent that does compile, `if x != y: raise ValueError(...)`,
    // cost 24 bytes over the same program with no assert. Compiling asserts out is what
    // `python -O` does, so the behaviour is defensible; being silent about it is not, because
    // the reader cannot tell a checked assertion from a dropped one.
    private void VisitAssert(AssertStmt stmt)
    {
        bool? folded = FoldAssertCondition(stmt.Condition);

        if (folded == false)
            throw UserError(
                "AssertionError" + (string.IsNullOrEmpty(stmt.Message) ? "" : ": " + stmt.Message),
                stmt.Condition);

        if (folded == true) return;

        // Line only, no file. UserError(msg, node) takes the node's line and the file of the
        // module being LOWERED, so a name-resolved node yields a plausible cursor naming the
        // wrong file; a warning printed here has no better source of truth, so it does not
        // claim one.
        PyMCU.Common.Diagnostic.Warning(line: stmt.Line, code: "assert-not-checked", text:
            $"assert on line {stmt.Line} is not checked. Its condition is not known at "
            + "compile time, and PyMCU emits no run-time check, the way `python -O` drops "
            + "asserts. Use `if <cond>: raise ...` for a check that survives to run time.");
    }

    /// <summary>True when every return in the method hands back bare `self`.</summary>
    private static bool MethodReturnsBareSelf(FunctionDef method)
    {
        bool sawReturn = false, allSelf = true;
        // The old recursion skipped match bodies: a `return self` in a case was
        // never counted.
        foreach (var st in TypeInference.WalkStatements(method.Body))
            if (st is ReturnStmt r)
            {
                sawReturn = true;
                if (r.Value is not VariableExpr { Name: "self" }) allSelf = false;
            }
        return sawReturn && allSelf;
    }

}