using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;
using IrUnaryOp = PyMCU.IR.UnaryOp;

namespace PyMCU.UnitTests;

public class IRGeneratorTests
{
    private static ProgramIR GenerateIR(string source, DeviceConfig? config = null)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var ast = parser.ParseProgram();
        var irGen = new IRGenerator();
        return irGen.Generate(ast, new Dictionary<string, ProgramNode>(), config ?? new DeviceConfig());
    }

    [Fact]
    public void SimpleReturn()
    {
        var ir = GenerateIR("def main():\n    return 42");

        Assert.Single(ir.Functions);
        Assert.Equal("main", ir.Functions[0].Name);

        var ret = ir.Functions[0].Body.OfType<Return>().First();
        var c = Assert.IsType<Constant>(ret.Value);
        Assert.Equal(42, c.Value);
    }

    [Fact]
    public void ImplicitReturn()
    {
        var ir = GenerateIR("def main():\n    return");

        Assert.Single(ir.Functions);
        var ret = ir.Functions[0].Body.OfType<Return>().First();
        Assert.IsType<NoneVal>(ret.Value);
    }

    [Fact]
    public void MultipleFunctions()
    {
        var ir = GenerateIR("def a():\n    return 1\ndef b():\n    return 2");

        Assert.Equal(2, ir.Functions.Count);
        Assert.Equal("a", ir.Functions[0].Name);
        Assert.Equal("b", ir.Functions[1].Name);
    }

    [Fact]
    public void IfStatement()
    {
        var ir = GenerateIR(
            "def f(x: int):\n" +
            "    if x:\n" +
            "        return 1\n" +
            "    else:\n" +
            "        return 2");

        var body = ir.Functions[0].Body;
        Assert.Contains(body, i => i is JumpIfZero);
        Assert.Contains(body, i => i is Label);
    }

    [Fact]
    public void WhileStatement()
    {
        var ir = GenerateIR("def f():\n    while 1:\n        pass");

        var body = ir.Functions[0].Body;
        Assert.Contains(body, i => i is Jump);
        Assert.True(body.OfType<Label>().Count() >= 2);
    }

    [Fact]
    public void BinaryOps()
    {
        var ir = GenerateIR("def f(a: int, b: int):\n    return a + b");

        var bin = ir.Functions[0].Body.OfType<Binary>().First();
        Assert.Equal(IrBinaryOp.Add, bin.Op);
    }

    [Fact]
    public void BitManipulation()
    {
        var ir = GenerateIR("def f(port: ptr):\n    port[0] = 1\n    return port[1]");

        var body = ir.Functions[0].Body;
        Assert.Contains(body, i => i is BitSet);
        Assert.Contains(body, i => i is BitCheck);
    }

    [Fact]
    public void NoneReturnCall()
    {
        var ir = GenerateIR(
            "def void_func():\n    pass\n" +
            "def main():\n    void_func()");

        Assert.Equal(2, ir.Functions.Count);
        var mainBody = ir.Functions[1].Body;

        var call = mainBody.OfType<Call>().First(c => c.FunctionName == "void_func");
        Assert.IsType<NoneVal>(call.Dst);
    }

    [Fact]
    public void IntReturnCall()
    {
        var ir = GenerateIR(
            "def int_func() -> int:\n    return 42\n" +
            "def main():\n    x = int_func()");

        Assert.Equal(2, ir.Functions.Count);
        var mainBody = ir.Functions[1].Body;

        var call = mainBody.OfType<Call>().First(c => c.FunctionName == "int_func");
        Assert.IsNotType<NoneVal>(call.Dst);
    }

    [Fact]
    public void ContinueStatement()
    {
        // Should not throw "Unknown Statement type"
        var ir = GenerateIR("def main():\n    while 1:\n        continue");
        Assert.Single(ir.Functions);
    }

    [Fact]
    public void BreakStatement()
    {
        var ir = GenerateIR("def main():\n    while 1:\n        break");
        Assert.Single(ir.Functions);
    }

    [Fact]
    public void MatchStatement()
    {
        // Use a runtime parameter so the match isn't constant-folded away.
        var ir = GenerateIR(
            "def main(x):\n" +
            "    match x:\n" +
            "        case 1:\n" +
            "            return 1\n" +
            "        case _:\n" +
            "            return 0");

        var body = ir.Functions[0].Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Equal });
        Assert.Contains(body, i => i is JumpIfZero);
    }

    // Regression: visitVarDecl inside @inline must use current_inline_prefix
    // when building the variable_types key. Without the fix, `i: uint16 = 0`
    // defaulted to UINT8, and `count_up(1000)` would compare against 232
    // (1000 truncated to uint8) instead of 1000.
    [Fact]
    public void InlineUint16VarDecl_PreservesType()
    {
        const string src =
            "from pymcu.types import uint16, inline\n\n" +
            "@inline\n" +
            "def count_up(limit: uint16):\n" +
            "    i: uint16 = 0\n" +
            "    while i < limit:\n" +
            "        i = i + 1\n\n" +
            "def main():\n" +
            "    count_up(1000)\n";

        var ir = GenerateIR(src, new DeviceConfig { Chip = "atmega328p", Arch = "avr" });
        Assert.Single(ir.Functions);

        // After inlining, the comparison i < 1000 emits JumpIfGreaterOrEqual(i, 1000, end).
        // The constant 1000 must not be truncated to 232 (0xFF & 1000 = 232).
        var found1000 = ir.Functions[0].Body
            .OfType<JumpIfGreaterOrEqual>()
            .Any(j => j.Src2 is Constant { Value: 1000 });

        Assert.True(found1000,
            "JumpIfGreaterOrEqual should compare against 1000 (uint16), not 232 (uint8 truncation)");
    }

    // -------------------------------------------------------------------------
    // Group 1 -- AugAssign / Operators
    // -------------------------------------------------------------------------

    [Fact]
    public void AugAssign_Add()
    {
        var ir = GenerateIR("def f(x):\n    x += 1");

        var aa = ir.Functions[0].Body.OfType<AugAssign>().Single();
        Assert.Equal(IrBinaryOp.Add, aa.Op);
        Assert.IsType<Constant>(aa.Operand);
        Assert.Equal(1, ((Constant)aa.Operand).Value);
    }

    [Fact]
    public void AugAssign_AllSixOperators()
    {
        const string src =
            "def f(x, mask):\n" +
            "    x -= 5\n" +
            "    x &= mask\n" +
            "    x |= mask\n" +
            "    x ^= mask\n" +
            "    x <<= 1\n" +
            "    x >>= 1\n";

        var body = GenerateIR(src).Functions[0].Body.OfType<AugAssign>().ToList();

        Assert.Equal(6, body.Count);
        Assert.Equal(IrBinaryOp.Sub,    body[0].Op);
        Assert.Equal(IrBinaryOp.BitAnd, body[1].Op);
        Assert.Equal(IrBinaryOp.BitOr,  body[2].Op);
        Assert.Equal(IrBinaryOp.BitXor, body[3].Op);
        Assert.Equal(IrBinaryOp.LShift, body[4].Op);
        Assert.Equal(IrBinaryOp.RShift, body[5].Op);
    }

    [Fact]
    public void UnaryOps_BitNot_Neg_Not()
    {
        const string src =
            "def f(x):\n" +
            "    a = ~x\n" +
            "    b = -x\n" +
            "    c = not x\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is Unary { Op: IrUnaryOp.BitNot });
        Assert.Contains(body, i => i is Unary { Op: IrUnaryOp.Neg });
        Assert.Contains(body, i => i is Unary { Op: IrUnaryOp.Not });
    }

    // -------------------------------------------------------------------------
    // Group 2 -- Bit Manipulation (ptr / indexed non-array variables)
    // -------------------------------------------------------------------------

    [Fact]
    public void BitSet_BitClear_OnConstantIndex()
    {
        // Bit-slicing requires an explicit ptr[uint8] (or wider) type annotation.
        // port[0] = 1  ->  BitSet(port, 0)
        // port[7] = 0  ->  BitClear(port, 7)
        const string src =
            "def f(port: ptr[uint8]):\n" +
            "    port[0] = 1\n" +
            "    port[7] = 0\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is BitSet { Bit: 0 });
        Assert.Contains(body, i => i is BitClear { Bit: 7 });
    }

    [Fact]
    public void BitCheck_ConstantIndex()
    {
        // x = port[3]  ->  BitCheck(port, 3, dst)
        // Explicit ptr[uint8] is required to signal bit-slicing intent.
        const string src = "def f(port: ptr[uint8]):\n    x = port[3]\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is BitCheck { Bit: 3 });
    }

    [Fact]
    public void BitWrite_RuntimeValue()
    {
        // port[3] = val  ->  BitWrite (not BitSet/BitClear) when val is runtime.
        // The ptr[uint8] annotation is required for bit-slicing.
        const string src =
            "def f(port: ptr[uint8], val: uint8):\n" +
            "    port[3] = val\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is BitWrite { Bit: 3 });
        Assert.DoesNotContain(body, i => i is BitSet);
        Assert.DoesNotContain(body, i => i is BitClear);
    }

    [Fact]
    public void RuntimeBitIndex_ThroughPointer_Rejected()
    {
        // A runtime bit index on a chip register (PORTB[bit]=1, a MemoryAddress) is
        // supported — it lowers to a runtime mask (1 << bit) + read-modify-write
        // (exercised by the AVR examples). Through a RUNTIME POINTER it is rejected
        // with a clear error rather than miscompiling the pointer value as the port.
        const string src =
            "def f(port: ptr[uint8], bit: uint8):\n" +
            "    port[bit] = 1\n";

        Assert.ThrowsAny<Exception>(() => GenerateIR(src));
    }

    [Fact]
    public void ConstDivisionByZero_RaisesValueError()
    {
        // `5 // 0` must fold to a clean ValueError diagnostic, not leak a C#
        // DivideByZeroException that the pipeline reports as an InternalCompilerError.
        const string src =
            "def main():\n" +
            "    x: uint8 = 5 // 0\n";
        Assert.Throws<PyMCU.Common.ValueError>(() => GenerateIR(src));
    }

    [Fact]
    public void ConstModuloByZero_RaisesValueError()
    {
        const string src =
            "def main():\n" +
            "    x: uint8 = 7 % 0\n";
        Assert.Throws<PyMCU.Common.ValueError>(() => GenerateIR(src));
    }

    [Fact]
    public void UndefinedFunctionCall_RaisesCompileError()
    {
        // A call to a function that resolves to nothing must be reported at compile time
        // (typo / missing import) instead of emitting a Call to an undefined symbol that
        // only fails at link. Gated to real chip targets, so pass an AVR config.
        const string src =
            "def main():\n" +
            "    nonexistent_func(1)\n";
        Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
    }

    [Fact]
    public void SliceAssignment_EqualLength_Compiles()
    {
        // `arr[1:3] = [9, 9]` — equal-length slice assignment lowers to element copies.
        const string src =
            "arr: uint8[5] = [1, 2, 3, 4, 5]\n" +
            "def main():\n" +
            "    arr[1:3] = [9, 9]\n";
        Assert.NotNull(GenerateIR(src));
    }

    [Fact]
    public void SliceAssignment_LengthMismatch_RaisesClearError()
    {
        // Differing lengths (insert/delete) have no bare-metal representation.
        const string src =
            "arr: uint8[5] = [1, 2, 3, 4, 5]\n" +
            "def main():\n" +
            "    arr[1:3] = [9, 9, 9]\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
        Assert.Contains("length mismatch", ex.Message);
    }

    [Fact]
    public void IntegerTrueDivision_YieldsFloat()
    {
        // Python 3's `/` is true division and always yields a float, even for two ints. PyMCU
        // promotes integer operands to float and emits float division (it must compile, not
        // reject, so a naive `count / 10` is faithful to Python's 2.5 rather than C's 2).
        const string src =
            "def main(a: uint16, b: uint16) -> float:\n" +
            "    return a / b\n";
        var ir = GenerateIR(src);
        Assert.NotNull(ir);
    }

    [Fact]
    public void FloorDivision_StillCompiles()
    {
        // `//` is the integer-division operator and must keep working.
        var ir = GenerateIR(
            "def main(a: uint16, b: uint16) -> uint16:\n" +
            "    return a // b\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void FoldedArithmeticConstant_OutOfRange_RaisesError()
    {
        // 50 * 20 = 1000 folds at compile time and overflows uint8: caught like a bare literal.
        const string src =
            "def main():\n" +
            "    x: uint8 = 50 * 20\n";
        var ex = Assert.Throws<PyMCU.Common.ValueError>(() => GenerateIR(src));
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void FoldedBitwiseConstant_FullWidth_StillCompiles()
    {
        // Bitwise/shift idioms that use the full width must NOT be range-flagged.
        var ir = GenerateIR(
            "def main():\n" +
            "    a: uint8 = 0xFFFF & 0xFF\n" +
            "    b: uint8 = 1 << 7\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void FoldedArithmeticConstant_ExplicitCast_Wraps()
    {
        // The uint8(...) cast is the escape hatch for intentional wraparound; it must compile.
        var ir = GenerateIR(
            "def main():\n" +
            "    x: uint8 = uint8(50 * 20)\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void FStringWithRuntimeValue_InPrint_Compiles()
    {
        // print(f"...") lowers each part to a direct stream write, so a runtime interpolation
        // is allowed in a stream context (no buffer, no string built at runtime).
        var ir = GenerateIR(
            "def main(x: uint16):\n" +
            "    print(f\"v={x}\")\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void FStringWithRuntimeValue_AsValue_NeedsStrfmtHelpers()
    {
        // `s = f"..."` with a runtime interpolation lowers to pymcu.strfmt calls into a fixed
        // buffer; compiling without that module loaded (the build driver injects it) must
        // report clearly rather than silently producing garbage.
        const string src =
            "def main(x: uint16):\n" +
            "    name = f\"v={x}\"\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
        Assert.Contains("pymcu.strfmt", ex.Message);
    }

    [Fact]
    public void NegativeArrayInitializers_AreStored()
    {
        // `arr: int8[3] = [-1, -2, -3]` — negative literals parse as UnaryExpr(Negate), not
        // IntegerLiteral, and were silently dropped (every element initialized to 0). The
        // initializer must evaluate constant expressions, so the stores carry -1, -2, -3.
        const string src =
            "arr: int8[3] = [-1, -2, -3]\n" +
            "def main():\n" +
            "    pass\n";
        var prog = GenerateIR(src);
        var stores = prog.Functions
            .SelectMany(f => f.Body)
            .OfType<ArrayStore>()
            .Where(a => a.ArrayName == "arr")
            .ToList();
        Assert.Contains(stores, a => a.Src is Constant { Value: -1 });
        Assert.Contains(stores, a => a.Src is Constant { Value: -2 });
        Assert.Contains(stores, a => a.Src is Constant { Value: -3 });
    }

    [Fact]
    public void ConstructClassWithoutInit_RaisesClearError()
    {
        // Constructing a class that has no __init__ is reported specifically (PyMCU does not
        // synthesize a default constructor), not as a generic 'undefined function'.
        const string src =
            "class Math:\n" +
            "    def double(self, x: uint8) -> uint8:\n" +
            "        return x * 2\n" +
            "def main():\n" +
            "    m = Math()\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("__init__", ex.Message);
    }

    [Fact]
    public void CallNonCallableVariable_RaisesClearError()
    {
        // Calling a value (`x(3)` where x is uint8) reports 'not callable', not 'undefined
        // function' (x is defined, just not a function).
        const string src =
            "def main():\n" +
            "    x: uint8 = 5\n" +
            "    y: uint8 = x(3)\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("not callable", ex.Message);
    }

    [Fact]
    public void ModuleGuard_UsingSymbol_ReportsGuardMessage()
    {
        // An imported module whose module-level `raise CompileError(...)` survived
        // compile-time folding (an arch guard, e.g. hal/wifi.py on AVR) never imports its
        // symbols. Using one must surface the guard's message, not "undefined function".
        var modTokens = new Lexer("raise CompileError(\"WiFi is only supported on rp2350\")\n").Tokenize();
        var modAst = new Parser(modTokens).ParseProgram();
        var mainTokens = new Lexer(
            "from pymcu.hal.wifi import CYW43\n" +
            "def main():\n" +
            "    w = CYW43()\n").Tokenize();
        var mainAst = new Parser(mainTokens).ParseProgram();
        var modules = new Dictionary<string, ProgramNode> { ["pymcu.hal.wifi"] = modAst };
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => new IRGenerator().Generate(mainAst, modules, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("WiFi is only supported on rp2350", ex.Message);
        Assert.DoesNotContain("undefined function", ex.Message);
    }

    [Fact]
    public void ModuleGuard_UnusedImport_StillCompiles()
    {
        // The guard must stay lazy: a module with a surviving module-level CompileError can
        // be pulled in transitively (hal/__init__.py imports every HAL) — as long as none
        // of its symbols are used, the build proceeds.
        var modTokens = new Lexer("raise CompileError(\"WiFi is only supported on rp2350\")\n").Tokenize();
        var modAst = new Parser(modTokens).ParseProgram();
        var mainTokens = new Lexer(
            "from pymcu.hal.wifi import CYW43\n" +
            "def main():\n" +
            "    x: uint8 = 1\n").Tokenize();
        var mainAst = new Parser(mainTokens).ParseProgram();
        var modules = new Dictionary<string, ProgramNode> { ["pymcu.hal.wifi"] = modAst };
        var ir = new IRGenerator().Generate(mainAst, modules, new DeviceConfig { Arch = "avr" });
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void RuntimePtr_AugAssign_CarriesElemWidth()
    {
        // `p.value += n` through a runtime ptr[uint16] lowers to LoadIndirect + Binary +
        // StoreIndirect. Elem must ride on BOTH indirect instructions: the optimizer may
        // collapse the typed temporaries into raw constants, and a Constant's type is its
        // magnitude — without Elem the backend would narrow the access to one byte.
        const string src =
            "from pymcu.types import ptr, uint16\n" +
            "def f(off: uint16):\n" +
            "    p: ptr[uint16] = ptr(0x0200 + off)\n" +
            "    p.value += 1\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        var body = ir.Functions.Single(f => f.Name == "f").Body;
        Assert.Contains(body, i => i is LoadIndirect { Elem: DataType.UINT16 });
        Assert.Contains(body, i => i is StoreIndirect { Elem: DataType.UINT16 });
    }

    [Fact]
    public void ModuleBytearray_UnannotatedConstSize_Registers()
    {
        // MicroPython declares buffers without annotation and sizes them with module
        // constants: `samples = bytearray(WINDOW)`. Both the missing annotation and the
        // non-literal size used to fall through to a runtime call to an undefined
        // 'bytearray' function.
        const string src =
            "WINDOW = 8\n" +
            "buf = bytearray(WINDOW)\n" +
            "def main():\n" +
            "    i: uint8 = 3\n" +
            "    buf[i] = 7\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        var stores = ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(a => a.ArrayName.EndsWith("buf")).ToList();
        Assert.NotEmpty(stores);
    }

    [Fact]
    public void InOperator_AcceptsTupleLiteral()
    {
        // `x in (1, 2, 3)` (a tuple literal on the RHS) is valid Python and must compile the
        // same as `x in [1, 2, 3]` — previously only a list literal was accepted.
        const string src =
            "out: uint8 = 0\n" +
            "def main():\n" +
            "    global out\n" +
            "    x: uint8 = 3\n" +
            "    if x in (1, 2, 3):\n" +
            "        out = 1\n";
        // Should not throw.
        GenerateIR(src);
    }

    [Fact]
    public void BareTupleReturn_ParsesAndLowersInInlineFunction()
    {
        // `return a, b` (a bare comma-separated tuple, no parens) must parse and, from an
        // @inline function, lower into the caller's unpack targets — previously the parser
        // rejected the bare form and required explicit parentheses.
        const string src =
            "ra: uint8 = 0\n" +
            "rb: uint8 = 0\n" +
            "@inline\n" +
            "def swap(a: uint8, b: uint8):\n" +
            "    return b, a\n" +
            "def main():\n" +
            "    global ra, rb\n" +
            "    ra, rb = swap(3, 7)\n";
        // Should not throw.
        GenerateIR(src);
    }

    [Fact]
    public void TupleReturnFromRegularFunction_RaisesClearError()
    {
        // Returning multiple values from a non-@inline subroutine is unsupported; it must be
        // a clear error, not the cryptic "Unknown Expression type: TupleExpr".
        const string src =
            "def minmax(a: uint8, b: uint8):\n" +
            "    return a, b\n" +
            "def main():\n" +
            "    x: uint8 = 0\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
        Assert.Contains("multiple values", ex.Message);
    }

    [Fact]
    public void TypeAnnotatedLocalInstance_ResolvesMethodCall()
    {
        // `c: Counter = Counter(5)` (a type-annotated local instance) must register the
        // instance->class link exactly like the unannotated `c = Counter(5)`, so `c.get()`
        // resolves to the class method `Counter_get` — not a fabricated, undefined `c_get`
        // that fails at link.
        const string src =
            "out: uint8 = 0\n" +
            "class Counter:\n" +
            "    def __init__(self, start: uint8):\n" +
            "        self.n = start\n" +
            "    def get(self) -> uint8:\n" +
            "        return self.n\n" +
            "def main():\n" +
            "    global out\n" +
            "    c: Counter = Counter(5)\n" +
            "    out = c.get()\n";
        var body = GenerateIR(src).Functions.First(f => f.Name == "main").Body;
        Assert.Contains(body, i => i is Call { FunctionName: "Counter_get" });
        Assert.DoesNotContain(body, i => i is Call { FunctionName: "c_get" });
    }

    [Fact]
    public void UndefinedInstanceAttribute_RaisesCompileError()
    {
        // Reading an attribute that is assigned nowhere in the program (a typo) must be a
        // compile error instead of fabricating an undefined member read as 0. Gated to real
        // chip targets (like the undefined-function check).
        const string src =
            "out: uint8 = 0\n" +
            "class Sensor:\n" +
            "    def __init__(self):\n" +
            "        self.a = 1\n" +
            "        self.b = 2\n" +
            "    def read(self):\n" +
            "        return self.a\n" +
            "def main():\n" +
            "    global out\n" +
            "    s: Sensor = Sensor()\n" +
            "    out = s.nonexistent\n";
        Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
    }

    [Fact]
    public void DefinedInstanceAttribute_DoesNotError()
    {
        // The flip side: reading a field that IS assigned (even in a non-__init__ method)
        // must still compile — assignedMemberNames is collected across all methods.
        const string src =
            "out: uint8 = 0\n" +
            "class Sensor:\n" +
            "    def __init__(self):\n" +
            "        self.a = 1\n" +
            "    def configure(self):\n" +
            "        self.cfg = 5\n" +
            "def main():\n" +
            "    global out\n" +
            "    s: Sensor = Sensor()\n" +
            "    out = s.cfg\n";
        // Should not throw (cfg is assigned in configure()).
        GenerateIR(src, new DeviceConfig { Arch = "avr" });
    }

    [Fact]
    public void ModuleConstStr_Subscript_FoldsToCharCode()
    {
        // A module-level `const[str]` is owned by ScanGlobals, which previously never
        // recorded its value, so `S[i]` / len(S) silently dropped. It now resolves: S[1]
        // of "abc" folds to the char code 'b' (98).
        const string src =
            "S: const[str] = \"abc\"\n" +
            "out: uint8 = 0\n" +
            "def main():\n" +
            "    global out\n" +
            "    out = S[1]\n";
        var body = GenerateIR(src).Functions.First(f => f.Name == "main").Body;
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 98 } });
    }

    [Fact]
    public void ModuleConstStr_SubscriptOutOfRange_RaisesCompileError()
    {
        const string src =
            "S: const[str] = \"abc\"\n" +
            "out: uint8 = 0\n" +
            "def main():\n" +
            "    global out\n" +
            "    out = S[10]\n";
        Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
    }

    [Fact]
    public void LenOfStringConstant_FoldsToLength()
    {
        // len() of a compile-time string (literal or a str/const[str] variable) is its length.
        const string src =
            "S: const[str] = \"abcd\"\n" +
            "out: uint8 = 0\n" +
            "def main():\n" +
            "    global out\n" +
            "    out = len(S)\n";
        var body = GenerateIR(src).Functions.First(f => f.Name == "main").Body;
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 4 } });
    }

    [Fact]
    public void RuntimeRangeZeroStep_RaisesCompileError()
    {
        // range(start, stop, 0) never advances the loop variable (Python raises ValueError).
        // The compile-time-unrolled path rejected this; the runtime-loop path (range over a
        // non-constant bound) emitted an infinite loop. A literal-zero step must be rejected.
        const string src =
            "def f(n: uint8):\n" +
            "    x: uint8 = 0\n" +
            "    for i in range(0, n, 0):\n" +
            "        x += 1\n";
        Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
    }

    [Fact]
    public void ComputedFloatToIntVariable_RaisesTypeError()
    {
        // `y: uint8 = 5 // 2.0` folds to a FloatConstant; the bare-float-literal check only
        // sees a direct FloatLiteral, so the folded float slipped through and the Copy was
        // silently dropped. A compile-time float result into an int var must require a cast.
        const string src =
            "def main():\n" +
            "    y: uint8 = 5 // 2.0\n";
        Assert.Throws<PyMCU.Common.TypeError>(() => GenerateIR(src));
    }

    [Fact]
    public void ChrArgumentOutOfByteRange_RaisesValueError()
    {
        // chr(300) passed the value through as a Constant(300); since it is a folded constant
        // (not an IntegerLiteral) the literal-range check never fired, so it was silently
        // truncated into the uint8. chr() must be limited to a single byte (0..255).
        const string src =
            "def main():\n" +
            "    c: uint8 = chr(300)\n";
        Assert.Throws<PyMCU.Common.ValueError>(() => GenerateIR(src));
    }

    [Fact]
    public void NumericCastOfString_RaisesCompileError()
    {
        // uint8("hello") folded the string to its flash id and used it as an integer, then
        // dropped the assignment. A string argument to a numeric cast must be rejected.
        const string src =
            "def main():\n" +
            "    x: uint8 = uint8(\"hello\")\n";
        Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
    }

    [Fact]
    public void AbsOfString_RaisesCompileError()
    {
        const string src =
            "def main():\n" +
            "    x: uint8 = abs(\"foo\")\n";
        Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
    }

    [Fact]
    public void FloatBitwiseNot_RaisesTypeError()
    {
        // Unary bitwise NOT (`~`) on a float is undefined (Python raises TypeError); it
        // previously fell through to a Unary BitNot over a FloatConstant (silent miscompile).
        const string src =
            "def main():\n" +
            "    x: uint8 = ~1.5\n";
        Assert.Throws<PyMCU.Common.TypeError>(() => GenerateIR(src));
    }

    [Fact]
    public void FloatBitwiseOperand_RaisesTypeError()
    {
        // A bitwise/shift operator on a float operand is undefined (Python raises TypeError).
        // It was silently folded to 0.0 and the whole assignment was dropped.
        const string src =
            "def main():\n" +
            "    x: uint8 = 1.5 & 2\n";
        Assert.Throws<PyMCU.Common.TypeError>(() => GenerateIR(src));
    }

    [Fact]
    public void FlashArrayConstIndexOutOfRange_RaisesIndexError()
    {
        // A compile-time out-of-bounds index into a const[uint8[N]] flash array emitted an
        // out-of-bounds flash load with no diagnostic (the fixed-SRAM path checked bounds,
        // the flash path did not). It must now raise IndexError like any other array.
        const string src =
            "A: const[uint8[3]] = [1, 2, 3]\n" +
            "def main():\n" +
            "    y: uint8 = A[5]\n";
        Assert.Throws<PyMCU.Common.IndexError>(() => GenerateIR(src));
    }

    [Fact]
    public void ConstAugmentedAssignment_RaisesCompileError()
    {
        // `K += 1` mutates a const-declared name just like `K = ...`; both must be rejected.
        const string src =
            "K: const[uint8] = 5\n" +
            "def main():\n" +
            "    K += 1\n";
        Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
    }

    [Fact]
    public void RegularFunctionKeywordArg_BindsByName()
    {
        // A keyword argument to a regular (non-@inline) subroutine must bind by parameter
        // name (Python-style), not surface the cryptic "Unknown Expression type" from
        // evaluating the KeywordArgExpr node as an expression.
        const string src =
            "def f(a: uint8, b: uint8, c: uint8):\n" +
            "    return a + b + c\n" +
            "def main():\n" +
            "    y: uint8 = f(1, c=3, b=2)\n";
        // Should not throw.
        GenerateIR(src);
    }

    [Fact]
    public void RegularFunctionUnknownKeywordArg_RaisesCompileError()
    {
        const string src =
            "def f(x: uint8):\n" +
            "    return x\n" +
            "def main():\n" +
            "    y: uint8 = f(zzz=3)\n";
        Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
    }

    [Fact]
    public void ConstReassignment_RaisesCompileError()
    {
        // A name declared with a `const[...]` annotation is immutable; reassigning it
        // must be a clean compile error, not a silent overwrite of the constant.
        const string src =
            "K: const[uint8] = 5\n" +
            "def main():\n" +
            "    K = 7\n";
        Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
    }

    [Fact]
    public void PtrUint16_Param_BitSet_PreservesType()
    {
        // A function parameter declared as ptr[uint16] must propagate its type
        // into variableTypes so that the BitSet target operand carries
        // DataType.UINT16, not the default UINT8.
        const string src =
            "def f(reg: ptr[uint16]):\n" +
            "    reg[0] = 1\n";

        var body = GenerateIR(src).Functions[0].Body;

        var bs = body.OfType<BitSet>().Single();
        Assert.Equal(0, bs.Bit);
        Assert.IsType<Variable>(bs.Target);
        Assert.Equal(DataType.UINT16, ((Variable)bs.Target).Type);
    }

    [Fact]
    public void PtrUint16_LocalVar_BitSet_PreservesType()
    {
        // A *local* variable declared as ptr[uint16] (via AnnAssign) must also
        // carry DataType.UINT16 in the BitSet target.
        const string src =
            "def f():\n" +
            "    reg: ptr[uint16] = 0\n" +
            "    reg[0] = 1\n";

        var body = GenerateIR(src).Functions[0].Body;

        var bs = body.OfType<BitSet>().Single();
        Assert.Equal(0, bs.Bit);
        Assert.IsType<Variable>(bs.Target);
        Assert.Equal(DataType.UINT16, ((Variable)bs.Target).Type);
    }

    [Fact]
    public void WhileBitSet_EmitsJumpIfBitClear()
    {
        // while port[5]: pass
        // The loop exits when bit 5 is clear, so the exit-condition jump is
        // JumpIfBitClear (not a BitCheck + JumpIfZero pair).
        // ptr[uint8] annotation is required for bit-slicing.
        const string src =
            "def f(port: ptr[uint8]):\n" +
            "    while port[5]:\n" +
            "        pass\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is JumpIfBitClear { Bit: 5 });
        Assert.DoesNotContain(body, i => i is BitCheck);
    }

    [Fact]
    public void WhileNotBitSet_EmitsJumpIfBitSet()
    {
        // while not port[5]: pass
        // The loop exits when bit 5 IS set, so the exit-condition jump is
        // JumpIfBitSet (not a BitCheck + JumpIfNotZero pair).
        // ptr[uint8] annotation is required for bit-slicing.
        const string src =
            "def f(port: ptr[uint8]):\n" +
            "    while not port[5]:\n" +
            "        pass\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is JumpIfBitSet { Bit: 5 });
        Assert.DoesNotContain(body, i => i is BitCheck);
    }

    // -------------------------------------------------------------------------
    // Group 3 -- Fixed-size Arrays
    // -------------------------------------------------------------------------

    [Fact]
    public void Uint8Array_ConstantIndex_EmitsCopy_NotArrayStore()
    {
        // Constant-only index access -> register path: Copy to named element
        // variable (arr__0, arr__1, ...).  No ArrayStore should appear.
        const string src =
            "def f():\n" +
            "    arr: uint8[4] = [10, 20, 30, 40]\n" +
            "    arr[2] = 99\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.DoesNotContain(body, i => i is ArrayStore);
        // After arr[2]=99 a Copy to the element variable ending in "__2" must exist.
        Assert.Contains(body, i =>
            i is Copy { Dst: Variable v } && v.Name.EndsWith("__2"));
    }

    [Fact]
    public void Uint8Array_VariableIndex_EmitsArrayStoreLoad()
    {
        // Variable index -> SRAM path: ArrayStore / ArrayLoad.
        // No Copy to named element variables should be emitted.
        const string src =
            "def f(idx):\n" +
            "    arr: uint8[4] = [10, 20, 30, 40]\n" +
            "    arr[idx] = 7\n" +
            "    x = arr[idx]\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is ArrayStore);
        Assert.Contains(body, i => i is ArrayLoad);
        Assert.DoesNotContain(body, i =>
            i is Copy { Dst: Variable v } && v.Name.Contains("arr__"));
    }

    [Fact]
    public void MixedArray_VariableIndexTriggersSramForAll()
    {
        // Even though arr[0] uses a constant index, because arr[idx] (variable
        // index) also exists in the same function, the pre-scan forces ALL
        // accesses to go through SRAM (ArrayStore), including the constant-index
        // write arr[0] = 5.
        const string src =
            "def f(idx):\n" +
            "    arr: uint8[4] = [0, 0, 0, 0]\n" +
            "    arr[0] = 5\n" +
            "    arr[idx] = 7\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is ArrayStore);
        Assert.DoesNotContain(body, i =>
            i is Copy { Dst: Variable v } && v.Name.Contains("arr__"));
    }

    [Fact]
    public void Uint16Array_ElementTypePreserved()
    {
        // uint16[3] array elements must be emitted as Variable/Temporary with
        // DataType.UINT16, not the default UINT8.
        const string src =
            "def f():\n" +
            "    buf: uint16[3] = [100, 200, 300]\n";

        var body = GenerateIR(src).Functions[0].Body;

        // All Copy instructions whose destination is an array element variable
        // must carry the UINT16 type.
        var elemCopies = body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.Contains("buf__"))
            .ToList();

        Assert.NotEmpty(elemCopies);
        Assert.All(elemCopies, c =>
            Assert.Equal(DataType.UINT16, ((Variable)c.Dst).Type));
    }

    // -------------------------------------------------------------------------
    // Group 4 -- Global Variables
    // -------------------------------------------------------------------------

    [Fact]
    public void GlobalUint8_AppearsInGlobals()
    {
        const string src =
            "x: uint8 = 0\n" +
            "def main():\n" +
            "    pass\n";

        var ir = GenerateIR(src);

        var g = ir.Globals.SingleOrDefault(v => v.Name == "x");
        Assert.NotNull(g);
        Assert.Equal(DataType.UINT8, g.Type);
    }

    [Fact]
    public void GlobalUint16_TypePreserved()
    {
        const string src =
            "counter: uint16 = 0\n" +
            "def main():\n" +
            "    pass\n";

        var ir = GenerateIR(src);

        var g = ir.Globals.SingleOrDefault(v => v.Name == "counter");
        Assert.NotNull(g);
        Assert.Equal(DataType.UINT16, g.Type);
    }

    // -------------------------------------------------------------------------
    // Group 5 -- @inline Functions
    // -------------------------------------------------------------------------

    [Fact]
    public void InlineFunc_NotInIrFunctions()
    {
        // An @inline function must not appear as a separate Function entry.
        // Its body is inlined at the call site.
        const string src =
            "@inline\n" +
            "def add_one(x) -> int:\n" +
            "    return x + 1\n" +
            "def main(v):\n" +
            "    y = add_one(v)\n";

        var ir = GenerateIR(src);

        Assert.Single(ir.Functions);
        Assert.Equal("main", ir.Functions[0].Name);
        Assert.DoesNotContain(ir.Functions[0].Body,
            i => i is Call { FunctionName: "add_one" });
    }

    [Fact]
    public void InlineFunc_ResultCapturedViaCopy()
    {
        // The return value of an @inline function is captured via Copy into a
        // ResultTemp, never via a Return instruction visible to the caller.
        const string src =
            "@inline\n" +
            "def double_val(x) -> int:\n" +
            "    return x + x\n" +
            "def main(v):\n" +
            "    result = double_val(v)\n";

        var body = GenerateIR(src).Functions[0].Body;

        // The only Return in the caller body must be the implicit NoneVal.
        var returns = body.OfType<Return>().ToList();
        Assert.Single(returns);
        Assert.IsType<NoneVal>(returns[0].Value);

        // At least one Copy must carry the inline result.
        Assert.Contains(body, i => i is Copy);
    }

    [Fact]
    public void InlineFunc_EarlyReturn_JumpsToExitLabel()
    {
        // An early `return 100` inside @inline must become Copy + Jump to the
        // inline exit label -- NOT a Return instruction in the outer function.
        const string src =
            "@inline\n" +
            "def clamp(x) -> int:\n" +
            "    if x > 100:\n" +
            "        return 100\n" +
            "    return x\n" +
            "def main(v):\n" +
            "    r = clamp(v)\n";

        var body = GenerateIR(src).Functions[0].Body;

        // No Return with value Constant(100) should appear -- that is now a Copy.
        Assert.DoesNotContain(body,
            i => i is Return { Value: Constant { Value: 100 } });

        // The early return path emits Copy(Constant(100), ResultTemp).
        Assert.Contains(body,
            i => i is Copy { Src: Constant { Value: 100 } });

        // At least one unconditional Jump (to the exit label).
        Assert.Contains(body, i => i is Jump);
    }

    [Fact]
    public void InlineFunc_ConstUint8Param_Folded()
    {
        // A const[uint8] parameter passed a literal is folded into the body as
        // a Constant -- no Copy instruction is emitted for that parameter, and
        // the body Binary uses Constant(3) directly.
        const string src =
            "@inline\n" +
            "def shift_left(x, n: const[uint8]) -> int:\n" +
            "    return x << n\n" +
            "def main(v):\n" +
            "    y = shift_left(v, 3)\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body,
            i => i is Binary { Op: IrBinaryOp.LShift, Src2: Constant { Value: 3 } });
    }

    [Fact]
    public void NestedInlineFuncs_FlattenedToSingleFunction()
    {
        // Two levels of @inline: inner + outer, called from main.
        // The IR must contain exactly one Function (main); both inline bodies
        // are flattened in.
        const string src =
            "@inline\n" +
            "def inner(x) -> int:\n" +
            "    return x + 1\n" +
            "@inline\n" +
            "def outer(x) -> int:\n" +
            "    return inner(x) + 1\n" +
            "def main(v):\n" +
            "    r = outer(v)\n";

        var ir = GenerateIR(src);

        Assert.Single(ir.Functions);
        // Both Add operations (one from inner, one from outer) must be present.
        var adds = ir.Functions[0].Body.OfType<Binary>()
            .Where(b => b.Op == IrBinaryOp.Add).ToList();
        Assert.True(adds.Count >= 2,
            $"Expected >= 2 Add instructions, got {adds.Count}");
    }

    [Fact]
    public void InlineFunc_MultipleParams_AllBound()
    {
        // Three-parameter @inline: two runtime vars (aliased) and one constant
        // (folded).  The body must contain Mul and Add, and the constant 2
        // must appear as Constant(2) in the Add instruction.
        const string src =
            "@inline\n" +
            "def muladd(a, b, c) -> int:\n" +
            "    return a * b + c\n" +
            "def main(x, y):\n" +
            "    z = muladd(x, y, 2)\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mul });
        Assert.Contains(body,
            i => i is Binary { Op: IrBinaryOp.Add, Src2: Constant { Value: 2 } });
    }

    // -------------------------------------------------------------------------
    // Group 6 -- Relational Jump Optimisations
    // -------------------------------------------------------------------------

    [Fact]
    public void IfLessThan_EmitsJumpIfGreaterOrEqual()
    {
        // if a < b: -> jump on INVERTED condition to skip then-block.
        // Inverted Less is GreaterOrEqual.
        const string src =
            "def f(a, b):\n" +
            "    if a < b:\n" +
            "        return 1\n" +
            "    return 0\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is JumpIfGreaterOrEqual);
        Assert.DoesNotContain(body,
            i => i is Binary { Op: IrBinaryOp.LessThan });
    }

    [Fact]
    public void IfEqual_EmitsJumpIfNotEqual()
    {
        // if a == b:  ->  jump-over on NotEqual
        const string src =
            "def f(a, b):\n" +
            "    if a == b:\n" +
            "        return 1\n" +
            "    return 0\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is JumpIfNotEqual);
    }

    [Fact]
    public void IfGreaterOrEqual_EmitsJumpIfLessThan()
    {
        // if a >= b:  ->  jump-over on LessThan
        const string src =
            "def f(a, b):\n" +
            "    if a >= b:\n" +
            "        return 1\n" +
            "    return 0\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is JumpIfLessThan);
    }

    [Fact]
    public void IfNotEqual_EmitsJumpIfEqual()
    {
        // if a != b:  ->  jump-over on Equal
        const string src =
            "def f(a, b):\n" +
            "    if a != b:\n" +
            "        return 1\n" +
            "    return 0\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is JumpIfEqual);
    }

    // -------------------------------------------------------------------------
    // Group 7 -- Match / Case Advanced
    // -------------------------------------------------------------------------

    [Fact]
    public void MatchMultipleCases_EmitsOrderedComparisons()
    {
        // Three literal cases against a runtime subject: each emits
        // Binary(Equal) + JumpIfZero for runtime comparison.
        const string src =
            "def f(x):\n" +
            "    match x:\n" +
            "        case 1:\n" +
            "            return 10\n" +
            "        case 2:\n" +
            "            return 20\n" +
            "        case 3:\n" +
            "            return 30\n" +
            "        case _:\n" +
            "            return 0\n";

        var body = GenerateIR(src).Functions[0].Body;

        int equalBinaries = body.OfType<Binary>()
            .Count(b => b.Op == IrBinaryOp.Equal);
        Assert.Equal(3, equalBinaries);

        int jizCount = body.OfType<JumpIfZero>().Count();
        Assert.True(jizCount >= 3,
            $"Expected >= 3 JumpIfZero, got {jizCount}");
    }

    [Fact]
    public void MatchConstantSubject_OnlyMatchingBranchEmitted()
    {
        // Subject is an integer literal (2).  The compiler evaluates all
        // comparisons at compile-time and emits only the matching branch.
        // No Binary(Equal) comparisons should appear and the non-matching
        // returns (10, 0) must be absent.
        const string src =
            "def f():\n" +
            "    match 2:\n" +
            "        case 1:\n" +
            "            return 10\n" +
            "        case 2:\n" +
            "            return 20\n" +
            "        case _:\n" +
            "            return 0\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body,
            i => i is Return { Value: Constant { Value: 20 } });
        Assert.DoesNotContain(body,
            i => i is Binary { Op: IrBinaryOp.Equal });
        Assert.DoesNotContain(body,
            i => i is Return { Value: Constant { Value: 10 } });
        Assert.DoesNotContain(body,
            i => i is Return { Value: Constant { Value: 0 } });
    }

    // -------------------------------------------------------------------------
    // Group 8 -- While Loop Structure
    // -------------------------------------------------------------------------

    [Fact]
    public void WhileLoop_WithRuntimeCondition_EmitsLoopStructure()
    {
        // while i < n: i += 1
        // Must produce: Label(start), JumpIfGreaterOrEqual (loop-exit condition),
        // AugAssign(Add), Jump (back-edge), Label(end).
        const string src =
            "def f(n):\n" +
            "    i: uint8 = 0\n" +
            "    while i < n:\n" +
            "        i += 1\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i => i is JumpIfGreaterOrEqual);
        Assert.Contains(body, i => i is AugAssign { Op: IrBinaryOp.Add });
        // Back-edge jump + potential break-exit jump.
        Assert.Contains(body, i => i is Jump);
        Assert.True(body.OfType<Label>().Count() >= 2,
            "Expected at least 2 labels (loop-start and loop-end)");
    }

    // -------------------------------------------------------------------------
    // Group 9 -- InlineAsm and Type Preservation
    // -------------------------------------------------------------------------

    [Fact]
    public void InlineAsm_EmitsCorrectInstruction()
    {
        // asm("NOP") must produce an InlineAsm instruction with Code == "NOP".
        const string src =
            "def f():\n" +
            "    asm(\"NOP\")\n" +
            "    asm(\"NOP\")\n";

        var body = GenerateIR(src).Functions[0].Body;

        var asms = body.OfType<InlineAsm>().ToList();
        Assert.Equal(2, asms.Count);
        Assert.All(asms, a => Assert.Equal("NOP", a.Code));
    }

    [Fact]
    public void Uint16VarDecl_TypePreservedInCopy()
    {
        // y: uint16 = 500  ->  Copy(Constant(500), Variable(_, UINT16))
        // The Copy destination must carry DataType.UINT16, not the default UINT8.
        const string src =
            "def f():\n" +
            "    y: uint16 = 500\n" +
            "    return y\n";

        var body = GenerateIR(src).Functions[0].Body;

        Assert.Contains(body, i =>
            i is Copy { Src: Constant { Value: 500 }, Dst: Variable v }
            && v.Type == DataType.UINT16);
    }

    [Fact]
    public void TupleReturnAnnotation_OnInline_Compiles()
    {
        // `-> (uint8, uint8)` on an @inline function: the caller's unpack targets receive
        // the values, exactly as for the same function with no annotation.
        const string src =
            "@inline\n" +
            "def divmod8(a: uint8, b: uint8) -> (uint8, uint8):\n" +
            "    q: uint8 = a // b\n" +
            "    return (q, a - q * b)\n" +
            "def main(n: uint8):\n" +
            "    q, r = divmod8(n, 3)\n" +
            "    return q + r\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void TupleReturnAnnotation_OnNonInline_RaisesClearError()
    {
        // A real subroutine has one return register, so the annotation cannot be honoured.
        // The error must land on the definition, not on some later return statement.
        const string src =
            "def divmod8(a: uint8, b: uint8) -> (uint8, uint8):\n" +
            "    return (a, b)\n" +
            "def main():\n" +
            "    q, r = divmod8(10, 3)\n";

        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("@inline", ex.Message);
        Assert.Equal(1, ex.Line);
    }

    [Fact]
    public void BareAssignToPtrRegister_IsALocatedError_NotASilentNoOp()
    {
        // `OCR1AH = hi` used to compile to nothing (name rebind + DCE), which
        // silently broke Timer.set_compare in the stdlib.
        const string src =
            "from pymcu.types import uint8, ptr\n" +
            "def main():\n" +
            "    OCR1AH: ptr[uint8] = ptr(0x89)\n" +
            "    hi: uint8 = 0x12\n" +
            "    OCR1AH = hi\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("never writes the register", ex.Message);
        Assert.Contains(".value", ex.Message);
    }

    [Fact]
    public void Getattr_NamesReflectionAsTheReason()
    {
        const string src =
            "def main():\n" +
            "    x: uint8 = 1\n" +
            "    y = getattr(x, \"value\")\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("runtime reflection", ex.Message);
    }

    [Fact]
    public void Eval_NamesReflectionAsTheReason()
    {
        const string src =
            "def main():\n" +
            "    y = eval(\"1 + 1\")\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("runtime reflection", ex.Message);
    }

    [Fact]
    public void TupleReturnAnnotation_UnpackArityMismatch_RaisesClearError()
    {
        const string src =
            "@inline\n" +
            "def f(a: uint8) -> (uint8, uint8, uint8):\n" +
            "    return (a, a, a)\n" +
            "def main(n: uint8):\n" +
            "    x, y = f(n)\n" +
            "    return x + y\n";

        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("3 values", ex.Message);
    }

    [Fact]
    public void TupleReturnAnnotation_ReturnArityMismatch_RaisesClearError()
    {
        // The annotation says two values; the body returns three.
        const string src =
            "@inline\n" +
            "def f(a: uint8) -> (uint8, uint8):\n" +
            "    return (a, a, a)\n" +
            "def main(n: uint8):\n" +
            "    x, y = f(n)\n" +
            "    return x + y\n";

        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("this return has 3", ex.Message);
    }

    [Fact]
    public void TupleReturnAnnotation_SingleValueReturn_RaisesClearError()
    {
        const string src =
            "@inline\n" +
            "def f(a: uint8) -> (uint8, uint8):\n" +
            "    return a\n" +
            "def main(n: uint8):\n" +
            "    x, y = f(n)\n" +
            "    return x + y\n";

        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("single value", ex.Message);
    }

    [Fact]
    public void TupleReturnAnnotation_CalledWithoutUnpacking_RaisesClearError()
    {
        const string src =
            "@inline\n" +
            "def f(a: uint8) -> (uint8, uint8):\n" +
            "    return (a, a)\n" +
            "def main(n: uint8):\n" +
            "    x: uint8 = f(n)\n" +
            "    return x\n";

        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("unpack", ex.Message);
    }

    [Fact]
    public void TupleReturnAnnotation_WidensResultSlot()
    {
        // Without the annotation both result slots default to uint8 and `n * 300` would be
        // truncated. The declared uint16 element must reach the slot the caller reads.
        const string src =
            "@inline\n" +
            "def scale(a: uint8) -> (uint8, uint16):\n" +
            "    return (a, a * 300)\n" +
            "def main(n: uint8):\n" +
            "    lo, hi = scale(n)\n" +
            "    return hi\n";

        var body = GenerateIR(src, new DeviceConfig { Arch = "avr" })
            .Functions.First(f => f.Name == "main").Body;

        Assert.Contains(body, i =>
            i is Copy { Dst: Variable v } && v.Name.EndsWith("iret_1_1") && v.Type == DataType.UINT16);
        Assert.Contains(body, i =>
            i is Copy { Dst: Variable v } && v.Name.EndsWith("iret_1_0") && v.Type == DataType.UINT8);
    }
}
