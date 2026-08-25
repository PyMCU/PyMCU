using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;
using IrUnaryOp = PyMCU.IR.UnaryOp;

namespace PyMCU.UnitTests;

public class OptimizerTests
{
    private static ProgramIR GenerateAndOptimize(string source)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var ast = parser.ParseProgram();
        var irGen = new IRGenerator();
        var ir = irGen.Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
        return Optimizer.Optimize(ir);
    }

    private static ProgramIR MakeProgram(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        return prog;
    }

    // ─── Constant Folding ──────────────────────────────────────────────────

    [Fact]
    public void ConstantFoldingBinary()
    {
        var optimized = GenerateAndOptimize("def main():\n    return 2 + 3 * 4");
        var body = optimized.Functions[0].Body;

        Assert.DoesNotContain(body, i => i is Binary);

        var ret = body.OfType<Return>().First();
        var c = Assert.IsType<Constant>(ret.Value);
        Assert.Equal(14, c.Value);
    }

    [Fact]
    public void CopyPropagation_DoesNotForwardThroughFloatToIntCast()
    {
        // uint32(float_var) emits Copy(FLOAT -> UINT32 temp). FLOAT and UINT32 are
        // both 4 bytes, so size/signedness checks alone let copy propagation forward
        // the float source through the cast: the conversion vanished and the consumer
        // received raw float bits (uint32(3.25) printed 16464 on a real Uno).
        var optimized = GenerateAndOptimize(
            "def main():\n" +
            "    x: float = 3.25\n" +
            "    c: uint32 = uint32(x)\n" +
            "    return c");
        var body = optimized.Functions[0].Body;

        var ret = body.OfType<Return>().First();
        var retType = ret.Value switch
        {
            Variable v => v.Type,
            Temporary t => t.Type,
            _ => DataType.UNKNOWN
        };
        Assert.NotEqual(DataType.FLOAT, retType);
        Assert.Contains(body, i =>
            i is Copy c && c.Src is Variable { Type: DataType.FLOAT }
            && (c.Dst is Variable { Type: DataType.UINT32 } or Temporary { Type: DataType.UINT32 }));
    }

    [Fact]
    public void InlineParam_ShadowsSameNamedModuleGlobal()
    {
        // A user global named like a library @inline's parameter must not hijack the
        // body's reads of that parameter: `data = 5` at module level made
        // uart.write('hello') error claiming 'data' varies at runtime.
        var optimized = GenerateAndOptimize(
            "@inline\n" +
            "def emit(data: const[str]) -> uint8:\n" +
            "    return len(data)\n" +
            "data = 5\n" +
            "def main():\n" +
            "    x: uint8 = emit(\"hola\")\n" +
            "    return x");
        var ret = optimized.Functions[0].Body.OfType<Return>().First();
        var c = Assert.IsType<Constant>(ret.Value);
        Assert.Equal(4, c.Value);
    }

    [Fact]
    public void UnannotatedModuleGlobal_WidensToInlineCallResultType()
    {
        // ScanGlobals cannot type `f0 = get()` and registered it uint8; the store
        // then wrapped 1000 to 232. The first top-level assignment must widen the
        // global to the call's declared return type.
        var optimized = GenerateAndOptimize(
            "@inline\n" +
            "def get() -> uint16:\n" +
            "    return 1000\n" +
            "f0 = get()\n" +
            "def main():\n" +
            "    return f0");
        var body = optimized.Functions[0].Body;
        Assert.Contains(body, i =>
            i is Copy { Src: Constant { Value: 1000 }, Dst: Variable { Type: DataType.UINT16 } });
    }

    [Fact]
    public void DeadCodeElimination()
    {
        // Unused temporary `a = 1 + 2` — after DCE no Binary should remain.
        var optimized = GenerateAndOptimize("def main():\n    a = 1 + 2\n    return 42");

        Assert.DoesNotContain(optimized.Functions[0].Body, i => i is Binary);
    }

    [Fact]
    public void UnusedExpressionDCE()
    {
        // `1 + 2` as a statement with no consumer — the Binary must be eliminated.
        var optimized = GenerateAndOptimize("def main():\n    1 + 2\n    return 42");

        Assert.DoesNotContain(optimized.Functions[0].Body, i => i is Binary);
    }

    [Fact]
    public void RedundantArrayLoad_SameIndex_IsEliminated()
    {
        // Two reads of arr[i] with no intervening write collapse to a single load (CSE).
        var prog = GenerateAndOptimize(
            "arr: uint8[8] = [0,0,0,0,0,0,0,0]\n" +
            "out: uint8 = 0\n" +
            "def access(i: uint8):\n" +
            "    global out\n" +
            "    out = arr[i] + arr[i]\n" +
            "def main():\n" +
            "    access(3)\n");
        var access = prog.Functions.First(f => f.Name.EndsWith("access"));
        // The two arr[i] reads must collapse to one ArrayLoad.
        Assert.Equal(1, access.Body.OfType<ArrayLoad>().Count(a => a.ArrayName == "arr"));
    }

    [Fact]
    public void ArrayLoad_AcrossStore_IsNotEliminated()
    {
        // A store to the same array between two reads of arr[i] must invalidate the cache:
        // the second read sees the new value, so both loads must survive.
        var prog = GenerateAndOptimize(
            "arr: uint8[8] = [0,0,0,0,0,0,0,0]\n" +
            "total: uint16 = 0\n" +
            "def upd(i: uint8, v: uint8):\n" +
            "    global total\n" +
            "    total = total - arr[i]\n" +
            "    arr[i] = v\n" +
            "    total = total + arr[i]\n" +
            "def main():\n" +
            "    upd(2, 50)\n");
        var upd = prog.Functions.First(f => f.Name.EndsWith("upd"));
        Assert.Equal(2, upd.Body.OfType<ArrayLoad>().Count(a => a.ArrayName == "arr"));
    }

    [Fact]
    public void DivByConstFoldedZeroVariable_RaisesValueError()
    {
        // End-to-end: `z: uint8 = 0; out = 10 // z`. z is a Variable at IR-gen time (the
        // front-end guard can't see it); constant propagation folds the divisor to 0, which
        // the optimizer must reject rather than leave as a runtime divide-by-zero.
        Assert.Throws<ValueError>(() => GenerateAndOptimize(
            "out: uint8 = 0\n" +
            "def main():\n" +
            "    global out\n" +
            "    z: uint8 = 0\n" +
            "    out = 10 // z\n"));
    }

    // ─── Copy Propagation (via Optimize) ──────────────────────────────────

    [Fact]
    public void CopyPropagation()
    {
        // x = param; t1 = x; return t1
        // After propagation the Return should use `x` (or an earlier equivalent),
        // not `t1`.
        var prog = MakeProgram(
            new Copy(new Variable("param"), new Variable("x")),
            new Copy(new Variable("x"), new Temporary("t1")),
            new Return(new Temporary("t1"))
        );

        var optimized = Optimizer.Optimize(prog);
        var body = optimized.Functions[0].Body;
        var ret = body.OfType<Return>().First();

        // After propagation: Return should not use t1 anymore.
        Assert.False(ret.Value is Temporary { Name: "t1" },
            "t1 should have been propagated away");
    }

    // ─── Instruction Coalescing (via Optimize) ─────────────────────────────

    [Fact]
    public void InstructionCoalescing()
    {
        // t1 = a + b; x = t1
        // After coalescing: x = a + b (the Binary dst is rewritten to x, Copy eliminated).
        var prog = MakeProgram(
            new Binary(IrBinaryOp.Add, new Variable("a"), new Variable("b"), new Temporary("t1")),
            new Copy(new Temporary("t1"), new Variable("x")),
            new Return(new Variable("x"))
        );

        var optimized = Optimizer.Optimize(prog);
        var body = optimized.Functions[0].Body;

        // The Copy `x = t1` must be gone (coalesced into the Binary's dst).
        Assert.DoesNotContain(body, i =>
            i is Copy { Dst: Variable { Name: "x" }, Src: Temporary { Name: "t1" } });

        // The Binary (or a folded Copy) must target x.
        var assignsToX = body.Any(i =>
            i is Binary { Dst: Variable { Name: "x" } } ||
            i is Copy { Dst: Variable { Name: "x" } });
        Assert.True(assignsToX, "Result of addition must land in x");
    }

    // ─── Full Optimization Chain ────────────────────────────────────────────

    [Fact]
    public void FullOptimizationChain()
    {
        // t1 = 10; t2 = 20; t3 = t1 + t2; res = t3; return res
        // After constant folding + coalescing + DCE: res = 30; return res
        var prog = MakeProgram(
            new Copy(new Constant(10), new Temporary("t1")),
            new Copy(new Constant(20), new Temporary("t2")),
            new Binary(IrBinaryOp.Add, new Temporary("t1"), new Temporary("t2"), new Temporary("t3")),
            new Copy(new Temporary("t3"), new Variable("res")),
            new Return(new Variable("res"))
        );

        var optimized = Optimizer.Optimize(prog);
        var body = optimized.Functions[0].Body;

        // The temporaries t1, t2, t3 should all be gone.
        Assert.DoesNotContain(body, i => i is Binary);

        // Somewhere `res` must receive the value 30 (or Return directly returns 30).
        var foundRes30 = body.Any(i =>
            i is Copy { Src: Constant { Value: 30 }, Dst: Variable { Name: "res" } });
        var foundReturn30 = body.OfType<Return>().Any(r => r.Value is Constant { Value: 30 });

        Assert.True(foundRes30 || foundReturn30,
            "res must be assigned 30, or Return must carry the constant 30 directly");
    }

    // ─── Power-of-two rewrites are integer-only ────────────────────────────

    private static DataType TypeOf(Val v) => v switch
    {
        Variable x => x.Type,
        Temporary t => t.Type,
        _ => DataType.UNKNOWN,
    };

    private static bool TouchesFloat(Binary b) =>
        TypeOf(b.Src1) == DataType.FLOAT
        || TypeOf(b.Src2) == DataType.FLOAT
        || TypeOf(b.Dst) == DataType.FLOAT;

    [Fact]
    public void PowerOfTwoStrengthReduction_SkipsFloats()
    {
        // x * 2^n -> x << n and x // 2^n -> x >> n are integer identities and nothing
        // more: a float's bit pattern is not its magnitude. Applied to a float they
        // reached AvrCodeGen as a float LShift/RShift, which it refuses outright
        // (pymcu-avr#5). The constant is an integer literal, so the rewrite fired
        // whichever side the float was on.
        var optimized = GenerateAndOptimize(
            "def scale(x: float) -> float:\n" +
            "    return x * 2\n" +
            "\n" +
            "def half(x: float) -> float:\n" +
            "    return x // 2\n");

        foreach (var func in optimized.Functions)
            Assert.DoesNotContain(func.Body, i =>
                i is Binary { Op: IrBinaryOp.LShift or IrBinaryOp.RShift or IrBinaryOp.BitAnd } b
                && TouchesFloat(b));
    }

    [Fact]
    public void FlooredDivisionByOne_IsNotAnIdentityOnAFloat()
    {
        // x // 1 -> x is true of integers and false of floats: 3.5 // 1 is 3.0, not 3.5.
        // The rewrite dropped the operation entirely, so the wrong number reached the port
        // with no diagnostic (PyMCU#128). The division has to survive optimization.
        var optimized = GenerateAndOptimize(
            "def floor_it(x: float) -> float:\n" +
            "    return x // 1\n");

        var body = optimized.Functions.Single(f => f.Name.EndsWith("floor_it")).Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.FloorDiv } b && TouchesFloat(b));
    }

    [Fact]
    public void PowerOfTwoStrengthReduction_StillFiresOnIntegers()
    {
        // The guard above must not disable the rewrite it protects: this is why the pass
        // exists, and hi * 256 becoming a byte placement is what pays for it.
        var optimized = GenerateAndOptimize(
            "def pack(hi: uint8, lo: uint8) -> uint16:\n" +
            "    return hi * 256 + lo\n");

        var body = optimized.Functions.Single(f => f.Name.EndsWith("pack")).Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.LShift });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.Mul });
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Per-pass tests using FluentAssertions
// ─────────────────────────────────────────────────────────────────────────────

public class OptimizerPassTests
{
    private static ProgramIR MakeProgram(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        return prog;
    }

    private static ProgramIR MakeProgramWithFunctions(params Function[] funcs)
    {
        var prog = new ProgramIR();
        prog.Functions.AddRange(funcs);
        return prog;
    }

    private static List<Instruction> Optimize(params Instruction[] body)
        => Optimizer.Optimize(MakeProgram(body)).Functions[0].Body;

    // ─── FoldConstants — Unary ───────────────────────────────────────────────

    [Fact]
    public void FoldConstants_Unary_Neg_ProducesNegatedConstant()
    {
        // The default Temporary is UINT8, so the folded constant is wrapped to that
        // destination type: -5 as a uint8 is 251 (matches the runtime result).
        var body = Optimize(
            new Unary(IrUnaryOp.Neg, new Constant(5), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Return>().First().Value
            .Should().Be(new Constant(unchecked((byte)-5)));   // 251
    }

    [Fact]
    public void FoldConstants_Unary_Not_Zero_ProducesOne()
    {
        var body = Optimize(
            new Unary(IrUnaryOp.Not, new Constant(0), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Return>().First().Value
            .Should().Be(new Constant(1));
    }

    [Fact]
    public void FoldConstants_Unary_Not_NonZero_ProducesZero()
    {
        var body = Optimize(
            new Unary(IrUnaryOp.Not, new Constant(42), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Return>().First().Value
            .Should().Be(new Constant(0));
    }

    [Fact]
    public void FoldConstants_Unary_BitNot_ProducesFlippedBits()
    {
        // UINT8 destination -> the folded ~5 is wrapped to a uint8: (byte)~5 == 250.
        var body = Optimize(
            new Unary(IrUnaryOp.BitNot, new Constant(5), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Return>().First().Value
            .Should().Be(new Constant(unchecked((byte)~5)));   // 250
    }

    // ─── FoldConstants — Binary edge cases ───────────────────────────────────

    [Fact]
    public void FoldConstants_DivByZero_RaisesValueError()
    {
        // A divisor that constant propagation proves to be zero is a guaranteed fault.
        // Previously the optimizer left it as a runtime Binary with a const-0 divisor
        // (silent miscompile); it now raises a clean ValueError, catching zeros that only
        // become visible after folding (e.g. `z = 0; x // z`), not just literal `x / 0`.
        Assert.Throws<ValueError>(() => Optimize(
            new Binary(IrBinaryOp.Div, new Constant(10), new Constant(0), new Temporary("t1")),
            new Return(new Temporary("t1"))));
    }

    [Fact]
    public void FoldConstants_Equal_TrueCase_ProducesOne()
    {
        var body = Optimize(
            new Binary(IrBinaryOp.Equal, new Constant(3), new Constant(3), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Return>().First().Value.Should().Be(new Constant(1));
    }

    [Fact]
    public void FoldConstants_LessThan_ProducesCorrectResult()
    {
        var body = Optimize(
            new Binary(IrBinaryOp.LessThan, new Constant(2), new Constant(5), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Return>().First().Value.Should().Be(new Constant(1));
    }

    [Fact]
    public void FoldConstants_BitAnd_ProducesCorrectResult()
    {
        var body = Optimize(
            new Binary(IrBinaryOp.BitAnd, new Constant(0b1100), new Constant(0b1010), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Return>().First().Value.Should().Be(new Constant(0b1000));
    }

    // ─── CollapseBoolJumps ────────────────────────────────────────────────────

    [Fact]
    public void CollapseBoolJumps_Equal_WithJumpIfZero_BecomesJumpIfNotEqual()
    {
        var a = new Variable("a");
        var b = new Variable("b");
        var t = new Temporary("t1");
        var body = Optimize(
            new Binary(IrBinaryOp.Equal, a, b, t),
            new JumpIfZero(t, "end"),
            new Return(new Constant(1)),
            new Label("end"),
            new Return(new Constant(0)));

        body.OfType<JumpIfNotEqual>().Should().ContainSingle(j => j.Target == "end");
        body.OfType<JumpIfZero>().Should().BeEmpty();
    }

    [Fact]
    public void CollapseBoolJumps_Equal_WithJumpIfNotZero_BecomesJumpIfEqual()
    {
        var a = new Variable("a");
        var b = new Variable("b");
        var t = new Temporary("t1");
        var body = Optimize(
            new Binary(IrBinaryOp.Equal, a, b, t),
            new JumpIfNotZero(t, "hit"),
            new Return(new Constant(0)),
            new Label("hit"),
            new Return(new Constant(1)));

        body.OfType<JumpIfEqual>().Should().ContainSingle(j => j.Target == "hit");
        body.OfType<JumpIfNotZero>().Should().BeEmpty();
    }

    [Fact]
    public void CollapseBoolJumps_LessThan_WithJumpIfZero_BecomesJumpIfGreaterOrEqual()
    {
        var a = new Variable("a");
        var b = new Variable("b");
        var t = new Temporary("t1");
        var body = Optimize(
            new Binary(IrBinaryOp.LessThan, a, b, t),
            new JumpIfZero(t, "end"),
            new Return(new Constant(1)),
            new Label("end"),
            new Return(new Constant(0)));

        body.OfType<JumpIfGreaterOrEqual>().Should().ContainSingle(j => j.Target == "end");
    }

    [Fact]
    public void CollapseBoolJumps_GreaterThan_WithJumpIfNotZero_BecomesJumpIfGreaterThan()
    {
        var a = new Variable("a");
        var b = new Variable("b");
        var t = new Temporary("t1");
        var body = Optimize(
            new Binary(IrBinaryOp.GreaterThan, a, b, t),
            new JumpIfNotZero(t, "hit"),
            new Return(new Constant(0)),
            new Label("hit"),
            new Return(new Constant(1)));

        body.OfType<JumpIfGreaterThan>().Should().ContainSingle(j => j.Target == "hit");
    }

    // ─── CollapseBitChecks ────────────────────────────────────────────────────

    [Fact]
    public void CollapseBitChecks_JumpIfEqual1_BecomesJumpIfBitSet()
    {
        var src = new Variable("port");
        var t = new Temporary("t1");
        var body = Optimize(
            new BitCheck(src, 3, t),
            new JumpIfEqual(t, new Constant(1), "set"),
            new Return(new Constant(0)),
            new Label("set"),
            new Return(new Constant(1)));

        body.OfType<JumpIfBitSet>().Should().ContainSingle(j => j.Bit == 3 && j.Target == "set");
        body.OfType<JumpIfEqual>().Should().BeEmpty();
    }

    [Fact]
    public void CollapseBitChecks_JumpIfEqual0_BecomesJumpIfBitClear()
    {
        var src = new Variable("port");
        var t = new Temporary("t1");
        var body = Optimize(
            new BitCheck(src, 2, t),
            new JumpIfEqual(t, new Constant(0), "clear"),
            new Return(new Constant(1)),
            new Label("clear"),
            new Return(new Constant(0)));

        body.OfType<JumpIfBitClear>().Should().ContainSingle(j => j.Bit == 2 && j.Target == "clear");
    }

    [Fact]
    public void CollapseBitChecks_JumpIfNotEqual0_BecomesJumpIfBitSet()
    {
        var src = new Variable("port");
        var t = new Temporary("t1");
        var body = Optimize(
            new BitCheck(src, 1, t),
            new JumpIfNotEqual(t, new Constant(0), "set"),
            new Return(new Constant(0)),
            new Label("set"),
            new Return(new Constant(1)));

        body.OfType<JumpIfBitSet>().Should().ContainSingle(j => j.Bit == 1 && j.Target == "set");
    }

    [Fact]
    public void CollapseBitChecks_JumpIfNotEqual1_BecomesJumpIfBitClear()
    {
        var src = new Variable("port");
        var t = new Temporary("t1");
        var body = Optimize(
            new BitCheck(src, 0, t),
            new JumpIfNotEqual(t, new Constant(1), "clear"),
            new Return(new Constant(1)),
            new Label("clear"),
            new Return(new Constant(0)));

        body.OfType<JumpIfBitClear>().Should().ContainSingle(j => j.Bit == 0 && j.Target == "clear");
    }

    // ─── Dead Function Elimination ────────────────────────────────────────────

    [Fact]
    public void DFE_RemovesUnreachableFunction()
    {
        var prog = MakeProgramWithFunctions(
            new Function { Name = "main",  Body = [new Return(new Constant(0))] },
            new Function { Name = "unused", Body = [new Return(new Constant(1))] });

        var optimized = Optimizer.Optimize(prog);
        optimized.Functions.Should().NotContain(f => f.Name == "unused");
    }

    [Fact]
    public void DFE_KeepsTransitivelyCalledFunction()
    {
        var prog = MakeProgramWithFunctions(
            new Function
            {
                Name = "main",
                Body = [new Call("helper", [], new Temporary("r")), new Return(new Constant(0))]
            },
            new Function { Name = "helper", Body = [new Return(new Constant(1))] });

        var optimized = Optimizer.Optimize(prog);
        optimized.Functions.Should().ContainSingle(f => f.Name == "helper");
    }

    [Fact]
    public void DFE_KeepsISR_EvenIfNotCalledFromMain()
    {
        var prog = MakeProgramWithFunctions(
            new Function { Name = "main",    Body = [new Return(new Constant(0))] },
            new Function { Name = "isr_tim", Body = [new Return(new Constant(0))], IsInterrupt = true });

        var optimized = Optimizer.Optimize(prog);
        optimized.Functions.Should().ContainSingle(f => f.Name == "isr_tim");
    }

    [Fact]
    public void DFE_RemovesDeadChain_KeepsReachableChain()
    {
        var prog = MakeProgramWithFunctions(
            new Function
            {
                Name = "main",
                Body = [new Call("a", [], new Temporary("r")), new Return(new Constant(0))]
            },
            new Function
            {
                Name = "a",
                Body = [new Call("b", [], new Temporary("r")), new Return(new Constant(0))]
            },
            new Function { Name = "b",    Body = [new Return(new Constant(0))] },
            new Function { Name = "dead", Body = [new Return(new Constant(0))] });

        var optimized = Optimizer.Optimize(prog);
        optimized.Functions.Select(f => f.Name)
            .Should().BeEquivalentTo(["main", "a", "b"]);
    }

    // ─── Dead Variable Store Elimination ─────────────────────────────────────

    [Fact]
    public void DVSE_RemovesCopyToVariable_WhenNeverRead()
    {
        // main.x = 5, but x is never read — the Copy must be eliminated.
        var body = Optimize(
            new Copy(new Constant(5), new Variable("main.x", DataType.UINT8)),
            new Return(new Constant(0)));

        Assert.DoesNotContain(body, i =>
            i is Copy { Dst: Variable { Name: "main.x" } });
    }

    [Fact]
    public void DVSE_KeepsCopyToVariable_WhenRead()
    {
        // main.x = 5, then return x — the assignment (or its propagated constant) must survive.
        var body = Optimize(
            new Copy(new Constant(5), new Variable("main.x", DataType.INT16)),
            new Return(new Variable("main.x", DataType.INT16)));

        var hasCopyOrPropagated =
            body.Any(i => i is Copy { Dst: Variable { Name: "main.x" } }) ||
            body.OfType<Return>().Any(r => r.Value is Constant { Value: 5 });
        Assert.True(hasCopyOrPropagated,
            "live write to main.x (or propagated constant) must survive");
    }

    [Fact]
    public void DVSE_DoesNotRemoveMemoryAddressWrite()
    {
        // Writes to MemoryAddress (MMIO) must never be eliminated.
        var body = Optimize(
            new Copy(new Constant(1), new MemoryAddress(0x25)),
            new Return(new Constant(0)));

        Assert.Contains(body, i => i is Copy { Dst: MemoryAddress { Address: 0x25 } });
    }

    // ─── Outlining of @inline expansions ─────────────────────────────────────

    private const string Tag = Optimizer.InlineMarkerTag + "hexify";

    // Four copies of one expansion, each reading a fresh value out of a port and
    // running `body` over it. `body` receives the site's input variable and the
    // label suffix it must use, so every copy is structurally identical but has
    // its own label names — exactly what the inliner produces.
    private static ProgramIR FourSites(Func<Variable, string, Instruction[]> body)
    {
        var instrs = new List<Instruction>();
        for (int s = 0; s < 4; s++)
        {
            var input = new Variable($"main.v{s}", DataType.UINT8);
            instrs.Add(new Copy(new MemoryAddress(0xC0), input));   // not const-foldable
            instrs.Add(new InlineExpansionMarker(Tag, false));
            instrs.AddRange(body(input, $"_s{s}"));
            instrs.Add(new InlineExpansionMarker(Tag, true));
        }
        instrs.Add(new Return(new NoneVal()));
        return MakeProgram(instrs.ToArray());
    }

    private static List<Function> OutlinedFunctions(ProgramIR prog) =>
        Optimizer.Optimize(prog).Functions
            .Where(f => f.Name.StartsWith("__pymcu_outline_")).ToList();

    [Fact]
    public void Outline_RegionWithInternalBranch_IsShared()
    {
        // if v < 10: PORT = 48 else: PORT = 65 — an if/else wholly inside the region.
        var prog = FourSites((v, sfx) =>
        [
            new JumpIfLessThan(v, new Constant(10), "low" + sfx),
            new Copy(new Constant(65), new MemoryAddress(0x100)),
            new Jump("done" + sfx),
            new Label("low" + sfx),
            new Copy(new Constant(48), new MemoryAddress(0x100)),
            new Label("done" + sfx),
        ]);

        var outlined = OutlinedFunctions(prog);
        outlined.Should().ContainSingle("the four identical branchy copies collapse into one body");
        outlined[0].Body.Should().Contain(i => i is Label,
            "the branch targets move into the subroutine with it");
        outlined[0].Params.Should().ContainSingle("the value being tested is the only live-in");
    }

    [Fact]
    public void Outline_RejectsJumpThatLeavesTheRegion()
    {
        // Same shape, but the "done" label sits after the region end: outlining
        // would cut a control-flow edge in half.
        var instrs = new List<Instruction>();
        for (int s = 0; s < 4; s++)
        {
            var v = new Variable($"main.v{s}", DataType.UINT8);
            instrs.Add(new Copy(new MemoryAddress(0xC0), v));
            instrs.Add(new InlineExpansionMarker(Tag, false));
            instrs.Add(new JumpIfLessThan(v, new Constant(10), $"out_s{s}"));
            instrs.Add(new Copy(new Constant(65), new MemoryAddress(0x100)));
            instrs.Add(new InlineExpansionMarker(Tag, true));
            instrs.Add(new Label($"out_s{s}"));
        }
        instrs.Add(new Return(new NoneVal()));

        OutlinedFunctions(MakeProgram(instrs.ToArray())).Should().BeEmpty();
    }

    [Fact]
    public void Outline_RejectsLoopThatDoesWorkPerIteration()
    {
        // A loop that counts while polling is calibrated timing (bit-banged
        // protocols, pulse widths); only an empty "wait for the hardware" spin
        // may be moved behind a CALL.
        var prog = FourSites((v, sfx) =>
        {
            var n = new Variable("main.n" + sfx, DataType.UINT8);
            return
            [
                new Copy(new Constant(0), n),
                new Label("spin" + sfx),
                new Binary(IrBinaryOp.Add, n, new Constant(1), n),
                new JumpIfBitClear(new MemoryAddress(0xC0), 5, "spin" + sfx),
                new Copy(v, new MemoryAddress(0x100)),
            ];
        });

        OutlinedFunctions(prog).Should().BeEmpty();
    }

    [Fact]
    public void Outline_AcceptsEmptyPollingLoop()
    {
        // The same region with nothing in the loop body: waiting for a hardware
        // flag costs whatever the hardware costs, so the added CALL is harmless.
        var prog = FourSites((v, sfx) =>
        [
            new Label("spin" + sfx),
            new JumpIfBitClear(new MemoryAddress(0xC0), 5, "spin" + sfx),
            new Copy(v, new MemoryAddress(0x100)),
            new Copy(v, new MemoryAddress(0x101)),
        ]);

        OutlinedFunctions(prog).Should().ContainSingle();
    }

    [Fact]
    public void Outline_RejectsRegionThatSignalsError()
    {
        // The T-flag model aborts the enclosing function; a raise reached inside a
        // subroutine would unwind only the subroutine.
        var prog = FourSites((v, sfx) =>
        [
            new JumpIfLessThan(v, new Constant(10), "ok" + sfx),
            new SignalError(new Constant(6)),
            new Label("ok" + sfx),
            new Copy(v, new MemoryAddress(0x100)),
            new Copy(v, new MemoryAddress(0x101)),
        ]);

        OutlinedFunctions(prog).Should().BeEmpty();
    }
}

