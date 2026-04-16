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
    public void RuntimeBitIndex_Throws()
    {
        // Using a runtime variable as bit index on a non-array must fail at
        // compile time -- bit indices must be compile-time constants.
        const string src =
            "def f(port: ptr[uint8], bit: uint8):\n" +
            "    port[bit] = 1\n";

        Assert.ThrowsAny<Exception>(() => GenerateIR(src));
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
}
