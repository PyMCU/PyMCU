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

namespace PyMCU.IR;

// --- Operand Types ---
public abstract record Val;

public record Constant(int Value) : Val;

public record FloatConstant(double Value) : Val;

public record Variable(string Name, DataType Type = DataType.UINT8) : Val;

public record Temporary(string Name, DataType Type = DataType.UINT8) : Val;

// Represents a physical memory address (MMIO or Static Global)
public record MemoryAddress(int Address, DataType Type = DataType.UINT8) : Val;

public record NoneVal() : Val;

public enum UnaryOp
{
    Not,
    Neg,
    BitNot
}

public enum BinaryOp
{
    Add,
    Sub,
    Mul,
    Div,
    FloorDiv,
    Mod,
    Equal,
    NotEqual,
    LessThan,
    LessEqual,
    GreaterThan,
    GreaterEqual,
    BitAnd,
    BitOr,
    BitXor,
    LShift,
    RShift
}

// --- Instructions ---
public abstract record Instruction;

public record Return(Val Value) : Instruction;

public record Unary(UnaryOp Op, Val Src, Val Dst) : Instruction;

public record Binary(BinaryOp Op, Val Src1, Val Src2, Val Dst) : Instruction;

public record Copy(Val Src, Val Dst) : Instruction;

// Indirect Memory Access (Pointer Dereference)
public record LoadIndirect(Val SrcPtr, Val Dst) : Instruction;

public record StoreIndirect(Val Src, Val DstPtr) : Instruction;

public record Jump(string Target) : Instruction;

public record JumpIfZero(Val Condition, string Target) : Instruction;

public record JumpIfNotZero(Val Condition, string Target) : Instruction;

// --- Relational Jumps (Optimization) ---
public record JumpIfEqual(Val Src1, Val Src2, string Target) : Instruction;

public record JumpIfNotEqual(Val Src1, Val Src2, string Target) : Instruction;

public record JumpIfLessThan(Val Src1, Val Src2, string Target) : Instruction;

public record JumpIfLessOrEqual(Val Src1, Val Src2, string Target) : Instruction;

public record JumpIfGreaterThan(Val Src1, Val Src2, string Target) : Instruction;

public record JumpIfGreaterOrEqual(Val Src1, Val Src2, string Target) : Instruction;

public record Label(string Name) : Instruction;

public record Call(string FunctionName, List<Val> Args, Val Dst) : Instruction;

public record BitSet(Val Target, int Bit) : Instruction;

public record BitClear(Val Target, int Bit) : Instruction;

public record BitCheck(Val Source, int Bit, Val Dst) : Instruction;

public record BitWrite(Val Target, int Bit, Val Src) : Instruction;

// Optimized conditional jumps on bit state (for tight polling loops)
public record JumpIfBitSet(Val Source, int Bit, string Target) : Instruction;

public record JumpIfBitClear(Val Source, int Bit, string Target) : Instruction;

// Augmented assignment: target op= operand (in-place modification)
public record AugAssign(BinaryOp Op, Val Target, Val Operand) : Instruction;

// Inline assembly.
// When Operands is non-null, the code string contains %0, %1, ... placeholders
// that are substituted with registers assigned to the corresponding operands by
// the backend.  All operands are treated as read-write (loaded before, stored after).
// Maximum 4 operands (%0–%3).  Only uint8 (single-register) operands are supported.
public record InlineAsm(string Code, IList<Val>? Operands = null) : Instruction;

// Debugging
public record DebugLine(int Line, string Text, string SourceFile) : Instruction;

// Variable-index array load: dst = array_name[index]
public record ArrayLoad(string ArrayName, Val Index, Val Dst, DataType ElemType, int Count) : Instruction;

// ArrayLoad for flash-resident (PROGMEM) byte arrays: read via LPM Z.
public record ArrayLoadRo(string ArrayName, Val Index, Val Dst) : Instruction;

// Flash-resident read-only byte array (placed in .text / PROGMEM via const[uint8[N]]).
// Bytes holds the literal initializer values; AVR codegen emits a .db table in flash.
public record RoData(string Name, List<int> Bytes) : Instruction;

// Variable-index array store: array_name[index] = src
public record ArrayStore(string ArrayName, Val Index, Val Src, DataType ElemType, int Count) : Instruction;

// Floating-point binary operation (explicit float arithmetic instruction).
// Op is one of Add/Sub/Mul/Div/FloorDiv/Mod; Dst has DataType.FLOAT.
public record FloatBinary(BinaryOp Op, Val Src1, Val Src2, Val Dst) : Instruction;

// Zero-extend (unsigned) or sign-extend (signed) a narrower value to a wider type.
public record Widen(Val Src, DataType FromType, DataType ToType, Val Dst) : Instruction;

// Truncate a wider value to a narrower type.
public record Narrow(Val Src, DataType FromType, DataType ToType, Val Dst) : Instruction;

// --- Function Definition ---
public class Function
{
    public string Name { get; set; } = "";
    public List<string> Params { get; set; } = new();
    public DataType ReturnType { get; set; } = DataType.VOID;
    public List<Instruction> Body { get; set; } = new();
    public bool IsInline { get; set; } = false;
    public bool IsInterrupt { get; set; } = false;
    public int InterruptVector { get; set; } = 0;
}

public class ProgramIR
{
    public List<Variable> Globals { get; set; } = new();

    public List<Function> Functions { get; set; } = new();

    // C symbols declared via @extern("name") in the source.
    public List<string> ExternSymbols { get; set; } = new();
}