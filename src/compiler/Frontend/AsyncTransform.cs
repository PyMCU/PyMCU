using System;
using System.Collections.Generic;
using System.Linq;
using PyMCU.Common;

namespace PyMCU.Frontend;

// Coroutine -> state-machine desugar (RFC: async/await).
//
// An `async def` is rewritten, at the AST level, into a zero-cost ZCA class with a
// `poll()` method -- the exact pattern users would otherwise hand-write. Each `await`
// is a suspension point: the body is split into numbered states, locals that must
// survive a suspension become instance fields, and `poll()` re-dispatches on
// `self._state` (a flat if-chain; every state transition suspends with PENDING, so
// each poll call advances at least one state -- v1 semantics, which the ZCA slot-method
// compiler handles; a re-dispatching `while True:` poll body currently compiles to an
// empty slot-method body, a separate bug).
//
// poll() returns 1 while the coroutine is still running (PENDING) and 0 when it has
// finished (DONE; a `return expr` stores the result in `self._value` first). Drive it
// from `asyncio.run()` / `asyncio.gather()` or a hand-written cooperative loop.
//
// v2 scope: `await asyncio.sleep(n)` / `sleep_ms(n)` anywhere in the body -- inside
// if/elif/else, `while <cond>`, and `for i in range(...)` (any nesting), with
// break/continue targeting those loops, and `return [expr]` at any point. Locals are
// lifted to fields ONLY when they must survive a state boundary. Awaiting arbitrary
// futures / other coroutines is future work (assigning a fresh ZCA instance to a
// field outside __init__ is not supported by the ZCA machinery yet) -- a clear error
// is raised so the limitation is explicit, never miscompiled.
public static class AsyncTransform
{
    public static void TransformProgram(ProgramNode prog)
    {
        var asyncFns = prog.Functions.Where(f => f.IsAsync).ToList();
        // A plain function containing `yield` is a GENERATOR: same state-machine
        // lowering, with `yield v` as the suspension point (sets self._value, returns
        // PENDING). Generators need no asyncio (no time source involved).
        var genFns = prog.Functions
            .Where(f => !f.IsAsync && !f.IsInline && ContainsYield(f.Body)).ToList();
        if (asyncFns.Count == 0 && genFns.Count == 0) return;

        string? alias = null;
        if (asyncFns.Count > 0)
        {
            // Using `async def` requires `import asyncio` (as in CPython, where the
            // keywords pair with the asyncio runtime). The local name bound to the module
            // is how `await asyncio.sleep(...)` and the generated ticks() are spelled.
            alias = FindAsyncioAlias(prog);
            if (alias == null)
                throw new SyntaxError(
                    "this module uses `async def` but never imports asyncio. Add `import asyncio` " +
                    "(or `import pymcu.asyncio as asyncio`) and await `asyncio.sleep(...)`.", 0, 0);
        }

        foreach (var fn in asyncFns)
        {
            prog.Functions.Remove(fn);
            prog.GlobalStatements.Add(TransformFunction(fn, alias!));
        }
        var genNames = new HashSet<string>();
        foreach (var fn in genFns)
        {
            prog.Functions.Remove(fn);
            prog.GlobalStatements.Add(TransformFunction(fn, asyncioAlias: null));
            genNames.Add(fn.Name);
        }

        // `for x in gen(args):` iterates a generator: desugar to an explicit poll loop
        //     __gen = gen(args)
        //     while __gen.poll() == 1:
        //         x = __gen._value
        //         <body>
        if (genNames.Count > 0)
        {
            int counter = 0;
            foreach (var f in prog.Functions)
                RewriteGenFors(f.Body.Statements, genNames, ref counter);
            RewriteGenFors(prog.GlobalStatements, genNames, ref counter);
        }
    }

    private static void RewriteGenFors(List<Statement> stmts, HashSet<string> genNames, ref int counter)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            if (s is ForStmt f && f.Iterable is CallExpr call
                && call.Callee is VariableExpr fnName && genNames.Contains(fnName.Name))
            {
                string g = "__gen" + counter;
                string r = "__gr" + counter;
                counter++;

                // while True:
                //     __gr = __gen.poll()
                //     if __gr == 0: break          (done)
                //     if __gr != 2: continue       (internal transition, no value)
                //     x = __gen._value
                //     <body>
                // The user's own break/continue inside <body> target this while, which
                // matches Python semantics (break abandons the generator; continue
                // advances to the next yielded value).
                var loop = new Block();
                loop.Statements.Add(new AssignStmt(new VariableExpr(r),
                    new CallExpr(new MemberAccessExpr(new VariableExpr(g), "poll"),
                                 new List<Expression>())));
                var brk = new Block(); brk.Statements.Add(new BreakStmt());
                loop.Statements.Add(new IfStmt(
                    new BinaryExpr(new VariableExpr(r), BinaryOp.Equal, new IntegerLiteral(0)), brk));
                var cont = new Block(); cont.Statements.Add(new ContinueStmt());
                loop.Statements.Add(new IfStmt(
                    new BinaryExpr(new VariableExpr(r), BinaryOp.NotEqual, new IntegerLiteral(2)), cont));
                loop.Statements.Add(new AssignStmt(new VariableExpr(f.VarName),
                    new MemberAccessExpr(new VariableExpr(g), "_value")));
                if (f.Body is Block fb) loop.Statements.AddRange(fb.Statements);
                else loop.Statements.Add(f.Body);
                RewriteGenFors(loop.Statements, genNames, ref counter);

                var repl = new Block();
                repl.Statements.Add(new AssignStmt(new VariableExpr(g), call));
                repl.Statements.Add(new WhileStmt(new BooleanLiteral(true), loop));
                stmts[i] = repl;
                continue;
            }
            // Recurse into nested statements.
            switch (s)
            {
                case Block b: RewriteGenFors(b.Statements, genNames, ref counter); break;
                case IfStmt iff:
                    RewriteGenForsIn(iff.ThenBranch, genNames, ref counter);
                    foreach (var (_, eb) in iff.ElifBranches) RewriteGenForsIn(eb, genNames, ref counter);
                    if (iff.ElseBranch != null) RewriteGenForsIn(iff.ElseBranch, genNames, ref counter);
                    break;
                case WhileStmt w: RewriteGenForsIn(w.Body, genNames, ref counter); break;
                case ForStmt f2: RewriteGenForsIn(f2.Body, genNames, ref counter); break;
            }
        }
    }

    private static void RewriteGenForsIn(Statement s, HashSet<string> genNames, ref int counter)
    {
        if (s is Block b) RewriteGenFors(b.Statements, genNames, ref counter);
    }

    internal static bool ContainsYieldStatic(Statement s) => ContainsYield(s);

    private static bool ContainsYield(Statement s)
    {
        switch (s)
        {
            case ExprStmt es: return es.Expr is YieldExpr;
            case Block b: return b.Statements.Any(ContainsYield);
            case IfStmt iff:
                return ContainsYield(iff.ThenBranch)
                    || (iff.ElseBranch != null && ContainsYield(iff.ElseBranch))
                    || iff.ElifBranches.Any(e => ContainsYield(e.Item2));
            case WhileStmt w: return ContainsYield(w.Body);
            case ForStmt f: return ContainsYield(f.Body);
            default: return false;
        }
    }

    // The local name a module bound to the asyncio module, or null if not imported.
    private static string? FindAsyncioAlias(ProgramNode prog)
    {
        foreach (var imp in prog.Imports)
        {
            // `import asyncio` / `import asyncio as aio` / `import pymcu.asyncio as aio`
            if (imp.ModuleName == "asyncio" || imp.ModuleName == "pymcu.asyncio")
                return string.IsNullOrEmpty(imp.ModuleAlias) ? imp.ModuleName.Split('.')[^1] : imp.ModuleAlias;
            // `from pymcu import asyncio` / `from pymcu import asyncio as aio`
            if ((imp.ModuleName == "pymcu" || imp.ModuleName == "") && imp.Symbols.Contains("asyncio"))
                return imp.Aliases.TryGetValue("asyncio", out var a) ? a : "asyncio";
        }
        return null;
    }

    private sealed class State
    {
        public int Id;
        public List<Statement> Raw = new();   // statements with locals still unrewritten
    }

    public static ClassDef TransformFunction(FunctionDef fn, string? asyncioAlias)
    {
        var paramNames = fn.Params.Select(p => p.Name).ToList();

        // ── Phase A: split the body into states (locals kept raw) ────────────────
        var b = new Builder(asyncioAlias, fn.Name);
        b.Build(fn.Body);

        // ── Phase B: decide which locals must be fields ──────────────────────────
        // A local becomes a field only when it must survive a state boundary: it is
        // touched in more than one state, or its first touch within its single state
        // is a READ (its value flows in from a previous activation / the zero init).
        // Params are always fields (initialized once in __init__).
        var localNames = new List<string>();
        CollectAssignedLocals(fn.Body, new HashSet<string>(paramNames), localNames);

        var touchStates = new Dictionary<string, HashSet<int>>();
        var firstTouchIsRead = new Dictionary<string, bool>();
        var tracked = new HashSet<string>(localNames);
        foreach (var st in b.States)
            foreach (var s in st.Raw)
                TrackTouches(s, st.Id, tracked, touchStates, firstTouchIsRead);

        var fields = new HashSet<string>(paramNames);
        foreach (var l in localNames)
        {
            bool multiState = touchStates.TryGetValue(l, out var ts) && ts.Count > 1;
            bool readFirst = firstTouchIsRead.TryGetValue(l, out var rf) && rf;
            if (multiState || readFirst) fields.Add(l);
        }

        // ── Phase C: rewrite field references to self.<name> ─────────────────────
        var rw = new Rewriter(fields);
        foreach (var st in b.States)
            for (int i = 0; i < st.Raw.Count; i++)
                st.Raw[i] = rw.RewriteStmt(st.Raw[i]);

        // __init__(self, <params>): _state=0, params, field locals, machinery fields.
        var initBody = new Block();
        initBody.Statements.Add(SelfDecl("_state", "uint32", Int(0)));
        foreach (var p in fn.Params)
            initBody.Statements.Add(SelfDecl(p.Name, ScalarType(p.Type), new VariableExpr(p.Name)));
        foreach (var l in localNames)
            if (fields.Contains(l))
                initBody.Statements.Add(SelfDecl(l, "uint32", Int(0)));
        foreach (var sf in b.StartFields)
            initBody.Statements.Add(SelfDecl(sf, "uint32", Int(0)));
        if (b.NeedsValue)
            initBody.Statements.Add(SelfDecl("_value", "uint32", Int(0)));

        var initParams = new List<Param> { new Param("self", "") };
        initParams.AddRange(fn.Params);
        // Constructors are force-inlined (expanded at the construction site), matching how
        // the parser marks a hand-written __init__; otherwise it gets outlined and slot
        // construction can't find it.
        var initFn = new FunctionDef("__init__", initParams, "", initBody, isInline: true);

        // poll(self) -> uint32: flat state dispatch. Each state body ends with a
        // suspend (`return 1`), a done (`return 0`) or a goto (`_state = n; return 1`).
        // Unknown/terminal states fall through to `return 0`.
        var dispatch = new Block();
        foreach (var st in b.States)
        {
            var blk = new Block();
            blk.Statements.AddRange(st.Raw);
            dispatch.Statements.Add(new IfStmt(
                new BinaryExpr(SelfRef("_state"), BinaryOp.Equal, Int(st.Id)), blk));
        }
        dispatch.Statements.Add(new ReturnStmt(Int(0)));
        var pollFn = new FunctionDef("poll", new List<Param> { new Param("self", "") }, "uint32", dispatch);

        var classBody = new Block();
        classBody.Statements.Add(initFn);
        classBody.Statements.Add(pollFn);
        return new ClassDef(fn.Name, new List<string>(), classBody) { Line = fn.Line };
    }

    // ── Phase A: CFG-of-AST state splitting ─────────────────────────────────────
    private sealed class Builder
    {
        private readonly string? _aio;
        private readonly string _fnName;
        public readonly List<State> States = new();
        public readonly List<string> StartFields = new();
        public bool NeedsValue;
        private State _cur = null!;
        private int _nextId;
        private int _awaitCounter;
        // Flattened-loop context for break/continue rewriting: (headId, afterId).
        private readonly Stack<(int Head, int After)> _loops = new();
        private const int Terminal = 0x7FFF;   // never emitted as a state -> poll returns 0

        public Builder(string? asyncioAlias, string fnName)
        {
            _aio = asyncioAlias;
            _fnName = fnName;
        }

        public void Build(Block body)
        {
            SwitchTo(NewState());
            EmitSeq(body.Statements);
            // Fall off the end of the body: DONE.
            _cur.Raw.Add(SelfAssign("_state", Int(Terminal)));
            _cur.Raw.Add(new ReturnStmt(Int(0)));
        }

        private int NewState()
        {
            var st = new State { Id = _nextId++ };
            States.Add(st);
            return st.Id;
        }

        private void SwitchTo(int id) => _cur = States.First(s => s.Id == id);

        // Transfer control to state `id` and re-dispatch within the same poll call.
        // `continue` targets poll's `while True:` dispatch loop -- inside a state body it
        // is always the nearest loop, because flattened source loops no longer exist and
        // un-flattened ones keep their own break/continue untouched.
        private void Goto(int id)
        {
            _cur.Raw.Add(SelfAssign("_state", Int(id)));
            _cur.Raw.Add(new ReturnStmt(Int(1)));
        }

        private void EmitSeq(List<Statement> stmts)
        {
            foreach (var s in stmts) EmitStmt(s);
        }

        private void EmitStmt(Statement s)
        {
            // `return [expr]` terminates the coroutine wherever it appears.
            if (s is ReturnStmt ret)
            {
                if (ret.Value != null)
                {
                    NeedsValue = true;
                    _cur.Raw.Add(SelfAssign("_value", ret.Value));
                }
                _cur.Raw.Add(SelfAssign("_state", Int(Terminal)));
                _cur.Raw.Add(new ReturnStmt(Int(0)));
                return;
            }

            // `yield [v]` -- a generator suspension: publish the value and return 2
            // ("yielded"). Internal state transitions return 1 ("working"), so the
            // for-in consumer can tell a fresh value from machine bookkeeping.
            if (s is ExprStmt { Expr: YieldExpr y })
            {
                NeedsValue = true;
                _cur.Raw.Add(SelfAssign("_value", y.Value ?? Int(0)));
                int afterY = NewState();
                _cur.Raw.Add(SelfAssign("_state", Int(afterY)));
                _cur.Raw.Add(new ReturnStmt(Int(2)));
                SwitchTo(afterY);
                return;
            }

            if (!ContainsAwait(s) && !ContainsYieldStatic(s))
            {
                // Await-free statement: keep it whole (nested ifs/loops compile normally
                // inside the state), but break/continue that target a FLATTENED loop must
                // become state transitions.
                _cur.Raw.Add(_loops.Count > 0 ? RewriteLoopExits(s) : s);
                return;
            }

            switch (s)
            {
                case ExprStmt or AssignStmt when _aio != null && TryGetAwaitSleep(s, _aio, out var durUs):
                    EmitAwaitSleep(durUs);
                    return;

                case IfStmt iff:
                    EmitIf(iff);
                    return;

                case WhileStmt w:
                    EmitWhile(w.Condition, w.Body is Block wb ? wb.Statements : new List<Statement> { w.Body });
                    return;

                case ForStmt f:
                    EmitFor(f);
                    return;

                case ExprStmt { Expr: AwaitExpr } or AssignStmt { Value: AwaitExpr }:
                    // TryGetAwaitSleep already threw for a non-sleep awaitable.
                    throw new SyntaxError(
                        $"async def '{_fnName}': only `await {_aio}.sleep(n)` / `sleep_ms(n)` " +
                        "can be awaited (awaiting another coroutine/future is not supported yet).", 0, 0);

                default:
                    throw new SyntaxError(
                        $"async def '{_fnName}': `await` inside a {s.GetType().Name} is not " +
                        "supported (supported: if/elif/else, while, for-in-range).", 0, 0);
            }
        }

        // ── await asyncio.sleep[_ms](n) ─────────────────────────────────────────
        private void EmitAwaitSleep(Expression durUsExpr)
        {
            string sf = "_start" + _awaitCounter++;
            StartFields.Add(sf);

            // Arm: record the start timestamp (asyncio.ticks(), microseconds).
            _cur.Raw.Add(SelfAssign(sf, AioCall("ticks")));
            int waitId = NewState();
            int afterId = NewState();
            Goto(waitId);

            // Wait state: stay PENDING until `asyncio.ticks() - start >= duration_us`.
            SwitchTo(waitId);
            _cur.Raw.Add(new IfStmt(
                new BinaryExpr(
                    new BinaryExpr(AioCall("ticks"), BinaryOp.Sub, SelfRef(sf)),
                    BinaryOp.Less,
                    durUsExpr),
                ReturnBlock(1)));
            Goto(afterId);

            SwitchTo(afterId);
        }

        // ── if/elif/else containing awaits ──────────────────────────────────────
        private void EmitIf(IfStmt iff)
        {
            int joinId = NewState();

            var branches = new List<(Expression Cond, Statement Body, int Id)>();
            branches.Add((iff.Condition, iff.ThenBranch, NewState()));
            foreach (var (c, body) in iff.ElifBranches) branches.Add((c, body, NewState()));
            int elseId = iff.ElseBranch != null ? NewState() : joinId;

            // Dispatch replica: same conditions, each branch body replaced by a goto.
            Statement DispatchGoto(int id)
            {
                var blk = new Block();
                blk.Statements.Add(SelfAssign("_state", Int(id)));
                blk.Statements.Add(new ReturnStmt(Int(1)));
                return blk;
            }
            var elifGotos = branches.Skip(1)
                .Select(br => (br.Cond, DispatchGoto(br.Id))).ToList();
            _cur.Raw.Add(new IfStmt(branches[0].Cond, DispatchGoto(branches[0].Id), elifGotos,
                DispatchGoto(elseId)));

            foreach (var br in branches)
            {
                SwitchTo(br.Id);
                EmitSeq(br.Body is Block bb ? bb.Statements : new List<Statement> { br.Body });
                Goto(joinId);
            }
            if (iff.ElseBranch != null)
            {
                SwitchTo(elseId);
                EmitSeq(iff.ElseBranch is Block eb ? eb.Statements : new List<Statement> { iff.ElseBranch });
                Goto(joinId);
            }

            SwitchTo(joinId);
        }

        // ── while <cond> containing awaits ──────────────────────────────────────
        private void EmitWhile(Expression cond, List<Statement> body)
        {
            int headId = NewState();
            int bodyId = NewState();
            int afterId = NewState();

            Goto(headId);

            SwitchTo(headId);
            var enter = new Block();
            enter.Statements.Add(SelfAssign("_state", Int(bodyId)));
            enter.Statements.Add(new ReturnStmt(Int(1)));
            _cur.Raw.Add(new IfStmt(cond, enter));
            Goto(afterId);

            _loops.Push((headId, afterId));
            SwitchTo(bodyId);
            EmitSeq(body);
            Goto(headId);
            _loops.Pop();

            SwitchTo(afterId);
        }

        // ── for i in range(...) containing awaits: desugar to a while ───────────
        private void EmitFor(ForStmt f)
        {
            if (f.Iterable != null || f.RangeStop == null)
                throw new SyntaxError(
                    $"async def '{_fnName}': `await` inside a for-loop is only supported for " +
                    "`for i in range(...)`.", 0, 0);
            int step = 1;
            if (f.RangeStep != null)
            {
                if (f.RangeStep is IntegerLiteral stepLit && stepLit.Value > 0) step = stepLit.Value;
                else throw new SyntaxError(
                    $"async def '{_fnName}': for-range with `await` needs a positive constant step.", 0, 0);
            }

            var iv = new VariableExpr(f.VarName);
            _cur.Raw.Add(new AssignStmt(iv, f.RangeStart ?? Int(0)));
            var body = f.Body is Block fb ? new List<Statement>(fb.Statements) : new List<Statement> { f.Body };
            body.Add(new AssignStmt(iv, new BinaryExpr(iv, BinaryOp.Add, Int(step))));
            EmitWhile(new BinaryExpr(iv, BinaryOp.Less, f.RangeStop), body);
        }

        // break/continue inside an await-free statement nested in a FLATTENED loop:
        // rewrite to state transitions. Recurses through ifs/blocks but NOT into nested
        // while/for loops -- their break/continue belong to them.
        private Statement RewriteLoopExits(Statement s)
        {
            var (head, after) = _loops.Peek();
            switch (s)
            {
                case BreakStmt:
                {
                    var blk = new Block();
                    blk.Statements.Add(SelfAssign("_state", Int(after)));
                    blk.Statements.Add(new ReturnStmt(Int(1)));
                    return blk;
                }
                case ContinueStmt:
                {
                    var blk = new Block();
                    blk.Statements.Add(SelfAssign("_state", Int(head)));
                    blk.Statements.Add(new ReturnStmt(Int(1)));
                    return blk;
                }
                case Block b:
                {
                    var nb = new Block();
                    foreach (var st in b.Statements) nb.Statements.Add(RewriteLoopExits(st));
                    return nb;
                }
                case IfStmt iff:
                    return new IfStmt(iff.Condition, RewriteLoopExits(iff.ThenBranch),
                        iff.ElifBranches.Select(e => (e.Condition, RewriteLoopExits(e.Body))).ToList(),
                        iff.ElseBranch == null ? null : RewriteLoopExits(iff.ElseBranch)) { Line = iff.Line };
                default:
                    return s;
            }
        }

        // `<asyncio>.<method>()` -- a dotted call on the imported asyncio module.
        private Expression AioCall(string method) =>
            new CallExpr(new MemberAccessExpr(new VariableExpr(_aio), method), new List<Expression>());
    }

    // ── Phase B: per-state touch tracking (ordered: reads before writes) ────────
    private static void TrackTouches(Statement s, int stateId, HashSet<string> tracked,
        Dictionary<string, HashSet<int>> touchStates, Dictionary<string, bool> firstTouchIsRead)
    {
        void Touch(string name, bool isRead)
        {
            if (!tracked.Contains(name)) return;
            if (!touchStates.TryGetValue(name, out var set))
            {
                touchStates[name] = set = new HashSet<int>();
                firstTouchIsRead[name] = isRead;
            }
            set.Add(stateId);
        }

        void Expr(Expression? e)
        {
            switch (e)
            {
                case null: return;
                case VariableExpr v: Touch(v.Name, isRead: true); return;
                case BinaryExpr b: Expr(b.Left); Expr(b.Right); return;
                case UnaryExpr u: Expr(u.Operand); return;
                case CallExpr c: foreach (var a in c.Args) Expr(a is KeywordArgExpr kw ? kw.Value : a); return;
                case MemberAccessExpr m: Expr(m.Object); return;
                case IndexExpr ix: Expr(ix.Target); Expr(ix.Index); return;
                case TernaryExpr t: Expr(t.Condition); Expr(t.TrueVal); Expr(t.FalseVal); return;
                case AwaitExpr aw: Expr(aw.Operand); return;
            }
        }

        switch (s)
        {
            case ExprStmt es: Expr(es.Expr); break;
            case AssignStmt a:
                Expr(a.Value);
                if (a.Target is VariableExpr tv) Touch(tv.Name, isRead: false);
                else Expr(a.Target);
                break;
            case AugAssignStmt ag:
                // x += v reads x first.
                if (ag.Target is VariableExpr av) Touch(av.Name, isRead: true);
                else Expr(ag.Target);
                Expr(ag.Value);
                break;
            case VarDecl vd: Expr(vd.Init); Touch(vd.Name, isRead: false); break;
            case AnnAssign aa: Expr(aa.Value); Touch(aa.Target, isRead: false); break;
            case ReturnStmt r: Expr(r.Value); break;
            case IfStmt iff:
                Expr(iff.Condition);
                TrackTouches(iff.ThenBranch, stateId, tracked, touchStates, firstTouchIsRead);
                foreach (var (c, body) in iff.ElifBranches)
                {
                    Expr(c);
                    TrackTouches(body, stateId, tracked, touchStates, firstTouchIsRead);
                }
                if (iff.ElseBranch != null)
                    TrackTouches(iff.ElseBranch, stateId, tracked, touchStates, firstTouchIsRead);
                break;
            case WhileStmt w:
                Expr(w.Condition);
                TrackTouches(w.Body, stateId, tracked, touchStates, firstTouchIsRead);
                break;
            case ForStmt f:
                Expr(f.RangeStart); Expr(f.RangeStop); Expr(f.RangeStep); Expr(f.Iterable);
                Touch(f.VarName, isRead: false);
                TrackTouches(f.Body, stateId, tracked, touchStates, firstTouchIsRead);
                break;
            case Block b:
                foreach (var st in b.Statements)
                    TrackTouches(st, stateId, tracked, touchStates, firstTouchIsRead);
                break;
        }
    }

    // ── Phase C: local -> self.field rewriting ──────────────────────────────────
    private sealed class Rewriter
    {
        private readonly HashSet<string> _fields;
        public Rewriter(HashSet<string> fields) => _fields = fields;

        public Statement RewriteStmt(Statement s)
        {
            switch (s)
            {
                case ExprStmt es: return new ExprStmt(Rewrite(es.Expr)) { Line = es.Line };
                case AssignStmt asg:
                    return new AssignStmt(Rewrite(asg.Target), Rewrite(asg.Value))
                        { Line = asg.Line, AnnotatedType = asg.AnnotatedType };
                case AugAssignStmt aug:
                    return new AugAssignStmt(Rewrite(aug.Target), aug.Op, Rewrite(aug.Value)) { Line = aug.Line };
                case VarDecl vd when _fields.Contains(vd.Name):
                    // A declared local promoted to a field: `x: T = v` -> `self.x = v`.
                    return new AssignStmt(new MemberAccessExpr(new VariableExpr("self"), vd.Name),
                        Rewrite(vd.Init ?? new IntegerLiteral(0))) { Line = vd.Line };
                case VarDecl vd2:
                    return new VarDecl(vd2.Name, vd2.VarType, vd2.Init == null ? null : Rewrite(vd2.Init))
                        { Line = vd2.Line };
                case ReturnStmt r:
                    return r.Value == null ? r : new ReturnStmt(Rewrite(r.Value)) { Line = r.Line };
                case IfStmt iff:
                    return new IfStmt(Rewrite(iff.Condition), RewriteStmt(iff.ThenBranch),
                        iff.ElifBranches.Select(e => (Rewrite(e.Condition), RewriteStmt(e.Body))).ToList(),
                        iff.ElseBranch == null ? null : RewriteStmt(iff.ElseBranch)) { Line = iff.Line };
                case WhileStmt w:
                    return new WhileStmt(Rewrite(w.Condition), RewriteStmt(w.Body)) { Line = w.Line };
                case ForStmt:
                    // Await-free for loops pass through whole; their loop var stays local to
                    // the state (never a field: assigned before every use inside one state).
                    return s;
                case Block b:
                {
                    var nb = new Block();
                    foreach (var st in b.Statements) nb.Statements.Add(RewriteStmt(st));
                    return nb;
                }
                default:
                    return s;
            }
        }

        private Expression Rewrite(Expression e)
        {
            switch (e)
            {
                case VariableExpr v:
                    return _fields.Contains(v.Name) ? SelfRef(v.Name) : v;
                case BinaryExpr b:
                    return new BinaryExpr(Rewrite(b.Left), b.Op, Rewrite(b.Right));
                case UnaryExpr u:
                    return new UnaryExpr(u.Op, Rewrite(u.Operand));
                case CallExpr c:
                    // Don't rewrite the callee name into a field; only its arguments.
                    return new CallExpr(c.Callee, c.Args.Select(Rewrite).ToList());
                case MemberAccessExpr m:
                    return new MemberAccessExpr(Rewrite(m.Object), m.Member);
                case IndexExpr ix:
                    return new IndexExpr(Rewrite(ix.Target), Rewrite(ix.Index));
                case TernaryExpr t:
                    return new TernaryExpr(Rewrite(t.TrueVal), Rewrite(t.Condition), Rewrite(t.FalseVal));
                case AwaitExpr:
                    throw new SyntaxError("`await` is only valid as a statement (e.g. `await sleep(n)`).", 0, 0);
                default:
                    return e;
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    // Recognize `await asyncio.sleep(n)` / `asyncio.sleep_ms(n)` and return the delay
    // converted to MICROSECONDS (asyncio.ticks() units): sleep(n)=n*1_000_000,
    // sleep_ms(n)=n*1000.
    private static bool TryGetAwaitSleep(Statement s, string aio, out Expression durUsExpr)
    {
        durUsExpr = null!;
        Expression? val = s switch
        {
            ExprStmt es => es.Expr,
            AssignStmt asg => asg.Value, // `x = await asyncio.sleep(n)` -- the result is None
            _ => null,
        };
        if (val is not AwaitExpr aw) return false;

        if (aw.Operand is CallExpr c && c.Args.Count == 1
            && c.Callee is MemberAccessExpr m && m.Object is VariableExpr mod && mod.Name == aio
            && (m.Member == "sleep" || m.Member == "sleep_ms"))
        {
            int scale = m.Member == "sleep" ? 1_000_000 : 1000;
            durUsExpr = new BinaryExpr(c.Args[0], BinaryOp.Mul, new IntegerLiteral(scale));
            return true;
        }
        return false;
    }

    private static bool ContainsAwait(Statement s)
    {
        switch (s)
        {
            case ExprStmt es: return ContainsAwait(es.Expr);
            case AssignStmt a: return ContainsAwait(a.Value) || ContainsAwait(a.Target);
            case AugAssignStmt ag: return ContainsAwait(ag.Value);
            case ReturnStmt r: return r.Value != null && ContainsAwait(r.Value);
            case VarDecl vd: return vd.Init != null && ContainsAwait(vd.Init);
            case AnnAssign aa: return aa.Value != null && ContainsAwait(aa.Value);
            case IfStmt iff:
                return ContainsAwait(iff.Condition) || ContainsAwait(iff.ThenBranch)
                    || (iff.ElseBranch != null && ContainsAwait(iff.ElseBranch))
                    || iff.ElifBranches.Any(e => ContainsAwait(e.Item1) || ContainsAwait(e.Item2));
            case WhileStmt w: return ContainsAwait(w.Condition) || ContainsAwait(w.Body);
            case ForStmt f: return ContainsAwait(f.Body);
            case TryStmt t:
                return t.Body.Any(ContainsAwait)
                    || t.Handlers.Any(h => h.Handler.Any(ContainsAwait))
                    || (t.Finally != null && t.Finally.Any(ContainsAwait))
                    || (t.ElseBody != null && t.ElseBody.Any(ContainsAwait));
            case WithStmt ws: return ContainsAwait(ws.Body);
            case MatchStmt m: return m.Branches.Any(c => c.Body != null && ContainsAwait(c.Body));
            case Block b: return b.Statements.Any(ContainsAwait);
            default: return false;
        }
    }

    private static bool ContainsAwait(Expression e) => e switch
    {
        AwaitExpr => true,
        BinaryExpr b => ContainsAwait(b.Left) || ContainsAwait(b.Right),
        UnaryExpr u => ContainsAwait(u.Operand),
        CallExpr c => c.Args.Any(ContainsAwait) || ContainsAwait(c.Callee),
        MemberAccessExpr m => ContainsAwait(m.Object),
        IndexExpr ix => ContainsAwait(ix.Target) || ContainsAwait(ix.Index),
        TernaryExpr t => ContainsAwait(t.Condition) || ContainsAwait(t.TrueVal) || ContainsAwait(t.FalseVal),
        _ => false,
    };

    private static void CollectAssignedLocals(Statement s, HashSet<string> exclude, List<string> outList)
    {
        void Add(string n)
        {
            if (!exclude.Contains(n) && !outList.Contains(n)) outList.Add(n);
        }
        switch (s)
        {
            case AssignStmt a when a.Target is VariableExpr v: Add(v.Name); break;
            case VarDecl vd: Add(vd.Name); break;
            case AnnAssign aa: Add(aa.Target); break;
            case Block b: foreach (var st in b.Statements) CollectAssignedLocals(st, exclude, outList); break;
            case IfStmt iff:
                CollectAssignedLocals(iff.ThenBranch, exclude, outList);
                if (iff.ElseBranch != null) CollectAssignedLocals(iff.ElseBranch, exclude, outList);
                foreach (var e in iff.ElifBranches) CollectAssignedLocals(e.Item2, exclude, outList);
                break;
            case WhileStmt w: CollectAssignedLocals(w.Body, exclude, outList); break;
            case ForStmt f:
                Add(f.VarName);
                CollectAssignedLocals(f.Body, exclude, outList);
                break;
        }
    }

    private static string ScalarType(string t) => string.IsNullOrEmpty(t) ? "uint32" : t;

    private static Expression SelfRef(string field) => new MemberAccessExpr(new VariableExpr("self"), field);
    private static Expression Int(int v) => new IntegerLiteral(v);

    // `self.f: T = init` -- annotated so the ZCA field gets its declared width.
    private static Statement SelfDecl(string field, string type, Expression init) =>
        new AssignStmt(SelfRef(field), init) { AnnotatedType = type };

    private static Statement SelfAssign(string field, Expression value) =>
        new AssignStmt(SelfRef(field), value);

    private static Block ReturnBlock(int v)
    {
        var b = new Block();
        b.Statements.Add(new ReturnStmt(Int(v)));
        return b;
    }
}
