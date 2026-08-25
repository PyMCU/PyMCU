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

using System.Text.Json.Serialization;

namespace PyMCU.IR;

// --- Operand Types ---
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(Constant),      "const")]
[JsonDerivedType(typeof(FloatConstant), "fconst")]
[JsonDerivedType(typeof(Variable),      "var")]
[JsonDerivedType(typeof(Temporary),     "tmp")]
[JsonDerivedType(typeof(MemoryAddress), "mem")]
[JsonDerivedType(typeof(NoneVal),       "none")]
[JsonDerivedType(typeof(FunctionRef),   "fref")]
[JsonDerivedType(typeof(ArrayBase),     "abase")]
[JsonDerivedType(typeof(FlashStrAddr),  "fstr")]
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

// Address-of an interned flash string (null-terminated FlashData byte table).
// Materializes the 16-bit flash byte-address lo8/hi8(__flash_<Name>) so a
// const[str] argument can be passed by reference to a non-@inline function,
// which then reads it byte-by-byte with FlashLoadPtr. Name matches the FlashData
// label (same convention as ArrayLoadFlash's "__flash_" + name).
public record FlashStrAddr(string Name) : Val;

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
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(Return),               "ret")]
[JsonDerivedType(typeof(Unary),                "unary")]
[JsonDerivedType(typeof(Binary),               "binary")]
[JsonDerivedType(typeof(Copy),                 "copy")]
[JsonDerivedType(typeof(LoadIndirect),         "lind")]
[JsonDerivedType(typeof(StoreIndirect),        "sind")]
[JsonDerivedType(typeof(Jump),                 "jmp")]
[JsonDerivedType(typeof(JumpIfZero),           "jz")]
[JsonDerivedType(typeof(JumpIfNotZero),        "jnz")]
[JsonDerivedType(typeof(JumpIfEqual),          "jeq")]
[JsonDerivedType(typeof(JumpIfNotEqual),       "jne")]
[JsonDerivedType(typeof(JumpIfLessThan),       "jlt")]
[JsonDerivedType(typeof(JumpIfLessOrEqual),    "jle")]
[JsonDerivedType(typeof(JumpIfGreaterThan),    "jgt")]
[JsonDerivedType(typeof(JumpIfGreaterOrEqual), "jge")]
[JsonDerivedType(typeof(Label),                "lbl")]
[JsonDerivedType(typeof(Call),                 "call")]
[JsonDerivedType(typeof(BitSet),               "bset")]
[JsonDerivedType(typeof(BitClear),             "bclr")]
[JsonDerivedType(typeof(BitCheck),             "bchk")]
[JsonDerivedType(typeof(BitWrite),             "bwrt")]
[JsonDerivedType(typeof(JumpIfBitSet),         "jbs")]
[JsonDerivedType(typeof(JumpIfBitClear),       "jbc")]
[JsonDerivedType(typeof(AugAssign),            "aug")]
[JsonDerivedType(typeof(InlineAsm),            "asm")]
[JsonDerivedType(typeof(DebugLine),            "dbg")]
[JsonDerivedType(typeof(ArrayLoad),            "ald")]
[JsonDerivedType(typeof(ArrayLoadFlash),       "alf")]
[JsonDerivedType(typeof(FlashLoadPtr),         "flp")]
[JsonDerivedType(typeof(FlashData),            "fdata")]
[JsonDerivedType(typeof(ArrayStore),           "ast")]
[JsonDerivedType(typeof(BytearrayLoad),        "bald")]
[JsonDerivedType(typeof(BytearrayStore),       "bast")]
[JsonDerivedType(typeof(Bitcast),              "bitcast")]
[JsonDerivedType(typeof(IndirectCall),         "icall")]
[JsonDerivedType(typeof(GcAlloc),              "galloc")]
[JsonDerivedType(typeof(GcRoot),               "groot")]
[JsonDerivedType(typeof(GcUnroot),             "gunroot")]
[JsonDerivedType(typeof(SignalError),          "sigerr")]
[JsonDerivedType(typeof(SignalSuccess),        "sigok")]
[JsonDerivedType(typeof(BranchOnError),        "boe")]
[JsonDerivedType(typeof(VirtualCall),          "vcall")]
[JsonDerivedType(typeof(InlineExpansionMarker), "imarker")]
public abstract record Instruction;

public record Return(Val Value) : Instruction;

public record Unary(UnaryOp Op, Val Src, Val Dst) : Instruction;

public record Binary(BinaryOp Op, Val Src1, Val Src2, Val Dst) : Instruction;

public record Copy(Val Src, Val Dst) : Instruction;
public record Bitcast(Val Src, Val Dst) : Instruction;

// Indirect Memory Access (Pointer Dereference). `Elem` is the access width
// (the pointed-to element type); it defaults to UINT8 for backward compatibility
// but the frontend sets it from the runtime pointer's declared element type so a
// 16/32-bit MMIO access through a computed address is not narrowed by later passes.
public record LoadIndirect(Val SrcPtr, Val Dst, DataType Elem = DataType.UINT8) : Instruction;

public record StoreIndirect(Val Src, Val DstPtr, DataType Elem = DataType.UINT8) : Instruction;

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

// Indirect call through a function pointer (ICALL on AVR)
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

// ArrayLoad for flash-resident (PROGMEM) byte arrays: read via LPM Z.
public record ArrayLoadFlash(string ArrayName, Val Index, Val Dst) : Instruction;

// Runtime-base flash byte load: Dst = flash[Ptr + Index] via LPM. Ptr is a 16-bit
// flash byte-address (e.g. a FlashStrAddr passed into a non-@inline function, held
// in a uint16 parameter). Enables one shared subroutine to walk any flash string
// instead of inlining the loop per call site.
public record FlashLoadPtr(Val Ptr, Val Index, Val Dst) : Instruction;

// Flash-resident read-only byte array (placed in .text / PROGMEM via const[uint8[N]]).
// Bytes holds the literal initializer values; AVR codegen emits a .db table in flash.
public record FlashData(string Name, List<int> Bytes) : Instruction;

// Variable-index array store: array_name[index] = src
public record ArrayStore(string ArrayName, Val Index, Val Src, DataType ElemType, int Count) : Instruction;

// Indexed load through a bytearray pointer parameter (ptr stored in stack slot PtrName).
public record BytearrayLoad(string PtrName, Val Index, Val Dst) : Instruction;

// Indexed store through a bytearray pointer parameter.
public record BytearrayStore(string PtrName, Val Index, Val Src) : Instruction;

// GC: allocate Size bytes on the managed heap; Dst receives a GC_REF (null=0x0000 on OOM)
public record GcAlloc(Val Size, Val Dst) : Instruction;

// GC: register a live GC_REF local as a root (shadow-stack push in prologue)
public record GcRoot(Val Var) : Instruction;

// GC: deregister a GC_REF root (shadow-stack pop before Return)
public record GcUnroot(Val Var) : Instruction;

// --- Error propagation (T-flag / Result model, architecture-agnostic) ---
// (The legacy SJLJ TryBegin/RaiseExn instructions were removed: try/except now
// lowers to SignalError/BranchOnError/SignalSuccess, the T-flag model below.)

// Signal an error.
// When CatchLabel is null the error propagates to the caller:
//   Backend AVR : load code into the error register, set the T flag, RET.
// When CatchLabel is non-null the raise is caught inside the SAME function (a
// `raise` lexically inside that function's `try` body): the error is delivered
// directly to the local catch dispatcher without touching the T flag.
//   Backend AVR : load code into the error register, JMP CatchLabel (no SET, no RET).
public record SignalError(Val Code, string? CatchLabel = null) : Instruction;

// Signal that this function is returning successfully (happy path).
// Must appear immediately before every Return inside a CanFail function.
// Backend AVR : emits CLT. Backend Cortex-M0 : emits MOV R1, 0.
public record SignalSuccess() : Instruction;

// After a call to a CanFail function, branch to ErrorLabel if the callee signaled error.
// Backend AVR : emits BRTS ErrorLabel. Backend Cortex-M0 : CMP R1,0 ; BNE ErrorLabel.
public record BranchOnError(string ErrorLabel) : Instruction;

// Marks the boundary of a force-inlined non-@inline method expansion.
// IsEnd=false → begin; IsEnd=true → end.
// The AVR codegen uses these markers to outline repeated copies into a single subroutine.
public record InlineExpansionMarker(string FuncName, bool IsEnd) : Instruction;

// Virtual method call through a flash-resident vtable.
// DeclaredClass: static receiver type.  DefiningClass: MRO-resolved defining class.
// SlotIndex: vtable slot (byte offset = SlotIndex * 2).  Self: receiver variable.
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
    public string? OriginalName { get; set; }

    // Set by CanFailAnalyzer after IR generation.
    // True when the function may propagate an error to its caller via SignalError.
    public bool CanFail { get; set; } = false;

    // True for @extern("symbol") functions — FFI inbound, always CanFail = false.
    public bool IsExtern { get; set; } = false;

    // True for @export_c functions — FFI outbound, must be CanFail = false.
    public bool IsExportC { get; set; } = false;
}

// One slot in a class's flash-resident vtable.
public class VtableEntry
{
    public string MethodName    { get; set; } = "";
    public string DefiningClass { get; set; } = "";
}

// Vtable layout for a single class.
public class VtableSpec
{
    public string ClassName           { get; set; } = "";
    public List<VtableEntry> Entries  { get; set; } = new();
}

// Signature of an @extern("symbol") function.
//
// The backend needs the DECLARED parameter widths, not the widths of the values
// at the call site: passing the literal 5 to a `uint16_t` parameter must still
// load both bytes of the register pair. Absent in .mir files from older
// compilers — deserializes to empty, which restores the value-width fallback.
public class ExternSignature
{
    public string Symbol { get; set; } = "";
    public List<DataType> ParamTypes { get; set; } = new();
    public DataType ReturnType { get; set; } = DataType.VOID;
}

public class ProgramIR
{
    // Memory geometry of the target part, from device_info() in the chip file.
    //
    // Null, never a zeroed instance: a .mir written by a compiler older than this
    // field deserializes to null, and RequireDevice() then refuses the build. An
    // all-zero default would instead hand the backend a chip with no flash and no
    // SRAM and let it carry on, which is the failure this whole field exists to
    // end. Reach it through RequireDevice(), not directly.
    public DeviceGeometry? Device { get; set; }

    public List<Variable> Globals { get; set; } = new();

    // Names of module-level globals referenced both inside an ISR (or a function
    // reachable from one) and in non-ISR code. These carry volatile semantics:
    // the optimizer never caches their value across reads/writes, and backends
    // may promote single-byte entries to fast always-volatile storage (e.g. the
    // AVR GPIORn I/O registers). Absent in .mir files from older compilers —
    // deserializes to empty, which simply disables the promotion.
    public List<string> IsrSharedGlobals { get; set; } = new();

    // Module-level SRAM arrays (e.g. `_task_fns: Callable[4]`).
    // Stored separately because array types cannot be represented as a single DataType.
    // The StackAllocator allocates these in the global section (before any function locals)
    // so the overlay algorithm never aliases them with function-local arrays.
    public Dictionary<string, int> GlobalArrays { get; set; } = new();

    public List<Function> Functions { get; set; } = new();

    // C symbols declared via @extern("name") in the source.
    public List<string> ExternSymbols { get; set; } = new();

    // Declared signature of each entry in ExternSymbols, same order.
    public List<ExternSignature> ExternSignatures { get; set; } = new();

    // True when the program uses GC_REF values; the backend injects the GC runtime.
    public bool NeedsGc { get; set; } = false;

    // Class hierarchy for the devirtualization pass.
    public Dictionary<string, HashSet<string>> ClassChildren      { get; set; } = new();
    public Dictionary<string, HashSet<string>> ClassDirectMethods { get; set; } = new();

    // Vtable specs surviving after devirtualization (empty for most programs).
    public List<VtableSpec> Vtables { get; set; } = new();

    /// <summary>
    /// The target's memory geometry, or a build error when this .mir predates the
    /// geometry contract. Backends call this instead of touching <see cref="Device"/>,
    /// so a compiler/backend version mismatch stops the build with one sentence
    /// rather than silently compiling for a part with zero flash.
    /// </summary>
    public DeviceGeometry RequireDevice()
        => Device ?? throw new InvalidOperationException(
            "this .mir carries no device geometry: it was written by a pymcuc older than " +
            "the geometry contract, which is the only place a backend can learn the chip's " +
            "ram_size, flash_size and eeprom_size. Rebuild with a matching compiler.");
}