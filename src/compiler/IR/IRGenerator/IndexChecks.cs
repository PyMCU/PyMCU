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
    // A subscript with a run-time index into a fixed-size sequence (a typed array, a tuple or
    // list table, a string) is checked against the sequence's length, as CPython does: an index
    // outside it raises IndexError instead of reading or writing whatever sits next to the data,
    // and a negative one counts from the end (a[-1]). The check is dropped where the index is
    // PROVEN to be in range, and only where the proof is structural -- never a fact learned from
    // the flow of values, which is what the earlier range-narrowing attempts got wrong:
    //
    //   - the index type is unsigned and cannot reach the length (a uint8 into 256 elements);
    //   - the index is `x & MASK` with a constant MASK below the length;
    //   - the index is `x % N` with a constant N within the length and an unsigned result;
    //   - the index is the counter of an enclosing `for v in range(...)` whose bounds are
    //     constants within the length, and whose body never assigns v (see provenLoopCounters).
    //
    // Constant indexes never get here: they are diagnosed at compile time.

    // An interrupt service routine has no caller to receive an exception, and neither has anything
    // it calls: CanFailAnalyzer rejects an ISR that can raise. The standard library's ring buffers
    // are indexed in ISRs, so code reachable from an ISR is not checked -- the contract it already
    // had. The set is computed before lowering from the call names in the source, so it errs on the
    // side of leaving a function unchecked rather than refusing the build.
    private HashSet<FunctionDef> isrReachableFunctions = new();
    private bool suppressIndexChecks;

    private void ComputeIsrReachableFunctions(ProgramNode main, IEnumerable<ProgramNode> imported)
    {
        var byName = new Dictionary<string, List<FunctionDef>>();
        var all = new List<FunctionDef>();

        void CollectDefs(Statement? st)
        {
            foreach (var s in TypeInference.WalkStatements(st))
            {
                switch (s)
                {
                    case FunctionDef fd:
                        all.Add(fd);
                        if (!byName.TryGetValue(fd.Name, out var l)) byName[fd.Name] = l = new();
                        l.Add(fd);
                        CollectDefs(fd.Body);
                        break;
                    case ClassDef cd:
                        CollectDefs(cd.Body);
                        break;
                }
            }
        }

        foreach (var p in imported.Prepend(main))
        {
            foreach (var st in p.GlobalStatements) CollectDefs(st);
            foreach (var fn in p.Functions)
            {
                all.Add(fn);
                if (!byName.TryGetValue(fn.Name, out var l)) byName[fn.Name] = l = new();
                l.Add(fn);
                CollectDefs(fn.Body);
            }
        }

        var reach = new HashSet<FunctionDef>();
        var work = new Stack<FunctionDef>();
        foreach (var f in all.Where(f => f.IsInterrupt)) { reach.Add(f); work.Push(f); }

        // The library registers its handlers with `compile_isr(handler, vector)` rather than
        // decorating them, and the compiler marks the function as an interrupt only once every
        // function is lowered; the registration is the earlier evidence.
        var registered = new HashSet<string>();
        void FindRegistrations(IEnumerable<Statement> stmts)
        {
            foreach (var e in TypeInference.WalkExpressions(TypeInference.WalkStatements(stmts).ToList()))
                if (e is CallExpr { Callee: VariableExpr { Name: "compile_isr" } } c
                    && c.Args.Count > 0 && c.Args[0] is VariableExpr h)
                    registered.Add(h.Name);
        }
        foreach (var p in imported.Prepend(main))
        {
            FindRegistrations(p.GlobalStatements);
            foreach (var fn in p.Functions) FindRegistrations(new[] { (Statement)fn.Body });
        }
        foreach (var f in all)
            FindRegistrations(new[] { (Statement)f.Body });
        foreach (var name in registered)
            if (byName.TryGetValue(name, out var handlers))
                foreach (var f in handlers)
                    if (reach.Add(f)) work.Push(f);
        while (work.Count > 0)
        {
            var f = work.Pop();
            var names = new HashSet<string>();
            foreach (var e in TypeInference.WalkExpressions(TypeInference.WalkStatements(f.Body).ToList()))
            {
                if (e is CallExpr { Callee: VariableExpr cv }) names.Add(cv.Name);
                else if (e is CallExpr { Callee: MemberAccessExpr cm }) names.Add(cm.Member);
            }
            foreach (var n in names)
                if (byName.TryGetValue(n, out var targets))
                    foreach (var t in targets)
                        if (reach.Add(t)) work.Push(t);
        }
        isrReachableFunctions = reach;
    }

    // A buffer whose length is only known at run time (a bytearray(n) in the arena) has no
    // constant size to compare against; its own length variable would be the bound, and that is a
    // separate piece of work. Everything else registered in arraySizes has a fixed length.
    private bool IndexCheckAppliesTo(string arrayKey) =>
        !arenaBufferNames.Contains(arrayKey) && !arenaBufferNames.Contains(FollowAliases(arrayKey));

    // The parts of PyMCU's own runtime left unchecked on purpose, one entry per place with the reason
    // its index is guaranteed. The user's code and the third-party libraries (adafruit_framebuf and
    // the rest) are what the check is for and stay checked. The path of the file being lowered is
    // used, so an @inline helper expanded into user code is judged by where it is written.
    private static readonly string[] UncheckedRuntimeFiles =
    {
        // The arena is the backing store of run-time-sized bytearrays: the compiler adds the element
        // index to an offset alloc() returned and reads `_arena[off]`, so comparing that sum with the
        // arena's size would say nothing about the buffer it belongs to. A buffer's real bound is its
        // own run-time length, which is not checked yet.
        "/pymcu/arena.py",

        // The float printer fills digit buffers it sized for the worst case, with counters the
        // algorithm bounds (xd[nd - 1 - i] with nd <= the buffer and i < nd); nothing the caller
        // passes reaches those indexes.
        "/pymcu/hal/uart_text.py",
    };

    // Functions of the HAL whose index is internal state, not something the program passes. Each
    // entry is (file, function); the rest of the HAL stays checked, so a buffer index or a length
    // that comes from the program (PulseIn[i], a transfer length) still raises.
    private static readonly (string File, string Function, string Why)[] UncheckedRuntimeFunctions =
    {
        // The UART receive ring: `_rx_tail` advances with `& 0x3F` and the buffer has 64 slots, so
        // the tail is inside it by construction; only the receive interrupt writes `_rx_head`.
        ("/pymcu/hal/avr/uart/avr.py", "uart_rx_read", "ring tail masked to the 64-slot buffer"),
        ("/pymcu/hal/avr/uart/avr.py", "uart_rx_read_timeout", "ring tail masked to the 64-slot buffer"),
        ("/pymcu/hal/avr/uart/atmega32u4.py", "uart_rx_read", "ring tail masked to the 64-slot buffer"),
        ("/pymcu/hal/avr/uart/attiny2313.py", "uart_rx_read", "ring tail masked to the 64-slot buffer"),
        // The pulse capture ring: `_pulse_tail` wraps to 0 when it reaches `_pulse_maxlen`, the
        // size the buffer was created with.
        ("/pymcu/hal/avr/pulse/atmega328p.py", "pulse_capture_popleft", "ring tail wraps at the buffer size"),
    };

    // Deliberately unlike CPython: code reachable from an interrupt handler is not checked
    // (suppressIndexChecks above). An index out of range inside an ISR does not raise -- an ISR has
    // no caller to receive the exception and CanFailAnalyzer refuses one that could raise.

    private bool SourceIsUncheckedRuntime()
    {
        string path = "/" + (currentSourcePath ?? "").Replace('\\', '/').TrimStart('/');
        foreach (var f in UncheckedRuntimeFiles)
            if (path.EndsWith(f, StringComparison.Ordinal)) return true;

        // The innermost function being lowered: an @inline callee's own name while it is expanded,
        // otherwise the function being compiled.
        string fn = inlineStack.Count > 0 && inlineStack[^1].CalleeName.Length > 0
            ? inlineStack[^1].CalleeName : currentFunction;
        foreach (var (file, function, _) in UncheckedRuntimeFunctions)
            if (path.EndsWith(file, StringComparison.Ordinal)
                && (fn == function || fn.EndsWith("_" + function, StringComparison.Ordinal)))
                return true;
        return false;
    }

    private int indexChecksEmitted;
    private int indexChecksElided;
    private int indexChecksReused;

    // A second access with the same index, in straight-line code with nothing in between that could
    // change it, reuses the first check's answer (the index itself, or its adjusted copy when it is
    // signed) instead of checking again. Keyed by the index's name and the length; cleared at every
    // label except the check's own, and when the index is written. Anything the cache does not
    // understand clears all of it, so the worst case is a second check, never a skipped one.
    private readonly Dictionary<(string Name, int Size), Val> indexCheckCache = new();
    private readonly HashSet<string> indexOkLabels = new();

    private static string? ValName(Val v) => v switch
    {
        Variable x => x.Name,
        Temporary t => t.Name,
        _ => null,
    };

    private void NoteInstructionForIndexChecks(Instruction inst)
    {
        if (indexCheckCache.Count == 0) return;
        switch (inst)
        {
            case Label l:
                if (!indexOkLabels.Contains(l.Name)) indexCheckCache.Clear();
                return;
            case DebugLine or ArrayStore or BytearrayStore or ArrayLoad or ArrayLoadFlash or BytearrayLoad
                or JumpIfLessThan or JumpIfGreaterOrEqual or JumpIfZero or JumpIfNotZero or JumpIfEqual
                or JumpIfNotEqual or JumpIfLessOrEqual or JumpIfGreaterThan or Jump or SignalError:
                return;
            case Copy c:
                DropIndexFactsFor(ValName(c.Dst));
                return;
            case AugAssign a:
                DropIndexFactsFor(ValName(a.Target));
                return;
            case Binary b:
                DropIndexFactsFor(ValName(b.Dst));
                return;
            default:
                indexCheckCache.Clear();
                return;
        }
    }

    private void DropIndexFactsFor(string? name)
    {
        if (name == null) { indexCheckCache.Clear(); return; }
        // An expression entry may mention any variable, so a write to one that is not a temporary
        // drops them all; a temporary is a fresh name no expression refers to.
        if (!name.StartsWith("tmp_", StringComparison.Ordinal))
            foreach (var k in indexCheckCache.Keys.Where(k => k.Name.StartsWith("expr:", StringComparison.Ordinal)).ToList())
                indexCheckCache.Remove(k);
        foreach (var k in indexCheckCache.Keys.Where(k => k.Name == name).ToList())
            indexCheckCache.Remove(k);
    }

    // Counter key (as the index Val names it) -> the exclusive upper bound it is proven to stay
    // below, for the duration of the loop body being lowered. The key is the qualified name the
    // loop variable lives under, so an @inline callee's own variable of the same bare name is a
    // different key and does not borrow the proof.
    private readonly Dictionary<string, int> provenLoopCounters = new();

    // Counters of range() loops that start at a constant >= 0 and step by a constant > 0, whatever
    // their stop: such a counter is never negative while the body runs and nothing assigns it.
    private readonly HashSet<string> nonNegativeLoopCounters = new();

    // Whether the expression can never be negative, by its shape alone: an unsigned variable, a
    // non-negative literal, a non-negative range counter, a sum or product of those, an AND with
    // a non-negative side, a right shift or a floor-mod by a positive constant of one. Anything
    // else is unknown, and unknown means the negative path stays.
    private bool ExprIsNonNegative(Expression e)
    {
        switch (e)
        {
            case IntegerLiteral il: return il.Value >= 0;
            case VariableExpr v:
            {
                string key = QualifyLoopVar(v.Name);
                if (nonNegativeLoopCounters.Contains(key)) return true;
                return variableTypes.TryGetValue(key, out var vt) && IsIntegerType(vt) && !vt.IsSigned();
            }
            case BinaryExpr { Op: Frontend.BinaryOp.Add or Frontend.BinaryOp.Mul } b:
                return ExprIsNonNegative(b.Left) && ExprIsNonNegative(b.Right);
            case BinaryExpr { Op: Frontend.BinaryOp.BitAnd } b:
                return ExprIsNonNegative(b.Left) || ExprIsNonNegative(b.Right);
            case BinaryExpr { Op: Frontend.BinaryOp.Mod } b:
                return TryFoldInt(b.Right, out int d) && d > 0;
            case BinaryExpr { Op: Frontend.BinaryOp.RShift } b:
                return ExprIsNonNegative(b.Left);
            default:
                return false;
        }
    }

    // A canonical text for a side-effect-free index expression, so that two evaluations of it in
    // straight-line code are recognised as the same value. Null when the shape is not understood.
    private static string? PureExprKey(Expression? e) => e switch
    {
        IntegerLiteral il => il.Value.ToString(),
        VariableExpr v => v.Name,
        BinaryExpr b when PureExprKey(b.Left) is { } l && PureExprKey(b.Right) is { } r
            => "(" + l + " " + b.Op + " " + r + ")",
        _ => null,
    };

    // Arrays declared as bytearray(...) or bytes(...), which CPython words differently from a
    // list when an index misses.
    private readonly HashSet<string> byteBufferArrays = new();

    // The wording for a constant sequence held under a name: a tuple says so, anything else is a list.
    private string SequenceIndexText(string name) =>
        IsTupleBound(name) ? "tuple index out of range" : "list index out of range";

    private string IndexErrorText(string arrayKey, bool store)
    {
        if (byteBufferArrays.Contains(arrayKey))
            return arrayKey.Length > 0 && bytesKindArrays.Contains(arrayKey)
                ? "index out of range" : "bytearray index out of range";
        return store ? "list assignment index out of range" : "list index out of range";
    }

    private readonly HashSet<string> bytesKindArrays = new();

    /// <summary>
    /// Returns the index to load or store with: the original when the check was elided or the
    /// index is unsigned, the original adjusted for a negative value when it is signed. Emits the
    /// IndexError path otherwise.
    /// </summary>
    private Val CheckRuntimeIndex(Val idx, int size, string text, Expression? indexExpr)
    {
        if (size <= 0 || idx is Constant || suppressIndexChecks || SourceIsUncheckedRuntime()) return idx;

        DataType t = GetValType(idx);
        if (!IsIntegerType(t)) return idx;

        if (IndexIsProvenInRange(idx, t, size, indexExpr))
        {
            indexChecksElided++;
            return idx;
        }

        string? idxName = ValName(idx);
        if (idxName != null && indexCheckCache.TryGetValue((idxName, size), out var checkedIdx))
        {
            indexChecksReused++;
            return checkedIdx;
        }
        // The same index EXPRESSION seen again with nothing written in between (`a[i + j] = f(a[i + j])`
        // evaluates `i + j` into a fresh temporary each time, so the name above never matches).
        string? exprKey = PureExprKey(indexExpr);
        if (exprKey != null && indexCheckCache.TryGetValue(("expr:" + exprKey, size), out var checkedByExpr))
        {
            indexChecksReused++;
            return checkedByExpr;
        }
        bool nonNegative = !t.IsSigned() || (indexExpr != null && ExprIsNonNegative(indexExpr));

        indexChecksEmitted++;
        Val result = idx;

        // Outside any try/finally the failure path does not depend on where the check sits: it
        // records the text and signals, and a signal leaves the function. So every such check in
        // the function jumps to ONE tail emitted after the function body, and a check costs a
        // compare and a branch instead of a compare and a copy of the raise.
        if (tryCatchStack.Count == 0 && finallyStack.Count == 0)
        {
            string tail = SharedIndexFailLabel(text);
            if (!nonNegative)
            {
                Temporary adj = MakeTemp(t);
                Emit(new Copy(idx, adj));
                string nonNeg = MakeLabel();
                Emit(new JumpIfGreaterOrEqual(adj, new Constant(0), nonNeg));
                Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, adj, new Constant(size)));
                Emit(new JumpIfLessThan(adj, new Constant(0), tail));
                Emit(new Label(nonNeg));
                Emit(new JumpIfGreaterOrEqual(adj, new Constant(size), tail));
                result = adj;
            }
            else
            {
                Emit(new JumpIfGreaterOrEqual(idx, new Constant(size), tail));
            }
            if (idxName != null) indexCheckCache[(idxName, size)] = result;
            if (exprKey != null) indexCheckCache[("expr:" + exprKey, size)] = result;
            return result;
        }

        string ok = MakeLabel();
        indexOkLabels.Add(ok);
        string fail = MakeLabel();
        if (!nonNegative)
        {
            // adj = idx, plus the length when negative; out of range if still negative or >= length.
            Temporary adj = MakeTemp(t);
            Emit(new Copy(idx, adj));
            string nonNeg = MakeLabel();
            Emit(new JumpIfGreaterOrEqual(adj, new Constant(0), nonNeg));
            Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, adj, new Constant(size)));
            Emit(new JumpIfLessThan(adj, new Constant(0), fail));
            Emit(new Label(nonNeg));
            Emit(new JumpIfLessThan(adj, new Constant(size), ok));
            result = adj;
        }
        else
        {
            Emit(new JumpIfLessThan(idx, new Constant(size), ok));
        }
        Emit(new Label(fail));
        RecordRuntimeExceptionText("IndexError", text);
        EmitRaiseUnwind(new Constant(3 /* IndexError */), unhandledInMain: true);
        Emit(new Label(ok));
        if (idxName != null) indexCheckCache[(idxName, size)] = result;
        if (exprKey != null) indexCheckCache[("expr:" + exprKey, size)] = result;
        return result;
    }

    // Text -> label of the failure tail the function being lowered will end with.
    private Dictionary<string, string> sharedIndexFailTails = new();

    private readonly HashSet<string> everSharedIndexFailLabels = new();

    private string SharedIndexFailLabel(string text)
    {
        if (!sharedIndexFailTails.TryGetValue(text, out var label))
        {
            sharedIndexFailTails[text] = label = MakeLabel();
            everSharedIndexFailLabels.Add(label);
        }
        return label;
    }

    // A check jumps to a tail that its function emits when the body is done. A path that builds a
    // function body without going through the two places that flush them would leave a jump to a
    // label nothing defines, which the linker reports as "undefined reference to L_105" long after
    // the cause. Looking for it here turns that into a compiler error at the function.
    private void VerifyIndexFailTailsDefined(ProgramIR program)
    {
        if (everSharedIndexFailLabels.Count == 0) return;
        foreach (var fn in program.Functions)
        {
            var defined = new HashSet<string>(fn.Body.OfType<Label>().Select(l => l.Name));
            foreach (var ins in fn.Body)
            {
                string? target = ins switch
                {
                    JumpIfLessThan j => j.Target,
                    JumpIfGreaterOrEqual j => j.Target,
                    _ => null,
                };
                if (target != null && everSharedIndexFailLabels.Contains(target) && !defined.Contains(target))
                    throw new PyMCU.Common.CompilerError("CompileError",
                        $"internal error: the index-check failure tail {target} is not defined in "
                        + $"'{fn.Name}'; this function body was built by a path that does not flush them",
                        lastLine);
            }
        }
    }

    // Emits the failure tails the checks of this function jumped to. Called once the body is done.
    private void FlushSharedIndexFailures()
    {
        foreach (var (text, label) in sharedIndexFailTails)
        {
            Emit(new Label(label));
            RecordRuntimeExceptionText("IndexError", text);
            EmitRaiseUnwind(new Constant(3 /* IndexError */), unhandledInMain: true);
        }
        sharedIndexFailTails.Clear();
    }

    private bool IndexIsProvenInRange(Val idx, DataType t, int size, Expression? indexExpr)
    {
        // An unsigned index whose largest value is below the length.
        if (!t.IsSigned())
        {
            long max = t.SizeOf() switch { 1 => 255L, 2 => 65535L, _ => uint.MaxValue };
            if (max < size) return true;
        }

        // The counter of an enclosing range() loop that fits.
        if (idx is Variable v && provenLoopCounters.TryGetValue(v.Name, out int bound) && bound <= size)
            return true;

        // `x & MASK`, either side the constant.
        if (indexExpr is BinaryExpr { Op: Frontend.BinaryOp.BitAnd } band)
        {
            if (TryFoldInt(band.Right, out int m1) && m1 >= 0 && m1 < size) return true;
            if (TryFoldInt(band.Left, out int m2) && m2 >= 0 && m2 < size) return true;
        }

        // `x % N` with an unsigned result: always below N.
        if (!t.IsSigned() && indexExpr is BinaryExpr { Op: Frontend.BinaryOp.Mod } mod
            && TryFoldInt(mod.Right, out int n) && n >= 1 && n <= size)
            return true;

        return false;
    }

    // Called by the range() loop lowering once its counter exists: registers the proof for the
    // body when the bounds are constants that stay within a non-negative window and nothing in
    // the body assigns the counter. Returns the key to release with ReleaseLoopCounter, or null.
    private string? ProveLoopCounter(ForStmt stmt, string counterKey, Val start, Val stop, Val step)
    {
        if (start is not Constant { Value: >= 0 } || step is not Constant { Value: > 0 })
            return null;
        var names = new HashSet<string>();
        var receivers = new HashSet<(string, string)>();
        CollectMutatedNames(stmt.Body, names, receivers);
        if (names.Contains(stmt.VarName)) return null;
        nonNegativeLoopCounters.Add(counterKey);
        if (stop is Constant stopC
            && !(provenLoopCounters.TryGetValue(counterKey, out int existing) && existing <= stopC.Value))
            provenLoopCounters[counterKey] = stopC.Value;
        return counterKey;
    }

    private void ReleaseLoopCounter(string? key)
    {
        if (key != null)
        {
            provenLoopCounters.Remove(key);
            nonNegativeLoopCounters.Remove(key);
        }
    }
}
