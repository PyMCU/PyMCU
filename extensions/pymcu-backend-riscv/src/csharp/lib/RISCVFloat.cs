/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

using PyMCU.IR;

namespace PyMCU.Backend.Targets.RiscV;

// float on a core with no FPU. A FLOAT value is its IEEE 754 binary32 bits in an ordinary
// integer register or frame slot, and every operation is a call into the routines of
// src/softfloat/softfloat.c (emitted below, only those the program reaches). The shape of
// the lowering follows the other backends: an integer operand next to a float one is
// converted first, a float stored into an integer destination truncates toward zero, and a
// comparison yields 0 or 1.
public partial class RiscvCodeGen
{
    private readonly HashSet<string> softFloatNeeded = new();

    private static bool IsFloatVal(Val v) => v is FloatConstant || TypeOf(v) == DataType.FLOAT;

    private static int FloatBits(double value) => BitConverter.SingleToInt32Bits((float)value);

    private void CallSoftFloat(string routine)
    {
        softFloatNeeded.Add(routine);
        Emit("call", routine);
    }

    // Value of v as a float in `reg`. An integer source is converted; a constant is converted
    // here, at compile time.
    private void LoadAsFloat(Val v, string reg)
    {
        if (IsFloatVal(v))
        {
            LoadIntoReg(v, reg);
            return;
        }

        if (v is Constant c)
        {
            Emit("li", reg, FloatBits(c.Value).ToString());
            return;
        }

        LoadIntoReg(v, "a0");
        CallSoftFloat(TypeOf(v) == DataType.UINT32 ? "__pymcu_u2f" : "__pymcu_i2f");
        if (reg != "a0") Emit("mv", reg, "a0");
    }

    private static bool NeedsConversionCall(Val v) => !IsFloatVal(v) && v is not Constant;

    // a0 = first operand, a1 = second, both as floats. A conversion is a call, and a call
    // clobbers a0 and a1, so the operand that needs one goes first and the other is loaded
    // after it; with two conversions the first result waits on the stack.
    private void LoadFloatPair(Val a, Val b)
    {
        bool convA = NeedsConversionCall(a), convB = NeedsConversionCall(b);

        if (convA && convB)
        {
            LoadAsFloat(b, "a0");
            Emit("addi", "sp", "sp", "-4");
            Emit("sw", "a0", "0(sp)");
            LoadAsFloat(a, "a0");
            Emit("lw", "a1", "0(sp)");
            Emit("addi", "sp", "sp", "4");
        }
        else if (convB)
        {
            LoadAsFloat(b, "a0");
            Emit("mv", "a1", "a0");
            LoadAsFloat(a, "a0");
        }
        else
        {
            LoadAsFloat(a, "a0");
            LoadAsFloat(b, "a1");
        }
    }

    // The float in a0 stored into `dst`, truncated toward zero when dst is an integer.
    private void StoreFloatResult(Val dst)
    {
        if (IsFloatVal(dst))
        {
            StoreRegInto("a0", dst);
            return;
        }

        CallSoftFloat(TypeOf(dst) == DataType.UINT32 ? "__pymcu_f2u" : "__pymcu_f2i");
        StoreRegInto("a0", dst);
    }

    private static bool IsComparison(PyMCU.IR.BinaryOp op) => op is PyMCU.IR.BinaryOp.Equal
        or PyMCU.IR.BinaryOp.NotEqual or PyMCU.IR.BinaryOp.LessThan or PyMCU.IR.BinaryOp.LessEqual
        or PyMCU.IR.BinaryOp.GreaterThan or PyMCU.IR.BinaryOp.GreaterEqual;

    // Leaves 0 or 1 in t0 from the -1/0/1/2 (less/equal/greater/unordered) __pymcu_fcmp left in
    // a0. An unordered pair is false for every relation but !=, as in IEEE 754.
    private void ReduceComparison(PyMCU.IR.BinaryOp op)
    {
        switch (op)
        {
            case PyMCU.IR.BinaryOp.Equal: Emit("seqz", "t0", "a0"); break;
            case PyMCU.IR.BinaryOp.NotEqual: Emit("snez", "t0", "a0"); break;
            case PyMCU.IR.BinaryOp.LessThan: Emit("addi", "t0", "a0", "1"); Emit("seqz", "t0", "t0"); break;
            case PyMCU.IR.BinaryOp.LessEqual: Emit("addi", "t0", "a0", "1"); Emit("sltiu", "t0", "t0", "2"); break;
            case PyMCU.IR.BinaryOp.GreaterThan: Emit("addi", "t0", "a0", "-1"); Emit("seqz", "t0", "t0"); break;
            case PyMCU.IR.BinaryOp.GreaterEqual: Emit("sltiu", "t0", "a0", "2"); break;
        }
    }

    private void CompileFloatBinary(PyMCU.IR.BinaryOp op, Val src1, Val src2, Val dst)
    {
        LoadFloatPair(src1, src2);

        if (IsComparison(op))
        {
            CallSoftFloat("__pymcu_fcmp");
            ReduceComparison(op);
            StoreRegInto("t0", dst);
            return;
        }

        string routine = op switch
        {
            PyMCU.IR.BinaryOp.Add => "__pymcu_fadd",
            PyMCU.IR.BinaryOp.Sub => "__pymcu_fsub",
            PyMCU.IR.BinaryOp.Mul => "__pymcu_fmul",
            PyMCU.IR.BinaryOp.Div or PyMCU.IR.BinaryOp.FloorDiv => "__pymcu_fdiv",
            _ => throw new NotSupportedException($"RISC-V backend: float binary op {op} is not supported."),
        };
        CallSoftFloat(routine);
        StoreFloatResult(dst);
    }

    private void CompileFloatUnary(Unary arg)
    {
        LoadAsFloat(arg.Src, "t0");
        switch (arg.Op)
        {
            case PyMCU.IR.UnaryOp.Neg:
                Emit("li", "t1", "-2147483648");
                Emit("xor", "t0", "t0", "t1");
                if (IsFloatVal(arg.Dst))
                {
                    StoreRegInto("t0", arg.Dst);
                    return;
                }
                Emit("mv", "a0", "t0");
                StoreFloatResult(arg.Dst);
                return;
            case PyMCU.IR.UnaryOp.Not:
                // not x is x == 0.0: only +0.0 and -0.0 have all bits but the sign clear, and
                // a NaN is truthy, as in CPython.
                Emit("slli", "t0", "t0", "1");
                Emit("seqz", "t0", "t0");
                StoreRegInto("t0", arg.Dst);
                return;
            default:
                throw new NotSupportedException($"RISC-V backend: float unary op {arg.Op} is not supported.");
        }
    }

    // Copy is also the conversion between the two kinds.
    private void CompileFloatCopy(Copy arg)
    {
        bool srcF = IsFloatVal(arg.Src), dstF = IsFloatVal(arg.Dst);
        if (srcF && dstF)
        {
            LoadIntoReg(arg.Src, "t0");
            StoreRegInto("t0", arg.Dst);
            return;
        }

        if (dstF)
        {
            LoadAsFloat(arg.Src, "a0");
            StoreRegInto("a0", arg.Dst);
            return;
        }

        LoadIntoReg(arg.Src, "a0");
        StoreFloatResult(arg.Dst);
    }

    // A branch on a relation between floats (or a float and an integer): the same __pymcu_fcmp
    // as the value form, then the branch on its answer.
    private void CompileFloatJump(PyMCU.IR.BinaryOp relation, Val src1, Val src2, string target)
    {
        LoadFloatPair(src1, src2);
        CallSoftFloat("__pymcu_fcmp");
        ReduceComparison(relation);
        Emit("bnez", "t0", target);
    }

    // if x: / if not x: on a float is a test against 0.0 (a NaN counts as true).
    private void CompileFloatTruthJump(Val cond, string target, bool jumpWhenZero)
    {
        LoadIntoReg(cond, "t0");
        Emit("slli", "t0", "t0", "1");
        Emit(jumpWhenZero ? "beqz" : "bnez", "t0", target);
    }

    private void EmitSoftFloat() => EmitCRuntime(softFloatNeeded, SoftFloatRoutines, "sf");

    // Emits the routines in `roots` and everything they call, from a runtime compiled from C
    // (see tools/gen_c_runtimes.py). The integer multiply the compiled code may call is the
    // backend's own helper, which is then emitted too.
    private void EmitCRuntime(HashSet<string> roots, Dictionary<string, (string[] Asm, string[] Calls)> routines,
        string labelPrefix)
    {
        if (roots.Count == 0) return;

        var order = new List<string>();
        var seen = new HashSet<string>();
        void Visit(string name)
        {
            if (!seen.Add(name)) return;
            if (name == "__mulsi3") { softFloatNeedsMul = true; return; }
            if (!routines.TryGetValue(name, out var routine)) return;
            order.Add(name);
            foreach (var callee in routine.Calls) Visit(callee);
        }
        foreach (var name in roots.OrderBy(n => n, StringComparer.Ordinal)) Visit(name);

        EmitRaw(".section .text");
        EmitRaw(".align 2");
        // Each runtime was compiled on its own, so each numbers its local labels from .L0 up: a
        // program that reaches the soft float AND the heap would define .L44 twice, and the
        // assembler stops at the second. Prefixing the numbers keeps the two apart.
        foreach (var name in order)
            foreach (var line in routines[name].Asm)
                EmitRaw(System.Text.RegularExpressions.Regex.Replace(line, @"\.L(\d+)", $".L{labelPrefix}$1"));
    }

    // The routines call the runtime's multiply when the core has none of its own.
    private bool softFloatNeedsMul;
}
