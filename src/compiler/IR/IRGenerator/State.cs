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

using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    private List<Instruction> currentInstructions = new();
    private int tempCounter = 0;
    private int labelCounter = 0;
    private Dictionary<string, SymbolInfo> globals = new();
    private Dictionary<string, DataType> mutableGlobals = new();
    private Dictionary<string, DataType> variableTypes = new();
    private Dictionary<string, string?> instanceClasses = new(); // Tracks led -> Pin
    private Dictionary<string, string> methodInstanceTypes = new(); // method -> class
    private Dictionary<string, string?> functionReturnTypes = new();
    private Dictionary<string, List<string>> functionParams = new();
    private Dictionary<string, List<DataType>> functionParamTypes = new();
    // Per-function default value expressions (null where a param has no default).
    // Lets a non-inline call site fill in omitted trailing arguments, so defaults
    // work for real subroutines and not just @inline functions.
    private Dictionary<string, List<Frontend.Expression?>> functionParamDefaults = new();
    // Functions currently being inline/force-inline expanded up the call chain.
    // If a callee is already here, expanding it again is recursion through inlined
    // calls — which would loop forever and segfault the compiler. Detected here so
    // a recursive @inline/ZCA method gets a clear error instead of a crash.
    private HashSet<string> activeInlineExpansions = new();
    // Module prefix where each function was DEFINED, preserved across re-export so
    // inlining resolves the body's internal calls (e.g. a private @inline helper) in
    // the right module rather than the facade that re-exported the function.
    private Dictionary<string, string> functionModulePrefix = new();
    private Dictionary<string, FunctionDef?> inlineFunctions = new(); // Map for inlining
    // Names currently bound to None (the real null, not the integer -1). Used to
    // resolve `x is None` / `x is not None` at compile time: a name here IS None,
    // an integer or a concrete instance is NOT. This is what keeps None from
    // colliding with a real value like 255 / 0xFFFF / -1.
    private HashSet<string> noneValuedNames = new();
    private string currentFunction = "";
    private HashSet<string> currentFunctionGlobals = new();
    private int inlineDepth = 0;
    private int ctorAnonId = 0; // Counter for synthetic ZCA constructor-as-arg targets
    private string currentInlinePrefix = "";
    private string? currentModulePrefix = "";
    // Set by an explicit numeric cast wrapping an arithmetic expression (e.g. `uint8(a + b)`):
    // the wrapped binary op is computed AT this width instead of promoting, giving fixed-width
    // wraparound (and the matching 8/16-bit ADD/SUB flags). The escape hatch from default
    // arithmetic promotion. Consumed (cleared) by the immediate binary op so nested ops promote.
    private DataType? castWidthHint = null;

    // Value range (inclusive) of arithmetic temporaries, keyed by temp name. Arithmetic
    // promotion widens by STORAGE type, which over-widens whenever the operands cannot
    // actually reach the type's limits: `hi * 256` with hi:uint8 peaks at 65280, so the
    // uint16 product needs no uint32. Knowing the product's real range then also keeps
    // `lo + hi * 256` at 16 bits. Only temps whose range is provably tighter than their
    // type appear here; anything absent falls back to the full range of its type.
    private Dictionary<string, (long Min, long Max)> tempRanges = new();

    private Dictionary<string, ModuleScope> modules = new();

    private HashSet<string> classNames = new(); // Tracks known class names for callee resolution
    private HashSet<string> valueClasses = new(); // @value-decorated classes: always use ZCA path, never heap-allocated

    // Maps "ClassName.property_name" -> qualified setter inline function key.
    // Populated by scan_functions when a @name.setter method is encountered.
    // Used by visitAssign to desugar "obj.attr = val" into an inline setter call.
    private Dictionary<string, string?> propertySetters = new();

    // Set of "ClassName.property_name" for every @property getter. Populated by
    // scan_functions; used by VisitMemberAccess to desugar a bare `obj.prop` read
    // into an inline getter call instead of reading a non-existent data field.
    private HashSet<string> propertyGetters = new();

    // Function overloading: tracks qualified function names that have multiple
    // @inline overloads distinguished by parameter types.
    // scan_functions populates this; visitCall uses it for type-based dispatch.
    private HashSet<string> overloadedFunctions = new();

    // Class inheritance: maps "ChildClassName" -> "base_prefix_" (e.g., "GPIODevice_")
    // so that super().__init__() and default-ctor inheritance can be resolved.
    private Dictionary<string, string?> classBasePrefixes = new();

    // Non-inline class instance methods: maps fully-qualified name → FunctionDef AST.
    // Populated alongside functionsToCompile so Call.cs can force-inline them when called
    // on a ZCA instance with a known concrete type (field aliasing requires inlining).
    private Dictionary<string, FunctionDef> instanceMethodDefs = new();

    // Mangled symbols of methods whose body calls a sibling method on self (self.<m>()). When
    // such a method is reached on a subclass instance (concrete type != defining class), it must
    // be force-inlined so the inner self-call dispatches to the concrete override (virtual call).
    private HashSet<string> methodsWithSelfCall = new();

    // Every instance method's AST keyed by mangled symbol (e.g. "Base_score"), regardless of
    // inline/outline/force-inline. Lets super().<method>() inline-expand the base body even when
    // the base method is outlined (and thus absent from inlineFunctions).
    private Dictionary<string, FunctionDef> methodAstByName = new();

    // Class keys whose own __init__ delegates to the base via super().__init__(). Their slot
    // construction can't use the positional fast-path (a base-set field has no constructor
    // param of this class); run the real __init__ (flattened) and materialize into the slot.
    private HashSet<string> classInitCallsSuper = new();

    // Class hierarchy graph (both populated by ScanFunctions).
    // Keys use the unqualified class name WITHOUT trailing underscore (e.g. "dht_DHT11").
    // classChildren:      parent → set of direct subclass names.
    // classDirectMethods: class  → set of method names defined *directly* in that class body
    //                     (excludes methods inherited via the toInherit copy loop).
    private Dictionary<string, HashSet<string>> classChildren      = new();
    private Dictionary<string, HashSet<string>> classDirectMethods = new();
    // Every member name that appears anywhere as an assignment target (`obj.X = ...`,
    // `obj.X[i] = ...`, `obj.X: T = ...`, `obj.X += ...`), collected program-wide before IR
    // generation. A real instance field is always assigned somewhere (in __init__ or a
    // method), so this set is a superset of every class's fields — used to detect a read of
    // an undefined instance attribute (a typo) without per-class layout completeness, which
    // is unreliable. Unioned with method/property names at the check site.
    private HashSet<string> assignedMemberNames = new();
    private Dictionary<string, string?> importedAliases = new(); // Tracks Pin/_Pin -> pymcu.hal.gpio
    private Dictionary<string, string?> aliasToOriginal = new(); // Tracks _Pin -> Pin (for "from X import Pin as _Pin")
    private Dictionary<string, int> constantVariables = new(); // Tracks variables holding constants (for folding)

    // f-string-as-value targets: qualified buffer name (== the target variable, which IS the
    // bytearray) -> (unqualified length-variable name, buffer capacity incl. NUL). len(s) reads
    // the length variable; print(s)/write_str(s) stream the buffer up to it.
    private Dictionary<string, (string LenVar, int Capacity)> runtimeStrVars = new();

    // Names that carry a real Python bool, so an interpolation prints True/False instead of
    // 1/0. Collected program-wide, by UNQUALIFIED name, before IR generation: boolNames holds
    // every name bound to a True/False literal (or declared `: bool` with such an init);
    // nonBoolNames holds every name that anywhere receives something else (a comparison, an
    // integer, a loop variable, a parameter). A name prints as a bool only when it is in the
    // first set and absent from the second, so a name that is a bool at one point and an
    // integer later keeps printing as a number everywhere.
    private HashSet<string> boolNames = new();
    private HashSet<string> nonBoolNames = new();

    // Dict/set literals bound to a name: compile-time CLOSED lookup tables (no storage, no
    // GC). d[k] folds for a constant key or lowers to a compare chain for a runtime key
    // (missing key raises KeyError); `x in s` lowers like a constant list membership.
    private Dictionary<string, Frontend.DictExpr> dictLiteralBindings = new();
    private Dictionary<string, Frontend.SetExpr> setLiteralBindings = new();
    // Tracks names declared with a `const[...]` annotation (scalar or array). These are
    // immutable by definition, so any later assignment to one is a user error. Distinct
    // from constantVariables, which also holds const-FOLDED locals (which ARE reassignable).
    private HashSet<string> declaredConstants = new();
    // Tracks loop variables that are function references (from zip() over function lists).
    // Key = loop variable name (e.g. "fn"), Value = resolved mangled function name (e.g. "blink_task").
    private Dictionary<string, string> loopFunctionAliases = new();
    // Return type of the function each FUNCREF-typed variable points to (qualified var key ->
    // return DataType). Lets an indirect call (ICALL) type its result temp to the callee's
    // return width instead of a default uint8, which would truncate a uint16/int16 return.
    private Dictionary<string, DataType> funcrefReturnTypes = new();
    // Tracks instance fields that have been written with different constant values
    // (i.e., mutable at runtime). Once a field is killed it is never re-admitted
    // to constantVariables, preventing incorrect DCE of branches like
    // "if sensor.failed:".
    private HashSet<string> killedConstants = new();
    // Names the program binds through a path that files no type: a loop variable, a
    // multi-return unpack target. Read only by the undefined-name check, which must not
    // mistake "bound elsewhere" for "never defined".
    // Per-function widths for unannotated locals assigned only integer literals; see
    // CollectLiteralOnlyLocalWidths. Keyed by the bare name, valid for the function being
    // lowered only.
    private Dictionary<string, DataType> literalOnlyLocalWidths = new();

    private HashSet<string> boundNames = new();

    private Dictionary<string, string?> variableAliases = new(); // Tracks param -> arg mappings for properties

    // variableAliases keys that are WRITE-THROUGH (nonlocal: the alias IS the variable's
    // storage) rather than value tracking -- exempt from invalidation on writes.
    private HashSet<string> writeThroughAliases = new();

    // variableAliases keys created by plain scalar `a = b` value tracking: flow-sensitive,
    // cleared at every Label (control-flow join) and on writes to either side.
    private HashSet<string> valueTrackingAliases = new();
    private string pendingConstructorTarget = ""; // Target variable for constructor inlining

    // Tuple-unpack multi-return support.
    private int pendingTupleCount = 0;
    private List<string> lastTupleResults = new();

    // Zero-Cost Abstraction: Virtual Instance Registry
    private HashSet<string> virtualInstances = new();

    // RFC 0001 Model A (@outline): methods compiled once as shared subroutines.
    // Key = mangled method symbol (e.g. "Counter_stepped"). The layout is the
    // ordered list of instance fields (from __init__) that become leading params.
    // SourceParam = the __init__ parameter whose value initializes the field (when the
    // RHS is a bare parameter), used by Model B factory lowering to map ctor args.
    private HashSet<string> outlinedMethods = new();
    private Dictionary<string, List<(string Field, string Type, string SourceParam)>> outlineFieldLayout = new();

    // Same layout keyed by class symbol (e.g. "Counter"), for factory return lowering.
    private Dictionary<string, List<(string Field, string Type, string SourceParam)>> classFieldLayout = new();

    // A field whose declared type is itself a ZCA class. Keyed "classKey|fieldName" -> the
    // field's class key. Resolved at scan time (in the defining module's import scope). Lets a
    // member access recover the nested class identity that a single-field ZCA loses when it
    // collapses to a bare scalar (machine.Pin -> hal.Pin -> pin number), so the universal
    // time_pulse_us's pin._pin.pulse_in() resolves on the single-field Pin chains (arm) too.
    private Dictionary<string, string> fieldClasses = new();

    // RFC 0001 Model B (register-packed handle): a non-@inline factory returning a ZCA
    // returns the instance's single packed field as a scalar. Instances bound from such
    // a factory are "handle instances": their field value IS the variable itself, so an
    // @outline method call passes the variable (not a per-field constant) as the field arg.
    private HashSet<string> factoryHandleInstances = new();
    // Class symbol -> the field type its register-packed handle carries (single-field only).
    private Dictionary<string, string> zcaFactoryClasses = new();

    // RFC 0001 Model B (SRAM slot): a ZCA with >= 2 fields is "boxed" -- its fields live in
    // a fixed SRAM slot and its @outline methods take a `self` pointer (bytearray), reading
    // fields via BytearrayLoad at byte offsets. This is the multi-field analogue of the
    // register handle (which only fits one small field in the return register).
    private HashSet<string> slotClasses = new();
    // Instance qualified name (e.g. "main.s") -> its SRAM slot array name ("main.s__slot").
    private Dictionary<string, string> slotInstances = new();
    // @outline method symbol -> field -> byte offset within the slot (for self.field loads).
    private Dictionary<string, Dictionary<string, int>> slotMethodFieldOffsets = new();
    // @outline method symbols compiled with the slot (self-ptr) ABI.
    private HashSet<string> slotMethods = new();

    // RFC 0001 (write-back): a single-field (Model A) void method that mutates its field
    // (e.g. `def inc(self, by): self.count += by`). The field is passed BY VALUE, so the
    // outlined body mutates only its local copy. To persist the mutation we make the body
    // RETURN the (updated) field and copy it back to the instance field at the call site.
    // Key = method symbol -> (field name, field type).
    private Dictionary<string, (string Field, DataType Type)> outlineWriteBack = new();
    // Class symbol -> the set of fields that a write-back method mutates. Such fields must
    // have a real runtime home (not a folded compile-time constant) so the write-back copy
    // has somewhere to write and later reads pick up the runtime value -- including across
    // loop iterations. Construction promotes these fields from constant to runtime storage.
    private Dictionary<string, HashSet<string>> zcaWriteBackFields = new();

    // RFC 0001 Model B (Class[N]): an array of boxed ZCA instances laid out contiguously in
    // SRAM. arr[i] is the slot at base + i*stride; arr[i].method() passes that element address
    // as the self pointer. Maps the array's qualified name to its element class and byte stride.
    private Dictionary<string, string> instanceArrayClass = new();
    private Dictionary<string, int> instanceArrayStride = new();

    private List<LoopLabels> loopStack = new();
    private List<InlineContext> inlineStack = new();

    // Module-level globals whose initializer could not be const-evaluated and that
    // carry no annotation: ScanGlobals had to register them as uint8. The first
    // top-level assignment may widen them to the RHS's real type (e.g.
    // `f0 = pwm.freq()` with a uint16 getter -- as uint8 the store wrapped
    // 1000 to 232 on real hardware).
    private HashSet<string> widenableGlobals = new();

    // Declared return type of the most recently completed @inline expansion, so an
    // assignment can recover the width of a call result that folded to a Constant.
    private DataType lastInlineReturnType = DataType.UNKNOWN;

    // Unique suffix for the synthesized index of a runtime-bounds slice iteration.
    private int sliceLoopId = 0;

    // Catch-dispatch labels of the `try` blocks whose BODY is currently being
    // lowered (innermost last). A `raise` lexically inside a try body is delivered
    // to the top label instead of propagating to the caller. Pushed/popped by
    // VisitTry around the body only — not around handler/finally blocks, where a
    // `raise` is a re-raise that must propagate.
    private List<string> tryCatchStack = new();

    // Pending `finally` blocks of the enclosing try statements (innermost last). A `return` that
    // escapes a try-with-finally must run these before returning (Python semantics).
    private List<List<Statement>> finallyStack = new();

    // Per-handler saved exception-code variable (innermost last): a bare `raise` re-raises this,
    // so the code survives handler body code that clobbers the error register (R22).
    private List<string> handlerCodeStack = new();
    private int exnCodeId = 0;

    private HashSet<string> exceptionNames = new()
        { "ValueError", "TypeError", "IndexError", "KeyError", "NotImplementedError", "ZeroDivisionError" };
    private int nextUserExceptionCode = 32;

    // Module-level `raise CompileError(...)` guards that survived compile-time if/match
    // folding in an IMPORTED module (e.g. the arch guard in hal/wifi.py once DCE picks the
    // else branch). Imported modules' top-level code never executes, so the guard is
    // recorded per module prefix; resolving a symbol from that module then reports the
    // guard's message instead of a misleading "call to undefined function".
    private readonly Dictionary<string, (string Msg, string File, int Line)> moduleGuardErrors = new();

    // Debugging
    private List<string> sourceLines = new();
    private Dictionary<string, List<string>> moduleSourceLines = new();
    private string currentSourceFile = "";
    private int lastLine = -1;
    private int currentStmtLine = 0; // Tracks the current statement's source line

    // Names assigned a constructor call at MODULE level of the entry file. Their init
    // runs inside main (module init), but references resolve them as module globals —
    // SlotInstanceKey uses this to register the boxed instance under its module key.
    private readonly HashSet<string> topLevelInstanceTargets = new();

    // The declared element type of a runtime ptr[T] value, or UINT8 when untyped/unknown.
    // Passed as the Elem of Load/StoreIndirect so the access width survives the optimizer
    // collapsing typed temporaries into raw constants.
    private DataType RuntimePtrElem(Val ptr) => ptr switch
    {
        Variable pv when runtimePtrVars.TryGetValue(pv.Name, out var e) => e,
        Temporary pt when runtimePtrVars.TryGetValue(pt.Name, out var e) => e,
        _ => DataType.UINT8
    };

    // Builds a located user-facing compile error from inside IR generation. Using this
    // instead of `throw new Exception(...)` means the message is reported as a clean
    // `file:line: error: CompileError: ...` diagnostic (with the current source line and
    // a caret) rather than a location-less "InternalCompilerError" that looks like a
    // compiler bug. For genuine compiler-invariant violations keep `throw new Exception`.
    private PyMCU.Common.CompilerError UserError(string message) =>
        new("CompileError", message, currentStmtLine > 0 ? currentStmtLine : (lastLine > 0 ? lastLine : 1), 1);

    // Intrinsic tracking
    private HashSet<string> intrinsicNames = new();

    // Depth counter for runtime-conditional branches currently being compiled.
    // > 0 means we are inside a branch whose predicate could not be folded at compile time.
    // VisitRaise uses this to distinguish a genuine compile-time CompileError (depth == 0)
    // from a CompileError guard inside a runtime if/match that const-propagation failed to
    // fold (depth > 0). In the latter case the raise is only a potential error; aborting
    // the compilation would be a false positive.
    private int _runtimeBranchDepth = 0;

    // compile_isr() registrations: bare function name -> interrupt vector.
    private Dictionary<string, int> pendingIsrRegistrations = new();
    private Dictionary<string, (string Function, int Line, string Module)> pendingIsrOrigins = new();

    // ZCA ISR synthesis: handler name -> root ZCA variable key (set by _set_irq_zca_arg).
    private Dictionary<string, string> pendingZcaIsrBindings = new();
    // ZCA ISR synthesis: handler name -> (FunctionDef, module prefix) for on-demand wrapper.
    private Dictionary<string, (FunctionDef Func, string Prefix)> zcaHandlerAstNodes = new();
    // ZCA ISR synthesis: synthesized Function objects collected during VisitCall, added to irProgram in Generate().
    private List<Function> pendingZcaSynthFunctions = new();

    // @asm_pio / @rp2.asm_pio programs: fullName -> assembled 16-bit PIO machine
    // code + state-machine config. Populated in ScanFunctions; these functions are
    // NEVER lowered as CPU code. Consumed by the rp2.StateMachine construction.
    private Dictionary<string, PyMCU.Frontend.Pio.AssembledPioProgram> pioPrograms = new();

    // @extern("symbol") registrations: PyMCU function name -> C symbol name.
    private Dictionary<string, string?> externFunctionMap = new();

    // Exception-related extern symbols to add to the program (e.g. _setjmp, longjmp).
    private HashSet<string> exnExterns = new();

    private List<FunctionEntry> functionsToCompile = new();
    private Dictionary<string, int> stringLiteralIds = new();
    private Dictionary<int, string?> stringIdToStr = new(); // reverse map: id → string value

    private int nextStringId = 256; // Start above uint8 range to avoid aliasing True(1)/False(0)

    // Tracks temporaries/variables that hold MemoryAddress values from inline returns.
    private Dictionary<string, int> constantAddressVariables = new();

    // Tracks Vals that hold a RUNTIME pointer address (from ptr(<runtime expr>), e.g.
    // ptr(BASE + x) with a non-constant offset). Maps the Val name to the pointed-at
    // element type. Reading/writing `.value` on such a Val lowers to Load/StoreIndirect
    // through the held address, rather than a direct store to a compile-time MemoryAddress.
    private Dictionary<string, DataType> runtimePtrVars = new();

    // Tracks compile-time string constant variables (for const[str] params / string for-in)
    private Dictionary<string, string?> strConstantVariables = new();

    // Tracks inline-function parameters bound to a bytes/list literal argument
    // (e.g. uart.write(b"Hi")). Lets the param be iterated via for-in and
    // unrolled at compile time, mirroring a direct `for b in b"Hi"` loop.
    private Dictionary<string, Frontend.ListExpr> listLiteralParams = new();

    // Functions already reported via the @warning informational diagnostic, so
    // the note is emitted at most once per function.
    private HashSet<string> warningNoticed = new();

    // Tracks compile-time float constant variables (legacy; new code uses FloatConstant nodes)
    private Dictionary<string, double> floatConstantVariables = new();

    // Maps class name → module prefix where the class is defined.
    private Dictionary<string, string?> classModuleMap = new();

    // Fixed-size array support
    private Dictionary<string, int> arraySizes = new(); // qualified_name → element count
    private Dictionary<string, DataType> arrayElemTypes = new(); // qualified_name → element DataType

    // Heap-allocated list[T] support: maps qualified_name → element DataType (GC_REF variables)
    private Dictionary<string, DataType> listVarElemTypes = new();

    // Function parameters declared as bytearray (passed as pointer, no length).
    // The parameter name is stored as qualified_name (funcname_paramname).
    private HashSet<string> bytearrayParams = new();

    // const[str] parameters of NON-@inline functions: received as a runtime 16-bit flash
    // byte-pointer (the caller passes a FlashStrAddr). Subscripting one (s[i]) emits a
    // FlashLoadPtr so a single shared subroutine can walk any flash string instead of the
    // loop being inlined per call site.
    private HashSet<string> flashStrPtrVars = new();

    // Arrays that are subscripted with at least one non-constant index anywhere in the current function.
    private HashSet<string> arraysWithVariableIndex = new();

    // Module-level arrays that unconditionally use SRAM (bytearray declarations at global scope).
    private HashSet<string> moduleSramArrays = new();

    // Global arrays declared with const[uint8[N]] annotation: placed in flash (PROGMEM).
    // Only uint8 element type is supported.  SRAM not allocated; access via LPM Z.
    private HashSet<string> flashArrays = new();

    // FlashData instructions collected during ScanGlobals for global const[uint8[N]] arrays.
    // Injected into the main function body in Generate() so the backend can emit .byte tables.
    private List<Instruction> pendingFlashData = new();

    // Lambda support (F9).
    private Dictionary<string, LambdaExpr> lambdaFunctionsMap = new();
    private Dictionary<string, string> lambdaVariableNames = new();
    private int lambdaCounter = 0;
    private string pendingLambdaKey = "";

    private DeviceConfig deviceConfig = null!;

    // Flash byte-pointers (const[str] by-reference params / FlashStrAddr values) carry the
    // TARGET's pointer width: 16-bit on AVR/PIC, 32-bit on ARM/RISC-V. Typing them UINT16
    // everywhere truncated the 0x1000xxxx flash addresses on ARM.
    private DataType FlashPtrType =>
        deviceConfig != null && deviceConfig.PointerWidth == 4 ? DataType.UINT32 : DataType.UINT16;
}