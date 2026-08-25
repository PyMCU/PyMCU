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

        // `yield from` in a coroutine is not the same construct: it would have to drive the
        // delegate between awaits, and the expansion below only walks generators. Left alone
        // it reached the state splitter as an ordinary yield and published the CALL as the
        // yielded value, which builds and is wrong without a word.
        foreach (var fn in asyncFns)
            if (DelegateYieldPosition(fn.Body.Statements) != null)
                throw new SyntaxError(
                    $"`yield from` is not supported inside `async def {fn.Name}`. It is available "
                    + "in a plain generator (a `def` whose body yields); a coroutine has no "
                    + "delegation form yet.", 0, 0);

        foreach (var fn in asyncFns)
        {
            prog.Functions.Remove(fn);
            prog.GlobalStatements.Add(TransformFunction(fn, alias!));
        }

        // `yield from inner()` is expanded into inner's own body, with its locals renamed,
        // BEFORE the state split: one generator delegating to another would otherwise need
        // to hold the delegate's state machine as a field and drive it, and a nested ZCA
        // instance has no address to poll through. Expanding keeps a single flat state
        // machine -- the same thing @inline does everywhere else in this compiler.
        if (genFns.Count > 0)
        {
            var genByName = new Dictionary<string, FunctionDef>();
            foreach (var g in genFns) genByName[g.Name] = g;
            int yfCounter = 0;
            foreach (var g in genFns)
                ExpandYieldFrom(g.Body.Statements, genByName,
                    new HashSet<string> { g.Name }, ref yfCounter);

            // Anything the expansion did not consume is a form it cannot express. Saying so
            // here beats the message the splitter would give, which talks about @inline
            // functions and class methods and names neither `yield` nor `from`.
            foreach (var g in genFns)
                if (DelegateYieldPosition(g.Body.Statements) is { } where)
                    throw new SyntaxError(
                        $"`yield from` {where}. It is supported as a statement of its own, "
                        + "delegating to a generator defined in this module and called by name: "
                        + "`yield from inner(a)`.", 0, 0);
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

    // ── `yield from` expansion ──────────────────────────────────────────────────
    // `yield from inner(a)` becomes inner's body spliced in place: the parameters are
    // bound to the call's arguments as declared locals, and every name inner owns (its
    // params and its own locals) is renamed with a per-site prefix so it cannot collide
    // with the delegating generator's. The result is ordinary generator source, so every
    // shape the state splitter already handles (yield in a while, in an if, break /
    // continue) keeps working with no change to the splitter.
    private static void ExpandYieldFrom(List<Statement> stmts,
        Dictionary<string, FunctionDef> genByName, HashSet<string> active, ref int counter)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            if (stmts[i] is ExprStmt { Expr: YieldExpr { IsDelegate: true } yf })
            {
                stmts[i] = ExpandOne(yf, genByName, active, ref counter);
                continue;
            }
            switch (stmts[i])
            {
                case Block b: ExpandYieldFrom(b.Statements, genByName, active, ref counter); break;
                case IfStmt iff:
                    ExpandIn(iff.ThenBranch, genByName, active, ref counter);
                    foreach (var (_, eb) in iff.ElifBranches) ExpandIn(eb, genByName, active, ref counter);
                    if (iff.ElseBranch != null) ExpandIn(iff.ElseBranch, genByName, active, ref counter);
                    break;
                case WhileStmt w: ExpandIn(w.Body, genByName, active, ref counter); break;
                case ForStmt f: ExpandIn(f.Body, genByName, active, ref counter); break;
            }
        }
    }

    private static void ExpandIn(Statement s, Dictionary<string, FunctionDef> genByName,
        HashSet<string> active, ref int counter)
    {
        if (s is Block b) ExpandYieldFrom(b.Statements, genByName, active, ref counter);
    }

    private static Statement ExpandOne(YieldExpr yf, Dictionary<string, FunctionDef> genByName,
        HashSet<string> active, ref int counter)
    {
        // The delegate has to be named at the call: there is no generator object to pass
        // around at run time, so `yield from x` where x is a variable cannot be resolved.
        if (yf.Value is not CallExpr { Callee: VariableExpr callee } call
            || !genByName.TryGetValue(callee.Name, out var inner))
            throw new SyntaxError(
                "`yield from` needs a direct call to a generator function defined in this " +
                "module (`yield from inner(...)`): the delegate is expanded at compile time, " +
                "so there is no generator object to take from a variable or another module.",
                0, 0);

        if (!active.Add(inner.Name))
            throw new SyntaxError(
                $"`yield from {inner.Name}(...)` is recursive (directly or through another " +
                "generator). Delegation is expanded inline, so a cycle has no finite " +
                "expansion; rewrite the generator as a loop.", 0, 0);

        // `return` inside a delegated generator ends the DELEGATION, not the delegating
        // generator -- a different jump target than the one the splitter emits for a
        // `return`. Refused rather than silently ending the outer generator early.
        if (ContainsReturn(inner.Body))
        {
            active.Remove(inner.Name);
            throw new SyntaxError(
                $"`yield from {inner.Name}(...)`: '{inner.Name}' contains a `return`, which " +
                "ends the delegation and not the delegating generator. That distinction is " +
                "not implemented yet -- let the delegate fall off the end of its body, or " +
                "consume it with `for v in " + inner.Name + "(...): yield v` in a plain loop.",
                0, 0);
        }

        string prefix = "__yf" + counter++ + "_";

        var owned = new List<string>();
        foreach (var p in inner.Params) owned.Add(p.Name);
        CollectAssignedLocals(inner.Body, new HashSet<string>(), owned);
        var renames = new Dictionary<string, string>();
        foreach (var n in owned) renames[n] = prefix + n;

        var repl = new Block { Line = inner.Line };
        for (int pi = 0; pi < inner.Params.Count; pi++)
        {
            var p = inner.Params[pi];
            Expression? arg = pi < call.Args.Count ? call.Args[pi]
                : p.DefaultValue;
            if (arg == null)
                throw new SyntaxError(
                    $"`yield from {inner.Name}(...)`: no argument for parameter '{p.Name}' " +
                    "and it has no default.", 0, 0);
            repl.Statements.Add(new VarDecl(renames[p.Name], p.Type, arg));
        }

        var renamer = new Renamer(renames);
        var body = new List<Statement>();
        foreach (var st in inner.Body.Statements) body.Add(renamer.Stmt(st));

        // The delegate may itself delegate.
        ExpandYieldFrom(body, genByName, active, ref counter);
        repl.Statements.AddRange(body);

        active.Remove(inner.Name);
        return repl;
    }

    private static bool ContainsReturn(Statement s)
    {
        switch (s)
        {
            case ReturnStmt: return true;
            case Block b: return b.Statements.Any(ContainsReturn);
            case IfStmt iff:
                return ContainsReturn(iff.ThenBranch)
                    || (iff.ElseBranch != null && ContainsReturn(iff.ElseBranch))
                    || iff.ElifBranches.Any(e => ContainsReturn(e.Item2));
            case WhileStmt w: return ContainsReturn(w.Body);
            case ForStmt f: return ContainsReturn(f.Body);
            case TryStmt t:
                return t.Body.Any(ContainsReturn)
                    || t.Handlers.Any(h => h.Handler.Any(ContainsReturn))
                    || (t.Finally != null && t.Finally.Any(ContainsReturn))
                    || (t.ElseBody != null && t.ElseBody.Any(ContainsReturn));
            case WithStmt ws: return ContainsReturn(ws.Body);
            default: return false;
        }
    }

    // Copies a delegated generator's body, renaming the names it owns. Anything this does
    // not know how to copy is refused rather than passed through unrenamed: a missed name
    // would silently bind to the delegating generator's variable of the same name.
    private sealed class Renamer
    {
        private readonly Dictionary<string, string> _map;
        public Renamer(Dictionary<string, string> map) => _map = map;

        private string N(string n) => _map.TryGetValue(n, out var r) ? r : n;

        public Statement Stmt(Statement s)
        {
            switch (s)
            {
                case ExprStmt es: return new ExprStmt(E(es.Expr)) { Line = es.Line };
                case AssignStmt a:
                    return new AssignStmt(E(a.Target), E(a.Value))
                        { Line = a.Line, AnnotatedType = a.AnnotatedType };
                case AugAssignStmt ag:
                    return new AugAssignStmt(E(ag.Target), ag.Op, E(ag.Value)) { Line = ag.Line };
                case VarDecl vd:
                    return new VarDecl(N(vd.Name), vd.VarType, vd.Init == null ? null : E(vd.Init))
                        { Line = vd.Line };
                case AnnAssign aa:
                    return new AnnAssign(N(aa.Target), aa.Annotation,
                        aa.Value == null ? null : E(aa.Value)) { Line = aa.Line };
                case IfStmt iff:
                    return new IfStmt(E(iff.Condition), Stmt(iff.ThenBranch),
                        iff.ElifBranches.Select(e => (E(e.Condition), Stmt(e.Body))).ToList(),
                        iff.ElseBranch == null ? null : Stmt(iff.ElseBranch)) { Line = iff.Line };
                case WhileStmt w:
                    return new WhileStmt(E(w.Condition), Stmt(w.Body)) { Line = w.Line };
                case ForStmt f:
                {
                    var nf = new ForStmt(N(f.VarName),
                        f.RangeStart == null ? null : E(f.RangeStart),
                        f.RangeStop == null ? null : E(f.RangeStop),
                        f.RangeStep == null ? null : E(f.RangeStep),
                        Stmt(f.Body)) { Line = f.Line, Var2Name = f.Var2Name };
                    if (f.Iterable != null)
                        throw new SyntaxError(
                            "`yield from`: the delegated generator iterates something other " +
                            "than range(...), which the delegation expansion cannot copy yet.",
                            0, 0);
                    return nf;
                }
                case Block b:
                {
                    var nb = new Block { Line = b.Line };
                    foreach (var st in b.Statements) nb.Statements.Add(Stmt(st));
                    return nb;
                }
                case BreakStmt: return new BreakStmt { Line = s.Line };
                case ContinueStmt: return new ContinueStmt { Line = s.Line };
                case PassStmt: return new PassStmt { Line = s.Line };
                default:
                    throw new SyntaxError(
                        $"`yield from`: the delegated generator uses a {s.GetType().Name}, " +
                        "which the delegation expansion cannot copy yet.", 0, 0);
            }
        }

        private Expression E(Expression e)
        {
            switch (e)
            {
                case VariableExpr v: return new VariableExpr(N(v.Name)) { Line = v.Line };
                case BinaryExpr b: return new BinaryExpr(E(b.Left), b.Op, E(b.Right)) { Line = b.Line };
                case UnaryExpr u: return new UnaryExpr(u.Op, E(u.Operand)) { Line = u.Line };
                case YieldExpr y:
                    return new YieldExpr(y.Value == null ? null : E(y.Value), y.IsDelegate) { Line = y.Line };
                case CallExpr c:
                    return new CallExpr(E(c.Callee), c.Args.Select(E).ToList()) { Line = c.Line };
                case MemberAccessExpr m: return new MemberAccessExpr(E(m.Object), m.Member) { Line = m.Line };
                case IndexExpr ix: return new IndexExpr(E(ix.Target), E(ix.Index)) { Line = ix.Line };
                case TernaryExpr t:
                    return new TernaryExpr(E(t.TrueVal), E(t.Condition), E(t.FalseVal)) { Line = t.Line };
                case KeywordArgExpr kw: return new KeywordArgExpr(kw.Key, E(kw.Value)) { Line = kw.Line };
                case IntegerLiteral or FloatLiteral or BooleanLiteral or StringLiteral or NoneLiteral:
                    return e;
                default:
                    throw new SyntaxError(
                        $"`yield from`: the delegated generator uses a {e.GetType().Name}, " +
                        "which the delegation expansion cannot copy yet.", 0, 0);
            }
        }
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
        var localTypes = new Dictionary<string, string>();
        CollectAssignedLocals(fn.Body, new HashSet<string>(paramNames), localNames, localTypes);

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
        // Field WIDTHS matter enormously on 8-bit targets: a blanket uint32 quadruples
        // the state and drags 4-byte load/store chains into every poll() -- a two-yield
        // generator once cost 4.2 KB of flash from this alone. _state only ever holds
        // small ids plus the 0x7FFF terminal (uint16); lifted locals keep their declared
        // annotation; _value is inferred from the yielded/returned expressions and falls
        // back to uint32 only when one of them cannot be classified.
        var initBody = new Block();
        initBody.Statements.Add(SelfDecl("_state", "uint16", Int(0)));
        foreach (var p in fn.Params)
            initBody.Statements.Add(SelfDecl(p.Name, ScalarType(p.Type), new VariableExpr(p.Name)));
        foreach (var l in localNames)
            if (fields.Contains(l))
                initBody.Statements.Add(SelfDecl(l,
                    localTypes.TryGetValue(l, out var lt) ? ScalarType(lt) : "uint32", Int(0)));
        if (b.NeedsStart)
            initBody.Statements.Add(SelfDecl(StartField, "uint32", Int(0)));
        if (b.NeedsDuration)
            initBody.Statements.Add(SelfDecl(DurationField, "uint32", Int(0)));
        if (b.NeedsValue)
            initBody.Statements.Add(SelfDecl("_value",
                InferValueFieldType(b, localTypes, fn.Params), Int(0)));

        var initParams = new List<Param> { new Param("self", "") };
        initParams.AddRange(fn.Params);
        // Constructors are force-inlined (expanded at the construction site), matching how
        // the parser marks a hand-written __init__; otherwise it gets outlined and slot
        // construction can't find it. The return type must be "void" for the same reason:
        // an empty one makes the expansion allocate a result temp and hand THAT back as the
        // value of `fast()`, so `gather(fast(), slow())` binds the parameter to a classless
        // temporary and `a.poll()` has no class to dispatch on.
        var initFn = new FunctionDef("__init__", initParams, "void", initBody, isInline: true);

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
        // poll() only ever returns the protocol codes 0/1/2 -- a byte, not a uint32.
        var pollFn = new FunctionDef("poll", new List<Param> { new Param("self", "") }, "uint8", dispatch);

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
        public bool NeedsStart;
        public bool NeedsDuration;
        public bool NeedsValue;
        private State _cur = null!;
        private int _nextId;
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

                // A bare block carrying suspension points: the `yield from` expansion
                // splices the delegate's body in as one, and there is no scope to open --
                // the statements belong to the enclosing state sequence.
                case Block blk:
                    EmitSeq(blk.Statements);
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
        // One `_deadline` field serves every await in the coroutine, because only one
        // of them can be suspended at a time. Storing the wake-up time rather than the
        // start timestamp is what makes that sharing possible: a start timestamp is only
        // meaningful next to its own duration, so it needed a field per await site (4
        // bytes each on top of the 2 for _state), while a deadline is self-contained.
        // It also moves the duration out of the hot path -- `ms * 1000` with a run-time
        // `ms` is a __mul32 call, and the old wait state paid for it on every single
        // poll instead of once when the await was armed.
        private void EmitAwaitSleep(Expression durUsExpr)
        {
            NeedsStart = true;

            // One `_start` serves every await in the coroutine: only one of them can be
            // suspended at a time, so the timestamp of whichever armed last is the only
            // one that means anything. The earlier shape gave each site its own `_startN`,
            // which cost 4 bytes of state per await for no gain.
            //
            // A literal duration stays inline in the comparison, exactly as before, so the
            // wait state is unchanged and the whole 2^32 us range the counter can express
            // is still available. Only a run-time duration needs storing, and storing it
            // is itself a win: `ms * 1000` is a __mul32 call, and leaving it in the wait
            // state paid for it on every poll instead of once when the await was armed.
            _cur.Raw.Add(SelfAssign(StartField, AioCall("ticks")));

            Expression waitAgainst;
            if (durUsExpr is IntegerLiteral)
            {
                waitAgainst = durUsExpr;
            }
            else
            {
                NeedsDuration = true;
                _cur.Raw.Add(SelfAssign(DurationField, durUsExpr));
                waitAgainst = SelfRef(DurationField);
            }

            int waitId = NewState();
            int afterId = NewState();
            Goto(waitId);

            // Wait state: stay PENDING until `ticks() - start >= duration`, in wrapping
            // uint32 arithmetic so a counter roll-over during the wait is handled.
            SwitchTo(waitId);
            _cur.Raw.Add(new IfStmt(
                new BinaryExpr(
                    new BinaryExpr(AioCall("ticks"), BinaryOp.Sub, SelfRef(StartField)),
                    BinaryOp.Less,
                    waitAgainst),
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
                case ForStmt f:
                {
                    // The loop VAR stays local to the state: it is assigned before every use
                    // inside one state, so it never needs to be a field. Its BODY is another
                    // matter -- a local promoted to a field is still a field in there, and
                    // passing the loop through whole left the body accumulating into a
                    // different variable from the one the rest of the coroutine reads.
                    bool varWasField = _fields.Remove(f.VarName);
                    bool var2WasField = !string.IsNullOrEmpty(f.Var2Name) && _fields.Remove(f.Var2Name);
                    ForStmt rewritten = f.Iterable != null
                        ? new ForStmt(f.VarName, Rewrite(f.Iterable), RewriteStmt(f.Body))
                            { Var2Name = f.Var2Name, Line = f.Line }
                        : new ForStmt(f.VarName,
                            f.RangeStart == null ? null : Rewrite(f.RangeStart),
                            f.RangeStop == null ? null : Rewrite(f.RangeStop),
                            f.RangeStep == null ? null : Rewrite(f.RangeStep),
                            RewriteStmt(f.Body)) { Var2Name = f.Var2Name, Line = f.Line };
                    if (varWasField) _fields.Add(f.VarName);
                    if (var2WasField) _fields.Add(f.Var2Name);
                    return rewritten;
                }
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
                    // Promoting the RECEIVER of a method call is what #116 needs, and it is
                    // not enough on its own: the promotion turns `a = Acc(s)` into
                    // `self.a = Acc(s)` outside __init__, which the ZCA machinery answers with
                    // "Unknown member access in assignment". Measured, and it breaks programs
                    // that build today, so the receiver stays local until a field can hold an
                    // instance.
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
            // A literal duration is folded here rather than left for the optimizer: it
            // keeps a __mul32 out of the arm state on 8-bit targets, and it is the only
            // point where the duration can be range-checked before it silently wraps.
            durUsExpr = new BinaryExpr(c.Args[0], BinaryOp.Mul, new IntegerLiteral(scale));
            // `-5` reaches here as a negation around the literal, not as a negative one.
            if (ConstantMillis(c.Args[0]) is { } lit)
            {
                long us = lit * scale;
                if (us < 0)
                    throw new SyntaxError(
                        $"`await {aio}.{m.Member}({lit})`: the duration cannot be negative.", 0, 0);
                // The wait compares `ticks() - start` against the duration in wrapping
                // uint32 arithmetic, so the whole 2^32 us range is usable, about 71
                // minutes. Past that the subtraction lands back inside the window and the
                // await would return immediately instead of waiting.
                if (us > uint.MaxValue)
                    throw new SyntaxError(
                        $"`await {aio}.{m.Member}({lit})` is longer than a single await can wait " +
                        "(the limit is 4294 seconds, about 71 minutes). Split it into shorter sleeps, " +
                        "or count them in a loop.", 0, 0);
                // IntegerLiteral is 32-bit signed, so only the lower half can be folded
                // here; the rest keeps the multiply, which the backend widens correctly.
                if (us > int.MaxValue) return true;
                durUsExpr = new IntegerLiteral((int)us);
            }
            return true;
        }
        return false;
    }

    // The compile-time value of a sleep argument, or null when it is a run-time
    // expression. A negated literal is a UnaryExpr, which is how `sleep_ms(-5)` arrives.
    private static long? ConstantMillis(Expression e) => e switch
    {
        IntegerLiteral i => i.Value,
        UnaryExpr { Op: UnaryOp.Negate, Operand: IntegerLiteral n } => -(long)n.Value,
        _ => null,
    };

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

    private static void CollectAssignedLocals(Statement s, HashSet<string> exclude, List<string> outList,
        Dictionary<string, string>? outTypes = null)
    {
        void Add(string n, string? declared = null)
        {
            if (!exclude.Contains(n) && !outList.Contains(n)) outList.Add(n);
            if (outTypes != null && !string.IsNullOrEmpty(declared) && !outTypes.ContainsKey(n))
                outTypes[n] = declared!;
        }
        switch (s)
        {
            case AssignStmt a when a.Target is VariableExpr v: Add(v.Name); break;
            case VarDecl vd: Add(vd.Name, vd.VarType); break;
            case AnnAssign aa: Add(aa.Target, aa.Annotation); break;
            case Block b: foreach (var st in b.Statements) CollectAssignedLocals(st, exclude, outList, outTypes); break;
            case IfStmt iff:
                CollectAssignedLocals(iff.ThenBranch, exclude, outList, outTypes);
                if (iff.ElseBranch != null) CollectAssignedLocals(iff.ElseBranch, exclude, outList, outTypes);
                foreach (var e in iff.ElifBranches) CollectAssignedLocals(e.Item2, exclude, outList, outTypes);
                break;
            case WhileStmt w: CollectAssignedLocals(w.Body, exclude, outList, outTypes); break;
            case ForStmt f:
                Add(f.VarName);
                CollectAssignedLocals(f.Body, exclude, outList, outTypes);
                break;
        }
    }

    // The narrowest type that can hold every expression assigned to self._value (the
    // yield / return payloads). Classifiable: non-negative integer literals and
    // variables with a declared scalar type. Anything else -> uint32 (the historical
    // width) so no value can silently truncate.
    private static string InferValueFieldType(Builder b, Dictionary<string, string> localTypes,
        List<Param> pars)
    {
        static int Rank(string t) => t switch
        {
            "uint8" or "int8" => 1,
            "uint16" or "int16" or "int" => 2,
            _ => 3,
        };
        static bool Signed(string t) => t.StartsWith("int");

        int rank = 1;
        bool signed = false;
        foreach (var st in b.States)
            foreach (var raw in st.Raw)
            {
                if (raw is not AssignStmt { Target: MemberAccessExpr { Member: "_value" } } asg) continue;
                string? t = asg.Value switch
                {
                    IntegerLiteral il when il.Value >= 0 && il.Value <= 255 => "uint8",
                    IntegerLiteral il when il.Value >= 0 && il.Value <= 65535 => "uint16",
                    VariableExpr v when localTypes.TryGetValue(v.Name, out var lt) => lt,
                    VariableExpr v when pars.FirstOrDefault(p => p.Name == v.Name) is { } p
                                        && !string.IsNullOrEmpty(p.Type) => p.Type,
                    _ => null,
                };
                if (t == null || Rank(t) >= 3) return "uint32";
                rank = Math.Max(rank, Rank(t));
                signed |= Signed(t);
            }
        if (signed) return rank == 1 ? "int8" : "int16";
        return rank == 1 ? "uint8" : "uint16";
    }

    // The single uint32 every `await asyncio.sleep(...)` in a coroutine shares. See
    // Builder.EmitAwaitSleep for why one pair is enough for any number of await sites.
    private const string StartField = "_start";
    private const string DurationField = "_duration";

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

    /// <summary>
    /// Describes where a `yield from` that the expansion did not consume sits, or null when
    /// there is none left. The phrasing completes the sentence "`yield from` ...".
    /// </summary>
    private static string? DelegateYieldPosition(List<Statement> stmts)
    {
        static bool IsDelegate(Expression? e) => e is YieldExpr { IsDelegate: true };

        string? Walk(Statement? st)
        {
            switch (st)
            {
                case null: return null;
                case Block b: return b.Statements.Select(Walk).FirstOrDefault(x => x != null);
                case ExprStmt es when IsDelegate(es.Expr):
                    return "delegates to something this compiler cannot expand: the target must "
                           + "be a direct call to a generator defined in this module, and it must "
                           + "not be recursive";
                case AssignStmt a when IsDelegate(a.Value):
                case VarDecl vd when IsDelegate(vd.Init):
                case AnnAssign an when IsDelegate(an.Value):
                    return "has no value to assign: what CPython returns there is the delegate's "
                           + "return value, which PyMCU generators do not carry";
                case ReturnStmt r when IsDelegate(r.Value):
                    return "cannot be returned";
                case IfStmt i:
                    return Walk(i.ThenBranch)
                           ?? i.ElifBranches.Select(br => Walk(br.Item2)).FirstOrDefault(x => x != null)
                           ?? Walk(i.ElseBranch);
                case WhileStmt w: return Walk(w.Body);
                case ForStmt f: return Walk(f.Body);
                case WithStmt wi: return Walk(wi.Body);
                case TryStmt t:
                    return t.Body.Select(Walk).FirstOrDefault(x => x != null)
                           ?? t.Handlers.SelectMany(h => h.Item2).Select(Walk).FirstOrDefault(x => x != null)
                           ?? (t.ElseBody?.Select(Walk).FirstOrDefault(x => x != null))
                           ?? (t.Finally?.Select(Walk).FirstOrDefault(x => x != null));
                default: return null;
            }
        }

        return stmts.Select(Walk).FirstOrDefault(x => x != null);
    }

}
