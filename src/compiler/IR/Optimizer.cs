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

using PyMCU.IR.CFG;

namespace PyMCU.IR;

public static class Optimizer
{
    public static ProgramIR Optimize(ProgramIR program)
    {
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

        foreach (var func in optimized.Functions)
            OptimizeFunction(func, globalNames);

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
                        foreach (var gName in globalVarNames)
                        {
                            if (asmStr.Contains(gName.Replace('.', '_')))
                                referencedGlobals.Add(gName);
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

        return optimized;


        void Enqueue(string name)
        {
            if (reachable.Add(name)) worklist.Enqueue(name);
        }
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

    private static void OptimizeFunction(Function func, HashSet<string>? globalNames = null)
    {
        for (var i = 0; i < 10; ++i)
        {
            PropagateCopies(func);
            FoldConstants(func);
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
                PropagateCopies(func);
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

    private static int? GetConstant(Val val) => val is Constant c ? c.Value : null;

    private static void FoldConstants(Function func)
    {
        for (var i = 0; i < func.Body.Count; ++i)
        {
            var instr = func.Body[i];
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
                            case BinaryOp.Div:
                                if (c2.Value != 0) result = c1.Value / c2.Value;
                                else foldable = false;
                                break;
                            case BinaryOp.FloorDiv:
                                if (c2.Value != 0)
                                {
                                    int q = c1.Value / c2.Value;
                                    if ((c1.Value ^ c2.Value) < 0 && q * c2.Value != c1.Value) q--;
                                    result = q;
                                }
                                else foldable = false;

                                break;
                            case BinaryOp.Mod:
                                if (c2.Value != 0) result = c1.Value % c2.Value;
                                else foldable = false;
                                break;
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

    private static void PropagateCopies(Function func)
    {
        var tempCopies = new Dictionary<string, Val>();
        var blacklistedTemps = new HashSet<string>();
        var varConsts = new Dictionary<string, int>();

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
                    if (!blacklistedTemps.Contains(tDst.Name))
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
                        if (copy.Src is Constant c)
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
                    varConsts.Clear();
                    break;
                // A call with ArrayBase args may modify variables through the pointer;
                // conservatively invalidate all tracked variable constants.
                case Call callInstr when callInstr.Args.Any(a => a is ArrayBase):
                    varConsts.Clear();
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
                        newBody.Add(ReplaceDst(func.Body[i], nextCopy.Dst));
                        i++; // skip the copy
                        continue;
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
        for (var i = 0; i + 1 < func.Body.Count; ++i)
        {
            if (func.Body[i] is not Binary bin) continue;
            if (bin.Dst is not Temporary dstTmp) continue;

            var j = i + 1;
            while (j < func.Body.Count && func.Body[j] is Label) j++;
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
            InlineAsm ia when ia.Operands != null => ia with { Operands = ia.Operands.Select(replace).ToList() },
            ArrayStore ast => ast with { Index = replace(ast.Index), Src = replace(ast.Src) },
            BytearrayStore bst => bst with { Index = replace(bst.Index), Src = replace(bst.Src) },
            BytearrayLoad bld => bld with { Index = replace(bld.Index) },
            _ => instr,
        };
    }

    private const int MaxUnrollTripCount = 16;

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
}