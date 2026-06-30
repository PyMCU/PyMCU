using System;
using System.Collections.Generic;
using System.Linq;
using PyMCU.Common;

namespace PyMCU.Frontend;

// Coroutine -> state-machine desugar (RFC: async/await).
//
// An `async def` is rewritten, at the AST level, into a zero-cost ZCA class with a
// `poll()` method -- the exact pattern users would otherwise hand-write (and that the
// async runtime PoC proved works). Each `await` is a suspension point: the body is
// split into numbered states, locals that must survive a suspension become instance
// fields, and `poll()` is a dispatch on `self._state` that runs the code between
// awaits and yields (returns PENDING) until each awaited future is ready.
//
// poll() returns 1 while the coroutine is still running (PENDING) and 0 when it has
// finished (DONE). Drive it from a cooperative loop (the executor), or one coroutine
// per RTOS task.
//
// v1 scope: the awaitable is `await sleep(n)` / `await sleep_ms(n)` -- a relative
// delay measured against a user-provided monotonic `ticks()` (wrap-safe elapsed
// compare). The body may be straight-line and/or wrapped in a single `while True:`
// loop. `await` inside `if`/nested loops, and awaiting arbitrary futures, are future
// work (a clear error is raised so the limitation is explicit, never miscompiled).
public static class AsyncTransform
{
    public static void TransformProgram(ProgramNode prog)
    {
        var asyncFns = prog.Functions.Where(f => f.IsAsync).ToList();
        if (asyncFns.Count == 0) return;

        // Using `async def` requires `import asyncio` (as in CPython, where the
        // keywords pair with the asyncio runtime). The local name bound to the module
        // is how `await asyncio.sleep(...)` and the generated ticks() are spelled.
        string? alias = FindAsyncioAlias(prog);
        if (alias == null)
            throw new SyntaxError(
                "this module uses `async def` but never imports asyncio. Add `import asyncio` " +
                "(or `import pymcu.asyncio as asyncio`) and await `asyncio.sleep(...)`.", 0, 0);

        foreach (var fn in asyncFns)
        {
            prog.Functions.Remove(fn);
            prog.GlobalStatements.Add(TransformFunction(fn, alias));
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
        public Block Block = new();
    }

    public static ClassDef TransformFunction(FunctionDef fn, string asyncioAlias)
    {
        var paramNames = fn.Params.Select(p => p.Name).ToList();

        // Fields = params + every assigned local. (v1 lifts ALL locals to fields, not
        // just those live across an await -- simpler and correct; the RAM cost is a few
        // words per coroutine.)
        var localNames = new List<string>();
        CollectAssignedLocals(fn.Body, new HashSet<string>(paramNames), localNames);
        var fields = new HashSet<string>(paramNames);
        foreach (var l in localNames) fields.Add(l);

        var b = new Builder(fields, asyncioAlias);
        b.Build(fn.Body, fn.Name);

        // __init__(self, <params>): _state=0, params, locals, await start-timestamps.
        var initBody = new Block();
        initBody.Statements.Add(SelfDecl("_state", "uint32", Int(0)));
        foreach (var p in fn.Params)
            initBody.Statements.Add(SelfDecl(p.Name, ScalarType(p.Type), new VariableExpr(p.Name)));
        foreach (var l in localNames)
            initBody.Statements.Add(SelfDecl(l, "uint32", Int(0)));
        foreach (var sf in b.StartFields)
            initBody.Statements.Add(SelfDecl(sf, "uint32", Int(0)));

        var initParams = new List<Param> { new Param("self", "") };
        initParams.AddRange(fn.Params);
        // Constructors are force-inlined (expanded at the construction site), matching how
        // the parser marks a hand-written __init__; otherwise it gets outlined and slot
        // construction can't find it.
        var initFn = new FunctionDef("__init__", initParams, "", initBody, isInline: true);

        // poll(self) -> uint32: dispatch on _state.
        var pollBody = new Block();
        foreach (var st in b.States)
            pollBody.Statements.Add(new IfStmt(
                new BinaryExpr(SelfRef("_state"), BinaryOp.Equal, Int(st.Id)), st.Block));
        pollBody.Statements.Add(new ReturnStmt(Int(0))); // DONE fallthrough
        var pollFn = new FunctionDef("poll", new List<Param> { new Param("self", "") }, "uint32", pollBody);

        var classBody = new Block();
        classBody.Statements.Add(initFn);
        classBody.Statements.Add(pollFn);
        return new ClassDef(fn.Name, new List<string>(), classBody) { Line = fn.Line };
    }

    // ── State splitting ──────────────────────────────────────────────────────
    private sealed class Builder
    {
        private readonly HashSet<string> _fields;
        private readonly string _aio;
        public readonly List<State> States = new();
        public readonly List<string> StartFields = new();
        private int _nextId;
        private int _awaitCounter;

        public Builder(HashSet<string> fields, string asyncioAlias)
        {
            _fields = fields;
            _aio = asyncioAlias;
        }

        private int NewId() => _nextId++;

        public void Build(Block body, string fnName)
        {
            var (pre, loopBody) = SplitTrailingWhileTrue(body.Statements);

            int startId = NewId();
            var cur = new List<Statement>();
            int curId = startId;

            curId = Process(pre, ref cur, curId, fnName);

            if (loopBody == null)
            {
                Finish(curId, cur, done: true);
                return;
            }

            // Transition from the pre-amble straight into the loop's entry state.
            int loopEntry = NewId();
            cur.Add(SetState(loopEntry));
            Finish(curId, cur, done: false);

            cur = new List<Statement>();
            curId = loopEntry;
            curId = Process(loopBody.Statements, ref cur, curId, fnName);

            // End of the loop body -> back to the loop entry state.
            cur.Add(SetState(loopEntry));
            Finish(curId, cur, done: false);
        }

        // Append statements to the current state; each `await` closes the current state
        // and opens a wait state + a continuation state. Returns the (possibly new)
        // current state id.
        private int Process(List<Statement> stmts, ref List<Statement> cur, int curId, string fnName)
        {
            foreach (var s in stmts)
            {
                if (TryGetAwaitSleep(s, _aio, out var durUsExpr))
                {
                    string sf = "_start" + _awaitCounter++;
                    StartFields.Add(sf);

                    // Arm: record the start timestamp (asyncio.ticks(), microseconds), suspend.
                    cur.Add(SelfAssign(sf, AioCall("ticks")));
                    int waitId = NewId();
                    cur.Add(SetState(waitId));
                    Finish(curId, cur, done: false);

                    // Wait state: stay PENDING until `asyncio.ticks() - start >= duration_us`.
                    int afterId = NewId();
                    var waitStmts = new List<Statement>
                    {
                        new IfStmt(
                            new BinaryExpr(
                                new BinaryExpr(AioCall("ticks"), BinaryOp.Sub, SelfRef(sf)),
                                BinaryOp.Less,
                                Rewrite(durUsExpr)),
                            ReturnBlock(1)),
                        SetState(afterId),
                    };
                    Finish(waitId, waitStmts, done: false);

                    cur = new List<Statement>();
                    curId = afterId;
                }
                else
                {
                    cur.Add(RewriteStmt(s, fnName));
                }
            }
            return curId;
        }

        private void Finish(int id, List<Statement> stmts, bool done)
        {
            var block = new Block();
            block.Statements.AddRange(stmts);
            block.Statements.Add(new ReturnStmt(Int(done ? 0 : 1)));
            States.Add(new State { Id = id, Block = block });
        }

        private Statement SetState(int id) => SelfAssign("_state", Int(id));

        // `<asyncio>.<method>()` -- a dotted call on the imported asyncio module.
        private Expression AioCall(string method) =>
            new CallExpr(new MemberAccessExpr(new VariableExpr(_aio), method), new List<Expression>());

        // ── local -> self.field rewriting ────────────────────────────────────
        private Statement RewriteStmt(Statement s, string fnName)
        {
            switch (s)
            {
                case ExprStmt es:
                    return new ExprStmt(Rewrite(es.Expr)) { Line = es.Line };
                case AssignStmt asg:
                    return new AssignStmt(Rewrite(asg.Target), Rewrite(asg.Value))
                        { Line = asg.Line, AnnotatedType = asg.AnnotatedType };
                case AugAssignStmt aug:
                    return new AugAssignStmt(Rewrite(aug.Target), aug.Op, Rewrite(aug.Value)) { Line = aug.Line };
                case IfStmt iff:
                    if (ContainsAwait(iff))
                        throw new SyntaxError(
                            $"async def '{fnName}': `await` inside an if/loop is not supported yet " +
                            "(v1 allows awaits at the top level of the body or a single `while True:`).", 0, 0);
                    var elifs = iff.ElifBranches
                        .Select(e => (Rewrite(e.Item1), (Statement)RewriteBlock(e.Item2, fnName))).ToList();
                    return new IfStmt(Rewrite(iff.Condition), RewriteBlock(iff.ThenBranch, fnName), elifs,
                        iff.ElseBranch == null ? null : RewriteBlock(iff.ElseBranch, fnName)) { Line = iff.Line };
                case Block blk:
                    return RewriteBlock(blk, fnName);
                default:
                    throw new SyntaxError(
                        $"async def '{fnName}': statement type {s.GetType().Name} is not supported in a " +
                        "coroutine body yet (v1: assignments, calls, simple if, and `await sleep(...)`).", 0, 0);
            }
        }

        private Block RewriteBlock(Statement s, string fnName)
        {
            var outBlk = new Block();
            if (s is Block b)
                foreach (var st in b.Statements) outBlk.Statements.Add(RewriteStmt(st, fnName));
            else
                outBlk.Statements.Add(RewriteStmt(s, fnName));
            return outBlk;
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
                    throw new SyntaxError("`await` is only valid as a statement in v1 (e.g. `await sleep(n)`).", 0, 0);
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
        throw new SyntaxError(
            $"async: only `await {aio}.sleep(n)` / `await {aio}.sleep_ms(n)` are supported.", 0, 0);
    }

    private static bool ContainsAwait(Statement s)
    {
        switch (s)
        {
            case ExprStmt es: return ContainsAwait(es.Expr);
            case AssignStmt a: return ContainsAwait(a.Value) || ContainsAwait(a.Target);
            case AugAssignStmt ag: return ContainsAwait(ag.Value);
            case ReturnStmt r: return r.Value != null && ContainsAwait(r.Value);
            case IfStmt iff:
                return ContainsAwait(iff.Condition) || ContainsAwait(iff.ThenBranch)
                    || (iff.ElseBranch != null && ContainsAwait(iff.ElseBranch))
                    || iff.ElifBranches.Any(e => ContainsAwait(e.Item1) || ContainsAwait(e.Item2));
            case WhileStmt w: return ContainsAwait(w.Condition) || ContainsAwait(w.Body);
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

    private static (List<Statement> Pre, Block? Loop) SplitTrailingWhileTrue(List<Statement> stmts)
    {
        if (stmts.Count > 0 && stmts[^1] is WhileStmt w && IsTrue(w.Condition) && w.Body is Block lb)
            return (stmts.Take(stmts.Count - 1).ToList(), lb);
        return (stmts, null);
    }

    private static bool IsTrue(Expression e) =>
        e is BooleanLiteral { Value: true } || (e is IntegerLiteral i && i.Value != 0);

    private static void CollectAssignedLocals(Statement s, HashSet<string> exclude, List<string> outList)
    {
        void Add(string n)
        {
            if (!exclude.Contains(n) && !outList.Contains(n)) outList.Add(n);
        }
        switch (s)
        {
            case AssignStmt a when a.Target is VariableExpr v: Add(v.Name); break;
            case Block b: foreach (var st in b.Statements) CollectAssignedLocals(st, exclude, outList); break;
            case IfStmt iff:
                CollectAssignedLocals(iff.ThenBranch, exclude, outList);
                if (iff.ElseBranch != null) CollectAssignedLocals(iff.ElseBranch, exclude, outList);
                foreach (var e in iff.ElifBranches) CollectAssignedLocals(e.Item2, exclude, outList);
                break;
            case WhileStmt w: CollectAssignedLocals(w.Body, exclude, outList); break;
            case ForStmt f: CollectAssignedLocals(f.Body, exclude, outList); break;
        }
    }

    private static string ScalarType(string t) => string.IsNullOrEmpty(t) ? "uint32" : t;

    private static Expression SelfRef(string field) => new MemberAccessExpr(new VariableExpr("self"), field);
    private static Expression Call(string fn) => new CallExpr(new VariableExpr(fn), new List<Expression>());
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
