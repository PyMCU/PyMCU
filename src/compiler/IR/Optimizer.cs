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

using System.Text;
using PyMCU.Common;
using PyMCU.IR.CFG;

namespace PyMCU.IR;

public static class Optimizer
{
    public static ProgramIR Optimize(ProgramIR program)
    {
        // PyMCU lays out locals in a static (overlay) frame, so a function that can
        // reach itself through synchronous calls would alias its own storage. Reject
        // it up front with a clear diagnostic instead of emitting silently-broken code.
        DetectRecursion(program);

        var optimized = new ProgramIR
        {
            Globals = [..program.Globals],
            GlobalArrays = new Dictionary<string, int>(program.GlobalArrays),
            Functions = program.Functions.Select(CloneFunction).ToList(),
            ExternSymbols = new List<string>(program.ExternSymbols),
            // Preserve class hierarchy for the devirt pass and downstream codegen.
            ClassChildren = new Dictionary<string, HashSet<string>>(
                program.ClassChildren.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value))),
            ClassDirectMethods = new Dictionary<string, HashSet<string>>(
                program.ClassDirectMethods.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value))),
        };

        // Build the set of global variable names so EliminateDeadVariableStores
        // never removes stores to module-level globals (they are read by other functions).
        var globalNames = new HashSet<string>(optimized.Globals.Select(g => g.Name));
        foreach (var arrName in optimized.GlobalArrays.Keys)
            globalNames.Add(arrName);

        // Globals shared between ISR and non-ISR context carry volatile semantics:
        // an ISR may rewrite them between any two instructions, so PropagateCopies
        // must never fold a read to a previously stored constant. Computed before
        // the per-function passes run so they see the full, unoptimized bodies.
        var isrShared = ComputeIsrSharedGlobals(optimized);

        foreach (var func in optimized.Functions)
            OptimizeFunction(func, globalNames, isrShared);

        // Parameterized outlining of @inline expansions. Runs after the cleanup
        // passes so the folded constants are visible (addresses/flags are baked,
        // not live-in locals) and the regions are already dead-store-free; the
        // freshly synthesised subroutines are then optimised individually.
        // See OutlineInlineExpansions for the full contract.
        var preOutline = new HashSet<string>(optimized.Functions.Select(f => f.Name));
        OutlineInlineExpansions(optimized, globalNames);
        foreach (var func in optimized.Functions)
            if (!preOutline.Contains(func.Name))
                OptimizeFunction(func, globalNames, isrShared);

        // Dead Function Elimination (DFE): remove functions that are never reachable
        // from main or any ISR.
        var callGraph = new Dictionary<string, HashSet<string>>();
        foreach (var func in optimized.Functions)
        {
            var callees = new HashSet<string>();
            foreach (var instr in func.Body)
            {
                if (instr is Call call) callees.Add(call.FunctionName);
                // Also treat FunctionRef values as call-graph edges so that
                // functions captured as Callable pointers survive DFE.
                RegisterUses(instr, val =>
                {
                    if (val is FunctionRef fref) callees.Add(fref.FunctionName);
                });
            }

            callGraph[func.Name] = callees;
        }

        var reachable = new HashSet<string>();
        var worklist = new Queue<string>();

        Enqueue("main");
        foreach (var func in optimized.Functions.Where(func => func.IsInterrupt))
        {
            Enqueue(func.Name);
        }

        while (worklist.Count > 0)
        {
            var cur = worklist.Dequeue();
            if (!callGraph.TryGetValue(cur, out var callees)) continue;
            foreach (var callee in callees) Enqueue(callee);
        }

        // Prune functions unreachable from main / any ISR. Only do this when a "main"
        // root exists, so passes that optimize a function in isolation (no entry point)
        // keep their functions. Edges come from Call and FunctionRef uses, matching the
        // reachability the DGE below already trusts; the per-target backend runs its own
        // call-graph DCE on top, so this is a conservative early prune.
        if (optimized.Functions.Any(f => f.Name == "main"))
            optimized.Functions.RemoveAll(f => !reachable.Contains(f.Name));

        // --- Dead Global Elimination (DGE) ---
        // A global is live only if a Variable with that name is referenced
        // (read OR written) inside a reachable function body.  Globals that
        // come from transitively-imported stdlib modules but are never touched
        // by reachable code (e.g. pymcu.exceptions.* when no raise/except is
        // used, _millis_count when millis_init() is never called) are removed
        // so they do not inflate _bssSize and trigger the BSS-zeroing loop.
        {
            var globalVarNames = new HashSet<string>(optimized.Globals.Select(g => g.Name));
            globalVarNames.UnionWith(optimized.GlobalArrays.Keys);

            var referencedGlobals = new HashSet<string>();
            foreach (var func in optimized.Functions.Where(f => reachable.Contains(f.Name)))
            {
                foreach (var instr in func.Body)
                {
                    // Collect all Val uses (reads)
                    RegisterUses(instr, val =>
                    {
                        if (val is Variable v && globalVarNames.Contains(v.Name))
                            referencedGlobals.Add(v.Name);
                        // ArrayBase (address-of) keeps a global array alive even when it is
                        // only ever passed by address -- e.g. a ZCA SRAM slot whose fields are
                        // written/read through a self pointer inside a factory/method, never by
                        // a direct ArrayStore in this function. (RFC 0001 Model B sret.)
                        else if (val is ArrayBase ab && globalVarNames.Contains(ab.ArrayName))
                            referencedGlobals.Add(ab.ArrayName);
                    });
                    // Also capture write destinations (Copy, AugAssign, etc.)
                    var dst = GetDst(instr);
                    if (dst is Variable vDst && globalVarNames.Contains(vDst.Name))
                        referencedGlobals.Add(vDst.Name);
                    if (instr is AugAssign { Target: Variable aaDst } && globalVarNames.Contains(aaDst.Name))
                        referencedGlobals.Add(aaDst.Name);

                    // Conservative: if a naked InlineAsm string literally contains
                    // a global's mangled name, keep that global alive.  This handles
                    // rare cases where raw asm references a global by its .equ symbol
                    // name without going through an IR Variable operand.
                    // Note: standard delay-loop asm ("PUSH R24", "DEC R25", etc.)
                    // never contains global variable names, so DGE still fires.
                    if (instr is InlineAsm { Operands: null or { Count: 0 }, Code: var asmStr })
                    {
                        // The asm string references globals two ways: by their full
                        // mangled name, or via a {placeholder} the backend interpolates.
                        // A placeholder uses the BARE name (e.g. {_tick}) while the global
                        // key is module-prefixed (rtos__tick), so also match a global whose
                        // name equals or ends with "_<placeholder>". Missing this dropped
                        // asm-only globals as dead, leaving {_tick} unresolved in the output.
                        var placeholders = new HashSet<string>();
                        foreach (System.Text.RegularExpressions.Match pm in
                                 System.Text.RegularExpressions.Regex.Matches(asmStr, @"\{([A-Za-z_][A-Za-z0-9_]*)\}"))
                            placeholders.Add(pm.Groups[1].Value);

                        foreach (var gName in globalVarNames)
                        {
                            if (asmStr.Contains(gName.Replace('.', '_')))
                            {
                                referencedGlobals.Add(gName);
                                continue;
                            }
                            foreach (var ph in placeholders)
                                if (gName == ph || gName.EndsWith("_" + ph, StringComparison.Ordinal))
                                {
                                    referencedGlobals.Add(gName);
                                    break;
                                }
                        }
                    }
                }
            }

            var deadGlobals = globalVarNames.Except(referencedGlobals).ToHashSet();

            if (deadGlobals.Count > 0)
            {
                optimized.Globals.RemoveAll(g => deadGlobals.Contains(g.Name));
                foreach (var k in deadGlobals.Where(k => optimized.GlobalArrays.ContainsKey(k)).ToList())
                    optimized.GlobalArrays.Remove(k);

                // Purge every instruction that references a dead global from ALL
                // function bodies (reachable and non-reachable alike).  This is
                // necessary so the StackAllocator never sees dead-global Variable
                // names and creates phantom SRAM slots for them — which would
                // otherwise cause spurious .equ symbols in the backend.
                //
                // For reachable functions: the global was never read, so stores
                // to it have no effect and are safe to remove.
                // For non-reachable functions: the whole body is dead code.
                foreach (var func in optimized.Functions)
                {
                    func.Body.RemoveAll(instr =>
                    {
                        // Remove any instruction whose sole destination is a dead global.
                        var dst = GetDst(instr);
                        if (dst is Variable dv && deadGlobals.Contains(dv.Name)) return true;
                        if (instr is AugAssign { Target: Variable aav } && deadGlobals.Contains(aav.Name)) return true;
                        // Remove Copy stores to dead globals.
                        if (instr is Copy { Dst: Variable cv } && deadGlobals.Contains(cv.Name)) return true;
                        return false;
                    });
                }
            }
        }
        // --- End DGE ---

        // --- Devirtualization pass ---
        // Convert VirtualCall nodes to direct Call when static analysis proves
        // the target is unambiguous.  For every current PyMCU program this pass
        // eliminates ALL VirtualCall nodes (Rule 2: instanceClasses always holds
        // the exact concrete type).
        foreach (var func in optimized.Functions)
            DevirtualizeCalls(func, optimized.ClassChildren, optimized.ClassDirectMethods);

        // Build vtable specs for VirtualCall nodes that survived devirtualization.
        // In the common case this list is empty (no vtable flash overhead).
        optimized.Vtables = BuildVtableSpecs(optimized);

        // Publish the ISR-shared set for the backend (e.g. AVR GPIOR promotion),
        // keeping only globals that survived DGE.
        var survivingGlobals = new HashSet<string>(optimized.Globals.Select(g => g.Name));
        optimized.IsrSharedGlobals = isrShared.Where(survivingGlobals.Contains).OrderBy(n => n, StringComparer.Ordinal).ToList();

        return optimized;


        void Enqueue(string name)
        {
            if (reachable.Add(name)) worklist.Enqueue(name);
        }
    }

// Detects a synchronous-call cycle (direct or mutual recursion) in the call graph.
// Only Call edges count: a FunctionRef (a function pointer handed to a scheduler or
// irq registration) is not a synchronous self-call, so callback/RTOS patterns are not
// flagged. Throws RecursionError on the first back edge found.
private static void DetectRecursion(ProgramIR program)
{
    var byName = new Dictionary<string, Function>();
    foreach (var f in program.Functions) byName.TryAdd(f.Name, f);

    var callees = new Dictionary<string, List<string>>();
    foreach (var f in program.Functions)
    {
        var list = new List<string>();
        foreach (var instr in f.Body)
            if (instr is Call c && byName.ContainsKey(c.FunctionName))
                list.Add(c.FunctionName);
        callees[f.Name] = list;
    }

    // DFS three-coloring: 0 = unvisited, 1 = on the current path, 2 = done.
    var color = new Dictionary<string, int>();
    foreach (var f in program.Functions) color[f.Name] = 0;

    void Visit(string u)
    {
        color[u] = 1;
        foreach (var v in callees[u])
        {
            if (color[v] == 1) ReportRecursion(v);   // back edge into the active path
            if (color[v] == 0) Visit(v);
        }
        color[u] = 2;
    }

    void ReportRecursion(string fn)
    {
        var f = byName[fn];
        string name = !string.IsNullOrEmpty(f.OriginalName) ? f.OriginalName! : fn;
        int line = 0;
        foreach (var instr in f.Body)
            if (instr is DebugLine dl) { line = dl.Line; break; }
        throw new RecursionError(
            $"function '{name}' is recursive; PyMCU uses a static stack layout " +
            "(no per-call frames), so recursion is not supported — rewrite it as a loop",
            line, 1);
    }

    foreach (var f in program.Functions)
        if (color[f.Name] == 0) Visit(f.Name);
}

private static Function CloneFunction(Function f)
{
    return new Function
    {
        Name = f.Name,
        OriginalName = f.OriginalName,
        Params = [..f.Params],
        ReturnType = f.ReturnType,
        Body = [..f.Body],
        IsInline = f.IsInline,
        IsInterrupt = f.IsInterrupt,
        IsNaked = f.IsNaked,
        InterruptVector = f.InterruptVector,
        CanFail = f.CanFail,
        IsExtern = f.IsExtern,
        IsExportC = f.IsExportC,
    };
}

    /// <summary>
    /// Computes the module-level scalar globals that are referenced both in ISR
    /// context (an ISR body or any function reachable from one) and in non-ISR
    /// context (main, @export_c entry points, or functions reachable from them
    /// without traversing into an ISR — a FunctionRef passed to an irq()
    /// registration call is not a synchronous call). These globals behave like
    /// C <c>volatile</c>: the optimizer must not cache their value, and backends
    /// may promote single-byte entries to always-volatile storage (AVR GPIORn).
    /// </summary>
    private static HashSet<string> ComputeIsrSharedGlobals(ProgramIR program)
    {
        var globalNames = new HashSet<string>(program.Globals.Select(g => g.Name));
        if (globalNames.Count == 0 || !program.Functions.Any(f => f.IsInterrupt))
            return [];

        var byName = new Dictionary<string, Function>();
        foreach (var f in program.Functions)
            byName.TryAdd(f.Name, f);

        var callGraph = new Dictionary<string, HashSet<string>>();
        foreach (var func in program.Functions)
        {
            var callees = new HashSet<string>();
            foreach (var instr in func.Body)
            {
                if (instr is Call call) callees.Add(call.FunctionName);
                RegisterUses(instr, val =>
                {
                    if (val is FunctionRef fref) callees.Add(fref.FunctionName);
                });
            }
            callGraph[func.Name] = callees;
        }

        HashSet<string> Reach(IEnumerable<string> roots, bool enterIsrs)
        {
            var seen = new HashSet<string>();
            var work = new Queue<string>();
            foreach (var r in roots)
                if (seen.Add(r)) work.Enqueue(r);
            while (work.Count > 0)
            {
                if (!callGraph.TryGetValue(work.Dequeue(), out var callees)) continue;
                foreach (var c in callees)
                {
                    if (!enterIsrs && byName.TryGetValue(c, out var cf) && cf.IsInterrupt) continue;
                    if (seen.Add(c)) work.Enqueue(c);
                }
            }
            return seen;
        }

        var isrFns = Reach(
            program.Functions.Where(f => f.IsInterrupt).Select(f => f.Name),
            enterIsrs: true);
        var mainFns = Reach(
            program.Functions.Where(f => !f.IsInterrupt && (f.Name == "main" || f.IsExportC)).Select(f => f.Name),
            enterIsrs: false);

        HashSet<string> RefsIn(HashSet<string> fns)
        {
            var refs = new HashSet<string>();
            foreach (var func in program.Functions.Where(f => fns.Contains(f.Name)))
                foreach (var instr in func.Body)
                {
                    RegisterUses(instr, val =>
                    {
                        if (val is Variable v && globalNames.Contains(v.Name)) refs.Add(v.Name);
                    });
                    if (GetDst(instr) is Variable d && globalNames.Contains(d.Name)) refs.Add(d.Name);
                }
            return refs;
        }

        var shared = RefsIn(isrFns);
        shared.IntersectWith(RefsIn(mainFns));
        return shared;
    }

    private static void OptimizeFunction(Function func, HashSet<string>? globalNames = null, HashSet<string>? volatileNames = null)
    {
        for (var i = 0; i < 10; ++i)
        {
            RemoveRedundantControlFlow(func);
            PropagateCopies(func, globalNames, volatileNames);
            PropagateVarCopies(func, globalNames);
            FoldConstants(func);
            EliminateRedundantMasks(func);
            EliminateRedundantArrayLoads(func, volatileNames);
            EliminateLocalDeadStores(func, globalNames);
            EliminateDeadVariableStores(func, globalNames);
            CoalesceInstructions(func);

            var cfg = BuildCfg(func);
            EliminateDeadCodeCfg(cfg);

            func.Body = cfg.Blocks.SelectMany(b => b.Instructions).ToList();
        }

        if (UnrollConstantLoops(func))
        {
            for (var i = 0; i < 10; ++i)
            {
                PropagateCopies(func, globalNames, volatileNames);
                FoldConstants(func);
                EliminateDeadVariableStores(func, globalNames);
                CoalesceInstructions(func);
                var cfg2 = BuildCfg(func);
                EliminateDeadCodeCfg(cfg2);
                func.Body = cfg2.Blocks.SelectMany(b => b.Instructions).ToList();
            }
        }

        CollapseBoolJumps(func);
        CollapseBitChecks(func);

        var finalCfg = BuildCfg(func);
        EliminateDeadCodeCfg(finalCfg);
        func.Body = finalCfg.Blocks.SelectMany(b => b.Instructions).ToList();
    }

    /// <summary>
    /// Removes Copy instructions whose destination is a Variable that is never
    /// read anywhere in the function body. This catches dead stores that arise
    /// from @inline expansion (e.g. compiler-generated bit-number temporaries
    /// like "main.led__bit") after PropagateCopies has replaced all reads with
    /// the folded constant.
    ///
    /// Only Copy-to-Variable is considered: MemoryAddress targets represent
    /// hardware I/O and must never be removed. AugAssign targets appear as
    /// uses in RegisterUses so they are never mistakenly eliminated.
    /// </summary>
    private static void EliminateDeadVariableStores(Function func, HashSet<string>? globalNames = null)
    {
        var readVars = new HashSet<string>();
        foreach (var instr in func.Body)
            RegisterUses(instr, v => { if (v is Variable vr) readVars.Add(vr.Name); });

        // If the function contains inline asm with no IR operands, float results may be
        // read via the AVR register convention (R22:R25) without any IR-level use.
        // Conservatively keep all Copy-to-Variable instructions whose source is a float
        // Temporary so that the preceding float computation (CALL __mulsf3 etc.) is not
        // eliminated by the subsequent dead-code pass.
        bool hasNakedAsm = func.Body.Any(i => i is InlineAsm { Operands: null or { Count: 0 } });

        func.Body = func.Body
            .Where(instr =>
            {
                if (instr is not Copy { Dst: Variable vDst }) return true;
                if (readVars.Contains(vDst.Name)) return true;
                if (hasNakedAsm && instr is Copy { Src: Temporary { Type: DataType.FLOAT } }) return true;
                // Never eliminate stores to FUNCREF variables — function pointer assignments
                // are observable side effects read by asm or cross-function via SRAM.
                if (instr is Copy cp2 && cp2.Dst is Variable dv2 && dv2.Type == DataType.FUNCREF)
                    return true;
                // Never eliminate stores to module-level globals — they are observable
                // side effects read by other functions in the program (e.g. randomSeed
                // writing to _state which is read by random()).
                if (globalNames != null && globalNames.Contains(vDst.Name)) return true;
                return false;
            })
            .ToList();
    }

    // Removes "Jump L" immediately followed by "Label L" (a jump to the next
    // instruction is a no-op) and then drops labels that no branch targets. The
    // @inline expander leaves dead skip-jumps from statically-taken `if`s (e.g.
    // move_to's `if row == 1` when row is constant), which split a straight-line
    // computation into separate basic blocks; PropagateCopies conservatively
    // clears variable constants at every Label, so the split blocks the constant
    // folding that would otherwise collapse the whole inline-expanded chain.
    // Merging the blocks lets the linear propagation flow across.
    private static void RemoveRedundantControlFlow(Function func)
    {
        var body = func.Body;

        // 1. Jump-to-next removal (skip over interleaved debug lines).
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i] is not Jump j) continue;
            int k = i + 1;
            while (k < body.Count && body[k] is DebugLine) k++;
            if (k < body.Count && body[k] is Label lab && lab.Name == j.Target)
            {
                body.RemoveAt(i);
                i--;
            }
        }

        // 2. Drop labels no remaining branch / error-edge targets. Collect every
        //    label reference: jumps (incl. JumpIfBit*) and BranchOnError edges.
        var targets = new HashSet<string>();
        foreach (var instr in body)
        {
            if (JumpTargetOf(instr) is string t) targets.Add(t);
            if (instr is BranchOnError boe) targets.Add(boe.ErrorLabel);
        }
        body.RemoveAll(instr => instr is Label l && !targets.Contains(l.Name));
    }

    // Known-bits (masked-value) analysis that removes a redundant `x & m` when x's
    // value already has every bit outside m clear. The @inline driver composition
    // produces these constantly: _byte passes `val & 0xF0` into _nibble, which does
    // `(nib & 0xF0) | ...` -> `(val & 0xF0) & 0xF0`. Constant folding can't touch it
    // because val is a runtime char; a C compiler eliminates it via known-bits.
    // Linear, per-basic-block (facts cleared at every label join); soundness rests
    // on tracking an over-approximation of possibly-set bits and only rewriting an
    // AND that provably removes nothing.
    private static void EliminateRedundantMasks(Function func)
    {
        const long ALL = 0xFFFFFFFFL;
        var bits = new Dictionary<string, long>();   // name -> possibly-set bits

        long WidthMask(DataType t) => t switch
        {
            DataType.UINT8 or DataType.INT8 => 0xFF,
            DataType.UINT16 or DataType.INT16 => 0xFFFF,
            _ => ALL,
        };
        long Bits(Val v) => v switch
        {
            Constant c => (uint)c.Value,
            Variable va => bits.TryGetValue(va.Name, out var m) ? m : ALL,
            Temporary t => bits.TryGetValue(t.Name, out var m) ? m : ALL,
            _ => ALL,
        };
        void Set(Val? dst, long m)
        {
            if (NameOf(dst) is string n) bits[n] = m & WidthMask(GetDataType(dst!));
        }
        void Clear(Val? dst) { if (NameOf(dst) is string n) bits.Remove(n); }

        for (int i = 0; i < func.Body.Count; i++)
        {
            switch (func.Body[i])
            {
                case Label:
                case Call { Args: var a } when a.Any(x => x is ArrayBase):
                    bits.Clear();
                    break;
                case Copy c: Set(c.Dst, Bits(c.Src)); break;
                case Binary b:
                {
                    // Redundant mask: `x & c` (c constant) where x's bits are a subset of c.
                    if (b.Op == BinaryOp.BitAnd && (b.Src1 is Constant || b.Src2 is Constant))
                    {
                        bool leftC = b.Src1 is Constant;
                        long c = (uint)((Constant)(leftC ? b.Src1 : b.Src2)).Value;
                        Val x = leftC ? b.Src2 : b.Src1;
                        long xm = Bits(x);
                        if ((xm & ~c) == 0) { func.Body[i] = new Copy(x, b.Dst); Set(b.Dst, xm); break; }
                        Set(b.Dst, xm & c);
                        break;
                    }
                    long bm = b.Op switch
                    {
                        BinaryOp.BitOr => Bits(b.Src1) | Bits(b.Src2),
                        BinaryOp.BitXor => Bits(b.Src1) | Bits(b.Src2),
                        BinaryOp.BitAnd => Bits(b.Src1) & Bits(b.Src2),
                        BinaryOp.LShift when b.Src2 is Constant s => Bits(b.Src1) << s.Value,
                        BinaryOp.RShift when b.Src2 is Constant s => Bits(b.Src1) >> s.Value,
                        _ => ALL,
                    };
                    Set(b.Dst, bm);
                    break;
                }
                case AugAssign aa: Clear(aa.Target); break;
                case BitSet bs: Clear(bs.Target); break;
                case BitClear bc: Clear(bc.Target); break;
                case BitWrite bw: Clear(bw.Target); break;
                case InlineAsm { Operands: not null } ia:
                    foreach (var op in ia.Operands) Clear(op);
                    break;
                default:
                    if (GetDst(func.Body[i]) is Val d) Set(d, ALL);
                    break;
            }
        }
    }

    // Local dead-store elimination: a pure store to a local that is overwritten
    // later in the same basic block before any read is dead. The @inline expander
    // reuses names across sibling expansions (e.g. the two `nib`/`base` of _byte's
    // two _nibble calls), so the first write is dead every iteration — but the
    // global "never read" DCE keeps it because the name *is* read after the later
    // write. Once a value is overwritten within the block it cannot reach a
    // successor, so no live-out analysis is needed.
    private static void EliminateLocalDeadStores(Function func, HashSet<string>? globalNames)
    {
        bool IsGlobal(string n) => globalNames != null && globalNames.Contains(n);
        var remove = new HashSet<int>();
        var pending = new Dictionary<string, int>();   // local -> index of its unread pure store

        void KillRead(Val v) { if (NameOf(v) is string n) pending.Remove(n); }

        for (int i = 0; i < func.Body.Count; i++)
        {
            var instr = func.Body[i];
            // Any control transfer ends the straight-line segment: a pending store
            // may be read at a branch target (or after a backward edge), so it is
            // NOT dead. Clearing here — not only at labels — keeps the pass sound
            // when a conditional jump sits between two writes of the same name.
            if (instr is Label or Jump or Return
                or BranchOnError or SignalError or SignalSuccess
                || JumpTargetOf(instr) != null)
            {
                pending.Clear();
                continue;
            }

            // Reads consume any pending store of the read name.
            RegisterUses(instr, KillRead);
            // In-place ops read their target too.
            switch (instr)
            {
                case AugAssign aa: KillRead(aa.Target); break;
                case BitSet bs: KillRead(bs.Target); break;
                case BitClear bc: KillRead(bc.Target); break;
                case BitWrite bw: KillRead(bw.Target); break;
            }

            // Definitions.
            bool pure = instr is Copy or Binary or Unary or Bitcast;
            if (NameOf(GetDst(instr)) is string d && !IsGlobal(d))
            {
                if (pending.TryGetValue(d, out int prev)) remove.Add(prev);  // overwritten unread
                if (pure) pending[d] = i; else pending.Remove(d);
            }
        }

        if (remove.Count == 0) return;
        var kept = new List<Instruction>(func.Body.Count);
        for (int i = 0; i < func.Body.Count; i++)
            if (!remove.Contains(i)) kept.Add(func.Body[i]);
        func.Body = kept;
    }

    private static int? GetConstant(Val val) => val is Constant c ? c.Value : null;

    // Algebraic identities for a Binary with exactly one constant operand. These
    // collapse the redundant masking/OR-ing the @inline driver expansions emit on
    // runtime values (e.g. (c & 0xF0) | 0 | _BL in the LCD nibble path), which
    // constant folding cannot touch because the other operand is not constant.
    // Returns a replacement Copy, or null when no identity applies.
    private static Instruction? SimplifyBinary(Binary b)
    {
        bool leftConst = b.Src1 is Constant;
        bool rightConst = b.Src2 is Constant;
        if (leftConst == rightConst) return null;          // need exactly one constant
        int k = (leftConst ? (Constant)b.Src1 : (Constant)b.Src2).Value;
        Val other = leftConst ? b.Src2 : b.Src1;

        Instruction Keep() => new Copy(other, b.Dst);
        Instruction Zero() => new Copy(new Constant(0), b.Dst);

        // Full-width mask for the destination type (an AND with it is identity).
        DataType dt = GetDataType(b.Dst);
        int fullMask = dt switch { DataType.UINT8 or DataType.INT8 => 0xFF,
                                   DataType.UINT16 or DataType.INT16 => 0xFFFF, _ => -1 };

        switch (b.Op)
        {
            // Commutative: the constant may be on either side.
            case BinaryOp.Add when k == 0: return Keep();
            case BinaryOp.BitOr when k == 0: return Keep();
            case BinaryOp.BitXor when k == 0: return Keep();
            case BinaryOp.Mul when k == 1: return Keep();
            case BinaryOp.Mul when k == 0: return Zero();
            // Strength-reduce a power-of-two multiply to a shift: x * 2^n -> x << n.
            // The backend lowers a constant shift to byte moves for multiples of 8
            // (e.g. hi * 256 in the ADC read becomes a high-byte placement instead
            // of a 16-bit MUL chain).
            case BinaryOp.Mul when k > 1 && (k & (k - 1)) == 0:
                return new Binary(BinaryOp.LShift, other, new Constant(System.Numerics.BitOperations.TrailingZeroCount(k)), b.Dst);
            case BinaryOp.BitAnd when k == 0: return Zero();
            case BinaryOp.BitAnd when fullMask >= 0 && (k & fullMask) == fullMask: return Keep();
            // Non-commutative: identity only when the constant is the right operand.
            case BinaryOp.Sub when rightConst && k == 0: return Keep();
            case BinaryOp.LShift when rightConst && k == 0: return Keep();
            case BinaryOp.RShift when rightConst && k == 0: return Keep();
            default: return null;
        }
    }

    private static void FoldConstants(Function func)
    {
        int curLine = 0;
        for (var i = 0; i < func.Body.Count; ++i)
        {
            var instr = func.Body[i];
            if (instr is DebugLine dbg) curLine = dbg.Line;
            switch (instr)
            {
                case Binary binary:
                {
                    var c1 = GetConstant(binary.Src1);
                    var c2 = GetConstant(binary.Src2);
                    if (c1.HasValue && c2.HasValue)
                    {
                        var result = 0;
                        var foldable = true;
                        switch (binary.Op)
                        {
                            case BinaryOp.Add: result = c1.Value + c2.Value; break;
                            case BinaryOp.Sub: result = c1.Value - c2.Value; break;
                            case BinaryOp.Mul: result = c1.Value * c2.Value; break;
                            // Division/modulo by a divisor that constant propagation has
                            // proven to be zero is a guaranteed fault. VisitBinary catches
                            // literal `x / 0`, but a zero that only becomes visible here
                            // (a folded local/param, e.g. `z = 0; x // z`) would otherwise be
                            // left as a runtime Binary with a const-0 divisor and miscompile.
                            case BinaryOp.Div or BinaryOp.FloorDiv or BinaryOp.Mod when c2.Value == 0:
                                throw new ValueError("integer division or modulo by zero",
                                    curLine > 0 ? curLine : 1, 1);
                            case BinaryOp.Div:
                                result = c1.Value / c2.Value;
                                break;
                            case BinaryOp.FloorDiv:
                            {
                                int q = c1.Value / c2.Value;
                                if ((c1.Value ^ c2.Value) < 0 && q * c2.Value != c1.Value) q--;
                                result = q;
                                break;
                            }
                            case BinaryOp.Mod:
                            {
                                // Python's % follows the sign of the divisor (floor-mod),
                                // not C#'s truncating remainder which follows the dividend.
                                // Mirror the FloorDiv adjustment above so -7 % 2 folds to 1,
                                // not -1. (For non-negative operands — all unsigned values —
                                // the adjustment never triggers.)
                                int r = c1.Value % c2.Value;
                                if (r != 0 && (r < 0) != (c2.Value < 0)) r += c2.Value;
                                result = r;
                                break;
                            }
                            case BinaryOp.Equal: result = c1.Value == c2.Value ? 1 : 0; break;
                            case BinaryOp.NotEqual: result = c1.Value != c2.Value ? 1 : 0; break;
                            case BinaryOp.LessThan: result = c1.Value < c2.Value ? 1 : 0; break;
                            case BinaryOp.LessEqual: result = c1.Value <= c2.Value ? 1 : 0; break;
                            case BinaryOp.GreaterThan: result = c1.Value > c2.Value ? 1 : 0; break;
                            case BinaryOp.GreaterEqual: result = c1.Value >= c2.Value ? 1 : 0; break;
                            case BinaryOp.BitAnd: result = c1.Value & c2.Value; break;
                            case BinaryOp.BitOr: result = c1.Value | c2.Value; break;
                            case BinaryOp.BitXor: result = c1.Value ^ c2.Value; break;
                            case BinaryOp.LShift: result = c1.Value << c2.Value; break;
                            case BinaryOp.RShift: result = c1.Value >> c2.Value; break;
                            default: foldable = false; break;
                        }

                        if (foldable)
                        {
                            var dstType = GetDataType(binary.Dst);
                            if (dstType != DataType.UNKNOWN) result = WrapToType(result, dstType);
                            func.Body[i] = new Copy(new Constant(result), binary.Dst);
                        }
                    }
                    else if (SimplifyBinary(binary) is Instruction simplified)
                    {
                        func.Body[i] = simplified;
                    }

                    break;
                }
                case JumpIfEqual je:
                {
                    var c1 = GetConstant(je.Src1);
                    var c2 = GetConstant(je.Src2);
                    if (c1.HasValue && c2.HasValue)
                    {
                        if (c1.Value == c2.Value) func.Body[i] = new Jump(je.Target);
                        else func.Body[i] = new Copy(new Constant(0), new Temporary("__dead_jmp__"));
                    }
                    break;
                }
                case JumpIfNotEqual jne:
                {
                    var c1 = GetConstant(jne.Src1);
                    var c2 = GetConstant(jne.Src2);
                    if (c1.HasValue && c2.HasValue)
                    {
                        if (c1.Value != c2.Value) func.Body[i] = new Jump(jne.Target);
                        else func.Body[i] = new Copy(new Constant(0), new Temporary("__dead_jmp__"));
                    }
                    break;
                }
                case JumpIfLessThan jlt:
                {
                    var c1 = GetConstant(jlt.Src1);
                    var c2 = GetConstant(jlt.Src2);
                    if (c1.HasValue && c2.HasValue)
                    {
                        if (c1.Value < c2.Value) func.Body[i] = new Jump(jlt.Target);
                        else func.Body[i] = new Copy(new Constant(0), new Temporary("__dead_jmp__"));
                    }
                    break;
                }
                case JumpIfLessOrEqual jle:
                {
                    var c1 = GetConstant(jle.Src1);
                    var c2 = GetConstant(jle.Src2);
                    if (c1.HasValue && c2.HasValue)
                    {
                        if (c1.Value <= c2.Value) func.Body[i] = new Jump(jle.Target);
                        else func.Body[i] = new Copy(new Constant(0), new Temporary("__dead_jmp__"));
                    }
                    break;
                }
                case JumpIfGreaterThan jgt:
                {
                    var c1 = GetConstant(jgt.Src1);
                    var c2 = GetConstant(jgt.Src2);
                    if (c1.HasValue && c2.HasValue)
                    {
                        if (c1.Value > c2.Value) func.Body[i] = new Jump(jgt.Target);
                        else func.Body[i] = new Copy(new Constant(0), new Temporary("__dead_jmp__"));
                    }
                    break;
                }
                case JumpIfGreaterOrEqual jge:
                {
                    var c1 = GetConstant(jge.Src1);
                    var c2 = GetConstant(jge.Src2);
                    if (c1.HasValue && c2.HasValue)
                    {
                        if (c1.Value >= c2.Value) func.Body[i] = new Jump(jge.Target);
                        else func.Body[i] = new Copy(new Constant(0), new Temporary("__dead_jmp__"));
                    }
                    break;
                }
                case JumpIfZero jz:
                {
                    var c1 = GetConstant(jz.Condition);
                    if (c1.HasValue)
                    {
                        if (c1.Value == 0) func.Body[i] = new Jump(jz.Target);
                        else func.Body[i] = new Copy(new Constant(0), new Temporary("__dead_jmp__"));
                    }
                    break;
                }
                case JumpIfNotZero jnz:
                {
                    var c1 = GetConstant(jnz.Condition);
                    if (c1.HasValue)
                    {
                        if (c1.Value != 0) func.Body[i] = new Jump(jnz.Target);
                        else func.Body[i] = new Copy(new Constant(0), new Temporary("__dead_jmp__"));
                    }
                    break;
                }
                case Unary unary:
                {
                    var c = GetConstant(unary.Src);
                    if (c.HasValue)
                    {
                        int result = 0;
                        bool foldable = true;
                        switch (unary.Op)
                        {
                            case UnaryOp.Neg: result = -c.Value; break;
                            case UnaryOp.Not: result = c.Value == 0 ? 1 : 0; break;
                            case UnaryOp.BitNot: result = ~c.Value; break;
                            default: foldable = false; break;
                        }

                        if (foldable)
                        {
                            var dstType = GetDataType(unary.Dst);
                            if (dstType != DataType.UNKNOWN) result = WrapToType(result, dstType);
                            func.Body[i] = new Copy(new Constant(result), unary.Dst);
                        }
                    }

                    break;
                }
            }
        }
    }

    private static DataType GetDataType(Val val)
    {
        if (val is Variable v) return v.Type;
        if (val is Temporary t) return t.Type;
        if (val is MemoryAddress m) return m.Type;
        return DataType.UNKNOWN;
    }

    // True if forwarding `src` in place of a value of type `dstType` would change the
    // value's representation — a different byte width or signedness. Such a copy is a
    // numeric cast / reinterpret whose type must be preserved (it governs sign/zero
    // extension, signed comparisons, and the print formatter); copy propagation must not
    // forward through it. Unknown types are treated as non-changing to avoid over-blocking.
    private static bool ChangesRepr(Val src, DataType dstType)
    {
        DataType st = GetDataType(src);
        if (st == DataType.UNKNOWN || dstType == DataType.UNKNOWN) return false;
        return st.SizeOf() != dstType.SizeOf() || st.IsSigned() != dstType.IsSigned();
    }

    private static int WrapToType(int value, DataType type)
    {
        switch (type)
        {
            case DataType.UINT8: return value & 0xFF;
            case DataType.INT8: return (sbyte)(value & 0xFF);
            case DataType.UINT16: return value & 0xFFFF;
            case DataType.INT16: return (short)(value & 0xFFFF);
            default: return value;
        }
    }

    private static void EliminateDeadCodeCfg(ControlFlowGraph cfg)
    {
        var liveOut = new Dictionary<BasicBlock, HashSet<string>>();
        var liveIn = new Dictionary<BasicBlock, HashSet<string>>();

        foreach (var block in cfg.Blocks)
        {
            liveOut[block] = [];
            liveIn[block] = [];
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = cfg.Blocks.Count - 1; i >= 0; i--)
            {
                var block = cfg.Blocks[i];
                var oldInCount = liveIn[block].Count;

                var newOut = new HashSet<string>();
                foreach (var succ in block.Successors)
                    newOut.UnionWith(liveIn[succ]);
                liveOut[block] = newOut;

                var currentLive = new HashSet<string>(newOut);

                for (var j = block.Instructions.Count - 1; j >= 0; j--)
                {
                    var instr = block.Instructions[j];
                    var dst = GetDst(instr);

                    if (dst is Temporary tDst)
                        currentLive.Remove(tDst.Name);

                    RegisterUses(instr, val =>
                    {
                        if (val is Temporary tUse) currentLive.Add(tUse.Name);
                    });
                }

                liveIn[block] = currentLive;
                if (liveIn[block].Count != oldInCount) changed = true;
            }
        }

        foreach (var block in cfg.Blocks)
        {
            var currentLive = new HashSet<string>(liveOut[block]);
            var newInstructions = new List<Instruction>();

            for (var j = block.Instructions.Count - 1; j >= 0; j--)
            {
                var instr = block.Instructions[j];
                var isDead = false;

                var dst = GetDst(instr);
                if (dst is Temporary tDst && instr is not Call)
                {
                    if (!currentLive.Contains(tDst.Name))
                    {
                        isDead = true;
                    }
                    else
                    {
                        currentLive.Remove(tDst.Name);
                    }
                }

                if (isDead) continue;
                newInstructions.Add(instr);
                RegisterUses(instr, val =>
                {
                    if (val is Temporary tUse) currentLive.Add(tUse.Name);
                });
            }

            newInstructions.Reverse();
            block.Instructions = newInstructions;
        }
    }

    private static void PropagateCopies(Function func, HashSet<string>? globalNames = null, HashSet<string>? volatileNames = null)
    {
        var tempCopies = new Dictionary<string, Val>();
        var blacklistedTemps = new HashSet<string>();
        var varConsts = new Dictionary<string, int>();
        // ISR-shared globals: never track a stored constant (an ISR may rewrite the
        // value between the store and a later read in the same basic block), and
        // never forward a temp that holds their value (each source-level read must
        // stay a single load — forwarding would duplicate or reorder reads).
        bool IsVolatile(Val v) => v is Variable vv && volatileNames != null && volatileNames.Contains(vv.Name);

        for (var i = 0; i < func.Body.Count; ++i)
        {
            // 1. Substitute uses — but NOT InlineAsm operands, which are
            //    read+write and must remain as Variables for backend writeback.
            if (func.Body[i] is InlineAsm { Operands: not null })
            {
                // Leave InlineAsm operands as-is (no constant propagation).
            }
            else
            {
                func.Body[i] = ReplaceUses(func.Body[i], v =>
                {
                    return v switch
                    {
                        Temporary t when tempCopies.TryGetValue(t.Name, out var replacement) => replacement,
                        Variable var2 when varConsts.TryGetValue(var2.Name, out int cv) => new Constant(cv),
                        _ => v
                    };
                });
            }

            // 2. Track new copies
            var instr = func.Body[i];
            switch (instr)
            {
                case Copy { Dst: Temporary tDst } copy:
                {
                    if (IsVolatile(copy.Src))
                    {
                        // The temp holds a snapshot of a volatile global; forwarding the
                        // global name to later uses would re-read it. Keep the load as-is.
                        tempCopies.Remove(tDst.Name);
                        blacklistedTemps.Add(tDst.Name);
                    }
                    else if (!blacklistedTemps.Contains(tDst.Name))
                    {
                        if (tempCopies.Remove(tDst.Name))
                            blacklistedTemps.Add(tDst.Name);
                        else
                        {
                            // Float constant to non-float temp: fold to integer constant.
                            if (copy.Src is FloatConstant fcTmp && tDst.Type != DataType.FLOAT)
                            {
                                var intConst = new Constant(WrapToType((int)fcTmp.Value, tDst.Type));
                                func.Body[i] = new Copy(intConst, tDst);
                                tempCopies[tDst.Name] = intConst;
                            }
                            // A width- or signedness-changing copy is a numeric cast/reinterpret
                            // (e.g. int8(uint8_var) emits Copy(uint8 -> int8 temp)). Forwarding
                            // the source would discard the cast's type, so later sign/zero
                            // extension at a call boundary, a signed comparison, or the print
                            // formatter would read the source's signedness instead of the cast's.
                            // Keep the temp materialized.
                            else if (ChangesRepr(copy.Src, tDst.Type))
                                blacklistedTemps.Add(tDst.Name);
                            else
                                tempCopies[tDst.Name] = copy.Src;
                        }
                    }

                    break;
                }
                case Copy copy:
                {
                    if (copy.Dst is Variable vDst)
                    {
                        if (IsVolatile(vDst))
                            varConsts.Remove(vDst.Name);
                        else if (copy.Src is Constant c)
                            varConsts[vDst.Name] = c.Value;
                        else if (copy.Src is FloatConstant fcVar && vDst.Type != DataType.FLOAT)
                        {
                            // Float constant to integer variable: fold at optimizer time.
                            int folded = WrapToType((int)fcVar.Value, vDst.Type);
                            varConsts[vDst.Name] = folded;
                            func.Body[i] = new Copy(new Constant(folded), vDst);
                        }
                        else
                            varConsts.Remove(vDst.Name);
                    }

                    break;
                }
                case AugAssign aug:
                    InvalidateVar(aug.Target);
                    break;
                case Binary bin:
                    InvalidateVar(bin.Dst);
                    break;
                case Unary un:
                    InvalidateVar(un.Dst);
                    break;
                case Bitcast bc:
                    InvalidateVar(bc.Dst);
                    break;
                // InlineAsm with operands may modify variables; invalidate them.
                case InlineAsm { Operands: not null } ia:
                    foreach (var op in ia.Operands) InvalidateVar(op);
                    break;
                case Label:
                    // A label is a basic-block boundary (a control-flow join). Tracked temp
                    // copies are only valid along the straight-line path that produced them:
                    // a temp assigned on one incoming edge and differently on another (e.g. a
                    // ternary/min/max/abs result temp, or a value returned by a call on one
                    // path and a constant on the other) must NOT forward one edge's value past
                    // the join. varConsts was already cleared here; tempCopies was not, which
                    // leaked a branch-local constant across the merge (`big() if c else 7`
                    // collapsed to a constant 7). RemoveRedundantControlFlow merges trivial
                    // straight-line blocks first, so genuine single-def forwarding survives.
                    varConsts.Clear();
                    tempCopies.Clear();
                    break;
                case Call callInstr:
                    // The call's result is a runtime value, so the dst no longer holds its
                    // previously-tracked constant. Without this, `x = 0; x = f(x); g(x)` folds
                    // g's argument to the pre-call 0 -- which silently breaks write-back
                    // mutators (`c = Counter(); c.inc(1); c.inc(1)` keeps reading 0).
                    InvalidateVar(callInstr.Dst);
                    // A call with ArrayBase args may modify variables through the pointer;
                    // conservatively invalidate all tracked variable constants.
                    if (callInstr.Args.Any(a => a is ArrayBase))
                        varConsts.Clear();
                    // Any other call may still reassign a module-level global (via `global x`),
                    // so its tracked constant is no longer valid past the call -- otherwise a
                    // read after the call folds to the pre-call value (e.g. a global seeded in
                    // main then bumped inside a callee). Locals cannot be touched by a callee,
                    // so only the globals are dropped.
                    else if (globalNames is { Count: > 0 })
                        foreach (var g in varConsts.Keys.Where(globalNames.Contains).ToList())
                            varConsts.Remove(g);
                    break;
            }
        }

        return;

        void InvalidateVar(Val dst)
        {
            switch (dst)
            {
                case Variable v:
                    varConsts.Remove(v.Name);
                    break;
                case Temporary t:
                    tempCopies.Remove(t.Name);
                    break;
            }
        }
    }

    /// <summary>
    /// Global (CFG-aware) copy propagation for variable-to-variable copies — the alias
    /// copies @inline expansion leaves behind, e.g. <c>inlineN.foo.result = inlineM.bar.result</c>.
    /// The linear <see cref="PropagateCopies"/> stops at basic-block boundaries; this pass runs
    /// an available-copies dataflow so a copy that dominates a use is propagated even when the
    /// use sits in a later block (after which dead alias copies fall to EliminateDeadVariableStores).
    ///
    /// Soundness: only plain scalar locals are tracked. A variable is excluded if it is global,
    /// GC_REF, modified in place (bit ops / inline asm), or address-taken (ArrayBase / pointer
    /// store target) — so the value held by dst and src can never diverge between the copy and a
    /// use where the copy is available, and only read positions are rewritten.
    /// </summary>
    private static void PropagateVarCopies(Function func, HashSet<string>? globalNames)
    {
        bool IsGlobal(string n) => globalNames != null && globalNames.Contains(n);

        // Variables whose identity must not be rewritten: modified in place or address-taken.
        var untrackable = new HashSet<string>();
        void Mark(Val v) { if (v is Variable vv) untrackable.Add(vv.Name); }
        foreach (var instr in func.Body)
        {
            switch (instr)
            {
                case BitSet bs: Mark(bs.Target); break;
                case BitClear bc: Mark(bc.Target); break;
                case BitWrite bw: Mark(bw.Target); break;
                case StoreIndirect si: Mark(si.DstPtr); break;
                case InlineAsm { Operands: not null } ia:
                    foreach (var op in ia.Operands) Mark(op);
                    break;
            }
            // ArrayBase(X) takes X's address; never propagate through it.
            RegisterUses(instr, v => { if (v is ArrayBase ab) untrackable.Add(ab.ArrayName); });
        }

        bool Trackable(string dstName, DataType dstType, Val srcVal, out Variable src)
        {
            src = null!;
            if (srcVal is not Variable s) return false;
            if (dstType == DataType.GC_REF || s.Type == DataType.GC_REF) return false;
            if (IsGlobal(dstName) || IsGlobal(s.Name)) return false;
            if (untrackable.Contains(dstName) || untrackable.Contains(s.Name)) return false;
            if (dstName == s.Name) return false;
            // A width-/signedness-changing copy is a cast/reinterpret; forwarding the
            // source would discard the type the cast established (see ChangesRepr).
            if (ChangesRepr(s, dstType)) return false;
            src = s;
            return true;
        }

        // Universal set of facts "dst holds src's value", keyed by (dst, src).
        var factId = new Dictionary<(string, string), int>();
        var factSrc = new List<Variable>();
        var factDst = new List<string>();
        int FactOf(string d, Variable s)
        {
            var key = (d, s.Name);
            if (!factId.TryGetValue(key, out int id))
            {
                id = factSrc.Count;
                factId[key] = id;
                factSrc.Add(s);
                factDst.Add(d);
            }
            return id;
        }
        foreach (var instr in func.Body)
            if (instr is Copy { Dst: Variable d } c && Trackable(d.Name, d.Type, c.Src, out var s))
                FactOf(d.Name, s);

        int n = factSrc.Count;
        if (n == 0) return;

        // The variable (if any) this instruction redefines — kills facts about it.
        string? DefVar(Instruction instr)
        {
            if (GetDst(instr) is Variable dv) return dv.Name;
            return instr switch
            {
                AugAssign { Target: Variable v } => v.Name,
                BitSet { Target: Variable v } => v.Name,
                BitClear { Target: Variable v } => v.Name,
                BitWrite { Target: Variable v } => v.Name,
                _ => null
            };
        }
        // Non-creating: the fact universe is frozen at `n` after the enumeration below, so
        // this only ever returns an id already in factId. A copy formed by substitution
        // (ReplaceUses can rewrite `d = a` into `d = b`) may key a fact that was never
        // enumerated; tracking it here would index the fixed-size avail/gen arrays out of
        // bounds. Returning null leaves it for the next optimizer iteration to enumerate.
        int? CopyFact(Instruction instr)
            => instr is Copy { Dst: Variable d } c && Trackable(d.Name, d.Type, c.Src, out var s)
               && factId.TryGetValue((d.Name, s.Name), out int id)
                ? id : null;

        var cfg = BuildCfg(func);
        var blocks = cfg.Blocks;
        var idx = new Dictionary<BasicBlock, int>();
        for (int i = 0; i < blocks.Count; i++) idx[blocks[i]] = i;
        // BuildCfg drops unreachable blocks from cfg.Blocks but leaves them in the
        // Predecessors lists of the survivors; keep only predecessors still present.
        var predsOf = blocks
            .Select(b => b.Predecessors.Where(p => idx.ContainsKey(p)).ToList())
            .ToList();

        // gen[B] = facts generated in B that survive to its end; kill[B] = facts B invalidates.
        var gen = new bool[blocks.Count][];
        var kill = new bool[blocks.Count][];
        for (int b = 0; b < blocks.Count; b++)
        {
            var g = new bool[n];
            var k = new bool[n];
            foreach (var instr in blocks[b].Instructions)
            {
                var x = DefVar(instr);
                if (x != null)
                    for (int f = 0; f < n; f++)
                        if (factDst[f] == x || factSrc[f].Name == x) { g[f] = false; k[f] = true; }
                if (CopyFact(instr) is int cf) g[cf] = true;
            }
            gen[b] = g;
            kill[b] = k;
        }

        // Forward "must" dataflow: out = gen ∪ (in − kill), in = ∩ out[preds]. Init out = all-true.
        var outS = new bool[blocks.Count][];
        var inS = new bool[blocks.Count][];
        for (int b = 0; b < blocks.Count; b++)
        {
            outS[b] = new bool[n];
            inS[b] = new bool[n];
            for (int f = 0; f < n; f++) outS[b][f] = true;
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int b = 0; b < blocks.Count; b++)
            {
                var preds = predsOf[b];
                var inb = inS[b];
                if (preds.Count == 0)
                {
                    for (int f = 0; f < n; f++) inb[f] = false;
                }
                else
                {
                    for (int f = 0; f < n; f++) inb[f] = true;
                    foreach (var p in preds)
                    {
                        var op = outS[idx[p]];
                        for (int f = 0; f < n; f++) inb[f] &= op[f];
                    }
                }
                var ob = outS[b];
                for (int f = 0; f < n; f++)
                {
                    bool nv = gen[b][f] || (inb[f] && !kill[b][f]);
                    if (nv != ob[f]) { ob[f] = nv; changed = true; }
                }
            }
        }

        // Substitution: walk each block with the locally-available facts, rewriting reads.
        foreach (var block in blocks)
        {
            var avail = (bool[])inS[idx[block]].Clone();
            var instrs = block.Instructions;
            for (int i = 0; i < instrs.Count; i++)
            {
                if (avail.Any(x => x))
                {
                    var map = new Dictionary<string, Val>();
                    var conflict = new HashSet<string>();
                    for (int f = 0; f < n; f++)
                    {
                        if (!avail[f]) continue;
                        if (conflict.Contains(factDst[f])) continue;
                        if (map.TryGetValue(factDst[f], out var prev) && !prev.Equals(factSrc[f]))
                        {
                            map.Remove(factDst[f]);
                            conflict.Add(factDst[f]);
                        }
                        else map[factDst[f]] = factSrc[f];
                    }
                    if (map.Count > 0)
                        instrs[i] = ReplaceUses(instrs[i],
                            v => v is Variable var && map.TryGetValue(var.Name, out var rep) ? rep : v);
                }

                var instr = instrs[i];
                var x = DefVar(instr);
                if (x != null)
                    for (int f = 0; f < n; f++)
                        if (factDst[f] == x || factSrc[f].Name == x) avail[f] = false;
                if (CopyFact(instr) is int cf) avail[cf] = true;
            }
        }

        func.Body = blocks.SelectMany(b => b.Instructions).ToList();
    }

    private static void CoalesceInstructions(Function func)
    {
        var useCount = new Dictionary<string, int>();

        foreach (var instr in func.Body)
            RegisterUses(instr, RegisterUse);

        var newBody = new List<Instruction>();
        for (var i = 0; i < func.Body.Count; ++i)
        {
            if (i + 1 < func.Body.Count && func.Body[i + 1] is Copy { Src: Temporary tSrc } nextCopy)
            {
                useCount.TryGetValue(tSrc.Name, out var count);
                if (count == 1)
                {
                    var dst = GetDst(func.Body[i]);
                    if (dst is Temporary tDst && tDst.Name == tSrc.Name)
                    {
                        // Retargeting the producer onto the copy's dst is unsound when it changes
                        // the value's representation. Two cases:
                        //  (a) the producer is itself a reinterpret/convert Copy and the second
                        //      copy converts again -- a two-step conversion that must not collapse
                        //      (`copy u8 -> i8; copy i8 -> i16` is sign-extend; `copy u8 -> i16` is
                        //      zero-extend).
                        //  (b) the producer is a fixed-width load or a call (its result width is
                        //      the element/pointee/return type, not its dst). Retargeting to a
                        //      wider/narrower/differently-signed dst leaves it mismatched (an int8
                        //      element/return loaded into an int16 temp never sign-extends the high
                        //      byte -> garbage; e.g. `print(neg_of(x))` for an int8-returning fn).
                        // Binary/Unary adopt their dst type as the compute width, so they are fine.
                        DataType newDstT = GetDataType(nextCopy.Dst);
                        bool blockCoalesce =
                            (func.Body[i] is Copy prod
                                && ChangesRepr(prod.Src, GetDataType(tDst))
                                && ChangesRepr(tDst, newDstT))
                            || (func.Body[i] is ArrayLoad or ArrayLoadFlash or BytearrayLoad
                                    or LoadIndirect or Call
                                && ChangesRepr(tDst, newDstT));
                        if (!blockCoalesce)
                        {
                            newBody.Add(ReplaceDst(func.Body[i], nextCopy.Dst));
                            i++; // skip the copy
                            continue;
                        }
                    }
                }
            }

            newBody.Add(func.Body[i]);
        }

        func.Body = newBody;
        return;

        void RegisterUse(Val v)
        {
            if (v is not Temporary t) return;
            useCount.TryGetValue(t.Name, out var count);
            useCount[t.Name] = count + 1;
        }
    }

    private static void CollapseBitChecks(Function func)
    {
        for (var i = 0; i + 1 < func.Body.Count; ++i)
        {
            if (func.Body[i] is not BitCheck bc) continue;
            if (bc.Dst is not Temporary dstTmp) continue;

            var j = i + 1;
            while (j < func.Body.Count && func.Body[j] is Label) j++;
            if (j >= func.Body.Count) continue;

            bool replaced = false;

            if (func.Body[j] is JumpIfEqual je)
            {
                (Temporary? s, Constant? c) = MatchTmpConst(je.Src1, je.Src2, dstTmp.Name);
                if (s == null) (s, c) = MatchTmpConst(je.Src2, je.Src1, dstTmp.Name);
                if (s != null && c != null)
                {
                    if (c.Value == 1) func.Body[j] = new JumpIfBitSet(bc.Source, bc.Bit, je.Target);
                    else if (c.Value == 0) func.Body[j] = new JumpIfBitClear(bc.Source, bc.Bit, je.Target);
                    replaced = true;
                }
            }
            else if (func.Body[j] is JumpIfNotEqual jne)
            {
                (Temporary? s, Constant? c) = MatchTmpConst(jne.Src1, jne.Src2, dstTmp.Name);
                if (s == null) (s, c) = MatchTmpConst(jne.Src2, jne.Src1, dstTmp.Name);
                if (s != null && c != null)
                {
                    if (c.Value == 0) func.Body[j] = new JumpIfBitSet(bc.Source, bc.Bit, jne.Target);
                    else if (c.Value == 1) func.Body[j] = new JumpIfBitClear(bc.Source, bc.Bit, jne.Target);
                    replaced = true;
                }
            }

            if (replaced)
                func.Body[i] = new Copy(new Constant(0), bc.Dst);
        }
    }

    private static void CollapseBoolJumps(Function func)
    {
        // Labels that some jump can land on. Fusing a comparison into a jump that sits AFTER such
        // a label is unsound: control reaching the jump via the label never executed the
        // comparison, yet the fused compare-branch would re-test the (now unrelated) operands.
        // This is exactly the `a or b` short-circuit shape -- `jnz a, end; ...; end: jz orResult`
        // -- where folding the `b` comparison into the post-`end` jump drops the `a`-true path.
        var jumpTargets = new HashSet<string>();
        foreach (var instr in func.Body)
            if (JumpTargetOf(instr) is string tgt) jumpTargets.Add(tgt);

        for (var i = 0; i + 1 < func.Body.Count; ++i)
        {
            if (func.Body[i] is not Binary bin) continue;
            if (bin.Dst is not Temporary dstTmp) continue;

            var j = i + 1;
            // Skip only labels that are NOT jump targets; a target label is a barrier (the jump
            // past it has another predecessor, so the comparison does not dominate it).
            while (j < func.Body.Count && func.Body[j] is Label lbl && !jumpTargets.Contains(lbl.Name)) j++;
            if (j >= func.Body.Count) continue;

            string target;
            bool isZeroCheck;

            switch (func.Body[j])
            {
                case JumpIfZero { Condition: Temporary t1 } jiz when t1.Name == dstTmp.Name:
                    target = jiz.Target;
                    isZeroCheck = true;
                    break;
                case JumpIfNotZero { Condition: Temporary t2 } jinz when t2.Name == dstTmp.Name:
                    target = jinz.Target;
                    isZeroCheck = false;
                    break;
                default:
                    continue;
            }

            var replaced = true;
            switch (bin.Op)
            {
                case BinaryOp.Equal:
                    func.Body[j] = isZeroCheck
                        ? new JumpIfNotEqual(bin.Src1, bin.Src2, target)
                        : new JumpIfEqual(bin.Src1, bin.Src2, target);
                    break;
                case BinaryOp.NotEqual:
                    func.Body[j] = isZeroCheck
                        ? new JumpIfEqual(bin.Src1, bin.Src2, target)
                        : new JumpIfNotEqual(bin.Src1, bin.Src2, target);
                    break;
                case BinaryOp.LessThan:
                    func.Body[j] = isZeroCheck
                        ? new JumpIfGreaterOrEqual(bin.Src1, bin.Src2, target)
                        : new JumpIfLessThan(bin.Src1, bin.Src2, target);
                    break;
                case BinaryOp.LessEqual:
                    func.Body[j] = isZeroCheck
                        ? new JumpIfGreaterThan(bin.Src1, bin.Src2, target)
                        : new JumpIfLessOrEqual(bin.Src1, bin.Src2, target);
                    break;
                case BinaryOp.GreaterThan:
                    func.Body[j] = isZeroCheck
                        ? new JumpIfLessOrEqual(bin.Src1, bin.Src2, target)
                        : new JumpIfGreaterThan(bin.Src1, bin.Src2, target);
                    break;
                case BinaryOp.GreaterEqual:
                    func.Body[j] = isZeroCheck
                        ? new JumpIfLessThan(bin.Src1, bin.Src2, target)
                        : new JumpIfGreaterOrEqual(bin.Src1, bin.Src2, target);
                    break;
                case BinaryOp.Add:
                case BinaryOp.Sub:
                case BinaryOp.Mul:
                case BinaryOp.Div:
                case BinaryOp.FloorDiv:
                case BinaryOp.Mod:
                case BinaryOp.BitAnd:
                case BinaryOp.BitOr:
                case BinaryOp.BitXor:
                case BinaryOp.LShift:
                case BinaryOp.RShift:
                default:
                    replaced = false;
                    break;
            }

            if (replaced)
                func.Body[i] = new Copy(new Constant(0), bin.Dst);
        }
    }

    private static ControlFlowGraph BuildCfg(Function func)
    {
        var cfg = new ControlFlowGraph();
        var blocks = new List<BasicBlock>();
        var labelToBlock = new Dictionary<string, BasicBlock>();

        var currentBlock = new BasicBlock("entry");
        blocks.Add(currentBlock);
        cfg.Entry = currentBlock;

        foreach (var instr in func.Body)
        {
            if (instr is Label lbl)
            {
                currentBlock = new BasicBlock(lbl.Name);
                blocks.Add(currentBlock);
                labelToBlock[lbl.Name] = currentBlock;
                currentBlock.Instructions.Add(instr);
            }
            else
            {
                currentBlock.Instructions.Add(instr);

                if (!IsTerminator(instr)) continue;
                currentBlock = new BasicBlock($"bb_{blocks.Count}");
                blocks.Add(currentBlock);
            }
        }

        blocks.RemoveAll(b => b.Instructions.Count == 0);
        cfg.Blocks = blocks;

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var lastInstr = block.Instructions.LastOrDefault();

            if (lastInstr == null) continue;

            if (lastInstr is Jump jmp)
            {
                if (labelToBlock.TryGetValue(jmp.Target, out var targetBlock))
                    Connect(block, targetBlock);
            }
            else if (IsConditionalJump(lastInstr, out var target))
            {
                if (labelToBlock.TryGetValue(target, out var targetBlock))
                {
                    Connect(block, targetBlock);
                }

                if (i + 1 < blocks.Count)
                    Connect(block, blocks[i + 1]);
            }
            else if (lastInstr is not Return)
            {
                if (i + 1 < blocks.Count)
                    Connect(block, blocks[i + 1]);
            }
        }

        // Eliminate unreachable blocks
        var reachable = new HashSet<BasicBlock>();
        var queue = new Queue<BasicBlock>();
        if (cfg.Entry != null)
        {
            reachable.Add(cfg.Entry);
            queue.Enqueue(cfg.Entry);
        }
        while (queue.Count > 0)
        {
            var b = queue.Dequeue();
            foreach (var s in b.Successors)
            {
                if (reachable.Add(s)) queue.Enqueue(s);
            }
        }
        cfg.Blocks.RemoveAll(b => !reachable.Contains(b));

        return cfg;
    }

    private static bool IsTerminator(Instruction instr) =>
        instr is Jump || instr is Return || IsConditionalJump(instr, out _);

    private static bool IsConditionalJump(Instruction instr, out string target)
    {
        target = string.Empty;
        switch (instr)
        {
            case JumpIfZero j:
                target = j.Target;
                return true;
            case JumpIfNotZero j:
                target = j.Target;
                return true;
            case JumpIfEqual j:
                target = j.Target;
                return true;
            case JumpIfNotEqual j:
                target = j.Target;
                return true;
            case JumpIfLessThan j:
                target = j.Target;
                return true;
            case JumpIfLessOrEqual j:
                target = j.Target;
                return true;
            case JumpIfGreaterThan j:
                target = j.Target;
                return true;
            case JumpIfGreaterOrEqual j:
                target = j.Target;
                return true;
            case JumpIfBitSet j:
                target = j.Target;
                return true;
            case JumpIfBitClear j:
                target = j.Target;
                return true;
            case BranchOnError b:
                target = b.ErrorLabel;
                return true;
            default: return false;
        }
    }

    private static void Connect(BasicBlock from, BasicBlock to)
    {
        if (!from.Successors.Contains(to)) from.Successors.Add(to);
        if (!to.Predecessors.Contains(from)) to.Predecessors.Add(from);
    }

    // --- Helpers ---

    private static (Temporary?, Constant?) MatchTmpConst(Val a, Val b, string tmpName)
    {
        if (a is Temporary t && t.Name == tmpName && b is Constant c)
            return (t, c);
        return (null, null);
    }

    // Within-block redundant array-load elimination (common-subexpression elimination).
    // Two identical ArrayLoad / ArrayLoadFlash of the same array and index, with no intervening
    // write that could change the result, collapse: the later load becomes a Copy of the
    // earlier load's destination (the existing copy-propagation/DCE then cleans up). Conservative
    // by construction — the availability table is cleared at every control-flow boundary, call,
    // indirect/opaque store or unrecognized instruction; a store to an array drops that array's
    // cached loads; writing a register drops entries holding or indexing on it. Volatile
    // (ISR-shared) arrays are never cached. Pure-flash loads are immutable so only their index
    // can invalidate them.
    private static void EliminateRedundantArrayLoads(Function func, HashSet<string>? volatileNames)
    {
        var avail = new Dictionary<(string Arr, string Idx), Val>();

        static string? ValKey(Val v) => v switch
        {
            Constant c  => "c" + c.Value,
            Variable vv => "v" + vv.Name,
            Temporary t => "t" + t.Name,
            _ => null,
        };
        static string? ValName(Val v) => v switch
        {
            Variable vv => vv.Name,
            Temporary t => t.Name,
            _ => null,
        };

        bool IsVol(string arr) => volatileNames != null && volatileNames.Contains(arr);

        for (int i = 0; i < func.Body.Count; i++)
        {
            var instr = func.Body[i];

            // (1) Invalidate based on the instruction's memory/control effect — BEFORE recording
            // this load, so a load never invalidates the very entry it is about to create.
            switch (instr)
            {
                case ArrayStore ast:
                    foreach (var k in avail.Keys.Where(k => k.Arr == ast.ArrayName).ToList()) avail.Remove(k);
                    break;
                case ArrayLoad or ArrayLoadFlash or Binary or Unary or Copy or Bitcast or BitCheck:
                    break; // side-effect-free data ops; register effects handled next
                default:
                    avail.Clear(); // call / branch / label / indirect store / GC / asm / unknown
                    break;
            }

            // (2) A register write invalidates entries that hold that value or index on it.
            string? wn = GetDst(instr) is { } w ? ValName(w) : null;
            if (wn != null && avail.Count > 0)
                foreach (var k in avail.Keys
                             .Where(k => ValName(avail[k]) == wn || k.Idx == "v" + wn || k.Idx == "t" + wn)
                             .ToList())
                    avail.Remove(k);

            // (3) Reuse a previously loaded value, or record this one.
            string? arr = instr switch { ArrayLoad al => al.ArrayName, ArrayLoadFlash alf => alf.ArrayName, _ => null };
            if (arr != null)
            {
                Val idx = instr is ArrayLoad a ? a.Index : ((ArrayLoadFlash)instr).Index;
                Val dst = instr is ArrayLoad a2 ? a2.Dst : ((ArrayLoadFlash)instr).Dst;
                string? ik = ValKey(idx);
                if (ik != null && !IsVol(arr))
                {
                    var key = (arr, ik);
                    if (avail.TryGetValue(key, out var prev) && !Equals(prev, dst))
                        func.Body[i] = new Copy(prev, dst);
                    else
                        avail[key] = dst;
                }
            }
        }
    }

    private static Val? GetDst(Instruction instr) => instr switch
    {
        Binary b => b.Dst,
        Unary u => u.Dst,
        Copy c => c.Dst,
        Bitcast bc => bc.Dst,
        Call cl => cl.Dst,
        BitCheck bck => bck.Dst,
        LoadIndirect li => li.Dst,
        ArrayLoad al => al.Dst,
        ArrayLoadFlash alf => alf.Dst,
        FlashLoadPtr flp => flp.Dst,
        BytearrayLoad bld => bld.Dst,
        _ => null,
    };

    private static Instruction ReplaceDst(Instruction instr, Val newDst) => instr switch
    {
        Binary b => b with { Dst = newDst },
        Unary u => u with { Dst = newDst },
        Copy c => c with { Dst = newDst },
        Bitcast bc => bc with { Dst = newDst },
        Call cl => cl with { Dst = newDst },
        BitCheck bck => bck with { Dst = newDst },
        LoadIndirect li => li with { Dst = newDst },
        ArrayLoad al => al with { Dst = newDst },
        ArrayLoadFlash alf => alf with { Dst = newDst },
        FlashLoadPtr flp => flp with { Dst = newDst },
        BytearrayLoad bld => bld with { Dst = newDst },
        _ => instr,
    };

    private static void RegisterUses(Instruction instr, Action<Val> register)
    {
        switch (instr)
        {
            case Return r: register(r.Value); break;
            case Unary u: register(u.Src); break;
            case Binary b:
                register(b.Src1);
                register(b.Src2);
                break;
            case Copy c: register(c.Src); break;
            case Bitcast bc: register(bc.Src); break;
            case JumpIfZero j: register(j.Condition); break;
            case JumpIfNotZero j: register(j.Condition); break;
            case Call cl:
                foreach (var a in cl.Args) register(a);
                break;
            case IndirectCall ic:
                register(ic.FuncAddr);
                foreach (var a in ic.Args) register(a);
                break;
            case BitCheck bc: register(bc.Source); break;
            case BitWrite bw:
                register(bw.Target);
                register(bw.Src);
                break;
            case BitSet bs: register(bs.Target); break;
            case BitClear bcl: register(bcl.Target); break;
            case JumpIfEqual je:
                register(je.Src1);
                register(je.Src2);
                break;
            case JumpIfNotEqual jne:
                register(jne.Src1);
                register(jne.Src2);
                break;
            case JumpIfLessThan jlt:
                register(jlt.Src1);
                register(jlt.Src2);
                break;
            case JumpIfLessOrEqual jle:
                register(jle.Src1);
                register(jle.Src2);
                break;
            case JumpIfGreaterThan jgt:
                register(jgt.Src1);
                register(jgt.Src2);
                break;
            case JumpIfGreaterOrEqual jge:
                register(jge.Src1);
                register(jge.Src2);
                break;
            case JumpIfBitSet jbs: register(jbs.Source); break;
            case JumpIfBitClear jbc: register(jbc.Source); break;
            case AugAssign aa:
                register(aa.Target);
                register(aa.Operand);
                break;
            case LoadIndirect li: register(li.SrcPtr); break;
            case StoreIndirect si:
                register(si.Src);
                register(si.DstPtr);
                break;
            case ArrayLoad al: register(al.Index); break;
            case ArrayLoadFlash alf: register(alf.Index); break;
            case FlashLoadPtr flp: register(flp.Ptr); register(flp.Index); break;
            case InlineAsm ia when ia.Operands != null:
                foreach (var op in ia.Operands) register(op);
                break;
            case ArrayStore ast:
                register(ast.Index);
                register(ast.Src);
                break;
            case BytearrayStore bst:
                register(bst.Index);
                register(bst.Src);
                break;
            case BytearrayLoad bld:
                register(bld.Index);
                break;
        }
    }

    private static Instruction ReplaceUses(Instruction instr, Func<Val, Val> replace)
    {
        return instr switch
        {
            Return r => r with { Value = replace(r.Value) },
            Unary u => u with { Src = replace(u.Src) },
            Binary b => b with { Src1 = replace(b.Src1), Src2 = replace(b.Src2) },
            Copy c => c with { Src = replace(c.Src) },
            Bitcast bc => bc with { Src = replace(bc.Src) },
            JumpIfZero j => j with { Condition = replace(j.Condition) },
            JumpIfNotZero j => j with { Condition = replace(j.Condition) },
            Call cl => cl with { Args = cl.Args.Select(replace).ToList() },
            BitCheck bc => bc with { Source = replace(bc.Source) },
            BitWrite bw => bw with { Target = replace(bw.Target), Src = replace(bw.Src) },
            BitSet bs => bs with { Target = replace(bs.Target) },
            BitClear bcl => bcl with { Target = replace(bcl.Target) },
            JumpIfEqual je => je with { Src1 = replace(je.Src1), Src2 = replace(je.Src2) },
            JumpIfNotEqual jne => jne with { Src1 = replace(jne.Src1), Src2 = replace(jne.Src2) },
            JumpIfLessThan jlt => jlt with { Src1 = replace(jlt.Src1), Src2 = replace(jlt.Src2) },
            JumpIfLessOrEqual jle => jle with { Src1 = replace(jle.Src1), Src2 = replace(jle.Src2) },
            JumpIfGreaterThan jgt => jgt with { Src1 = replace(jgt.Src1), Src2 = replace(jgt.Src2) },
            JumpIfGreaterOrEqual jge => jge with { Src1 = replace(jge.Src1), Src2 = replace(jge.Src2) },
            JumpIfBitSet jbs => jbs with { Source = replace(jbs.Source) },
            JumpIfBitClear jbc => jbc with { Source = replace(jbc.Source) },
            AugAssign aa => aa with { Operand = replace(aa.Operand) }, // Do NOT replace target
            LoadIndirect li => li with { SrcPtr = replace(li.SrcPtr) },
            StoreIndirect si => si with { Src = replace(si.Src), DstPtr = replace(si.DstPtr) },
            ArrayLoad al => al with { Index = replace(al.Index) },
            ArrayLoadFlash alf => alf with { Index = replace(alf.Index) },
            FlashLoadPtr flp => flp with { Ptr = replace(flp.Ptr), Index = replace(flp.Index) },
            InlineAsm ia when ia.Operands != null => ia with { Operands = ia.Operands.Select(replace).ToList() },
            ArrayStore ast => ast with { Index = replace(ast.Index), Src = replace(ast.Src) },
            BytearrayStore bst => bst with { Index = replace(bst.Index), Src = replace(bst.Src) },
            BytearrayLoad bld => bld with { Index = replace(bld.Index) },
            _ => instr,
        };
    }

    private const int MaxUnrollTripCount = 16;

    // Max size increase (in IR instructions) allowed when unrolling a constant loop.
    // Tiny bodies still unroll (loop overhead dominates); heavy bodies stay as loops.
    private const int UnrollSizeBudget = 64;

    private static bool UnrollConstantLoops(Function func)
    {
        bool anyUnrolled = false;
        bool changed = true;
        while (changed)
        {
            changed = false;
            var body = func.Body;
            for (int i = 0; i < body.Count - 1; i++)
            {
                if (body[i] is not Label { Name: var lStart }) continue;
                if (body[i + 1] is not JumpIfGreaterOrEqual {
                    Src1: Variable { Name: var loopVar },
                    Src2: Constant { Value: var tripN },
                    Target: var lEnd }) continue;
                if (tripN <= 0 || tripN > MaxUnrollTripCount) continue;

                int backJumpIdx = -1, endLabelIdx = -1;
                for (int j = i + 2; j < body.Count - 1; j++)
                {
                    if (body[j] is Jump { Target: var jt } && jt == lStart &&
                        body[j + 1] is Label { Name: var lt } && lt == lEnd)
                    { backJumpIdx = j; endLabelIdx = j + 1; break; }
                }
                if (backJumpIdx < 0) continue;

                var loopBody = body.GetRange(i + 2, backJumpIdx - (i + 2));

                if (loopBody.Any(instr => instr is Jump { Target: var t } && t == lEnd)) continue;
                if (loopBody.Any(instr => instr is Jump { Target: var t2 } && t2 == lStart)) continue;

                // The increment must be the last non-DebugLine instruction in the loop body.
                // This prevents matching conditional increments inside if-blocks.
                int incrIdx = -1;
                for (int bi = loopBody.Count - 1; bi >= 0; bi--)
                {
                    if (loopBody[bi] is DebugLine) continue;
                    if (loopBody[bi] is AugAssign { Op: BinaryOp.Add, Target: Variable { Name: var tv }, Operand: Constant { Value: 1 } }
                        && tv == loopVar)
                        incrIdx = bi;
                    break;
                }
                if (incrIdx < 0) continue;

                int initValue = -1;
                for (int j = i - 1; j >= Math.Max(0, i - 20); j--)
                {
                    if (body[j] is Copy { Src: Constant { Value: var cv }, Dst: Variable { Name: var dv } } && dv == loopVar)
                    { initValue = cv; break; }
                    if (body[j] is Label) break;
                }
                if (initValue < 0) continue;

                int tripCount = tripN - initValue;
                if (tripCount <= 0 || tripCount > MaxUnrollTripCount) continue;

                // Size guard: unrolling replaces a (body + ~5 instr overhead) loop with
                // tripCount copies of the body, so the size delta is ~(tripCount-1)*body.
                // Only unroll when that increase is small — duplicating a heavy body (e.g.
                // a per-iteration I2C/SPI write) explodes flash, the opposite of the intent.
                int realBody = loopBody.Count(instr => instr is not (Label or DebugLine));
                if ((tripCount - 1) * realBody > UnrollSizeBudget) continue;

                var bodyLabels = new HashSet<string>(loopBody.OfType<Label>().Select(l => l.Name));
                var unrolled = new List<Instruction>();
                for (int k = initValue; k < tripN; k++)
                {
                    unrolled.Add(new Copy(new Constant(k), new Variable(loopVar)));
                    for (int bi = 0; bi < loopBody.Count; bi++)
                    {
                        if (bi == incrIdx) continue;
                        unrolled.Add(RenameBodyLabels(loopBody[bi], bodyLabels, k));
                    }
                }
                unrolled.Add(new Copy(new Constant(tripN), new Variable(loopVar)));

                body.RemoveRange(i, endLabelIdx - i + 1);
                body.InsertRange(i, unrolled);
                func.Body = body;
                anyUnrolled = true;
                changed = true;
                break;
            }
        }
        return anyUnrolled;
    }

    // -------------------------------------------------------------------------
    // Devirtualization helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Replace VirtualCall instructions with direct Calls wherever the target
    /// can be proven unambiguous.
    /// </summary>
    private static void DevirtualizeCalls(
        Function func,
        Dictionary<string, HashSet<string>> classChildren,
        Dictionary<string, HashSet<string>> classDirectMethods)
    {
        for (int i = 0; i < func.Body.Count; i++)
        {
            if (func.Body[i] is not VirtualCall vc) continue;

            bool devirt =
                // Rule 1: declared class is a leaf (no known subclasses).
                !classChildren.TryGetValue(vc.DeclaredClass, out var ch) || ch.Count == 0
                // Rule 3: no subclass in the subtree overrides the method.
                || IsMethodNeverOverriddenOpt(vc.DeclaredClass, vc.MethodName, classChildren, classDirectMethods);

            if (!devirt) continue;

            // Direct call: self is passed as the first argument per the PyMCU ABI.
            string target = vc.DefiningClass + "_" + vc.MethodName;
            var callArgs = new List<Val> { vc.Self };
            callArgs.AddRange(vc.Args);
            func.Body[i] = new Call(target, callArgs, vc.Dst);
        }
    }

    private static bool IsMethodNeverOverriddenOpt(
        string cls, string methodName,
        Dictionary<string, HashSet<string>> classChildren,
        Dictionary<string, HashSet<string>> classDirectMethods)
    {
        if (!classChildren.TryGetValue(cls, out var children)) return true;
        foreach (var child in children)
        {
            if (classDirectMethods.TryGetValue(child, out var dm) && dm.Contains(methodName))
                return false;
            if (!IsMethodNeverOverriddenOpt(child, methodName, classChildren, classDirectMethods))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Build vtable specs for classes that still have residual VirtualCall nodes
    /// after devirtualization.  Returns an empty list for all current programs.
    /// </summary>
    private static List<VtableSpec> BuildVtableSpecs(ProgramIR program)
    {
        // Collect (declaredClass, methodName) pairs that survived devirtualization.
        var needsVtable = new Dictionary<string, HashSet<string>>();
        foreach (var func in program.Functions)
        {
            foreach (var instr in func.Body)
            {
                if (instr is not VirtualCall vc) continue;
                if (!needsVtable.TryGetValue(vc.DeclaredClass, out var ms))
                    needsVtable[vc.DeclaredClass] = ms = new HashSet<string>();
                ms.Add(vc.MethodName);
            }
        }

        var specs = new List<VtableSpec>();
        foreach (var (cls, methods) in needsVtable)
        {
            // Build vtable: one entry per virtual method.
            var entries = methods
                .Select(m => new VtableEntry
                {
                    MethodName    = m,
                    DefiningClass = WalkMROForMethodOpt(cls, m, program.ClassDirectMethods,
                                                        program.ClassChildren),
                })
                .ToList();

            specs.Add(new VtableSpec { ClassName = cls, Entries = entries });
        }
        return specs;
    }

    private static string WalkMROForMethodOpt(
        string cls, string methodName,
        Dictionary<string, HashSet<string>> classDirectMethods,
        Dictionary<string, HashSet<string>> classChildren)
    {
        // We don't have classBasePrefixes here; do a BFS over classChildren inverse to find
        // the declaring class.  For the common case (leaf class or not overridden) the
        // DevirtualizeCalls pass already handled this; this path is rarely reached.
        string? current = cls;
        // Build a reverse map on the fly: child → parent via classChildren values.
        var childToParent = new Dictionary<string, string>();
        foreach (var (parent, children) in classChildren)
            foreach (var child in children)
                childToParent[child] = parent;

        while (current != null)
        {
            if (classDirectMethods.TryGetValue(current, out var dm) && dm.Contains(methodName))
                return current;
            childToParent.TryGetValue(current, out current);
        }
        return cls;
    }

    private static Instruction RenameBodyLabels(Instruction instr, HashSet<string> bodyLabels, int iteration)
    {
        string R(string lbl) => bodyLabels.Contains(lbl) ? $"{lbl}_u{iteration}" : lbl;
        return instr switch
        {
            Label l when bodyLabels.Contains(l.Name) => new Label($"{l.Name}_u{iteration}"),
            Jump j => new Jump(R(j.Target)),
            JumpIfZero j => new JumpIfZero(j.Condition, R(j.Target)),
            JumpIfNotZero j => new JumpIfNotZero(j.Condition, R(j.Target)),
            JumpIfEqual j => new JumpIfEqual(j.Src1, j.Src2, R(j.Target)),
            JumpIfNotEqual j => new JumpIfNotEqual(j.Src1, j.Src2, R(j.Target)),
            JumpIfLessThan j => new JumpIfLessThan(j.Src1, j.Src2, R(j.Target)),
            JumpIfLessOrEqual j => new JumpIfLessOrEqual(j.Src1, j.Src2, R(j.Target)),
            JumpIfGreaterThan j => new JumpIfGreaterThan(j.Src1, j.Src2, R(j.Target)),
            JumpIfGreaterOrEqual j => new JumpIfGreaterOrEqual(j.Src1, j.Src2, R(j.Target)),
            JumpIfBitSet j => new JumpIfBitSet(j.Source, j.Bit, R(j.Target)),
            JumpIfBitClear j => new JumpIfBitClear(j.Source, j.Bit, R(j.Target)),
            _ => instr,
        };
    }

    // =====================================================================
    //  Parameterized outlining of @inline expansions  (generic / target-agnostic)
    //
    //  An @inline method on a zero-cost-abstraction object must be inlined (the
    //  receiver has no runtime representation), so a driver that issues e.g.
    //  command(0x28), command(0x0C), ... force-inlines one copy per call, each
    //  copy differing only in the *folded* constant.  This pass detects those
    //  repeated copies and collapses them into a single real subroutine whose
    //  parameters are exactly the constants that vary across the call sites; every
    //  site becomes an ordinary Call.
    //
    //  The frontend brackets each @inline expansion with a marker whose FuncName
    //  carries InlineMarkerTag.  For each *innermost* tagged region this pass:
    //    1. canonicalises it (drops debug/labels, runs region-local dead-store
    //       elimination, alpha-renames region-internal variables) so two
    //       expansions of the same method become byte-identical modulo constants;
    //    2. groups structurally-identical regions and turns the constants that
    //       vary across the group into parameters (invariant ones stay baked);
    //    3. synthesises a void subroutine and rewrites each region to a Call.
    //  It iterates to a fixpoint so that collapsing an inner expansion can expose
    //  an outer one, then strips every remaining tagged marker.
    //
    //  Contracts:
    //   * Conservative — anything it cannot prove safe (control flow, InlineAsm
    //     timing, memory-aliasing loads/stores, GC, exceptions, non-global live-in,
    //     any live-out, >4 varying constants, or a net size increase) is left
    //     exactly as the inliner produced it.
    //   * Idempotent — afterwards no tagged markers remain, so re-running is a
    //     no-op.
    //   * Target-independent — emits only standard Function/Call IR; no backend
    //     needs to know this pass ran.
    // =====================================================================
    public const string InlineMarkerTag = "@inl:";

    private static bool IsInlineTag(Instruction i) =>
        i is InlineExpansionMarker m &&
        m.FuncName.StartsWith(InlineMarkerTag, StringComparison.Ordinal);

    // Only side-effect-free arithmetic plus Call may be moved into a subroutine.
    private static bool IsOutlineable(Instruction i) =>
        i is Copy or Binary or Unary or Bitcast or Call;

    private static string? NameOf(Val? v) => v switch
    {
        Variable va => va.Name,
        Temporary t => t.Name,
        _ => null,
    };

    private static string? JumpTargetOf(Instruction i) => i switch
    {
        Jump j => j.Target,
        JumpIfZero j => j.Target,
        JumpIfNotZero j => j.Target,
        JumpIfEqual j => j.Target,
        JumpIfNotEqual j => j.Target,
        JumpIfLessThan j => j.Target,
        JumpIfLessOrEqual j => j.Target,
        JumpIfGreaterThan j => j.Target,
        JumpIfGreaterOrEqual j => j.Target,
        JumpIfBitSet j => j.Target,
        JumpIfBitClear j => j.Target,
        _ => null,
    };

    private sealed class RegionCanon
    {
        public required Function Func;
        public required int Start;          // index of the begin marker
        public required int End;            // index of the end marker
        public required string Callee;
        public required List<Instruction> Core;          // post-DCE region body
        public required Dictionary<string, int> Rename;  // local name -> canonical id
        public required HashSet<string> Inputs;          // names that are live-in locals
        public required List<Val> InputVals;             // live-in vals, canonical order
        public required string Signature;                // constants blanked
        public required List<long> HoleValues;           // one per blanked constant
        public required List<DataType> HoleTypes;        // inferred slot type
    }

    private static void OutlineInlineExpansions(ProgramIR program, HashSet<string> globalNames)
    {
        int counter = 0;
        // Fixpoint. Each step either outlines a viable group (rewriting its sites
        // to Calls) or, when no group is viable, "promotes" the current innermost
        // regions by dropping their boundary markers — which de-nests them and
        // exposes the enclosing expansion as the next innermost candidate. Both
        // operations strictly reduce the tagged-marker count, so this terminates;
        // the bound is a safety net.
        int budget = 100000;
        while (budget-- > 0)
        {
            if (OutlineOneRound(program, globalNames, ref counter)) continue;
            if (PromoteInnermostRegions(program)) continue;
            break;
        }

        // Strip any stragglers; un-outlined @inline regions simply stay inline.
        // After this the IR holds no tagged markers (idempotent) and backends only
        // ever see the untouched non-@inline markers.
        foreach (var func in program.Functions)
            func.Body.RemoveAll(IsInlineTag);
    }

    private static bool OutlineOneRound(ProgramIR program, HashSet<string> globalNames, ref int counter)
    {
        // Every jump target in the program, so a region-internal label that some
        // jump relies on is never dropped.
        var jumpTargets = new HashSet<string>();
        foreach (var func in program.Functions)
            foreach (var instr in func.Body)
                if (JumpTargetOf(instr) is string t) jumpTargets.Add(t);

        var paramTypeMemo = new Dictionary<string, List<DataType>>();
        var groups = new Dictionary<string, List<RegionCanon>>();
        foreach (var func in program.Functions)
            foreach (var (start, end, callee) in FindInnermostTaggedRegions(func))
            {
                var canon = TryCanonicalizeRegion(program, func, start, end, callee,
                    globalNames, jumpTargets, paramTypeMemo);
                if (canon == null) continue;
                string key = callee + "" + canon.Signature;
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<RegionCanon>();
                list.Add(canon);
            }

        foreach (var kv in groups)
            if (kv.Value.Count >= 2 && TryOutlineGroup(program, kv.Value, ref counter))
                return true;
        return false;
    }

    // Drop the boundary markers of every innermost tagged region, promoting its
    // body into the enclosing expansion (which becomes innermost next round).
    // Returns whether anything was removed.
    private static bool PromoteInnermostRegions(ProgramIR program)
    {
        bool any = false;
        foreach (var func in program.Functions)
        {
            var regions = FindInnermostTaggedRegions(func);
            if (regions.Count == 0) continue;
            var drop = new List<int>();
            foreach (var (start, end, _) in regions) { drop.Add(start); drop.Add(end); }
            drop.Sort();
            for (int i = drop.Count - 1; i >= 0; i--)
                func.Body.RemoveAt(drop[i]);
            any = true;
        }
        return any;
    }

    // Tagged regions that contain no nested tagged region (process these first;
    // outlining them exposes the enclosing ones on the next fixpoint round).
    private static List<(int start, int end, string callee)> FindInnermostTaggedRegions(Function func)
    {
        var body = func.Body;
        var open = new List<int>();                       // stack of begin indices
        var calleeOf = new Dictionary<int, string>();
        var hasNested = new HashSet<int>();               // begin had a nested begin
        var regions = new List<(int, int, string)>();
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i] is not InlineExpansionMarker m) continue;
            if (!m.FuncName.StartsWith(InlineMarkerTag, StringComparison.Ordinal)) continue;
            if (!m.IsEnd)
            {
                if (open.Count > 0) hasNested.Add(open[^1]);
                open.Add(i);
                calleeOf[i] = m.FuncName.Substring(InlineMarkerTag.Length);
            }
            else
            {
                if (open.Count == 0) continue;            // unbalanced; skip defensively
                int start = open[^1];
                open.RemoveAt(open.Count - 1);
                if (!hasNested.Contains(start))
                    regions.Add((start, i, calleeOf[start]));
            }
        }
        return regions;
    }

    private static RegionCanon? TryCanonicalizeRegion(
        ProgramIR program, Function func, int start, int end, string callee,
        HashSet<string> globalNames, HashSet<string> jumpTargets,
        Dictionary<string, List<DataType>> paramTypeMemo)
    {
        var body = func.Body;
        var raw = new List<Instruction>();
        var droppedLabels = new List<string>();
        for (int i = start + 1; i < end; i++)
        {
            switch (body[i])
            {
                case DebugLine: continue;
                case Label lab: droppedLabels.Add(lab.Name); continue;
                case InlineExpansionMarker: return null;
                default:
                    if (!IsOutlineable(body[i])) return null;
                    if (RegionHasUnsupportedVal(body[i])) return null;
                    raw.Add(body[i]);
                    break;
            }
        }
        if (raw.Count == 0) return null;
        foreach (var l in droppedLabels)
            if (jumpTargets.Contains(l)) return null;

        // Live-in / closed-region classification (on the pre-DCE body).
        // A local read before it is defined in the region is a *live-in input*:
        // it becomes a parameter and is passed by value at each call site. A
        // local defined in the region whose value is read after the region is a
        // live-out — the region is not closed, so we bail.
        var defined = new HashSet<string>();
        var liveIn = new HashSet<string>();
        var liveInVal = new Dictionary<string, Val>();
        foreach (var ins in raw)
        {
            RegisterUses(ins, v =>
            {
                if (NameOf(v) is string n && !defined.Contains(n) && !liveIn.Contains(n))
                {
                    liveIn.Add(n);
                    liveInVal[n] = v;
                }
            });
            if (NameOf(GetDst(ins)) is string d) defined.Add(d);
        }
        // Globals flow in/out by name; only *locals* may be inputs or escape.
        var inputs = new HashSet<string>();
        foreach (var n in liveIn)
        {
            if (globalNames.Contains(n)) continue;     // read-only global, referenced directly
            if (defined.Contains(n)) return null;  // mixed role
            inputs.Add(n);
        }
        // A region def escapes only if some path after the region reads it before
        // redefining it. The inliner reuses names (e.g. inline2._nibble.base) across
        // sibling expansions, so a plain "read anywhere outside" test gives false
        // positives — the next expansion rewrites the name before reading it. Walk
        // forward from the region end and decide per name.
        foreach (var d in defined)
        {
            if (globalNames.Contains(d)) return null;
            if (IsLiveAfter(body, end, d)) return null;
        }

        // Region-local dead-store elimination (removes folded-but-unused copies
        // the inliner leaves behind, so sibling expansions canonicalise alike).
        var core = new List<Instruction>(raw);
        for (bool removed = true; removed;)
        {
            removed = false;
            var readsAfter = new HashSet<string>();
            for (int i = core.Count - 1; i >= 0; i--)
            {
                var ins = core[i];
                bool pure = ins is Copy or Binary or Unary or Bitcast;
                if (pure && NameOf(GetDst(ins)) is string dn && !readsAfter.Contains(dn))
                {
                    core.RemoveAt(i);
                    removed = true;
                    break;
                }
                RegisterUses(ins, v => { if (NameOf(v) is string n) readsAfter.Add(n); });
            }
        }
        if (core.Count == 0) return null;

        // Canonical local rename: id by first appearance (uses before dst, so an
        // input gets a lower id than the value it feeds — and the ordering is
        // identical for two structurally-equal regions).
        var rename = new Dictionary<string, int>();
        int next = 0;
        void See(Val? v)
        {
            if (NameOf(v) is string n && !globalNames.Contains(n) && !rename.ContainsKey(n))
                rename[n] = next++;
        }
        foreach (var ins in core)
        {
            RegisterUses(ins, See);
            See(GetDst(ins));
        }

        // An input whose only uses were dead-store-eliminated no longer appears;
        // keep only inputs that survive in the canonical body.
        inputs.RemoveWhere(n => !rename.ContainsKey(n));
        // Inputs in canonical (id) order, with the actual val each site passes.
        var inputOrder = inputs.OrderBy(n => rename[n]).ToList();
        var inputVals = inputOrder.Select(n => liveInVal[n]).ToList();

        var sig = new StringBuilder();
        var holeVals = new List<long>();
        var holeTypes = new List<DataType>();
        foreach (var ins in core)
            EmitCanon(program, ins, rename, inputs, sig, holeVals, holeTypes, paramTypeMemo);

        return new RegionCanon
        {
            Func = func, Start = start, End = end, Callee = callee,
            Core = core, Rename = rename, Inputs = inputs, InputVals = inputVals,
            Signature = sig.ToString(), HoleValues = holeVals, HoleTypes = holeTypes,
        };
    }

    // True if `name` may be read after `endIdx` before being redefined. A straight
    // forward walk suffices for the straight-line driver sequences we outline; any
    // control flow before resolution is treated conservatively as live.
    private static bool IsLiveAfter(List<Instruction> body, int endIdx, string name)
    {
        for (int i = endIdx + 1; i < body.Count; i++)
        {
            var ins = body[i];
            bool reads = false;
            RegisterUses(ins, v => { if (NameOf(v) == name) reads = true; });
            if (reads) return true;
            if (NameOf(GetDst(ins)) == name) return false;     // redefined before any read
            if (JumpTargetOf(ins) != null) return true;        // branch -> conservative
        }
        return false;
    }

    // Reject vals the outliner does not model (floats, flash/array/funcref refs).
    private static bool RegionHasUnsupportedVal(Instruction ins)
    {
        bool bad = false;
        void Check(Val? v)
        {
            if (v is FloatConstant or ArrayBase or FunctionRef or FlashStrAddr) bad = true;
        }
        Check(GetDst(ins));
        RegisterUses(ins, Check);
        return bad;
    }

    // Serialise one instruction with constants blanked to '#'.  EmitCanon and
    // RebuildOutlined MUST visit constant-bearing slots in the SAME order.
    private static void EmitCanon(
        ProgramIR program, Instruction ins, Dictionary<string, int> rename, HashSet<string> inputs,
        StringBuilder sig, List<long> holeVals, List<DataType> holeTypes,
        Dictionary<string, List<DataType>> paramTypeMemo)
    {
        void Slot(Val v, DataType ctx)
        {
            if (v is Constant c) { sig.Append('#'); holeVals.Add(c.Value); holeTypes.Add(ctx); }
            else sig.Append(CanonTok(v, rename, inputs));
        }

        sig.Append(CanonTok(GetDst(ins), rename, inputs)).Append('=');
        switch (ins)
        {
            case Copy c: sig.Append("cp,"); Slot(c.Src, GetDataType(c.Dst)); break;
            case Bitcast bc: sig.Append("bc,"); Slot(bc.Src, GetDataType(bc.Dst)); break;
            case Unary u: sig.Append("un").Append((int)u.Op).Append(','); Slot(u.Src, GetDataType(u.Dst)); break;
            case Binary b:
                sig.Append("bin").Append((int)b.Op).Append(',');
                Slot(b.Src1, GetDataType(b.Dst));
                Slot(b.Src2, GetDataType(b.Dst));
                break;
            case Call cl:
                sig.Append("call:").Append(cl.FunctionName).Append('/').Append(cl.Args.Count).Append(',');
                for (int i = 0; i < cl.Args.Count; i++)
                    Slot(cl.Args[i], CalleeParamType(program, cl.FunctionName, i, paramTypeMemo));
                break;
        }
        sig.Append(';');
    }

    private static string CanonTok(Val? v, Dictionary<string, int> rename, HashSet<string> inputs)
    {
        string Tok(string name) =>
            rename.TryGetValue(name, out int id) ? (inputs.Contains(name) ? "p" + id : "t" + id)
                                                 : "G:" + name;   // read-only global
        return v switch
        {
            null => "_",
            NoneVal => "_",
            Variable va => Tok(va.Name),
            Temporary t => Tok(t.Name),
            MemoryAddress m => "M" + m.Address + "." + (int)m.Type,
            _ => "?" + v.GetType().Name,
        };
    }

    private static DataType CalleeParamType(
        ProgramIR program, string callee, int idx, Dictionary<string, List<DataType>> memo)
    {
        if (!memo.TryGetValue(callee, out var types))
        {
            types = new List<DataType>();
            var f = program.Functions.FirstOrDefault(x => x.Name == callee);
            if (f != null)
                foreach (var pn in f.Params)
                {
                    DataType t = DataType.UNKNOWN;
                    foreach (var ins in f.Body)
                    {
                        if (NameOf(GetDst(ins)) == pn) { t = GetDataType(GetDst(ins)); if (t != DataType.UNKNOWN) break; }
                        DataType found = DataType.UNKNOWN;
                        RegisterUses(ins, v => { if (NameOf(v) == pn) { var dt = GetDataType(v); if (dt != DataType.UNKNOWN) found = dt; } });
                        if (found != DataType.UNKNOWN) { t = found; break; }
                    }
                    types.Add(t);
                }
            memo[callee] = types;
        }
        return idx < types.Count ? types[idx] : DataType.UNKNOWN;
    }

    // Target-agnostic word-cost proxy for the size guard.
    private static int InstrCost(Instruction i) => i switch
    {
        Call c => 1 + c.Args.Count,
        _ => 1,
    };

    private static bool TryOutlineGroup(ProgramIR program, List<RegionCanon> regions, ref int counter)
    {
        var r0 = regions[0];
        int nHoles = r0.HoleValues.Count;

        // Which blanked constants actually vary across the call sites.
        var variant = new List<int>();
        for (int k = 0; k < nHoles; k++)
        {
            long v0 = r0.HoleValues[k];
            if (regions.Any(r => r.HoleValues[k] != v0)) variant.Add(k);
        }
        // Live-in inputs (one parameter each) come first, then the varying
        // constants.  Inputs and structure are identical across the group by
        // construction (the signature encodes them), so r0 defines the layout.
        var inputNames = r0.InputVals.Select(NameOf).ToList();
        int nInputs = inputNames.Count;
        int nParams = nInputs + variant.Count;
        if (nParams is 0 or > 4) return false;                // nothing to share / arg limit

        int nSites = regions.Count;
        if (r0.Core.Count < 2) return false;
        // Net size proof.  Cost is a target-agnostic word proxy: a Call costs
        // 1 + argc (load each argument, then the call), every other instruction 1.
        //   inline  = nSites * bodyCost
        //   outline = bodyCost + 1 (ret) + nSites * (nParams + 1 call)
        long bodyCost = r0.Core.Sum(InstrCost);
        long inlineTotal = (long)nSites * bodyCost;
        long outlineTotal = bodyCost + 1 + (long)nSites * (nParams + 1);
        if (outlineTotal >= inlineTotal) return false;

        // Parameter types.  Inputs take their val's type; varying constants take
        // the inferred slot type widened to cover the actual values.
        var inputParamIndex = new Dictionary<string, int>();
        for (int i = 0; i < nInputs; i++)
        {
            string? n = inputNames[i];
            if (n == null) return false;
            inputParamIndex[n] = i;
        }
        var variantParamIndexOf = new Dictionary<int, int>();
        var finalHoleTypes = new DataType[nHoles];
        for (int k = 0; k < nHoles; k++) finalHoleTypes[k] = r0.HoleTypes[k];
        for (int pi = 0; pi < variant.Count; pi++)
        {
            int k = variant[pi];
            variantParamIndexOf[k] = pi;
            DataType t = r0.HoleTypes[k];
            long mx = regions.Max(r => Math.Abs(r.HoleValues[k]));
            DataType vt = mx <= 0xFF ? DataType.UINT8 : mx <= 0xFFFF ? DataType.UINT16 : DataType.UINT32;
            if (t is DataType.UNKNOWN or DataType.VOID) t = vt;
            else if (t.SizeOf() < vt.SizeOf()) t = vt;
            finalHoleTypes[k] = t;
        }

        string gName = "__pymcu_outline_" + counter++;
        var g = new Function { Name = gName, ReturnType = DataType.VOID, IsInline = false };
        for (int p = 0; p < nParams; p++) g.Params.Add(gName + ".p" + p);

        var variantSet = new HashSet<int>(variant);
        int ctr = 0;
        foreach (var ins in r0.Core)
            g.Body.Add(RebuildOutlined(ins, gName, r0.Rename, inputParamIndex, nInputs,
                variantSet, variantParamIndexOf, finalHoleTypes, ref ctr));
        g.Body.Add(new Return(new NoneVal()));
        program.Functions.Add(g);

        // Replace each region with a Call; splice high->low so indices stay valid.
        // Args = [live-in vals at this site] ++ [varying constant values].
        foreach (var byFunc in regions.GroupBy(r => r.Func))
            foreach (var r in byFunc.OrderByDescending(r => r.Start))
            {
                var args = new List<Val>(nParams);
                args.AddRange(r.InputVals);
                args.AddRange(variant.Select(k => (Val)new Constant((int)r.HoleValues[k])));
                r.Func.Body.RemoveRange(r.Start, r.End - r.Start + 1);
                r.Func.Body.Insert(r.Start, new Call(gName, args, new NoneVal()));
            }
        return true;
    }

    // Rebuild one instruction for the synthesised subroutine: live-in inputs and
    // varying constants become parameter references; region-internal locals get a
    // g-qualified canonical name; invariant constants stay as-is.  Visits constant
    // slots in EmitCanon order.
    private static Instruction RebuildOutlined(
        Instruction ins, string gName, Dictionary<string, int> rename,
        Dictionary<string, int> inputParamIndex, int nInputs,
        HashSet<int> variantSet, Dictionary<int, int> variantParamIndexOf,
        DataType[] holeTypes, ref int ctr)
    {
        Val MapName(Val v)
        {
            string? n = NameOf(v);
            if (n == null || !rename.ContainsKey(n)) return v;             // global / none / mem
            DataType ty = GetDataType(v);
            if (inputParamIndex.TryGetValue(n, out int pi))
                return new Variable(gName + ".p" + pi, ty);                // live-in parameter
            return v is Temporary ? new Temporary(gName + ".v" + rename[n], ty)
                                  : new Variable(gName + ".v" + rename[n], ty);
        }

        int hk = ctr;     // capture for closure ordering
        Val MapSlot(Val v)
        {
            if (v is Constant)
            {
                int k = hk++;
                if (variantSet.Contains(k))
                    return new Variable(gName + ".p" + (nInputs + variantParamIndexOf[k]), holeTypes[k]);
                return v;     // invariant -> keep original constant
            }
            return MapName(v);
        }

        Instruction result = ins switch
        {
            Copy c => new Copy(MapSlot(c.Src), MapName(c.Dst)),
            Bitcast bc => new Bitcast(MapSlot(bc.Src), MapName(bc.Dst)),
            Unary u => new Unary(u.Op, MapSlot(u.Src), MapName(u.Dst)),
            Binary b => new Binary(b.Op, MapSlot(b.Src1), MapSlot(b.Src2), MapName(b.Dst)),
            Call cl => new Call(cl.FunctionName, cl.Args.Select(MapSlot).ToList(), MapName(cl.Dst)),
            _ => ins,
        };
        ctr = hk;
        return result;
    }
}