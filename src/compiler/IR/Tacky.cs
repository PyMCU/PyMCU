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

// Address-of a local or module-level array (passed as pointer to a bytearray param).
public record ArrayBase(string ArrayName) : Val;

// Compile-time resolved function address (for funcref() intrinsic)
public record FunctionRef(string FunctionName) : Val;

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
public record Bitcast(Val Src, Val Dst) : Instruction;

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

// Indirect call through a function pointer
public record IndirectCall(Val FuncAddr, List<Val> Args, Val Dst) : Instruction;

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
public record DebugLine(int Line, string Text, string SourceFile, bool IsInline = false) : Instruction;

// Variable-index array load: dst = array_name[index]
public record ArrayLoad(string ArrayName, Val Index, Val Dst, DataType ElemType, int Count) : Instruction;

// Indexed load through a bytearray pointer parameter (ptr stored in stack slot PtrName).
public record BytearrayLoad(string PtrName, Val Index, Val Dst) : Instruction;

// Indexed store through a bytearray pointer parameter.
public record BytearrayStore(string PtrName, Val Index, Val Src) : Instruction;

// ArrayLoad for ROM-resident byte arrays (e.g. PROGMEM on AVR, XIP flash on RP2040).
public record ArrayLoadFlash(string ArrayName, Val Index, Val Dst) : Instruction;

// ROM-resident read-only byte array (placed in read-only memory via const[uint8[N]]).
// Bytes holds the literal initializer values; backends emit this in their ROM section.
public record FlashData(string Name, List<int> Bytes) : Instruction;

// Variable-index array store: array_name[index] = src
public record ArrayStore(string ArrayName, Val Index, Val Src, DataType ElemType, int Count) : Instruction;

// GC: allocate Size bytes on the managed heap; Dst receives a GC_REF (null=0 on OOM)
public record GcAlloc(Val Size, Val Dst) : Instruction;

// GC: register a live GC_REF local as a root for the duration of the containing function.
// The backend emits a shadow-stack push in the function prologue for each GcRoot.
public record GcRoot(Val Var) : Instruction;

// GC: deregister a GC_REF root (shadow-stack pop). Emitted before every Return in a
// function that contains GC_REF locals.
public record GcUnroot(Val Var) : Instruction;

// Exception handling (SJLJ — legacy, being replaced by T-flag propagation model).
// Install a setjmp-based handler. JmpBufVar is a 22-byte local array.
// At runtime calls _setjmp(jmpbuf); jumps to CatchLabel if longjmp fires.
// ExnCodeVar receives the exception code passed to longjmp.
public record TryBegin(Val JmpBufVar, string CatchLabel, Val ExnCodeVar) : Instruction;

// Exception handling (SJLJ — legacy). Raise via longjmp.
// If no handler is active calls __pymcu_unhandled_exn(code).
public record RaiseExn(Val Code) : Instruction;

// --- Error propagation (T-flag / Result model, architecture-agnostic) ---

// Signal that this function is raising an error and the error is propagating to the caller.
// Backend AVR : emits SET  (sets T flag in SREG).
// Backend Cortex-M0 : emits MOV R1, code.
// Code carries the exception type integer (1=ValueError, 2=TypeError, …) for dispatch
// at the eventual catch site; 0 means "generic / unknown error".
public record SignalError(Val Code) : Instruction;

// Signal that this function is returning successfully (happy path).
// Must appear immediately before every Return inside a CanFail function.
// Backend AVR : emits CLT (clears T flag in SREG).
// Backend Cortex-M0 : emits MOV R1, 0.
public record SignalSuccess() : Instruction;

// After a call to a CanFail function, branch to ErrorLabel if the callee signaled error.
// Backend AVR : emits BRTS ErrorLabel  (branches if T is set).
// Backend Cortex-M0 : emits CMP R1, 0 ; BNE ErrorLabel.
public record BranchOnError(string ErrorLabel) : Instruction;

// Virtual method call through a flash-resident vtable.
// DeclaredClass: the static (declared) type of the receiver — used to locate the vtable.
// DefiningClass: the class in DeclaredClass's MRO that defines the method (pre-computed
//                so the devirt pass and codegen do not need to re-walk the MRO).
// MethodName:    unqualified method name (e.g. "_read_byte").
// SlotIndex:     index into the vtable (2 * SlotIndex = byte offset from vtable base).
// Self:          receiver variable (holds the vptr as its first SRAM field).
// Args:          call arguments excluding self.
// Dst:           destination for the return value.
public record VirtualCall(
    string DeclaredClass,
    string DefiningClass,
    string MethodName,
    int SlotIndex,
    Variable Self,
    List<Val> Args,
    Val Dst) : Instruction;

// --- Function Definition ---
public class Function
{
    public string Name { get; set; } = "";
    public List<string> Params { get; set; } = new();
    public DataType ReturnType { get; set; } = DataType.VOID;
    public List<Instruction> Body { get; set; } = new();
    public bool IsInline { get; set; } = false;
    public bool IsInterrupt { get; set; } = false;
    public bool IsNaked { get; set; } = false;
    public int InterruptVector { get; set; } = 0;

    // True when the function may propagate an error to its caller via SignalError.
    // Set by CanFailAnalyzer after IR generation; the backend uses it to inject
    // SignalSuccess before every Return and to validate FFI / ISR boundaries.
    public bool CanFail { get; set; } = false;

    // True for functions declared with @extern("symbol") — FFI inbound.
    // The compiler assumes CanFail = false; no T-flag check after the call.
    public bool IsExtern { get; set; } = false;

    // True for functions decorated with @export_c — FFI outbound.
    // CanFailAnalyzer enforces CanFail = false; errors must not cross the C boundary.
    public bool IsExportC { get; set; } = false;
}

// One slot in a class's flash-resident vtable.
public class VtableEntry
{
    public string MethodName    { get; set; } = "";
    // The class in the MRO that provides the implementation for this slot.
    public string DefiningClass { get; set; } = "";
}

// Vtable layout for a single class.
public class VtableSpec
{
    public string ClassName           { get; set; } = "";
    public List<VtableEntry> Entries  { get; set; } = new();
}

public class ProgramIR
{
    public List<Variable> Globals { get; set; } = new();

    // Module-level SRAM arrays (e.g. `_task_fns: Callable[4]`).
    // Stored separately because array types cannot be represented as a single DataType.
    // The StackAllocator allocates these in the global section (before any function locals)
    // so the overlay algorithm never aliases them with function-local arrays.
    public Dictionary<string, int> GlobalArrays { get; set; } = new();

    public List<Function> Functions { get; set; } = new();

    // C symbols declared via @extern("name") in the source.
    public List<string> ExternSymbols { get; set; } = new();

    // True when the program uses GC_REF values; the backend injects the GC runtime.
    public bool NeedsGc { get; set; } = false;

    // Class hierarchy graph — populated by IRGenerator.Generate() and consumed by the
    // devirtualization pass in Optimizer.Optimize().
    // Keys use the class name WITHOUT trailing underscore (e.g. "dht_DHT11").
    public Dictionary<string, HashSet<string>> ClassChildren      { get; set; } = new();
    public Dictionary<string, HashSet<string>> ClassDirectMethods { get; set; } = new();

    // Vtable specs for classes that still require runtime virtual dispatch after the
    // devirt pass.  Empty in the vast majority of programs (all calls devirtualized).
    public List<VtableSpec> Vtables { get; set; } = new();
}