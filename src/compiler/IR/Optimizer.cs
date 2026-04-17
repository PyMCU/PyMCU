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
            Functions = program.Functions.Select(CloneFunction).ToList(),
            ExternSymbols = new List<string>(program.ExternSymbols),
        };

        foreach (var func in optimized.Functions)
            OptimizeFunction(func);

        // Dead Function Elimination (DFE): remove functions that are never reachable
        // from main or any ISR.
        var callGraph = new Dictionary<string, HashSet<string>>();
        foreach (var func in optimized.Functions)
        {
            var callees = new HashSet<string>();
            foreach (var instr in func.Body)
            {
                if (instr is Call call) callees.Add(call.FunctionName);
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

        optimized.Functions.RemoveAll(f => !reachable.Contains(f.Name));
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
        Params = [..f.Params],
        ReturnType = f.ReturnType,
        Body = [..f.Body],
        IsInline = f.IsInline,
        IsInterrupt = f.IsInterrupt,
        InterruptVector = f.InterruptVector,
    };
}

    private static void OptimizeFunction(Function func)
    {
        for (var i = 0; i < 10; ++i)
        {
            PropagateCopies(func);
            FoldConstants(func);
            CoalesceInstructions(func);

            var cfg = BuildCfg(func);
            EliminateDeadCodeCfg(cfg);

            func.Body = cfg.Blocks.SelectMany(b => b.Instructions).ToList();
        }

        CollapseBoolJumps(func);
        CollapseBitChecks(func);

        var finalCfg = BuildCfg(func);
        EliminateDeadCodeCfg(finalCfg);
        func.Body = finalCfg.Blocks.SelectMany(b => b.Instructions).ToList();
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
                            func.Body[i] = new Copy(new Constant(result), binary.Dst);
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
                            func.Body[i] = new Copy(new Constant(result), unary.Dst);
                    }

                    break;
                }
            }
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
            // 1. Substitute uses
            func.Body[i] = ReplaceUses(func.Body[i], v =>
            {
                return v switch
                {
                    Temporary t when tempCopies.TryGetValue(t.Name, out var replacement) => replacement,
                    Variable var2 when varConsts.TryGetValue(var2.Name, out int cv) => new Constant(cv),
                    _ => v
                };
            });

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
                            tempCopies[tDst.Name] = copy.Src;
                    }

                    break;
                }
                case Copy copy:
                {
                    if (copy.Dst is Variable vDst)
                    {
                        if (copy.Src is Constant c) varConsts[vDst.Name] = c.Value;
                        else varConsts.Remove(vDst.Name);
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
                case Label:
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
                    Connect(block, targetBlock);

                if (i + 1 < blocks.Count)
                    Connect(block, blocks[i + 1]);
            }
            else if (lastInstr is not Return)
            {
                if (i + 1 < blocks.Count)
                    Connect(block, blocks[i + 1]);
            }
        }

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
        Call cl => cl.Dst,
        BitCheck bc => bc.Dst,
        LoadIndirect li => li.Dst,
        ArrayLoad al => al.Dst,
        ArrayLoadFlash alf => alf.Dst,
        _ => null,
    };

    private static Instruction ReplaceDst(Instruction instr, Val newDst) => instr switch
    {
        Binary b => b with { Dst = newDst },
        Unary u => u with { Dst = newDst },
        Copy c => c with { Dst = newDst },
        Call cl => cl with { Dst = newDst },
        BitCheck bc => bc with { Dst = newDst },
        LoadIndirect li => li with { Dst = newDst },
        ArrayLoad al => al with { Dst = newDst },
        ArrayLoadFlash alf => alf with { Dst = newDst },
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
            case JumpIfZero j: register(j.Condition); break;
            case JumpIfNotZero j: register(j.Condition); break;
            case Call cl:
                foreach (var a in cl.Args) register(a);
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
            _ => instr,
        };
    }
}