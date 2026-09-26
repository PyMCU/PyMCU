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
    // Pooled scratch temps minted inside inline expansions: tmp_N spelling (so the
    // MIR stays name-identical to a build without pooling) mapped to the canonical
    // "d{depth}_t{k}" slot key the backend allocator folds on. Serialized on
    // ProgramIR.CanonicalTemps.
    private readonly Dictionary<string, string> canonicalTemps = new();
    private int labelCounter = 0;
    private Dictionary<string, SymbolInfo> globals = new();
    private Dictionary<string, DataType> mutableGlobals = new();
    private Dictionary<string, DataType> variableTypes = new();
    private Dictionary<string, string?> instanceClasses = new(); // Tracks led -> Pin
    private Dictionary<string, string> methodInstanceTypes = new(); // method -> class
    private Dictionary<string, string?> functionReturnTypes = new();
    private Dictionary<string, List<string>> functionParams = new();
    private Dictionary<string, List<DataType>> functionParamTypes = new();
    // The parameter annotations AS WRITTEN, aligned with functionParams, so a call site can
    // ask what a parameter DECLARES and not only what it lowered to. `uint8` and
    // `const[uint8]` both lower to the same DataType and behave differently at a call: a
    // const parameter carries its literal through, a bare one is narrowed to its width.
    // Only the diagnostic in CheckConstantArgFitsParam reads this; nothing lowers from it.
    private Dictionary<string, List<string>> functionParamDeclared = new();
    // Per-function default value expressions (null where a param has no default).
    // Lets a non-inline call site fill in omitted trailing arguments, so defaults
    // work for real subroutines and not just @inline functions.
    private Dictionary<string, List<Frontend.Expression?>> functionParamDefaults = new();
    // RFC 0009 phase 2: the tag decision for union-annotated parameters, keyed by
    // resolved function name and aligned with functionParams. A non-null entry means
    // that parameter carries a runtime member tag immediately after its payload in
    // the argument run; a missing callee or null entry means the parameter is
    // provably single-state (or not a union) and the call is byte-identical to a
    // plain signature.
    private Dictionary<string, List<List<string>?>> functionParamTags = new();
    // The proven member index for a union-annotated parameter that did NOT get a tag:
    // every call site passes the same member (NoneIndex -> the param is always None,
    // so the body folds `is None` to true). Keyed by resolved function name, then
    // parameter name.
    private Dictionary<string, Dictionary<string, int>> functionParamProven = new();
    // Leading self-derived parameters of an outlined method signature (Model B slot
    // pointer = 1; Model A = one self_<field> per field). Used to map a bound call's
    // argument positions onto parameter indexes.
    private Dictionary<string, int> functionParamSelfCount = new();
    // Functions currently being inline/force-inline expanded up the call chain.
    // If a callee is already here, expanding it again is recursion through inlined
    // calls — which would loop forever and segfault the compiler. Detected here so
    // a recursive @inline/ZCA method gets a clear error instead of a crash.
    private HashSet<string> activeInlineExpansions = new();
    // Module prefix where each function was DEFINED, preserved across re-export so
    // inlining resolves the body's internal calls (e.g. a private @inline helper) in
    // the right module rather than the facade that re-exported the function.
    private Dictionary<string, string> functionModulePrefix = new();
    /// The file each function was scanned from, so a diagnostic raised while an @inline body
    /// is being expanded can name the callee's file instead of the caller's. Keyed by AST node
    /// identity, because a function reaches `inlineFunctions` under any of several mangled
    /// keys and none of them carries a path. An entry-file function maps to "", which is what
    /// `LocatedFile` already spells as "the entry file".
    /// Where the value bound to an inline parameter was WRITTEN, keyed by the prefixed
    /// parameter name. A `raise CompileError` that refuses an argument is about this position,
    /// not about the statement inside the library that happened to notice: `LCD(rs="PA0")` is
    /// a pin the caller has to change, and the caret belongs on `"PA0"`.
    ///
    /// Chained on binding, so a value handed down through several expansions keeps pointing at
    /// where the user wrote it: `PWM("PC0", d)` reaches `pwm_init(pin, ...)` two levels deep
    /// and the origin still names the literal. The chain only follows parameters; a value
    /// stored in a field and read back later loses it, which is the wider half of #193.
    private readonly Dictionary<string, Expression> argumentOrigin = new();

    /// The argument a `match` or `if` currently being lowered is testing, innermost last, for
    /// the subjects that ARE a parameter with a known origin. A raise in one of those branches
    /// is refusing that argument.
    private readonly List<Expression> blamedArgument = new();

    private readonly Dictionary<FunctionDef, string> functionSourcePath = new();

    private Dictionary<string, FunctionDef?> inlineFunctions = new(); // Map for inlining

    /// Emitted names of the functions whose every `return` hands back a `chr(...)`, so the
    /// byte a call to one produces is a CHARACTER. Filled by the scan, read where a value
    /// has to be written as text (#436).
    private HashSet<string> charReturningFunctions = new();
    /// Emitted names of the subroutines that hand back None on every path: only bare
    /// `return`, `return None`, a local bound to nothing but None, or the end of the body.
    /// A call to one has no value in the return register to read, so its result is the
    /// compile-time None, and `f() is None` answers what CPython answers. Filled by the scan
    /// beside charReturningFunctions, because a caller is often lowered before the callee.
    private HashSet<string> noneReturningFunctions = new();
    /// The NoneVal the last call to one of them produced (see VoidCallResult).
    private NoneVal? lastNoneCallResult;
    /// Outlined methods whose every return is `self.<field>` or None: the fields returned.
    private Dictionary<string, List<string>> outlinedSelfFieldReturns = new();
    // Names currently bound to None (the real null, not the integer -1). Used to
    // resolve `x is None` / `x is not None` at compile time: a name here IS None,
    // an integer or a concrete instance is NOT. This is what keeps None from
    // colliding with a real value like 255 / 0xFFFF / -1.
    private HashSet<string> noneValuedNames = new();

    // Names that stand for nothing at run time on this target: the symbols an OPTIONAL import
    // would have bound before its `try` folded to the handler, plus the `typing` spellings a
    // library writes whether or not it imports them (#367). An annotation built from one of
    // these is accepted where nothing reads the value and refused at the first read.
    private readonly HashSet<string> typingOnlyNames = new();

    // The values currently carrying such an annotation, by their qualified name, with the
    // annotation as the reader wrote it so the refusal can quote it.
    private readonly Dictionary<string, string> typingOnlyValues = new();

    // Type aliases recorded where an optional-import try folded away the assignment that
    // bound them (`ColorUnion = Union[int, Tuple[int, int, int]]` next to a failed
    // `from typing import Union`). The value is the rendered annotation text.
    private readonly Dictionary<string, string> typeAliases = new();
    private string currentFunction = "";
    private HashSet<string> currentFunctionGlobals = new();
    // docs/rfcs/0004-arena-allocator.md: nonzero while lowering the body of a WhileStmt or
    // ForStmt, compile-time-unrolled or not (ControlFlow.cs VisitWhile / Iteration.cs
    // VisitFor increment/decrement it around the body). The once rule an arena allocation
    // must satisfy checks this alongside currentFunction; nothing else reads it.
    private int loopDepth = 0;
    // Qualified names (the same qualification arraySizes/bytearrayParams use) of arena-
    // backed bytearrays: a plain uint16 global holding the byte OFFSET arena.alloc()
    // returned, not a pointer -- indexing rewrites to pymcu.arena.read8/write8 at that
    // offset (TryResolveArenaBuffer, TryLowerArenaBytearray in Assign.cs). The paired value
    // is the qualified name of the uint16 global len() reads.
    private readonly HashSet<string> arenaBufferNames = new();
    private readonly Dictionary<string, string> arenaBufferLenVar = new();
    // Counter for the hidden local `self.buf = bytearray(n)` rewrites into (Assign.cs,
    // EmitMemberAssign's #392 bytearray-field block) -- unique per site so two such
    // fields in the same expansion depth do not collide.
    private int arenaFieldTempId = 0;
    private int inlineDepth = 0;

    // The enclosing function's part of an inline frame's prefix. Every backend gives a
    // variable ONE storage per name, program-wide (a register home, an SRAM slot), while
    // an expansion's locals are frame-local: `inline1.__init__.base` in main and the same
    // spelling inside a function main calls were one slot, so a value main held across
    // the call came back as the callee's (`a = A(7)` whose __init__ calls a function that
    // builds a B read B's argument). main keeps the bare spelling; every other function
    // puts its own name in, so two frames never share a name.
    private string InlineFrameScope(string sep) =>
        currentFunction is "" or "main" ? "" : currentFunction + sep;
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
    // @classmethod expansion: qualified `cls` parameter -> the receiver class name.
    // Mode.add_values aliases cls to Mode so setattr(cls, name, value) and cls.string
    // populate that class's namespace. There is no runtime class object.
    private Dictionary<string, string> classmethodClsAlias = new();
    private HashSet<string> valueClasses = new(); // @value-decorated classes: always use ZCA path, never heap-allocated

    // RFC 0012: classes whose body declares at least one register (`TCCR1A: ptr[uint8] =
    // ptr(0x80)`). A grouped peripheral is a namespace over the silicon, not a type: it has
    // no fields, no constructor and no instance, so calling it means nothing. Recorded here
    // under both the bare and the module-qualified name, because a call site resolves the
    // callee either way depending on how the group was imported.
    private HashSet<string> registerGroupClasses = new();

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

    // A class that defines __del__, by the key instanceClasses uses, with the name the source
    // spells and the method's line. Reported only for classes the program constructs, which is
    // what constructedClasses records (#491).
    private readonly Dictionary<string, (string Name, int Line)> destructorSites = new();
    private readonly HashSet<string> constructedClasses = new();
    // Classes that list `Protocol` as a base. A Union member that names one of these is a
    // structural type: an argument matches if it has the protocol's members, not if it is
    // the protocol class itself (#465, adafruit_debouncer's ROValueIO).
    private HashSet<string> protocolClasses = new();
    // Every member name that appears anywhere as an assignment target (`obj.X = ...`,
    // `obj.X[i] = ...`, `obj.X: T = ...`, `obj.X += ...`), collected program-wide before IR
    // generation. A real instance field is always assigned somewhere (in __init__ or a
    // method), so this set is a superset of every class's fields — used to detect a read of
    // an undefined instance attribute (a typo) without per-class layout completeness, which
    // is unreliable. Unioned with method/property names at the check site.
    private HashSet<string> assignedMemberNames = new();

    // The same information keyed by the class that did the assigning, so a read can be checked
    // against ITS OWN receiver instead of the program-wide union above. #276: `d.mode` compiled
    // whenever ANY class anywhere declared a field called `mode`, and lowered to an unwritten
    // `<base>_mode` slot -- an indeterminate read, no diagnostic. Which is why the same source
    // line could be an error or silent wrong code depending on what else was linked in, and why
    // cutting a program down to reproduce it made it disappear.
    //
    // Deliberately assignment-based, exactly like the set above, NOT layout-based: classFieldLayout
    // omits array fields (a class with `self.buf: uint8[4]` reports "Declared fields: n, m") and
    // never learns fields assigned inside a `match`, so gating reads on it would refuse valid
    // code. This map inherits neither gap because it records what was written, not what was laid
    // out. Keys match classFieldLayout's convention: currentModulePrefix + class name.
    private Dictionary<string, HashSet<string>> assignedMemberNamesByClass = new();

    // Fields `__init__` fills with a buffer (`self._gpio = bytearray(n)`): real fields, but
    // element storage rather than a scalar slot, so classFieldLayout never lists them. The
    // undeclared-field write check would otherwise refuse the property setter that rebinds
    // one (adafruit_74hc595's `gpio.setter` storing `val` into `_gpio`). Keys match
    // classFieldLayout's convention: module prefix + class name.
    private Dictionary<string, HashSet<string>> classBufferFields = new();

    // Fields `__init__` (or a later method) fills with a class instance
    // (`self._device = i2c_device.I2CDevice(i2c, address)`): real fields, but the storage
    // is the nested object's own flattened fields (`<obj>_<field>_*`), never a scalar byte
    // the slot layout could hold. Kept in `layout` they boxed as a uint8 copy of the dead
    // anchor name, and a boxed `with self._device` read the manager's own flattened
    // `device_address`/`i2c` -- read-never-written slots that put 0x00 on the bus
    // (adafruit_tcs34725). Keys match classFieldLayout's convention: module prefix +
    // class name. A field annotated `self.f: SomeClass` does NOT land here: the
    // annotation already files the class as the layout entry's type, which is the
    // record the nested-instance machinery reads (held-instance-field).
    private Dictionary<string, HashSet<string>> classInstanceFields = new();

    // Class-body attributes that the ALL-CAPS convention does NOT turn into compile-time
    // constants (Scan.cs) get run-time storage instead -- and nothing ever ran their
    // initializer, so `class Dev: limit = 7` gave every read of `Dev.limit` a fabricated
    // zero while `LIMIT = 7` in the same body read 7 (#270). Keyed by the declaring module's
    // AST: the synthesized assignment is injected into that module's own init, so `Cls_attr`
    // resolves under that module's prefix exactly as every other name in it does.
    private Dictionary<ProgramNode, List<Statement>> classAttrInits = new();

    // `Cls.ATTR` used as an assignment target ANYWHERE in the program, as "Cls.ATTR".
    // An ALL-CAPS class attribute folds at its reads and has no storage, so the write had
    // nowhere to land and was dropped without a word: `Dev.LIMIT = 9` then reading it gave 7
    // (#272). Module level already knows this rule -- its own isAllUpper is gated on
    // `reassigned` -- and a name this program writes is not a constant whatever it is called.
    //
    // Collected program-wide BEFORE the first ScanGlobals, because the entry file is scanned
    // last: `from cfg import Dev` in main.py writing `Dev.LIMIT` has not been read yet when
    // cfg's own class body is scanned.
    //
    // Keyed by the written spelling and NOT by module, because the write and the class
    // routinely live in different files -- which is also why two same-named classes in two
    // modules share an entry. That over-reach only ever costs a fold.
    private HashSet<string> writtenClassAttributes = new();

    // Classes declared `class C(Enum)` / `class C(IntEnum)`. An enum's members fold to their
    // values and the class itself is deliberately never registered as a class -- which left
    // NOTHING keyed by the name, so `Color.RED = 9` walked past the class-variable write, was
    // lowered as an ordinary member store, and reported "name 'Color' is not defined" about a
    // name declared eight lines above (#273). Refusing is right; that sentence is not.
    //
    // Filed under the bare name and under the declaring module's prefix, because a member
    // write reaches an enum by whichever spelling imported it.
    private HashSet<string> enumClassNames = new();
    private Dictionary<string, string?> importedAliases = new(); // Tracks Pin/_Pin -> pymcu.hal.gpio

    // The same table, kept per module, because an import binds a name in ONE module and the
    // flat table above is shared by all of them: two modules that alias different things to
    // the same name got whichever was registered first, so a class could end up constructing
    // itself (#320) and a keyword argument could stop folding because the callee resolved to
    // another module's class (#324). Keyed by the module's mangled prefix ("bus_"), with the
    // entry file under "". Read through ModuleScopedAlias/ModuleScopedOriginal, which prefer
    // the module being lowered and fall back to the flat table.
    private Dictionary<string, Dictionary<string, string?>> perModuleImportedAliases = new();
    private Dictionary<string, Dictionary<string, string?>> perModuleAliasToOriginal = new();

    // Star imports in scope, module name -> the names the star actually brought in. A star
    // binds what its module defines at top level, so a name it only re-exports is missing;
    // without this the reader was told the name was "never imported" with the import that
    // was supposed to bring it in sitting in front of them.
    private readonly List<(string Module, List<string> Names)> starImports = new();
    private Dictionary<string, string?> aliasToOriginal = new(); // Tracks _Pin -> Pin (for "from X import Pin as _Pin")
    private Dictionary<string, int> constantVariables = new(); // Tracks variables holding constants (for folding)

    // What a FUNCTION-LOCAL name holds, at this point of the lowering. constantVariables
    // deliberately tracks module level only -- folding every read of a local is a different
    // and much wider change -- so this map exists for one job: letting a CALL hand the callee
    // the constant it dispatches on. Without it, a callee that selects on the value (the
    // calibrated delay loops, pwm_prescaler_for_freq, claim(), any `match` on a const
    // parameter) took its run-time path as soon as the caller put the value in a local first,
    // and a `const` parameter refused it outright (PyMCU#327).
    //
    // Written where a scalar assignment stores a Constant, dropped on any other write to the
    // name and on every name a loop body can assign.
    private Dictionary<string, int> localConstantValues = new();

    // Whether EvaluateConstantExpr may answer from localConstantValues. Off by default: that
    // evaluator serves array sizes, addresses and `assert`, and a local folded into those is a
    // separate decision. The range unroller turns it on around its own question.
    private bool foldLocalConstants;

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
    // The flat sets hold module-level bindings, which genuinely share one namespace. Bindings
    // inside a function live in boolScopes/nonBoolScopes keyed by the function's qualified
    // name: a parameter or local in a library routine can never be the same binding as a
    // same-spelled name in the program, so it must not veto it.
    private HashSet<string> boolNames = new();
    private HashSet<string> nonBoolNames = new();
    private readonly Dictionary<string, HashSet<string>> boolScopes = new();
    private readonly Dictionary<string, HashSet<string>> nonBoolScopes = new();

    // Dict/set literals bound to a name: compile-time CLOSED lookup tables (no storage, no
    // GC). d[k] folds for a constant key or lowers to a compare chain for a runtime key
    // (missing key raises KeyError); `x in s` lowers like a constant list membership.
    private Dictionary<string, Frontend.DictExpr> dictLiteralBindings = new();
    private Dictionary<string, Frontend.SetExpr> setLiteralBindings = new();
    // Tracks names declared with a `const[...]` annotation (scalar or array). These are
    // immutable by definition, so any later assignment to one is a user error. Distinct
    // from constantVariables, which also holds const-FOLDED locals (which ARE reassignable).
    //
    // Filed under the key of the scope that declares the name (DeclaredConstKey), never the
    // bare name: a local `ms: const[uint32]` in one function refused `ms = ms - 1` in every
    // other function of the program, the stdlib included, and a module's constant refused a
    // name of the same spelling in every other module.
    private HashSet<string> declaredConstants = new();

    /// The key a local `const[...]` declaration of <paramref name="name"/> is filed under:
    /// the inline expansion, else the function, else the module that declares it.
    private string DeclaredConstKey(string name) =>
        !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name
        : !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name
        : currentModulePrefix + name;

    /// Whether <paramref name="name"/>, written here, names a `const[...]` declaration:
    /// one of this scope, or one of the module the code belongs to.
    private bool IsDeclaredConst(string name) =>
        declaredConstants.Contains(DeclaredConstKey(name))
        || declaredConstants.Contains(currentModulePrefix + name);

    /// Class methods with no `self` parameter, compiled as ordinary functions under the class
    /// prefix (#201). Kept so the duplicate-definition check can see them: they land in none of
    /// the registries an instance method lands in.
    private readonly HashSet<string> classPlainFunctions = new();
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

    // Flattened fields of a MODULE-LEVEL instance that some function assigns to. They need
    // real storage: a function's write is otherwise a dead store to a name nothing else in
    // that function reads, and the reader in another function folds the constructor's value.
    private HashSet<string> moduleInstanceMutableFields = new();

    // Module-global names (mutableGlobals spelling: currentModulePrefix + name) whose value
    // can differ between program points: written a second time at module level, or declared
    // `global` inside a function/method. RecordLocalConstant refuses them outright -- the
    // init-only global is the one whose last store IS the initializer every reader sees;
    // these are the ones whose store is a fact about the flow that wrote it.
    private HashSet<string> reassignedGlobals = new();
    private HashSet<string> functionWrittenGlobals = new();

    // Constructor calls whose RESULT is held in a field that some method writes through, and
    // which have no name of their own: the inner call of `obj = Outer(Inner(0))`. The scan pass
    // can see that `Outer.go()` writes `inner_v`, but the instance holding that `v` is not named
    // until lowering invents `main.__c1`, so the fields to give storage to are recorded against
    // the AST node and claimed at the moment that name is minted. Keyed by reference: the node
    // scanned and the node lowered are the same object.
    private Dictionary<CallExpr, List<(string Leaf, string Type)>> anonCtorMutableLeaves =
        new(ReferenceEqualityComparer.Instance);
    // Names the program binds through a path that files no type: a loop variable, a
    // multi-return unpack target. Read only by the undefined-name check, which must not
    // mistake "bound elsewhere" for "never defined".
    // Per-function widths for unannotated locals assigned only integer literals; see
    // CollectLiteralOnlyLocalWidths. Keyed by the bare name, valid for the function being
    // lowered only.
    // Counter for the buffer a `uint8(input(...))` desugars into.
    private int inputDesugarId = 0;

    private Dictionary<string, DataType> literalOnlyLocalWidths = new();

    private HashSet<string> boundNames = new();
    // Loop variables whose type was INFERRED by a range loop rather than declared by the
    // program. A later `for i in range(...)` over the same name sizes its counter from its own
    // bounds instead of treating the earlier inference as an annotation it has to fit.
    private HashSet<string> rangeInferredCounterKeys = new();

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

    // `t = f()` where f returns several values: the name is bound to materialised
    // slots `t__0..N-1` (the values are COPIED -- the iret_ slots are shared
    // scratch between call sites at the same depth, so aliasing them would let a
    // later tuple call rewrite what t reads). The map lists those slot names so
    // print(t) / f"{t}" can write CPython's `(a, b, c)` text; arraySizes[key]
    // gives t[k], len(t) and `for x in t` the fixed-array shapes they know.
    private Dictionary<string, List<string>> namedTupleElements = new();

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

    // `__match_args__ = ("x", "y")` per class key, which is what gives a POSITIONAL class
    // pattern its field order. CPython requires it for positional sub-patterns and so does
    // this compiler, rather than quietly substituting the field layout and accepting a
    // program CPython rejects.
    private readonly Dictionary<string, List<string>> classMatchArgs = new();

    // A field whose declared type is itself a ZCA class. Keyed "classKey|fieldName" -> the
    // field's class key. Resolved at scan time (in the defining module's import scope). Lets a
    // member access recover the nested class identity that a single-field ZCA loses when it
    // collapses to a bare scalar (machine.Pin -> hal.Pin -> pin number), so the universal
    // time_pulse_us's pin._pin.pulse_in() resolves on the single-field Pin chains (arm) too.
    private Dictionary<string, string> fieldClasses = new();

    // `self.f = SomeClass` -- a field bound to a class OBJECT, not an instance. The field's
    // layout byte carries a tag: the index into this ordered, distinct candidate list. A read
    // like `self.f.ATTR` resolves the attribute on each candidate and selects on the tag.
    // adafruit_seesaw's `self.pin_mapping = SAMD09_Pinmap` (one of five pinmaps chosen by
    // chip id) is the demandant: the pinmap class's `analog_pins`/`pwm_pins` tuples are
    // compile-time data, so membership and .index() answer through the tag.
    private readonly Dictionary<string, List<string>> classObjectFields = new();

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
    // Classes the generator lowering synthesized. Used only to answer a call of the
    // generator protocol by name instead of as an undefined mangled symbol.
    private HashSet<string> generatorClasses = new();

    /// True while the RECEIVER of a method call is being lowered.
    ///
    /// Read by one thing: the refusal of a generator constructed in a value position (#243).
    /// `counter().send(1)` constructs one as a receiver, and for that shape the generator
    /// PROTOCOL message is the better answer -- it names send() and says why there is none --
    /// so the construction is allowed to finish and the method call is refused instead.
    private bool loweringMemberReceiver;

    /// True while an expression STATEMENT is being lowered, i.e. its result is discarded.
    ///
    /// Read by one thing: which of the two refusals a generator construction gets (#243). A
    /// generator in a value position cannot work; a generator whose result is thrown away is
    /// something else entirely, and the message says so.
    private bool loweringDiscardedExprStmt;

    /// <summary>
    /// True while the call about to be lowered is a whole statement, so nobody reads its
    /// result. Set by VisitStatement, read and cleared by the call lowering before it visits
    /// the arguments -- a call in an argument IS read, by this call.
    ///
    /// What it gates: a callee that reaches the end of its body without returning leaves the
    /// result temporary unwritten, and reading it is a miscompile (#302). Discarding it is
    /// not, and the stdlib does discard it (`Pin.mode(m)` returns nothing on the path that
    /// takes an argument, and every use of that path is a statement).
    /// </summary>
    private bool callResultIsDiscarded;
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

    // RFC 0009 phase 3: the scalar member names an UNANNOTATED union field's writes
    // contribute, keyed "class|field" in first-seen order. EnsureUnionField seeds an
    // evidence field's list from this table instead of bare [None], so every function
    // that touches the field -- the caller's flat storage AND a write-back subroutine's
    // self_<field> parameter -- orders the same tag values.
    private Dictionary<string, List<string>> fieldUnionMemberEvidence = new();

    // RFC 0009 phase 3 (slot fields): the ONE member list a union field carries,
    // keyed "class|field" where class is the field's declaring class (the topmost
    // ancestor whose layout still carries it). Every storage form of the same
    // field -- a flattened `<inst>_<field>`, a slot's payload bytes, a tagged val
    // produced by a slot read -- interprets the tag byte through this shared list,
    // so a write lowered inside an outlined method and a read lowered at the call
    // site agree on tag values no matter which lowers first.
    private Dictionary<string, List<string>> classUnionMembers = new();

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

    // Declared return type TEXT of the most recently emitted call (regular or force-inlined),
    // so a bare `x = f()` assigning a fresh local can recognise a `list[T]` result: the return
    // temp's DataType is UNKNOWN (StringToDataType has no case for "list["), and UNKNOWN alone
    // does not say the local is a list rather than an ordinary unrecognised-width value, nor
    // carries the element type len()/subscript need. Left unset it is null, which every use
    // treats the same as "not a list return".
    private string? lastCallReturnTypeText;

    // Element type of the most recently emitted call's list result, taken from the
    // callee's emitted `return <list var>` rather than its annotation -- the
    // unannotated counterpart of lastCallReturnTypeText's "list[T]" text.
    private DataType? lastCallReturnListElem;

    // `return <list[T] local>` inside an OUTLINED function records the element type
    // under the function's emitted name, so a later `x = f()` can register x's
    // list-ness even though the declaration says nothing about lists.
    private Dictionary<string, DataType> funcListReturnElems = new();

    // The sequence name an outlined function's returns agree on (`return tuple(v)`,
    // `return list(v)`, `return v` all answer "v"), recorded at scan time under the
    // function's full name. A module-level `x = f()` compiles BEFORE f's own body
    // emits, so funcListReturnElems cannot answer yet -- the call site resolves the
    // recorded name against its own maps instead: a parameter's element type is the
    // argument's, a module-level sequence's is already registered.
    private Dictionary<string, (string Name, string ModulePrefix)> funcReturnSeqExprs = new();

    // Unique suffix for the synthesized index of a runtime-bounds slice iteration.
    private int sliceLoopId = 0;

    // Catch-dispatch labels of the `try` blocks whose BODY is currently being
    // lowered (innermost last). A `raise` lexically inside a try body is delivered
    // to the top label instead of propagating to the caller. Pushed/popped by
    // VisitTry around the body only — not around handler/finally blocks, where a
    // `raise` is a re-raise that must propagate.
    private List<string> tryCatchStack = new();

    // Parallel to tryCatchStack: finallyStack.Count at the moment that try's dispatch
    // was pushed — the index just above its own pending finally (if any). A `raise`
    // delivered to tryCatchStack[^1] escapes every try whose finally sits above that
    // floor, so those run inline before the SignalError; the target try's own finally
    // runs at its dispatch instead.
    private List<int> tryFinallyFloor = new();

    // Pending `finally` blocks of the enclosing try statements (innermost last). A `return` that
    // escapes a try-with-finally must run these before returning (Python semantics).
    private List<List<Statement>> finallyStack = new();

    // Per-handler saved exception-code variable (innermost last): a bare `raise` re-raises this,
    // so the code survives handler body code that clobbers the error register (R22).
    private List<string> handlerCodeStack = new();
    private int exnCodeId = 0;

    // ── the bounded exception object (#369) ──────────────────────────────────
    //
    // One exception is live at a time in this model, which is what lets `except X as e` bind
    // an object without allocating one. The object is two static facts: the type code the
    // dispatcher already holds, and the flash address of a string-literal message.

    /// Whether ANY handler in the program binds a name. Kept as one input to
    /// <see cref="programRecordsRaiseMessages"/> -- the store itself is gated on that.
    private bool programBindsExceptionObject;

    /// Whether raises record their message for a later read: a handler that binds
    /// `except X as e` (#369), or an unhandled-exception report that can carry it to
    /// UART. The second form needs the string writer in the image, so the answer is
    /// taken after the module scan -- before the first raise is lowered, which is what
    /// the whole zero-cost claim rests on.
    private bool programRecordsRaiseMessages;

    /// Whether the unhandled-exception report can carry a message at all: some raise
    /// in the program has one AND the UART string writer the printer calls resolved.
    /// Gates the emission of <see cref="ExceptionMessageTail"/>.
    private bool programReportsRaiseMessage;

    /// Set when a raise that actually got lowered recorded a message (literal word,
    /// deferred-print site, or dynamic store). <c>programReportsRaiseMessage</c> is
    /// computed from the AST of every imported module, so it is true the moment an
    /// imported-but-uncalled function raises with a message; this flag is what keeps
    /// the report machinery out of images whose reachable code never raises one.
    private bool sawRaiseMessageStore;

    /// The module-level word holding the flash address of the live exception's message.
    /// One word, because one exception is live at a time.
    internal const string ExceptionMessageVar = "__exn_msg";

    /// Raise-site id for a deferred-print message (#435). 0 means the flash word in
    /// <see cref="ExceptionMessageVar"/> is the whole message (a string literal).
    internal const string ExceptionSiteVar = "__exn_site";

    /// Synthetic function that replays the live exception's print sequence.
    internal const string ExceptionMessagePrinter = "__pymcu_print_exn_msg";

    /// Synthetic function the unhandled-exception runtime calls after printing
    /// `E:<Type>`: emits ": " plus the recorded message and the CRLF, or just the CRLF
    /// when the raise carried no message. Reached only from raw asm, which is why the
    /// backend adds it to the reference graph by name.
    internal const string ExceptionMessageTail = "__pymcu_exn_tail";

    internal static string ExceptionArgVar(int i) => "__exn_arg" + i;
    internal static string ExceptionFloatArgVar(int i) => "__exn_farg" + i;

    /// True when some raise in the program has a non-literal message. Decided before the
    /// first function is lowered, so <c>print(e)</c> in a handler compiled before the raise
    /// still calls the printer. A program whose every raise is a string literal is unchanged.
    private bool programHasDynamicRaiseMessage;

    private sealed class RaiseMessagePiece
    {
        public string? Literal;
        public int IntSlot = -1;
        public int FloatSlot = -1;
        public bool IsBool;
        public DataType PrintAs = DataType.INT32;
        public string FormatSpec = "";
    }

    private sealed class RaiseMessageSite
    {
        public int Id;
        public List<RaiseMessagePiece> Pieces = new();
    }

    private readonly List<RaiseMessageSite> raiseMessageSites = new();
    private int nextRaiseSiteId = 1;

    /// Names bound by an enclosing `except ... as`, to the per-try variable holding the code
    /// and to the handler's declared type. A name is in scope only while its handler body is
    /// being lowered, so a read of it after the handler is an ordinary undefined name.
    private Dictionary<string, (string CodeVar, string ExnType)> exceptionBindings = new();

    // Derived, not copied. BuiltinExceptionNames' own docstring says a second copy of this
    // list would eventually disagree with it, and this WAS that second copy: same six names,
    // written out again, in a different form, in a file nobody reading that warning opens.
    // Adding StopIteration for a generator's exhaustion would have gone into Codes alone and
    // left the name unrecognised here. Derived removes the possibility rather than warning
    // about it (#245).
    private HashSet<string> exceptionNames = BuiltinExceptionNames.Codes.Keys.ToHashSet();
    private int nextUserExceptionCode = 32;

    // Module-level `raise CompileError(...)` guards that survived compile-time if/match
    // folding in an IMPORTED module (e.g. the arch guard in hal/wifi.py once DCE picks the
    // else branch). Imported modules' top-level code never executes, so the guard is
    // recorded per module prefix; resolving a symbol from that module then reports the
    // guard's message instead of a misleading "call to undefined function".
    // Column and Length as well as Line, so a use of a refused module can put its caret ON
    // the guard rather than name it in prose (#241). A RaiseStmt is stamped at its `raise`
    // keyword, so the three together are a real caret in the guard's own file.
    // Path as well as File, because they are different things and only one of them can carry a
    // caret. `currentSourceFile` is a DISPLAY name ("adc.py") built from the module name;
    // `currentSourcePath` is the real path the renderer can open to quote the line. Recording
    // only the display name is what made the first attempt at #241 report the guard's line
    // under the ENTRY file's name -- line from one file, label from another, which is the exact
    // pairing this fix exists to remove.
    private readonly Dictionary<string,
        (string Msg, string File, string Path, int Line, int Column, int Length)>
        moduleGuardErrors = new();

    // Debugging
    private List<string> sourceLines = new();
    private Dictionary<string, List<string>> moduleSourceLines = new();

    // The same source lines keyed by FILE PATH, which is the identity a compiled function
    // already carries (FunctionEntry.SourcePath -> currentSourcePath). The name-keyed map
    // cannot be queried from inside a function body: all that is in scope there is the
    // MANGLED prefix, and mangling is not reversible (`a.b` and `a_b` both give `a_b`).
    private Dictionary<string, List<string>> sourceLinesByPath = new();
    private string currentSourceFile = "";

    // The path of the module currently being scanned or lowered, empty for the entry file.
    // Stamped onto every diagnostic raised from here so an error inside an imported module
    // names that module's file (PyMCU#178). currentSourceFile cannot do this job: it is
    // "led.py" for drivers/led.py, which is neither unique nor openable.
    private string currentSourcePath = "";

    /// <summary>
    /// The name the rest of the generator labels a module's file with: the module's last
    /// segment plus .py, which for a package is the DIRECTORY name and not the literal
    /// __init__.py. Scanning derives it from the module name; an inline expansion has only
    /// the path, and has to arrive at the same answer or one module would be labelled two
    /// ways depending on whether it was inlined.
    /// </summary>
    private static string SourceFileLabel(string path)
    {
        string name = Path.GetFileName(path);
        if (!string.Equals(name, "__init__.py", StringComparison.Ordinal)) return name;

        string? dir = Path.GetDirectoryName(path);
        string pkg = string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir);
        return pkg.Length > 0 ? pkg + ".py" : name;
    }

    // Module name to the file it was loaded from, handed over by the module loader.
    private Dictionary<string, string> modulePaths = new();

    // Module name to its parsed AST, kept from Generate's parameter so late alias
    // registration (imports inside inlined bodies) can chase re-exports the same way the
    // entry-file pass does.
    private Dictionary<string, ProgramNode> importedModuleAsts = new();

    /// The file a module was loaded from, or empty when it is not known. Tries the qualified
    /// name first, then the trailing segment, because a module reaches IR generation under
    /// whichever name imported it and the loader records every name it was requested under.
    private string PathOfModule(string modName)
    {
        if (modulePaths.TryGetValue(modName, out var p)) return p;
        int dot = modName.LastIndexOf('.');
        if (dot != -1 && modulePaths.TryGetValue(modName.Substring(dot + 1), out var q)) return q;
        return "";
    }
    private int lastLine = -1;
    private int currentStmtLine = 0; // Tracks the current statement's source line

    /// True while expanding an @inline body whose defining file is known, which is when the
    /// statement line should follow the CALLEE rather than stay on the call. Naming the
    /// callee's file and the caller's line together is a location that does not exist, and
    /// the line the reader has to edit is the callee's. Issue #164.
    private bool inlineTracksCalleeLine = false;

    /// The line of the statement currently being lowered INSIDE an @inline body whose file is
    /// known. Separate from `currentStmtLine`, which stays on the call, because the two kinds
    /// of diagnostic want opposite ends: one is about the callee's code, the other about the
    /// caller's argument.
    private int inlineCalleeStmtLine = 0;

    // Names assigned a constructor call at MODULE level of the entry file. Their init
    // runs inside main (module init), but references resolve them as module globals —
    // SlotInstanceKey uses this to register the boxed instance under its module key.
    private readonly HashSet<string> topLevelInstanceTargets = new();

    // Module names whose file lives inside the entry file's own directory tree: the user's own
    // modules, as opposed to an installed distribution. Only these have their module level run.
    private HashSet<string> projectModules = new();

    // The declared element type of a runtime ptr[T] value, or UINT8 when untyped/unknown.
    // Passed as the Elem of Load/StoreIndirect so the access width survives the optimizer
    // collapsing typed temporaries into raw constants.
    private DataType RuntimePtrElem(Val ptr) => ptr switch
    {
        Variable pv when runtimePtrVars.TryGetValue(pv.Name, out var e) => e,
        Temporary pt when runtimePtrVars.TryGetValue(pt.Name, out var e) => e,
        _ => DataType.UINT8
    };

    // The element type of <paramref name="v"/> when it is a RUNTIME pointer (a `ptr(...)` of
    // an address known only at run time), else null. Its bits live at the address it holds,
    // not in the variable that holds the address.
    private DataType? RuntimePtrTargetElem(Val v) => v switch
    {
        Variable pv when runtimePtrVars.TryGetValue(pv.Name, out var e) => e,
        Temporary pt when runtimePtrVars.TryGetValue(pt.Name, out var e) => e,
        _ => null
    };

    // A bit index known at compile time: a literal, or a name bound to a constant.
    private int? ConstBitIndex(Val idx) => idx switch
    {
        Constant c => c.Value,
        Temporary t when constantVariables.TryGetValue(t.Name, out int tv) => tv,
        Variable v when constantVariables.TryGetValue(v.Name, out int vv) => vv,
        _ => null
    };

    // Builds a located user-facing compile error from inside IR generation. Using this
    // instead of `throw new Exception(...)` means the message is reported as a clean
    // `file:line: error: CompileError: ...` diagnostic (with the current source line and
    // a caret) rather than a location-less "InternalCompilerError" that looks like a
    // compiler bug. For genuine compiler-invariant violations keep `throw new Exception`.
    ///
    /// The column is deliberately left unset. IR generation runs long after the tokens are
    /// gone, and what it holds is the current STATEMENT's line: the start of the statement is
    /// not where the problem is, so pointing a caret there would be a confident lie about a
    /// character chosen only because it was the one position available. Use the overload below
    /// wherever the offending AST node is in hand; that node knows its own column.
    private PyMCU.Common.CompilerError UserError(string message)
    {
        // A compiler-generated diagnostic raised while an @inline body is being expanded is
        // about THAT body: a `for` over a plain `str` parameter is the callee's line to fix,
        // in the callee's file, and naming one without the other is a location that does not
        // exist. An author-written `raise CompileError` does not come through here; it keeps
        // the call site, which is the line its message is about. Issue #164.
        int line = inlineTracksCalleeLine && inlineCalleeStmtLine > 0
            ? inlineCalleeStmtLine
            : (currentStmtLine > 0 ? currentStmtLine : (lastLine > 0 ? lastLine : 1));
        return new("CompileError", message, line) { File = LocatedFile };
    }

    /// Lowers a parameter's default-value expression under the CALLEE's location.
    ///
    /// The rest of argument binding runs under the caller's, because the nodes it holds are
    /// the caller's. A default value is the one exception: it is text in the callee's file, so
    /// a diagnostic raised inside it has to name that file, and the line it names comes from
    /// the default's own node rather than from the call. Both halves move together and move
    /// back together. Issue #227.
    private Val VisitDefaultValueUnderCallee(PyMCU.Frontend.Expression defaultValue,
                                             string? calleeSourcePath)
    {
        if (calleeSourcePath == null) return VisitExpression(defaultValue);

        string savedPath = currentSourcePath;
        string savedFile = currentSourceFile;
        bool savedTracks = inlineTracksCalleeLine;
        int savedCalleeLine = inlineCalleeStmtLine;

        currentSourcePath = calleeSourcePath;
        currentSourceFile = SourceFileLabel(calleeSourcePath);
        inlineTracksCalleeLine = true;
        inlineCalleeStmtLine = defaultValue.Line;
        try
        {
            return VisitExpression(defaultValue);
        }
        finally
        {
            currentSourcePath = savedPath;
            currentSourceFile = savedFile;
            inlineTracksCalleeLine = savedTracks;
            inlineCalleeStmtLine = savedCalleeLine;
        }
    }

    /// The file to report against, or null to mean the entry file. The line numbers this
    /// generator carries belong to whichever module is being lowered, so a diagnostic that does
    /// not also say WHICH file states a line of one file against the name of another.
    private string? LocatedFile => string.IsNullOrEmpty(currentSourcePath) ? null : currentSourcePath;

    /// The call's i-th argument node, or null when the call does not have one.
    ///
    /// Guarded because these are read on the error path: several of them run after an arity
    /// check that guarantees the index, but a later edit that moves or weakens that check would
    /// turn a clean diagnostic into an IndexOutOfRangeException, which is the one outcome worse
    /// than a missing caret. A null here degrades to the statement-level location and no caret.
    private static PyMCU.Frontend.ASTNode? ArgAt(PyMCU.Frontend.CallExpr call, int i) =>
        i >= 0 && i < call.Args.Count ? call.Args[i] : null;

    /// Builds the same error, located at the node the message is about.
    ///
    /// A node the parser built from a token carries that token's line, column and length; one
    /// synthesised by a desugaring carries none, and then this degrades to exactly the
    /// statement-level location the overload above produces. Passing a node is therefore always
    /// at least as good as not passing one, and never invents a position that was not measured.
    private PyMCU.Common.CompilerError UserError(string message, PyMCU.Frontend.ASTNode? at)
    {
        if (at is null || (at.Line <= 0 && at.Column <= 0))
            return UserError(message);

        int line = at.Line > 0
            ? at.Line
            : (currentStmtLine > 0 ? currentStmtLine : (lastLine > 0 ? lastLine : 1));
        // A node the parser stamped with a line but no column (a statement, say) still
        // knows WHICH line it is: reporting that line without a caret beats falling back
        // to a statement counter that holds nothing during the scan and answering line 1.
        if (at.Column <= 0)
            return new PyMCU.Common.CompilerError("CompileError", message, line)
                { File = LocatedFile };
        return new PyMCU.Common.CompilerError(
            "CompileError", message, line, at.Column, at.Length > 0 ? at.Length : 1)
            { File = LocatedFile };
    }

    /// The same located error, carrying the three things a machine reader needs and a sentence
    /// cannot hold: a stable code, the fixes the message proposes in prose, and the other sites
    /// it names.
    ///
    /// It DELEGATES to the located overload rather than building a second error beside it, so
    /// the rules about which line and which file a diagnostic gets are stated once. A site that
    /// adopts this keeps every positional decision it already had.
    private PyMCU.Common.CompilerError UserError(
        string message,
        PyMCU.Frontend.ASTNode? at,
        string code,
        IReadOnlyList<PyMCU.Common.SuggestedFix>? fixes = null,
        IReadOnlyList<PyMCU.Common.RelatedSpan>? related = null)
    {
        var located = UserError(message, at);
        return new PyMCU.Common.CompilerError(
            located.TypeName, located.Message, located.Line, located.Column, located.Length)
        {
            File = located.File,
            LocationIsFinal = located.LocationIsFinal,
            Code = code,
            Fixes = fixes ?? [],
            Related = related ?? [],
        };
    }

    /// A refusal raised by a module guard, reported where the reader can act on it.
    ///
    /// TWO ANSWERS, and which one is right depends on where the failing use is.
    ///
    /// When the use is in the reader's own file, the old behaviour was already correct and is
    /// kept: caret on their line, guard named in the sentence. `radio = CYW43()` on an ATmega
    /// gets a caret under `CYW43()` in main.py, which is the line they can change.
    ///
    /// When the use is inside an imported module it is not. The reader is shown a line they did
    /// not write, in a file they have not opened, which is usually CORRECT code -- for
    /// `AnalogPin("A0")` on an ATtiny 4313 it was adc/__init__.py:36, a call fourteen lines
    /// below the guard that refused it (#241). There the caret goes on the guard instead, whose
    /// text IS the explanation.
    ///
    /// The discriminator is free: `currentSourcePath` is empty exactly for the entry module, so
    /// "is the use in the program in front of the reader" is already answered. That phrase is
    /// from the comment this replaces; the rule it stated was right and its implementation
    /// assumed the use site could only ever be the reader's file.
    ///
    /// `LocationIsFinal` on the guard branch because the position is complete before the
    /// pipeline's usual stamp, which fills the file in from the module being lowered. Without it
    /// the line is the guard's and the file the caller's, which points into a file that has no
    /// such line -- measured on the first attempt at this fix, which reported adc's line 22
    /// under main.py, a six-line file.
    ///
    /// `Path` and not `File`: `currentSourceFile` is a DISPLAY name built from the module name
    /// ("adc.py"), and only `currentSourcePath` can be opened to quote the line. An empty path
    /// means no caret is available, so that falls back too rather than inventing one.
    ///
    /// The two callers are twins -- a use as a CALL and a use as a plain READ -- and both come
    /// through here so they cannot drift into answering the same refusal differently.
    private PyMCU.Common.CompilerError ModuleGuardError(
        (string Msg, string File, string Path, int Line, int Column, int Length) guard,
        PyMCU.Frontend.ASTNode? reachedFrom)
    {
        if (string.IsNullOrEmpty(currentSourcePath) || string.IsNullOrEmpty(guard.Path))
        {
            // The sentence already names the second site, "(module guard at adc/__init__.py:36)",
            // and that is where the fact goes to die for a machine: the driver has a regular
            // expression in core/compiler.py whose only job is to renumber line citations inside
            // message text, and no editor can make one clickable. Carried as data as well, the
            // same fact is a second squiggle on the guard that refused the call. The text does
            // not change: a message people already recognise keeps its words, and the structure
            // is added beside it rather than carved out of it.
            var related = string.IsNullOrEmpty(guard.Path)
                ? []
                : new[]
                {
                    new PyMCU.Common.RelatedSpan(
                        guard.Path, guard.Line, guard.Column,
                        guard.Length > 0 ? guard.Length : 1,
                        "the module guard that refuses it"),
                };
            return UserError($"{guard.Msg} (module guard at {guard.File}:{guard.Line})",
                             reachedFrom, code: "module-guard", related: related);
        }

        // No "reached from" clause on this branch. The intermediate line is library plumbing the
        // reader did not write and cannot act on, and naming it is what the old message did
        // instead of moving the caret.
        return new PyMCU.Common.CompilerError(
            "CompileError", guard.Msg,
            guard.Line, guard.Column, guard.Length > 0 ? guard.Length : 1)
            { File = guard.Path, LocationIsFinal = true };
    }

    /// Every FunctionDef the scan put on the path to becoming a real subroutine.
    ///
    /// Recorded by NODE and not by name so it survives the outline path, which compiles a
    /// stand-in built from the method rather than the method itself: the original is what goes
    /// in here, because the original is what the user decorated.
    ///
    /// Read once, after every module has been scanned, by the check that refuses `@naked` and
    /// `@interrupt` on a function that is expanded instead of compiled. That check is written
    /// against this set rather than against the branches that fill it, so a path added later
    /// that expands a function fails the check instead of dropping the decorator in silence,
    /// which is how PyMCU#229 stayed invisible: five different registries, all of them a way
    /// out of being a subroutine, and no one place that noticed.
    private HashSet<FunctionDef> compiledAsSubroutine = new();

    // Intrinsic tracking
    private HashSet<string> intrinsicNames = new();

    /// <summary>
    /// Compile-time resource claims, `claim(key, value, owner, hint)` from pymcu.types. A
    /// HAL registers a value it is about to program into a resource that other code may
    /// program too (a timer's prescaler shared by its two channels), and the compiler
    /// refuses the second site that asks the same resource for a different value. Runtime
    /// values cannot be claimed and are let through. The registry lives for the whole
    /// program: claims are visited in lowering order and never released.
    /// </summary>
    private sealed class ClaimRecord
    {
        public int Value;
        public readonly List<string> Owners = new();
        public string Site = "";
    }
    private readonly Dictionary<string, ClaimRecord> claims = new();

    /// <summary>
    /// The name of the file this compilation was invoked on, for a diagnostic that has to cite
    /// a line of the ENTRY file inside its own sentence.
    ///
    /// Everywhere else the entry file is named by leaving `File` unset and letting the pipeline
    /// fill in the path it was handed, which is why nothing in IR generation carried it. A
    /// citation inside a message has no such slot: it is text, and text has to say which file
    /// or the number in it means nothing. Defaults to the name IR generation already assumes
    /// for the entry module, so a caller that does not set it reads as before. Issue #303.
    /// </summary>
    public string EntryFileName { get; set; } = "main.py";

    /// <summary>
    /// The unit is a library (--library): no entry point, and every top-level function is an
    /// export called from outside PyMCU. A buffer parameter of such a function gets a hidden
    /// trailing length parameter, and `len(buf)` inside the body reads it.
    ///
    /// Gated on the OPTION and not on IsExportC, because @export_c marks a function whose ABI
    /// a C caller already wrote down; silently adding an argument to it would break that
    /// caller. A library's callers are generated from the same signatures in the same build.
    /// </summary>
    public bool LibraryMode { get; set; } = false;

    /// <summary>
    /// Buffer parameter (qualified) -> its hidden length parameter (qualified). A kernel gets
    /// the pointer and nothing else, so without this `len(buf)` has no answer and every count
    /// has to be passed alongside and trusted -- which is exactly how a caller walks off the
    /// end of someone's bytearray.
    /// </summary>
    private Dictionary<string, string> bufferLengthParams = new();

    // Depth counter for runtime-conditional branches currently being compiled.
    // > 0 means we are inside a branch whose predicate could not be folded at compile time.
    // VisitRaise uses this to distinguish a genuine compile-time CompileError (depth == 0)
    // from a CompileError guard inside a runtime if/match that const-propagation failed to
    // fold (depth > 0). In the latter case the raise is only a potential error; aborting
    // the compilation would be a false positive.
    private int _runtimeBranchDepth = 0;

    // The position in the enclosing statement sequence is unreachable: a `return`/`raise`
    // lowered unconditionally at this level, or a statement AlwaysLeaves proves cannot fall
    // through, ended it. VisitBlock stops at it, so a statement after a compile-time-taken
    // arm that returns is never lowered -- its errors would be dead-code fictions (the
    // `NUMBERS[character]` after `_put`'s elif chain read an unbound `character`). Branch
    // arms and function bodies reset it: a runtime arm's termination is conditional, and a
    // callee's return only ends the callee's sequence.
    private bool _seqTerminated;

    // One fresh token per open run-time branch, pushed/popped by EnterRuntimeBranch /
    // LeaveRuntimeBranch. Depth alone cannot tell two sibling branches apart (`if a:` then
    // `if b:` both sit at depth 1); the token path can. A buffer records the path in effect
    // at its declaration, and `buf += src` (Assign.cs) lowers only when the statement's own
    // path equals it -- the bump happens once while compiling, so it is only correct when
    // every execution of the += is preceded by the declaration re-running.
    private readonly List<int> _runtimeBranchTokens = new();
    private int _nextBranchToken;

    // A callee that emitted no Jump to its exit label has no normal exit: every path
    // through it left unconditionally, so its _seqTerminated belongs to the caller's
    // sequence -- the statements after the call cannot run. A callee that also returns
    // on another path leaves the caller's tail reachable, and the flag drops back to
    // what it was before the call (`d[k]` probes the table and returns on a hit,
    // raising KeyError only on a miss -- the miss's termination used to leak through
    // the boundary and eat `d.get()` and everything below it, probe 086).
    private void RestoreSeqTerminatedAfterExpansion(bool saved, string exitLabel)
    {
        bool calleeNeverReturns = _seqTerminated
            && !currentInstructions.Any(i => i is Jump { Target: var jt } && jt == exitLabel);
        _seqTerminated = saved || calleeNeverReturns;
    }

    // Buffer key -> the run-time branch token path at its declaration (empty when the
    // declaration sits outside every run-time branch). Written by KeepGrownArraySize,
    // the chokepoint fresh bytearray/bytes buffers register through.
    private readonly Dictionary<string, List<int>> bufferDeclBranchTokens = new();

    // Buffer key -> its current LOGICAL element count: what the most recent
    // declaration statement gave it, plus whatever `+=` / `.extend()` appended on
    // this code path. arraySizes holds the storage MAX across every expansion that
    // shares the key -- sibling @inline expansions of `write` reuse
    // `inline4.write.full_buffer` -- while len(), writeto and the += tail offset
    // need the count for the expansion being lowered right now. A declaration
    // re-bases it: `full_buffer = bytearray([reg_base, reg])` in the buf=None
    // expansion means a fresh two-byte buffer even though a sibling expansion's
    // += grew the shared storage to 3.
    private readonly Dictionary<string, int> bufferLogicalLen = new();

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

    // Observer-mode name resolution (PYMCU_RESOLVE_OBSERVE=1). Null on a normal build, and
    // every call through it is guarded, so a build without the flag never touches it.
    private NameResolution? nameResolution;
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

    // Names bound to MORE THAN ONE string literal in the scope being lowered (collected from
    // the source before the body is visited). A str name is a compile-time value, so a second
    // binding is the only way its value can stop being single-valued -- and only these names
    // pay for a real 16-bit store of the interned id at every binding site.
    private Dictionary<string, List<string>> multiStrCandidates = new();

    // Names whose string value genuinely DIFFERS per path (both arms of a run-time branch
    // assigned, a loop body reassigned, another function assigned the global). The value is
    // the candidate set, in first-seen order: the id held at run time is one of these, and
    // print() dispatches on it. Reading such a name as a number is a located error.
    private Dictionary<string, List<string>> multiStrVariables = new();

    // Non-zero while a read of a multi-valued str name is allowed to resolve to its raw
    // interned id (the print dispatch and `s == "literal"`, which both compare ids).
    private int multiStrHandleReads;

    // Tracks inline-function parameters bound to a bytes/list literal argument
    // (e.g. uart.write(b"Hi")). Lets the param be iterated via for-in and
    // unrolled at compile time, mirroring a direct `for b in b"Hi"` loop.
    // A name bound to a short list/tuple of integer constants (`pins = [11, 12, 13]`), so a
    // `for` over the NAME unrolls the way the same literal written inline already does.
    private Dictionary<string, List<Frontend.Expression>> constSequenceBindings = new();

    // The subset of constSequenceBindings whose name was bound from a `range(...)` (#363). A
    // range is not a value on this target, and giving it a name does not make it one: the name
    // is iterable and nothing else. Reading it in a value position is refused by this set,
    // because the binding alone would let it through in silence.
    private HashSet<string> rangeBoundSequences = new();

    // The names currently bound to a TUPLE rather than to a list (#299). A tuple and a list of
    // the same elements share their storage here, so nothing downstream can tell them apart and
    // `T[0] = 5` was accepted where CPython raises TypeError. Immutability is a property of the
    // name, and the binding is the only place that property exists.
    //
    // A name is REMOVED when it is rebound to something writable: a set that only grows would
    // refuse `t = (1, 2, 3)` followed by `t = [1, 2, 3]`, which is a legal program.
    private readonly HashSet<string> tupleBoundNames = new();

    // The literal elements an ANNOTATED array was initialized with (`base: uint8[4] = [1,2,3,4]`).
    // Read only when a list comprehension iterates that array by name: the array itself keeps
    // living as stores, so nothing else changes shape because the elements are remembered here.
    private Dictionary<string, List<Frontend.Expression>> arrayLiteralElements = new();

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

    // For a list[list[T]] variable: qualified_name → the INNER list's element type.
    // `bins[b]` yields a Temporary whose own element type is T -- the entry lets the
    // second subscript (`bins[b][0]`), a `for kb in bins` loop variable, and nested
    // stores resolve T instead of falling through to a register-bit access.
    private Dictionary<string, DataType> listInnerElemTypes = new();

    // `x = []` is a compile-time empty sequence while nothing mutates it, but a
    // later `x.append(v)` in the same function means the program wanted a
    // runtime heap list whose element type is knowable only at that append.
    // The per-function prescan collects the qualified names (`promotable`);
    // `promoted` records the ones actually emitted as header-only GC objects
    // (count 0, capacity 0, scalar-payload flag clear) -- an append of a
    // GC_REF element must then set the object's ref-bearing bit, which the
    // alloc could not know at creation time.
    private HashSet<string> promotableEmptyLists = new();
    private HashSet<string> promotedEmptyLists = new();
    // Set when a heap object's payload may hold GC_REFs -- GcAlloc(Ref) sites
    // and the inline flag write EmitRefPayloadFlag emits. Stamped onto
    // ProgramIR.UsesRefPayloads so the backend can drop the ref-tracing GC
    // routines when no program code can produce a flagged payload.
    private bool usesRefPayloads = false;

    // Function parameters declared as bytearray (passed as pointer, no length).
    // The parameter name is stored as qualified_name (funcname_paramname).
    private HashSet<string> bytearrayParams = new();

    // const[str] parameters of NON-@inline functions: received as a runtime 16-bit flash
    // byte-pointer (the caller passes a FlashStrAddr). Subscripting one (s[i]) emits a
    // FlashLoadPtr so a single shared subroutine can walk any flash string instead of the
    // loop being inlined per call site.
    private HashSet<string> flashStrPtrVars = new();

    // Names of fixed-size arrays that own one contiguous SRAM block. The per-function scan
    // adds the locals a body subscripts with a non-constant index; the creation paths add the
    // arrays they emit ArrayStore init for -- a `self.buf = bytearray(n)` member, a grown
    // buffer, a memoryview window. Once an array has contiguous storage that fact is true for
    // every function, so the set is NEVER cleared between functions: a `self.buffer` made in
    // main's inlined __init__ is still the same storage when `draw` walks it, and forgetting
    // the mark made a later `enumerate(buffer)` invent `buffer__0..buffer__512` slot variables
    // beside the real array -- the framebuffer was allocated twice, and the slots were never
    // even written.
    private HashSet<string> arraysWithVariableIndex = new();

    // Module-level arrays that unconditionally use SRAM (bytearray declarations at global scope).
    private HashSet<string> moduleSramArrays = new();

    // `memoryview(buf)[k:]` is a writable window of `buf`, not a copy.
    // arraySizes[view] is the window length; loads and stores go to
    // arrayViewBase at index + arrayViewOffset (adafruit_ssd1306's
    // `super().__init__(memoryview(self.buffer)[1:])`).
    private Dictionary<string, string> arrayViewBase = new();
    private Dictionary<string, int> arrayViewOffset = new();

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

    // A lambda stored on an instance FIELD (`self.f = lambda: io.value`) reads
    // names from the scope it was WRITTEN in, and that scope is gone by the
    // time `self.f()` runs. The binding snapshots each free name's resolution
    // -- where it points, its class, its constant -- so the expansion at the
    // call site can re-seed the same bindings under its own prefix.
    private sealed class CapturedName
    {
        public string Alias = "";
        public string? Cls;
        public int Const;
        public bool HasConst;
        public string? Str;
    }
    private Dictionary<string, Dictionary<string, CapturedName>> lambdaCaptures = new();

    // A bound METHOD stored on an instance field (`self._readbit =
    // self._ow.read_bit`, adafruit_onewire): the field is compile-time, so the
    // call site re-visits <recv>.<member>(args) spelled through a fresh alias
    // name seeded in variableAliases -- the original receiver AST (`self._ow`
    // or a ctor param like `p`) need not resolve in the callee's scope, while
    // the alias name always does and chase-follows to the recorded instance.
    // The generic member-call path then resolves the method exactly as if the
    // source had spelled it that way, outlined or inline.
    private sealed class BoundMethodField
    {
        public string Recv = "";   // receiver's terminal instance key -- the call receiver
        public string Member = ""; // the method's source name on the receiver's class
        public string Fn = "";     // resolved <class>_<method>, for the rebind check
    }
    private Dictionary<string, BoundMethodField> boundMethodFields = new();
    private int boundMethodCounter = 0;

    private DeviceConfig deviceConfig = null!;

    // Flash byte-pointers (const[str] by-reference params / FlashStrAddr values) carry the
    // TARGET's pointer width: 16-bit on AVR/PIC, 32-bit on ARM/RISC-V. Typing them UINT16
    // everywhere truncated the 0x1000xxxx flash addresses on ARM.
    private DataType FlashPtrType =>
        deviceConfig != null && deviceConfig.PointerWidth == 4 ? DataType.UINT32 : DataType.UINT16;
}