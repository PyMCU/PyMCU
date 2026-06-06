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
    private Dictionary<string, FunctionDef?> inlineFunctions = new(); // Map for inlining
    private string currentFunction = "";
    private HashSet<string> currentFunctionGlobals = new();
    private int inlineDepth = 0;
    private int ctorAnonId = 0; // Counter for synthetic ZCA constructor-as-arg targets
    private string currentInlinePrefix = "";
    private string? currentModulePrefix = "";

    private Dictionary<string, ModuleScope> modules = new();

    private HashSet<string> classNames = new(); // Tracks known class names for callee resolution
    private HashSet<string> valueClasses = new(); // @value-decorated classes: always use ZCA path, never heap-allocated

    // Maps "ClassName.property_name" -> qualified setter inline function key.
    // Populated by scan_functions when a @name.setter method is encountered.
    // Used by visitAssign to desugar "obj.attr = val" into an inline setter call.
    private Dictionary<string, string?> propertySetters = new();

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

    // Class hierarchy graph (both populated by ScanFunctions).
    // Keys use the unqualified class name WITHOUT trailing underscore (e.g. "dht_DHT11").
    // classChildren:      parent → set of direct subclass names.
    // classDirectMethods: class  → set of method names defined *directly* in that class body
    //                     (excludes methods inherited via the toInherit copy loop).
    private Dictionary<string, HashSet<string>> classChildren      = new();
    private Dictionary<string, HashSet<string>> classDirectMethods = new();
    private Dictionary<string, string?> importedAliases = new(); // Tracks Pin/_Pin -> pymcu.hal.gpio
    private Dictionary<string, string?> aliasToOriginal = new(); // Tracks _Pin -> Pin (for "from X import Pin as _Pin")
    private Dictionary<string, int> constantVariables = new(); // Tracks variables holding constants (for folding)
    // Tracks loop variables that are function references (from zip() over function lists).
    // Key = loop variable name (e.g. "fn"), Value = resolved mangled function name (e.g. "blink_task").
    private Dictionary<string, string> loopFunctionAliases = new();
    // Tracks instance fields that have been written with different constant values
    // (i.e., mutable at runtime). Once a field is killed it is never re-admitted
    // to constantVariables, preventing incorrect DCE of branches like
    // "if sensor.failed:".
    private HashSet<string> killedConstants = new();
    private Dictionary<string, string?> variableAliases = new(); // Tracks param -> arg mappings for properties
    private string pendingConstructorTarget = ""; // Target variable for constructor inlining

    // Tuple-unpack multi-return support.
    private int pendingTupleCount = 0;
    private List<string> lastTupleResults = new();

    // Zero-Cost Abstraction: Virtual Instance Registry
    private HashSet<string> virtualInstances = new();

    private List<LoopLabels> loopStack = new();
    private List<InlineContext> inlineStack = new();

    // Debugging
    private List<string> sourceLines = new();
    private Dictionary<string, List<string>> moduleSourceLines = new();
    private string currentSourceFile = "";
    private int lastLine = -1;
    private int currentStmtLine = 0; // Tracks the current statement's source line

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

    // ZCA ISR synthesis: handler name -> root ZCA variable key (set by _set_irq_zca_arg).
    private Dictionary<string, string> pendingZcaIsrBindings = new();
    // ZCA ISR synthesis: handler name -> (FunctionDef, module prefix) for on-demand wrapper.
    private Dictionary<string, (FunctionDef Func, string Prefix)> zcaHandlerAstNodes = new();
    // ZCA ISR synthesis: synthesized Function objects collected during VisitCall, added to irProgram in Generate().
    private List<Function> pendingZcaSynthFunctions = new();

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

    // Tracks compile-time string constant variables (for const[str] params / string for-in)
    private Dictionary<string, string?> strConstantVariables = new();

    // Tracks inline-function parameters bound to a bytes/list literal argument
    // (e.g. uart.write(b"Hi")). Lets the param be iterated via for-in and
    // unrolled at compile time, mirroring a direct `for b in b"Hi"` loop.
    private Dictionary<string, Frontend.ListExpr> listLiteralParams = new();

    // Functions already reported via the @softfloat informational diagnostic, so
    // the note is emitted at most once per function.
    private HashSet<string> softFloatNoticed = new();

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
}