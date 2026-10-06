using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;
using IrUnaryOp = PyMCU.IR.UnaryOp;

namespace PyMCU.UnitTests;

[Collection(ConsoleCaptureCollection.Name)]
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
    public void PlainFunctionParam_ShadowsSameNamedModuleGlobal()
    {
        // The non-@inline half of the shadowing bug: a module global named like a
        // plain def's parameter hijacked every read of that parameter (a user-level
        // start_low_ms = 250 drove the DHT driver's start pulse for 250 ms).
        var src =
            "def probe(start_low_ms: uint16) -> uint32:\n" +
            "    wide: uint32 = start_low_ms\n" +
            "    return wide * 2\n" +
            "start_low_ms: uint16 = 250\n" +
            "def main():\n" +
            "    x: uint32 = probe(18)\n" +
            "    return x\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        var probe = ir.Functions.First(f => f.Name == "probe");
        Assert.Contains(probe.Body, i2 =>
            i2 is Copy { Src: Variable { Name: "probe.start_low_ms" } });
        Assert.DoesNotContain(probe.Body, i2 =>
            i2 is Copy { Src: Variable { Name: "start_low_ms" } });
    }

    [Fact]
    public void ForIn_SliceWithRuntimeBounds_BecomesRangeLoop()
    {
        // for b in buf[0:n] with a runtime n rewrites to a range loop reading
        // buf[__i] each iteration -- an ArrayLoad with a non-constant index.
        var src =
            "def main():\n" +
            "    buf: bytearray = bytearray(6)\n" +
            "    n: uint8 = 0\n" +
            "    while n < 4:\n" +
            "        buf[n] = n\n" +
            "        n = n + 1\n" +
            "    total: uint8 = 0\n" +
            "    for b in buf[0:n]:\n" +
            "        total = total + b\n" +
            "    return total\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>(),
            l => l.Index is not Constant);
    }

    [Fact]
    public void DunderSliceAssign_UnrollsToPerElementSetitem()
    {
        // obj[0:3] = [...] on a class with __setitem__/__len__ unrolls to one
        // __setitem__ dispatch per element (the CircuitPython nvm pattern).
        var src =
            "class Store:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __len__(self) -> uint16:\n" +
            "        return 8\n" +
            "    @inline\n" +
            "    def __setitem__(self, index: uint16, value: uint8):\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = value\n" +
            "store = Store()\n" +
            "def main():\n" +
            "    store[0:3] = [1, 2, 3]\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        var writes = ir.Functions.SelectMany(f => f.Body).OfType<StoreIndirect>().Count();
        Assert.Equal(3, writes);
    }

    [Fact]
    public void DunderSliceAssign_LengthMismatch_Errors()
    {
        var src =
            "class Store:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __len__(self) -> uint16:\n" +
            "        return 8\n" +
            "    @inline\n" +
            "    def __setitem__(self, index: uint16, value: uint8):\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = value\n" +
            "store = Store()\n" +
            "def main():\n" +
            "    store[0:2] = [1, 2, 3]\n";
        var ex = Assert.ThrowsAny<Exception>(() => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("length mismatch", ex.Message);
    }

    [Fact]
    public void RaiseCompileError_InInlineBody_FiresUnderCallSiteRuntimeBranch()
    {
        // The raise is unconditional INSIDE the @inline body; user control flow
        // around the CALL must not downgrade it to a warning (readline() inside
        // `while True: if uart.any():` silently compiled to garbage).
        var src =
            "@inline\n" +
            "def bad() -> uint8:\n" +
            "    raise CompileError(\"no heap here\")\n" +
            "def main():\n" +
            "    n: uint8 = 0\n" +
            "    while n < 10:\n" +
            "        x: uint8 = bad()\n" +
            "        n = n + 1\n";
        var ex = Assert.ThrowsAny<Exception>(() => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("no heap here", ex.Message);
    }

    [Fact]
    public void RaiseCompileError_BehindFoldedConstGuard_DoesNotFire()
    {
        var src =
            "@inline\n" +
            "def guarded(mode: const[uint8]) -> uint8:\n" +
            "    if mode == 1:\n" +
            "        raise CompileError(\"wrong mode\")\n" +
            "    return 7\n" +
            "def main():\n" +
            "    n: uint8 = 0\n" +
            "    while n < 10:\n" +
            "        x: uint8 = guarded(0)\n" +
            "        n = n + 1\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        Assert.NotNull(ir);
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

        // Synthetic main is now generated for entry files without executable statements
        Assert.Equal(3, ir.Functions.Count);
        Assert.Contains(ir.Functions, f => f.Name == "main");
        Assert.Contains(ir.Functions, f => f.Name == "a");
        Assert.Contains(ir.Functions, f => f.Name == "b");
        var aBody = ir.Functions.First(f => f.Name == "a").Body;
        var bBody = ir.Functions.First(f => f.Name == "b").Body;
        Assert.Contains(aBody, i => i is Return);
        Assert.Contains(bBody, i => i is Return);
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

        var body = ir.Functions.First(f => f.Name == "f").Body;
        Assert.Contains(body, i => i is JumpIfZero);
        Assert.Contains(body, i => i is Label);
    }

    [Fact]
    public void WhileStatement()
    {
        var ir = GenerateIR("def f():\n    while 1:\n        pass");

        var body = ir.Functions.First(f => f.Name == "f").Body;
        Assert.Contains(body, i => i is Jump);
        Assert.True(body.OfType<Label>().Count() >= 2);
    }

    [Fact]
    public void BinaryOps()
    {
        var ir = GenerateIR("def f(a: int, b: int):\n    return a + b");

        var bin = ir.Functions.First(f => f.Name == "f").Body.OfType<Binary>().First();
        Assert.Equal(IrBinaryOp.Add, bin.Op);
    }

    [Fact]
    public void BitManipulation()
    {
        var ir = GenerateIR("def f(port: ptr):\n    port[0] = 1\n    return port[1]");

        var body = ir.Functions.First(f => f.Name == "f").Body;
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

        var aa = ir.Functions.First(f => f.Name == "f").Body.OfType<AugAssign>().Single();
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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body.OfType<AugAssign>().ToList();

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

        Assert.Contains(body, i => i is Unary { Op: IrUnaryOp.BitNot });
        Assert.Contains(body, i => i is Unary { Op: IrUnaryOp.Neg });
        Assert.Contains(body, i => i is Unary { Op: IrUnaryOp.Not });
    }

    [Fact]
    public void UnaryNot_OnFloatOperand_ResultTempIsUint8()
    {
        // `not x` always yields a 1-byte bool. Minting the result temp with the
        // operand's type (regression from widening Negate/BitNot temps) leaves the
        // upper bytes of a FLOAT slot unwritten; a later conditional jump then
        // reads stale bytes and takes the raise path even when x != 0.
        const string src =
            "def f(x: float) -> float:\n" +
            "    if not x:\n" +
            "        return 0.0\n" +
            "    return x\n";

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

        var notIns = Assert.Single(body, i => i is Unary { Op: IrUnaryOp.Not });
        var unary = (Unary)notIns;
        var dst = Assert.IsType<Temporary>(unary.Dst);
        Assert.Equal(DataType.UINT8, dst.Type);
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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

        Assert.Contains(body, i => i is BitSet { Bit: 0 });
        Assert.Contains(body, i => i is BitClear { Bit: 7 });
    }

    [Fact]
    public void BitCheck_ConstantIndex()
    {
        // x = port[3]  ->  BitCheck(port, 3, dst)
        // Explicit ptr[uint8] is required to signal bit-slicing intent.
        const string src = "def f(port: ptr[uint8]):\n    x = port[3]\n";

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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
    public void IntCastOfAFloatConstantOutOfInt32Range_RaisesErrorInsteadOfGarbage()
    {
        // `(int)fc.Value` is UNSPECIFIED by the C# spec for a double outside int's range;
        // `int(1e10)` measured as -1 on the build host before this was guarded (a JIT/platform
        // truncation artifact, not a value anyone chose), and CPython's own int(1e10) --
        // 10000000000 -- does not fit any PyMCU integer type either (32-bit widest), so there
        // is no value to silently fold to here.
        const string src =
            "def main():\n" +
            "    x: int32 = int(1e10)\n";
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => GenerateIR(src));
        Assert.Contains("does not fit any PyMCU integer type", ex.Message);
    }

    [Fact]
    public void IntCastOfAFloatConstantWithinInt32Range_StillFolds()
    {
        var ir = GenerateIR(
            "def main():\n" +
            "    x: uint32 = uint32(3000000000.0)\n" +
            "    print(x)\n");
        Assert.Contains(ir.Functions.Single(f => f.Name == "main").Body.OfType<Copy>(),
            c => c.Src is Constant { Value: unchecked((int)3000000000) });
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
    public void ConstructClassWithoutInit_SynthesizesADefaultConstructor()
    {
        // PyMCU#391: CPython synthesizes a trivial no-op constructor for a class that
        // declares no __init__, and PyMCU now does too, instead of refusing every
        // construction of such a class as if it had a missing symbol.
        const string src =
            "class Math:\n" +
            "    def double(self, x: uint8) -> uint8:\n" +
            "        return x * 2\n" +
            "def main():\n" +
            "    m = Math()\n";
        var prog = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        Assert.Contains(prog.Functions, f => f.Name == "main");
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
    public void ZcaAugAssign_RoutesToInPlaceDunder()
    {
        // `obj += v` on a ZCA instance must invoke __iadd__ through the regular
        // method-call machinery (mutating the instance), not compile as a scalar
        // read-modify-write on the instance handle.
        const string src =
            "class Acc:\n" +
            "    def __init__(self):\n" +
            "        self.total: uint16 = 0\n" +
            "        self.count: uint8 = 0\n" +
            "    def __iadd__(self, v: uint8):\n" +
            "        self.total += v\n" +
            "        self.count += 1\n" +
            "        return self\n" +
            "def main(x: uint8):\n" +
            "    a = Acc()\n" +
            "    a += x\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        var main = ir.Functions.Single(f => f.Name == "main");
        // The dunder body mutates the instance fields -- boxed (slot array stores) or
        // flattened (writes to the *_total/*_count field variables), depending on the
        // representation the ZCA machinery picked.
        static bool WritesField(Instruction i) => i switch
        {
            ArrayStore st => st.ArrayName.Contains("__slot"),
            Copy { Dst: Variable v } => v.Name.Contains("total") || v.Name.Contains("count"),
            AugAssign { Target: Variable v } => v.Name.Contains("total") || v.Name.Contains("count"),
            _ => false,
        };
        Assert.Contains(main.Body, WritesField);
    }

    [Fact]
    public void ZcaAugAssign_WithoutDunder_RaisesClearError()
    {
        // Without __iadd__ (or __add__) the augmented assignment has no meaning on a
        // ZCA instance; it must be a located error, not a silent scalar RMW.
        const string src =
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self.a: uint8 = 0\n" +
            "        self.b: uint8 = 0\n" +
            "def main(x: uint8):\n" +
            "    bx = Box()\n" +
            "    bx += x\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        Assert.Contains("__iadd__", ex.Message);
    }

    [Fact]
    public void TopLevelInstance_MethodCall_PassesSlotByReference()
    {
        // A module-level `a = Acc()` in a top-level script constructs inside the
        // synthesized main, but later references resolve `a` as a module global. The
        // boxed instance must be tracked under its module key so the outlined method
        // call passes the slot base (by reference) — previously the lookup missed and
        // the call passed the flattened field VALUES, silently mutating copies.
        const string src =
            "class Acc:\n" +
            "    def __init__(self):\n" +
            "        self.total: uint16 = 0\n" +
            "        self.count: uint8 = 0\n" +
            "    def add(self, v: uint8):\n" +
            "        self.total += v\n" +
            "        self.count += 1\n" +
            "a = Acc()\n" +
            "i: uint8 = 0\n" +
            "while i < 4:\n" +
            "    a.add(3)\n" +
            "    i += 1\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        var main = ir.Functions.Single(f => f.Name == "main");
        var call = main.Body.OfType<Call>().First(c => c.FunctionName == "Acc_add");
        Assert.Contains(call.Args, a => a is ArrayBase ab && ab.ArrayName.Contains("__slot"));
    }

    [Fact]
    public void NamedConstInit_ReassignedGlobal_StaysMutable()
    {
        // `state: uint8 = IDLE` (initializer is a NAMED constant) must not register
        // `state` as a constant alias when the module writes it again -- the alias
        // folded every read to the initial value and every write vanished, so a state
        // machine with named states never left state 0. The alias shortcut only
        // applies to names that are never reassigned.
        const string src =
            "IDLE = 0\n" +
            "HEAT = 1\n" +
            "state: uint8 = IDLE\n" +
            "n: uint8 = 0\n" +
            "while n < 10:\n" +
            "    if n > 5:\n" +
            "        state = HEAT\n" +
            "    n += 1\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        var main = ir.Functions.Single(f => f.Name == "main");
        // The write inside the loop must land on the runtime global, not vanish.
        Assert.Contains(main.Body, i =>
            i is Copy { Src: Constant { Value: 1 }, Dst: Variable v } && v.Name.EndsWith("state"));
        // And no write may have been redirected into a constant destination.
        Assert.DoesNotContain(main.Body, i => i is Copy { Dst: Constant });
    }

    [Fact]
    public void FloorDivMod_PowerOfTwo_StrengthReduces()
    {
        // `x // 4` and `x % 8` with power-of-two constants lower to shift/mask --
        // floored semantics make ASR/AND exact for signed operands too -- so a
        // firmware that only divides by powers of two never links the division
        // runtime.
        const string src =
            "def f(x: int16) -> int16:\n" +
            "    return (x // 4) + (x % 8)\n" +
            "def main():\n" +
            "    f(10)\n";
        // Strength reduction lives in the optimizer, which GenerateIR does not run.
        var ir = Optimizer.Optimize(GenerateIR(src, new DeviceConfig { Arch = "avr" }));
        var body = ir.Functions.Single(fn => fn.Name == "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.RShift, Src2: Constant { Value: 2 } });
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 7 } });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.FloorDiv } or Binary { Op: IrBinaryOp.Mod });
    }

    [Fact]
    public void Uint32Literal_AboveInt32Max_Accepted()
    {
        // 4000000000 is a valid uint32 but exceeds int32; the literal arrives as its
        // wrapped 32-bit bit pattern and must pass the range check for uint32.
        const string src =
            "def main():\n" +
            "    g: uint32 = 4000000000\n" +
            "    h: uint32 = 4294967295\n";
        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        var consts = ir.Functions.Single(f => f.Name == "main").Body
            .OfType<Copy>().Select(c => c.Src).OfType<Constant>().ToList();
        Assert.Contains(consts, c => unchecked((uint)c.Value) == 4000000000u);
        Assert.Contains(consts, c => unchecked((uint)c.Value) == 4294967295u);
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
    public void TupleReturnFromRegularFunction_Compiles()
    {
        // A tuple-returning function without @inline is force-inlined at its call
        // sites (the caller's unpack targets become the result slots), so a plain
        // `return a, b` compiles rather than erroring.
        const string src =
            "def minmax(a: uint8, b: uint8):\n" +
            "    return a, b\n" +
            "def main():\n" +
            "    x: uint8 = 0\n";
        GenerateIR(src);
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
    public void AComputedConstTableInitializer_PopulatesFlash()
    {
        // `const[uint8[N]] = [0]*256 + [...] + [0]*40` had the right LENGTH but zeroed
        // contents: only a literal ListExpr populated the bytes, so every read of a
        // computed initializer came back 0. Repeats, concats and range() are all
        // compile-time sequences and must land in the table.
        const string src =
            "T: const[uint8[300]] = [0] * 256 + [11, 22, 33, 44] + [0] * 40\n" +
            "def main():\n" +
            "    y: uint8 = T[257]\n";
        var ir = GenerateIR(src);
        var table = ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
            .Single(t => t.Name.EndsWith("T"));
        Assert.Equal(300, table.Bytes.Count);
        Assert.Equal(11, table.Bytes[256]);
        Assert.Equal(22, table.Bytes[257]);
        Assert.Equal(44, table.Bytes[259]);
        Assert.Equal(0, table.Bytes[299]);
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
    public void ConstParam_RuntimeVariable_RaisesCompileError()
    {
        // Passing a runtime-varying variable where a const[...] parameter is declared
        // used to bind silently as an alias: the callee's compile-time dispatch (a
        // match over the value, a raise CompileError guard) then ran against a value
        // it could not see, and machine.Pin(loop_var) drove one fixed pin - PD0, the
        // Uno's RX - for every iteration, confirmed on real silicon. It must be a
        // located hard error instead.
        const string src =
            "from pymcu.types import uint8, const, inline, ptr\n" +
            "G: ptr[uint8] = ptr(0x3E)\n" +
            "@inline\n" +
            "def picky(n: const[uint8]) -> uint8:\n" +
            "    return n + 1\n" +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    t = picky(seed)\n";
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(src));
        Assert.Contains("requires a compile-time constant", ex.Message);
    }

    [Fact]
    public void ConstParam_ModuleConst_Resolves()
    {
        // The enforcement must not reject the legitimate paths: a module-level
        // const reaches the parameter as a constant, and the inline-expansion
        // alias chain (Watchdog.enable's wdp local) is covered by the AVR
        // integration suite.
        const string src =
            "from pymcu.types import uint8, const, inline\n" +
            "K: const[uint8] = 4\n" +
            "@inline\n" +
            "def picky(n: const[uint8]) -> uint8:\n" +
            "    return n + 1\n" +
            "def main():\n" +
            "    t = picky(K)\n";
        GenerateIR(src);
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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

        var bs = body.OfType<BitSet>().Single();
        Assert.Equal(0, bs.Bit);
        Assert.IsType<Variable>(bs.Target);
        Assert.Equal(DataType.UINT16, ((Variable)bs.Target).Type);
    }

    [Fact]
    public void PtrUint16_LocalVar_BitSet_PreservesType()
    {
        // A *local* variable declared as ptr[uint16] (via AnnAssign) must also carry
        // DataType.UINT16 to the access. Its address is a run-time value, so the bit is
        // changed at the address it holds, 16 bits wide: this used to assert a BitSet on
        // the variable itself, which set a bit of the address (see RuntimePointerBitTests).
        const string src =
            "def f():\n" +
            "    reg: ptr[uint16] = 0\n" +
            "    reg[0] = 1\n";

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

        Assert.DoesNotContain(body, i => i is BitSet { Target: Variable });
        Assert.Contains(body, i => i is LoadIndirect { Elem: DataType.UINT16 });
        Assert.Contains(body, i => i is StoreIndirect { Elem: DataType.UINT16 });
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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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

        var body = GenerateIR(src).Functions.First(f => f.Name == "f").Body;

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
    public void TupleReturnAnnotation_OnNonInline_ForceInlines()
    {
        // A real subroutine has one return register, so the annotation cannot be
        // honoured by a CALL -- the compiler force-inlines the callee instead and
        // the unpack targets receive the element slots directly.
        const string src =
            "def divmod8(a: uint8, b: uint8) -> (uint8, uint8):\n" +
            "    return (a, b)\n" +
            "def main():\n" +
            "    q, r = divmod8(10, 3)\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        Assert.Contains(ir.Functions, f => f.Name == "main");
        Assert.DoesNotContain(ir.Functions, f => f.Name == "divmod8");
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
            i is Copy { Dst: Variable v } && v.Name.Contains("iret_") && v.Name.EndsWith("_1") && v.Type == DataType.UINT16);
        Assert.Contains(body, i =>
            i is Copy { Dst: Variable v } && v.Name.Contains("iret_") && v.Name.EndsWith("_0") && v.Type == DataType.UINT8);
    }

    // ── An undefined name is an error, not a read of whatever the RAM held ──
    // PyMCU#41: the last fallback in ResolveBinding invented a local for any name nothing
    // knew, so a typo compiled into firmware with no diagnostic at any stage.

    [Fact]
    public void UndefinedName_IsRejected_InsteadOfBecomingAnUnwrittenSlot()
    {
        var ex = Assert.Throws<CompilerError>(
            () => GenerateIR("def main():\n    x: uint8 = number + 1\n", new DeviceConfig { Arch = "avr" }));

        Assert.Contains("'number' is not defined", ex.Message);
    }

    [Fact]
    public void NameBoundByAnEarlierStatement_StillResolves()
    {
        var body = GenerateIR("def main():\n    number: uint8 = 7\n    x: uint8 = number + 1\n",
            new DeviceConfig { Arch = "avr" }).Functions.First(f => f.Name == "main").Body;

        Assert.NotNull(body);
    }

    // ── A string converted to a number is parsed, or refused by name ────────
    // PyMCU#61: `s: str = "42"; uint8(s)` folded to a constant zero and the firmware
    // printed 0, with no diagnostic at any stage.

    [Fact]
    public void CompileTimeString_ConvertsToItsNumber()
    {
        var body = GenerateIR("def main():\n    s: str = \"42\"\n    n: uint8 = uint8(s)\n    p: uint8 = n + 0\n",
            new DeviceConfig { Arch = "avr" }).Functions.First(f => f.Name == "main").Body;

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 42 } });
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 0 }, Dst: Variable { Name: "main.n" } });
    }

    [Fact]
    public void StringThatIsNotANumber_IsRejected()
    {
        var ex = Assert.Throws<CompilerError>(
            () => GenerateIR("def main():\n    s: str = \"hola\"\n    n: uint8 = uint8(s)\n",
                new DeviceConfig { Arch = "avr" }));

        Assert.Contains("not a whole number", ex.Message);
    }

    [Fact]
    public void NumberThatDoesNotFitTheCast_IsRejected()
    {
        var ex = Assert.Throws<CompilerError>(
            () => GenerateIR("def main():\n    n: uint8 = uint8(\"300\")\n", new DeviceConfig { Arch = "avr" }));

        Assert.Contains("does not fit", ex.Message);
    }

    // ── A dunder PyMCU never calls says so where it is written ─────────────
    // PyMCU#70: __str__ compiled quietly and then failed at whichever use site the reader
    // reached first, in three different shapes, none of which mentioned __str__.

    private static string CaptureStderr(Action action)
    {
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { action(); }
        finally { Console.SetError(original); }
        return captured.ToString();
    }

    [Fact]
    public void DunderNeverCalled_WarnsAtTheDefinition()
    {
        const string src =
            "class V:\n" +
            "    def __init__(self):\n        self.n: uint8 = 5\n" +
            "    def __str__(self) -> str:\n        return \"V\"\n" +
            "def main():\n    v = V()\n    x: uint8 = v.n\n";

        var warnings = CaptureStderr(() => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("V.__str__", warnings);
        Assert.Contains("never called", warnings);
        Assert.Contains("no run-time string formatting", warnings);
    }

    // ── The construction hooks are refused where they are written ─────────
    // PyMCU#491: both compiled clean and never ran, producing the same firmware as a program
    // without them. Neither the stdlib, nor either compatibility layer, nor the whole AVR
    // example and fixture corpus defines one, so refusing costs nothing.
    [Fact]
    public void NewHook_IsRefusedAtItsDefinition()
    {
        const string src =
            "class C:\n" +
            "    def __new__(cls, n: uint8):\n        return 0\n" +
            "    def __init__(self, n: uint8):\n        self.n: uint8 = n\n" +
            "def main():\n    c = C(5)\n    x: uint8 = c.n\n";

        var ex = Assert.Throws<CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("C.__new__", ex.Message);
        Assert.Contains("never calls it", ex.Message);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void InitSubclassHook_IsRefusedAtItsDefinition()
    {
        const string src =
            "class Base:\n" +
            "    def __init__(self):\n        self.n: uint8 = 0\n" +
            "    def __init_subclass__(cls):\n        return 0\n" +
            "class Child(Base):\n" +
            "    def __init__(self):\n        self.n: uint8 = 1\n" +
            "def main():\n    c = Child()\n    x: uint8 = c.n\n";

        var ex = Assert.Throws<CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("Base.__init_subclass__", ex.Message);
        Assert.Contains("no class object", ex.Message);
        Assert.Equal(4, ex.Line);
    }

    // ── A destructor is dead, and says so for the classes built ───────────
    // PyMCU#491: __del__ was emitted nowhere and nothing was said. It cannot be run -- storage
    // is static, nothing collects an instance, and `del` is refused for the same reason -- so
    // it is reported rather than implemented.
    [Fact]
    public void Destructor_OnAConstructedClass_Warns()
    {
        const string src =
            "class C:\n" +
            "    def __init__(self, n: uint8):\n        self.n: uint8 = n\n" +
            "    def __del__(self):\n        self.n = 0\n" +
            "def main():\n    c = C(5)\n    x: uint8 = c.n\n";

        var warnings = CaptureStderr(() => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("C.__del__", warnings);
        Assert.Contains("never collects one", warnings);
    }

    // And stays quiet for a class the program never builds: a compatibility layer mirrors its
    // upstream's __del__ (machine.Timer does), and every MicroPython-layer program imports that
    // module without constructing one.
    [Fact]
    public void Destructor_OnAClassNeverConstructed_IsNotWarnedAbout()
    {
        const string src =
            "class Unused:\n" +
            "    def __init__(self, n: uint8):\n        self.n: uint8 = n\n" +
            "    def __del__(self):\n        self.n = 0\n" +
            "class C:\n" +
            "    def __init__(self, n: uint8):\n        self.n: uint8 = n\n" +
            "def main():\n    c = C(5)\n    x: uint8 = c.n\n";

        var warnings = CaptureStderr(() => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.DoesNotContain("Unused.__del__", warnings);
    }

    [Fact]
    public void IteratorDunder_GetsTheIteratorReason()
    {
        const string src =
            "class R:\n" +
            "    def __init__(self):\n        self.n: uint8 = 0\n" +
            "    def __iter__(self) -> uint8:\n        return self.n\n" +
            "def main():\n    r = R()\n    x: uint8 = r.n\n";

        var warnings = CaptureStderr(() => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("R.__iter__", warnings);
        Assert.Contains("iterator protocol", warnings);
    }

    // ── The sequence protocol iterates when __len__ is a constant ──────────
    // PyMCU#166: a class with __len__ and __getitem__ is what a table looks like, and
    // `for v in t` met a message listing seven other iterables without mentioning it.
    // CPython's old iteration protocol is __getitem__(0), __getitem__(1), ...; with a
    // constant __len__ the trip count is known, so it unrolls with no IndexError needed.
    [Fact]
    public void SequenceDunder_ForOverGetitem_UnrollsOncePerElement()
    {
        const string src =
            "class Table:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __len__(self) -> uint8:\n" +
            "        return 3\n" +
            "    @inline\n" +
            "    def __getitem__(self, index: uint8) -> uint8:\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = index\n" +
            "        return index\n" +
            "table = Table()\n" +
            "def main():\n" +
            "    for v in table:\n" +
            "        pass\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        var reads = ir.Functions.SelectMany(f => f.Body).OfType<StoreIndirect>().Count();
        Assert.Equal(3, reads);
    }

    [Fact]
    public void SequenceDunder_NonConstantLen_NamesGetitemAndTheTripCount()
    {
        const string src =
            "class Table:\n" +
            "    def __init__(self, n: uint8):\n        self.n: uint8 = n\n" +
            "    def __len__(self) -> uint8:\n        return self.n\n" +
            "    def __getitem__(self, index: uint8) -> uint8:\n        return index\n" +
            "def main():\n" +
            "    GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "    t = Table(GPIOR0.value)\n" +
            "    for v in t:\n        pass\n";

        var err = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("__getitem__", err.Message);
        Assert.Contains("not a compile-time constant", err.Message);
    }

    // The two fallbacks are worded apart on purpose: a class with no __len__ at all must
    // not be told that its __len__ is not constant, which describes a program nobody wrote.
    [Fact]
    public void SequenceDunder_NoLen_SaysNoLenRatherThanNotConstant()
    {
        const string src =
            "class Table:\n" +
            "    def __init__(self, n: uint8):\n        self.n: uint8 = n\n" +
            "    def __getitem__(self, index: uint8) -> uint8:\n        return index\n" +
            "def main():\n" +
            "    t = Table(1)\n" +
            "    for v in t:\n        pass\n";

        var err = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("no __len__", err.Message);
        Assert.DoesNotContain("not a compile-time constant", err.Message);
    }

    // ── A field write outside __init__ has to reach a declared field ───────
    // PyMCU#170: `self.tempreature = raw` compiled to a flattened name of its own, so the
    // field the author meant kept its old value and nothing said so.
    [Fact]
    public void FieldWrite_UndeclaredOutsideInit_NamesTheClassAndTheField()
    {
        const string src =
            "class Sensor:\n" +
            "    def __init__(self, raw: uint8):\n" +
            "        self.temperature: uint8 = raw\n" +
            "        self.humidity: uint8 = 0\n" +
            "    def update(self, raw: uint8):\n" +
            "        self.tempreature = raw\n" +
            "def main():\n" +
            "    s = Sensor(1)\n" +
            "    s.update(2)\n";

        var err = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("Sensor", err.Message);
        Assert.Contains("tempreature", err.Message);
        Assert.Contains("temperature", err.Message);
    }

    // INVARIANT, and the one that matters most. A class assigning its fields inside a `match`
    // in __init__ is the HAL's _PinRegs shape, and classFieldLayout does not know those fields
    // because it only collects top-level assignments. A first attempt at this diagnostic asked
    // the layout anyway and rejected 35 of the AVR suite's fixtures. Scoping to outside __init__
    // is what makes that gap unreachable, so this test is the guard on the scoping, not on the
    // message.
    [Fact]
    public void FieldWrite_InsideInitAtDepth_IsNeverDiagnosed()
    {
        const string src =
            "class Regs:\n" +
            "    def __init__(self, name: uint8):\n" +
            "        match name:\n" +
            "            case 0:\n" +
            "                self.port: uint8 = 1\n" +
            "            case _:\n" +
            "                self.port: uint8 = 2\n" +
            "def main():\n" +
            "    r = Regs(0)\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.NotNull(ir);
    }

    // INVARIANT: a declared field written from an ordinary method is the common case and has to
    // stay silent, or every mutating driver breaks.
    [Fact]
    public void FieldWrite_DeclaredFromAMethod_StillCompiles()
    {
        const string src =
            "class Sensor:\n" +
            "    def __init__(self, raw: uint8):\n" +
            "        self.temperature: uint8 = raw\n" +
            "    def update(self, raw: uint8):\n" +
            "        self.temperature = raw\n" +
            "def main():\n" +
            "    s = Sensor(1)\n" +
            "    s.update(2)\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.NotNull(ir);
    }

    // PyMCU#441: a field first assigned from a property SETTER (adafruit_tcs34725's
    // integration_time.setter sets self._integration_time) used to be invisible to the layout
    // and refused as "not a field". Measured against CPython, MicroPython and CircuitPython
    // (issue #441): a field a setter assigns is exactly as real as one __init__ assigns.
    [Fact]
    public void FieldWrite_FromPropertySetter_BecomesARealField()
    {
        const string src =
            "class Sensor:\n" +
            "    def __init__(self, raw: uint8):\n" +
            "        self._raw: uint8 = raw\n" +
            "    @property\n" +
            "    def offset(self) -> uint8:\n" +
            "        return self._offset\n" +
            "    @offset.setter\n" +
            "    def offset(self, val: uint8):\n" +
            "        self._offset = val\n" +
            "def main():\n" +
            "    s = Sensor(10)\n" +
            "    s.offset = 5\n" +
            "    x: uint8 = s.offset\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.NotNull(ir);
    }

    // PyMCU#441: a field first assigned from a plain method __init__ calls directly
    // (adafruit_motor.servo's __init__ calls set_pulse_width_range, which sets
    // self._min_duty) used to hit the same refusal.
    [Fact]
    public void FieldWrite_FromMethodCalledByInit_BecomesARealField()
    {
        const string src =
            "class Servo:\n" +
            "    def __init__(self, raw: uint8):\n" +
            "        self._raw: uint8 = raw\n" +
            "        self.configure(raw)\n" +
            "    def configure(self, raw: uint8):\n" +
            "        self._min_duty = raw\n" +
            "def main():\n" +
            "    s = Servo(10)\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.NotNull(ir);
    }

    // PyMCU#441: the SAME typo shape FieldWrite_UndeclaredOutsideInit_NamesTheClassAndTheField
    // guards must still be refused when the misspelled write sits in a method that is neither a
    // property setter nor called from __init__ -- extending field discovery to setters and
    // __init__-called helpers must not also reopen the typo hole for everything else.
    [Fact]
    public void FieldWrite_InUnrelatedMethod_StillRefused()
    {
        const string src =
            "class Sensor:\n" +
            "    def __init__(self, raw: uint8):\n" +
            "        self.temperature: uint8 = raw\n" +
            "    def update(self, raw: uint8):\n" +
            "        self.tempreature = raw\n" +
            "def main():\n" +
            "    s = Sensor(1)\n" +
            "    s.update(2)\n";

        var err = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("tempreature", err.Message);
    }

    // PyMCU#441: PyMCU lays each field out at one fixed type, so a field first typed numeric
    // and later assigned a string in a different method is a located compile error -- a design
    // choice, not interpreter fidelity (measured: CPython/MicroPython/CircuitPython allow a
    // field to change type freely across writes).
    [Fact]
    public void FieldWrite_LaterIncompatibleType_RaisesLocatedError()
    {
        const string src =
            "class C:\n" +
            "    def __init__(self):\n" +
            "        self.v: uint8 = 5\n" +
            "    def change(self):\n" +
            "        self.v = 'hello'\n" +
            "def main():\n" +
            "    c = C()\n" +
            "    c.change()\n";

        var err = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("v", err.Message);
        Assert.Contains("numeric", err.Message);
        Assert.Contains("str", err.Message);
    }

    // ── Reflected operators dispatch when the instance is on the right ─────
    // PyMCU#168: only the LEFT operand was consulted, so `2 + a` lowered numerically over the
    // instance slot, which is never written. The answer was 2, and every reflected dunder in
    // the program was dead code with no diagnostic.
    [Fact]
    public void ReflectedDunder_InstanceOnTheRight_Dispatches()
    {
        const string src =
            "class Acc:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "    @inline\n" +
            "    def __radd__(self, other: uint8) -> uint8:\n" +
            "        return other + self.v + 77\n" +
            "def main():\n" +
            "    a = Acc(1)\n" +
            "    x: uint8 = 2 + a\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        // 2 + 1 + 77 folds to 80, and 80 can only come from __radd__'s body. Before the fix
        // the operator lowered over the instance slot and the answer was 2.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Src: Constant { Value: 80 } });
    }

    // The forward dunder must keep winning when the left operand is an instance, or the
    // reflected lookup would change programs that already dispatched.
    [Fact]
    public void ReflectedDunder_ForwardStillWinsOnTheLeft()
    {
        const string src =
            "class Acc:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "    @inline\n" +
            "    def __add__(self, other: uint8) -> uint8:\n" +
            "        return self.v + other + 11\n" +
            "    @inline\n" +
            "    def __radd__(self, other: uint8) -> uint8:\n" +
            "        return other + self.v + 77\n" +
            "def main():\n" +
            "    a = Acc(1)\n" +
            "    x: uint8 = a + 2\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        // __add__ gives 1 + 2 + 11 = 14; __radd__ would have given 2 + 1 + 77 = 80.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Src: Constant { Value: 14 } });
        Assert.DoesNotContain(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Src: Constant { Value: 80 } });
    }

    // ── A module-level instance is a dunder receiver too ──────────────────
    // PyMCU#491: the receiver was looked up under ONE spelling, `currentFunction + "." + name`.
    // At module level currentFunction already reads "main" -- the synthesized module body --
    // while the binding is filed under its bare name, so the lookup missed and the operator
    // lowered numerically over a handle nobody writes. The same two lines inside a function
    // dispatched, which is what made it hard to see.
    [Fact]
    public void Dunder_OnAModuleLevelInstance_Dispatches()
    {
        const string src =
            "class Acc:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "    @inline\n" +
            "    def __add__(self, other: uint8) -> uint8:\n" +
            "        return self.v + other + 11\n" +
            "a = Acc(1)\n" +
            "x: uint8 = a + 2\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        // 1 + 2 + 11 = 14, and 14 can only come from __add__'s body.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Src: Constant { Value: 14 } });
    }

    // ── A comparison used as a CONDITION dispatches its dunder ────────────
    // PyMCU#491: an `if` does not lower its comparison through VisitBinary; it becomes a
    // conditional jump over the flattened instance handles, which are never written. So
    // `if a == b:` compared two zeroed slots and answered "equal" for every pair of
    // instances, and all six comparison dunders were dead in the position readers write
    // them in, while the same comparison assigned to a name dispatched.
    private static bool ComparesTheseHandles(Instruction i, string left, string right)
    {
        (Val S1, Val S2)? pair = i switch
        {
            JumpIfEqual j => (j.Src1, j.Src2),
            JumpIfNotEqual j => (j.Src1, j.Src2),
            JumpIfLessThan j => (j.Src1, j.Src2),
            JumpIfLessOrEqual j => (j.Src1, j.Src2),
            JumpIfGreaterThan j => (j.Src1, j.Src2),
            JumpIfGreaterOrEqual j => (j.Src1, j.Src2),
            _ => null,
        };
        return pair is { } p
               && p.S1 is Variable lv && lv.Name == left
               && p.S2 is Variable rv && rv.Name == right;
    }

    [Theory]
    [InlineData("__eq__", "==")]
    [InlineData("__ne__", "!=")]
    [InlineData("__lt__", "<")]
    [InlineData("__le__", "<=")]
    [InlineData("__gt__", ">")]
    [InlineData("__ge__", ">=")]
    public void ComparisonDunder_InACondition_Dispatches(string dunder, string op)
    {
        string src =
            "seen: uint8 = 0\n" +
            "class Acc:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "    @inline\n" +
            $"    def {dunder}(self, other) -> uint8:\n" +
            "        global seen\n" +
            "        seen = 41\n" +
            "        return 0\n" +
            "def main():\n" +
            "    a = Acc(1)\n" +
            "    b = Acc(2)\n" +
            "    x: uint8 = 3\n" +
            $"    if a {op} b:\n" +
            "        x = 7\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });
        var body = ir.Functions.SelectMany(f => f.Body).ToList();

        // The method body ran: 41 is written nowhere else in the program.
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 41 } });
        // And the raw handles are not what decided the branch.
        Assert.DoesNotContain(body, i => ComparesTheseHandles(i, "main.a", "main.b"));
    }

    // ── Without a dunder, CPython's own fallback decides ──────────────────
    // PyMCU#491: the comparison lowered numerically over the flattened handles, which are
    // never written, so `a == b` answered "equal" for every pair of objects. CPython falls
    // back to identity, and identity is a compile-time fact here: every instance owns a
    // distinct static slot.
    [Fact]
    public void Equality_OfTwoDistinctInstances_IsFalseByIdentity()
    {
        const string src =
            "class C:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "def main():\n" +
            "    a = C(3)\n" +
            "    b = C(3)\n" +
            "    x: uint8 = 3\n" +
            "    if a == b:\n" +
            "        x = 7\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.DoesNotContain(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Src: Constant { Value: 7 } });
    }

    // And a second name for the SAME object still answers true. `b = a` files main.b as an
    // alias whose own key carries no class, so the fold does not claim to know the pair; the
    // comparison keeps the path it had, which copies a into b and compares equal. That is why
    // the fold refuses a NAME on the other side and takes only a literal: answering "different
    // objects" here would be as wrong as the handle comparison it replaces.
    [Fact]
    public void Equality_OfAnInstanceAndItsAlias_IsTrueByIdentity()
    {
        const string src =
            "class C:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "def main():\n" +
            "    a = C(3)\n" +
            "    b = a\n" +
            "    x: uint8 = 3\n" +
            "    if a is b:\n" +
            "        x = 7\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Src: Constant { Value: 7 } });
    }

    // The other side does not have to be an instance: nothing that is not one is ever the same
    // object as one, and CPython raises TypeError for an ordering whatever sits opposite. A
    // LITERAL only, because a name carrying no class here may still be a second spelling of
    // the same object.
    [Fact]
    public void Equality_OfAnInstanceAndAScalar_IsFalseByIdentity()
    {
        const string src =
            "class C:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "def main():\n" +
            "    a = C(7)\n" +
            "    x: uint8 = 3\n" +
            "    if a == 0:\n" +
            "        x = 91\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.DoesNotContain(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Src: Constant { Value: 91 } });
    }

    [Fact]
    public void Ordering_OfAnInstanceAndAScalar_IsRefused()
    {
        const string src =
            "class C:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "def main():\n" +
            "    a = C(7)\n" +
            "    x: uint8 = 3\n" +
            "    if a < 1:\n" +
            "        x = 7\n";

        var ex = Assert.Throws<CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("__lt__", ex.Message);
        Assert.Contains("TypeError", ex.Message);
    }

    // An ordering has no fallback to fold to: CPython raises TypeError, and answering "not
    // less" over two zeroed slots was the silent version of that.
    [Fact]
    public void Ordering_OfTwoInstancesWithoutTheDunder_IsRefused()
    {
        const string src =
            "class C:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "def main():\n" +
            "    a = C(3)\n" +
            "    b = C(4)\n" +
            "    x: uint8 = 3\n" +
            "    if a < b:\n" +
            "        x = 7\n";

        var ex = Assert.Throws<CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("__lt__", ex.Message);
        Assert.Contains("TypeError", ex.Message);
    }

    // max() and min() compare numerically and never consult a class, so an instance argument
    // was read as the flattened handle -- a slot nobody writes -- and `max(a, b).n` printed 0
    // even for a class that defines __lt__ and __gt__ (#491).
    [Theory]
    [InlineData("max")]
    [InlineData("min")]
    public void MinMax_OverInstances_IsRefused(string builtin)
    {
        string src =
            "class C:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "    @inline\n" +
            "    def __lt__(self, other) -> uint8:\n" +
            "        return 1\n" +
            "def main():\n" +
            "    a = C(3)\n" +
            "    b = C(1)\n" +
            $"    m = {builtin}(a, b)\n" +
            "    x: uint8 = m.v\n";

        var ex = Assert.Throws<CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains($"{builtin}()", ex.Message);
        Assert.Contains("does not consult the class", ex.Message);
    }

    // The same reduction spelled over a sequence answers from the same handles.
    [Fact]
    public void MinMax_OverASequenceOfInstances_IsRefused()
    {
        const string src =
            "class C:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "def main():\n" +
            "    a = C(3)\n" +
            "    b = C(1)\n" +
            "    xs = [a, b]\n" +
            "    m = max(xs)\n" +
            "    x: uint8 = m.v\n";

        var ex = Assert.Throws<CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("sequence of 'C' instances", ex.Message);
    }

    // A class-typed FIELD is a receiver too, in both positions. `self.lhs == self.rhs` is the
    // shape a driver writes, and it resolved through neither table, so the operator lowered
    // numerically over the field's flattened slot.
    [Fact]
    public void ComparisonDunder_OnAFieldReceiver_Dispatches()
    {
        const string src =
            "seen: uint8 = 0\n" +
            "class Acc:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "    @inline\n" +
            "    def __eq__(self, other) -> uint8:\n" +
            "        global seen\n" +
            "        seen = 41\n" +
            "        return 0\n" +
            "class Owner:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self.lhs = Acc(1)\n" +
            "        self.rhs = Acc(2)\n" +
            "def main():\n" +
            "    o = Owner()\n" +
            "    x: uint8 = 3\n" +
            "    if o.lhs == o.rhs:\n" +
            "        x = 7\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Src: Constant { Value: 41 } });
    }

    // A comparison between two plain scalars keeps the jump it always had: the dunder lookup
    // must not turn every `if x == y:` in the program into a value plus a truth test.
    [Fact]
    public void Comparison_BetweenScalars_StillLowersAsAJump()
    {
        const string src =
            "def main():\n" +
            "    a: uint8 = 0\n" +
            "    b: uint8 = 0\n" +
            "    for i in range(4):\n" +
            "        a = a + i\n" +
            "        b = b + 1\n" +
            "        if a == b:\n" +
            "            a = 7\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => ComparesTheseHandles(i, "main.a", "main.b"));
    }

    // `a /= 2` was the only augmented assignment that errored, and it named a dunder the class
    // had. AugOp.Div was simply missing from the in-place map.
    [Fact]
    public void AugmentedDiv_DispatchesLikeTheOtherAugmentedOperators()
    {
        const string src =
            "class Acc:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "    @inline\n" +
            "    def __itruediv__(self, other: uint8) -> uint8:\n" +
            "        self.v = self.v + other + 77\n" +
            "        return self.v\n" +
            "def main():\n" +
            "    a = Acc(1)\n" +
            "    a /= 2\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        // 1 + 2 + 77 = 80 stored back into the field. Before the fix this line was the only
        // augmented assignment that errored outright.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Src: Constant { Value: 80 } });
    }

    // The no-dunder message rendered the class name in front of prose ("Acc.an in-place
    // dunder"), which is not a sentence. The qualified form is only correct when a name exists.
    [Fact]
    public void AugmentedAssign_NoDunder_DoesNotGlueTheClassOntoProse()
    {
        const string src =
            "class Acc:\n" +
            "    @inline\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "def main():\n" +
            "    a = Acc(1)\n" +
            "    a /= 2\n";

        var err = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        // Every AugOp now maps to a dunder name, so the qualified form is what a reader gets
        // and the glued prose that read as a lost placeholder is gone.
        Assert.DoesNotContain("Acc.an in-place dunder", err.Message);
        Assert.Contains("Acc.__itruediv__", err.Message);
    }

    // ── A subscript on an instance is not a register bit ───────────────────
    // PyMCU#171: with no __getitem__ the subscript fell through to the bit path, which gave
    // three different wrong answers by index. `a[0]` is the dangerous one: bits 0..7 are legal,
    // so it built clean and answered from an unassigned slot.
    [Fact]
    public void Subscript_InstanceWithoutGetitem_NamesTheClassInsteadOfReadingABit()
    {
        const string src =
            "class Plain:\n" +
            "    def __init__(self, v: uint8, m: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "        self.m: uint8 = m\n" +
            "def main():\n" +
            "    a = Plain(1, 5)\n" +
            "    x: uint8 = a[0]\n";

        var err = Assert.Throws<PyMCU.Common.CompilerError>(
            () => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        // Naming the class and __getitem__ is what distinguishes this from the bit message the
        // same line used to produce; the bit wording must not survive for an instance.
        Assert.Contains("Plain", err.Message);
        Assert.Contains("__getitem__", err.Message);
        Assert.DoesNotContain("Bit index must be constant", err.Message);
    }

    // The bit path is what a subscript is FOR on a register, so it has to stay. This is the
    // guard against fixing #171 by disabling it.
    [Fact]
    public void Subscript_RegisterBit_StillCompiles()
    {
        const string src =
            "def main():\n" +
            "    PINB: ptr[uint8] = ptr(0x23)\n" +
            "    x: uint8 = PINB[3]\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        Assert.Contains(ir.Functions.SelectMany(f => f.Body), i => i is BitCheck);
    }


    [Fact]
    public void SupportedDunder_IsNotWarnedAbout()
    {
        const string src =
            "class A:\n" +
            "    def __init__(self):\n        self.n: uint8 = 1\n" +
            "    def __add__(self, other: uint8) -> uint8:\n        return self.n + other\n" +
            "def main():\n    a = A()\n    x: uint8 = a + 2\n";

        var warnings = CaptureStderr(() => GenerateIR(src, new DeviceConfig { Arch = "avr" }));

        Assert.DoesNotContain("never called", warnings);
    }

    // ── A return is checked against ITS OWN function ───────────────────────
    // PyMCU#48: an @inline returning a string, expanded inside a `-> uint8` function, was
    // reported as "cannot return a string from a function declared to return uint8" -- a
    // mismatch present in neither function, which sent the reader rewriting the wrong thing.

    [Fact]
    public void InlineReturningString_InsideANumericFunction_Compiles()
    {
        const string src =
            "@inline\ndef port_name(k: uint8) -> str:\n" +
            "    if k == 0:\n        return \"PB5\"\n    return \"PB4\"\n" +
            "def get(k: uint8) -> uint8:\n" +
            "    s: str = port_name(k)\n    return 1\n" +
            "def main():\n    x: uint8 = get(0)\n";

        Assert.NotNull(GenerateIR(src, new DeviceConfig { Arch = "avr" }));
    }

    [Fact]
    public void FunctionReturningStringFromItsOwnBody_IsStillRejected()
    {
        var ex = Assert.Throws<CompilerError>(() => GenerateIR(
            "def f() -> uint8:\n    return \"hola\"\ndef main():\n    x: uint8 = f()\n",
            new DeviceConfig { Arch = "avr" }));

        Assert.Contains("cannot return a string", ex.Message);
    }

    // ── RFC 0013 P0: slot-alias-identity ────────────────────────────────────
    // A field that ALIASES an existing instance (`self.bus = bus`, a bare
    // parameter, not `self.bus = Bus(...)`) must share that instance's
    // storage: CPython gives `w.device.bus is bus` True, and a mutation
    // reached through either name is visible through the other. Modeled on
    // adafruit_bus_device.I2CDevice.i2c via Seesaw -- a nested construction
    // (Wrapper -> Device, a real `Device(bus, ...)` call) whose own
    // constructor ALIASES a further-out instance rather than building one.
    //
    // Before the fix, `self.bus = bus` fell into the "field assigned None"
    // bucket: `VisitExpression` on a bare reference to a non-foldable,
    // multi-field instance has no scalar Val to return, so the compiled
    // value was the same NoneVal placeholder a void `__init__` hands back
    // (RFC 0006 section 0), and IsNoneValued's alias chase found `bus`
    // itself marked None-valued by that SAME placeholder mechanism
    // (EmitScalarVarAssign, `bus = make_bus(1)`'s own construction, forwarded
    // through a plain function). The write returned without registering any
    // alias, and a later access through the field (`self.bus.try_lock()`)
    // resolved its receiver by NAME alone, minting a second, disjoint
    // `_locked` storage instead of reaching `bus`'s own -- except this
    // particular shape (a method call three hops in) does not even get that
    // far: without the fix, this exact source fails to compile at all,
    // `PyMCU.Common.CompilerError: call to undefined function
    // 'w_device_bus_try_lock' (typo, or a missing import?)` -- the minted
    // name has no body, because the method IS defined, just under the
    // canonical instance's own mangled name. Measured directly (reverting
    // just the Assign.cs/Expr.cs half of this commit locally and rerunning
    // this test) before writing this comment.
    [Fact]
    public void AliasedFieldThroughThreeLevels_MutatorSharesIdentityWithOriginal()
    {
        const string src =
            "class Bus:\n" +
            "    def __init__(self, seed: uint8):\n" +
            "        self.locked: uint8 = 0\n" +
            "    def try_lock(self) -> uint8:\n" +
            "        if self.locked:\n" +
            "            return 0\n" +
            "        self.locked = 1\n" +
            "        return 1\n" +
            "class Device:\n" +
            "    def __init__(self, bus, addr: uint8):\n" +
            "        self.bus = bus\n" +
            "        self.addr = addr\n" +
            "    def use(self) -> uint8:\n" +
            "        return self.bus.try_lock()\n" +
            "class Wrapper:\n" +
            "    def __init__(self, bus, extra: uint8):\n" +
            "        self.device = Device(bus, 0)\n" +
            "        self.extra = extra\n" +
            "    def run(self) -> uint8:\n" +
            "        return self.device.use()\n" +
            // A plain FUNCTION wrapping the constructor, not a direct `Bus(1)` --
            // the shape `board.I2C()` has (busio/board.py wraps `busio.I2C(...)`).
            // A direct class call is exempted from the "constructor's void
            // __init__ hands back None" marking by its own CallExpr guard
            // (EmitScalarVarAssign); a function that merely FORWARDS that same
            // constructor's placeholder return is not -- the fixture's actual
            // failure needs the wrapper, a bare `bus = Bus(1)` does not
            // reproduce it.
            "def make_bus(seed: uint8):\n" +
            "    return Bus(seed)\n" +
            "def main():\n" +
            "    bus = make_bus(1)\n" +
            "    w = Wrapper(bus, 9)\n" +
            "    r: uint8 = w.run()\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        static IEnumerable<string?> DstNames(Instruction i) => i switch
        {
            Copy c => new[] { (c.Dst as Variable)?.Name, (c.Dst as Temporary)?.Name },
            ArrayStore a => new[] { a.ArrayName },
            _ => Array.Empty<string?>(),
        };

        var lockedTargets = ir.Functions
            .SelectMany(f => f.Body)
            .SelectMany(DstNames)
            .Where(n => n != null && n.EndsWith("locked", StringComparison.Ordinal))
            .Select(n => n!)
            .Distinct()
            .ToList();

        // Exactly one storage location backs `locked`, whichever spelling the
        // compiler chose for it (`bus_locked`, `main.bus_locked`, ...) -- the
        // point is there is only ONE, not that a call through `w.run()` and a
        // direct `bus.try_lock()` would each mint their own.
        Assert.True(lockedTargets.Count <= 1,
            "expected a single shared storage for the aliased field's mutator "
            + $"target, found: {string.Join(", ", lockedTargets)}");
    }

    // Same identity requirement as the mutator-method test above, for a bare
    // field write and a bare field read through the alias chain -- no method
    // call, so this exercises VisitMemberAccess's own flattened-name chase
    // (Expr.cs) independently of the call-receiver resolution
    // (AnchorNameOf / EmitInlineFunctionCall) the mutator test exercises.
    // `bus.locked = 1` writes through the CANONICAL name; `w.device.bus.locked`
    // reads through the field three hops in. CPython: `w.device.bus is bus`,
    // so the read sees the write.
    [Fact]
    public void AliasedFieldThroughThreeLevels_ReadAndWriteAgreeOnOneStorage()
    {
        const string src =
            "class Bus:\n" +
            "    def __init__(self, seed: uint8):\n" +
            "        self.locked: uint8 = 0\n" +
            "class Device:\n" +
            "    def __init__(self, bus, addr: uint8):\n" +
            "        self.bus = bus\n" +
            "        self.addr = addr\n" +
            "class Wrapper:\n" +
            "    def __init__(self, bus, extra: uint8):\n" +
            "        self.device = Device(bus, 0)\n" +
            "        self.extra = extra\n" +
            "def make_bus(seed: uint8):\n" +
            "    return Bus(seed)\n" +
            "def main():\n" +
            "    bus = make_bus(1)\n" +
            "    w = Wrapper(bus, 9)\n" +
            "    bus.locked = 1\n" +
            "    r: uint8 = w.device.bus.locked\n";

        var ir = GenerateIR(src, new DeviceConfig { Arch = "avr" });

        var body = ir.Functions.SelectMany(f => f.Body).ToList();

        static string? NameOf(Val v) => v switch
        {
            Variable vv => vv.Name,
            Temporary t => t.Name,
            _ => null,
        };

        var writeTargets = body
            .OfType<Copy>()
            .Where(c => c.Src is Constant { Value: 1 })
            .Select(c => NameOf(c.Dst))
            .Where(n => n != null && n.EndsWith("locked", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        var readSources = body
            .OfType<Copy>()
            .Select(c => NameOf(c.Src))
            .Where(n => n != null && n.EndsWith("locked", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        Assert.Single(writeTargets);
        Assert.Contains(writeTargets[0], readSources);
    }
}
