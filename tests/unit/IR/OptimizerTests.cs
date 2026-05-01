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
        var body = Optimize(
            new Unary(IrUnaryOp.Neg, new Constant(5), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Return>().First().Value
            .Should().Be(new Constant(-5));
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
        var body = Optimize(
            new Unary(IrUnaryOp.BitNot, new Constant(5), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Return>().First().Value
            .Should().Be(new Constant(~5));
    }

    // ─── FoldConstants — Binary edge cases ───────────────────────────────────

    [Fact]
    public void FoldConstants_DivByZero_IsNotFolded()
    {
        var body = Optimize(
            new Binary(IrBinaryOp.Div, new Constant(10), new Constant(0), new Temporary("t1")),
            new Return(new Temporary("t1")));

        body.OfType<Binary>().Should().ContainSingle(b =>
            b.Op == IrBinaryOp.Div && b.Src2 == new Constant(0));
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
}

// ─────────────────────────────────────────────────────────────────────────────
// PropagateCopies — regression and property tests
// ─────────────────────────────────────────────────────────────────────────────

public class PropagateCopiesTests
{
    private static List<Instruction> Optimize(params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        return Optimizer.Optimize(prog).Functions[0].Body;
    }

    /// <summary>
    /// Regression test for the bug fixed in the PropagateCopies optimizer pass:
    /// a Call instruction that writes to a Variable must invalidate that variable
    /// in varConsts so subsequent instructions do not see the stale initialization
    /// constant.
    ///
    /// Scenario (mirrors examples/avr/ffi-crc8):
    ///   crc = 0                          ; varConsts["crc"] = 0
    ///   crc = crc8_update(crc, 0x28)     ; first call — should read crc = 0 here, then invalidate crc
    ///   crc = crc8_update(crc, 0x11)     ; second call — crc must be Variable("crc"), NOT Constant(0)
    ///   return crc
    ///
    /// Before the fix, "crc" was never removed from varConsts after the first Call
    /// wrote to it, so the second call received Constant(0) as its first argument.
    /// </summary>
    [Fact]
    public void CallDest_IsInvalidated_SubsequentCallSeesVariable()
    {
        var crc = new Variable("crc");
        var body = Optimize(
            new Copy(new Constant(0), crc),
            new Call("crc8_update", [crc, new Constant(0x28)], crc),
            new Call("crc8_update", [crc, new Constant(0x11)], crc),
            new Return(crc));

        // The second call's first argument must not be the stale initialization constant.
        var calls = body.OfType<Call>().ToList();
        calls.Should().HaveCountGreaterOrEqualTo(2,
            "both crc8_update calls must survive optimization");
        calls[^1].Args[0].Should().NotBe(new Constant(0),
            "after the first Call writes to crc, varConsts must no longer hold Constant(0) for crc");
    }

    /// <summary>
    /// Verifies that running Optimizer.Optimize() twice on the same program produces
    /// identical Call argument types.  If PropagateCopies were not idempotent,
    /// a second optimization pass would propagate stale constants that the first
    /// pass left behind, changing the instruction stream.
    /// </summary>
    [Fact]
    public void Optimize_IsIdempotent_ForCallChains()
    {
        var crc = new Variable("crc");
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body =
            [
                new Copy(new Constant(0), crc),
                new Call("crc8_update", [crc, new Constant(0x28)], crc),
                new Call("crc8_update", [crc, new Constant(0x11)], crc),
                new Return(crc),
            ]
        });

        var first  = Optimizer.Optimize(prog);
        var second = Optimizer.Optimize(first);   // second pass on already-optimized code

        var calls1 = first.Functions[0].Body.OfType<Call>().ToList();
        var calls2 = second.Functions[0].Body.OfType<Call>().ToList();

        calls1.Should().HaveCount(calls2.Count,
            "idempotency: second pass must not remove or add Call instructions");

        for (var i = 0; i < calls1.Count; i++)
        {
            calls1[i].Args.Should().BeEquivalentTo(calls2[i].Args,
                $"call[{i}] arguments must be identical after a second optimization pass");
        }
    }

    /// <summary>
    /// GetDst must cover every instruction type that defines a value so that
    /// PropagateCopies can invalidate it via the default branch.
    /// This test enumerates a representative set and asserts GetDst is non-null.
    /// </summary>
    [Fact]
    public void GetDst_ReturnsNonNull_ForAllDefiningInstructions()
    {
        var v = new Variable("x");
        var t = new Temporary("t1");

        Instruction[] definingInstructions =
        [
            new Binary(IrBinaryOp.Add, v, v, t),
            new Unary(IrUnaryOp.Neg, v, t),
            new Copy(v, t),
            new Call("f", [], t),
            new BitCheck(v, 0, t),
            new LoadIndirect(v, t),
            new ArrayLoad("arr", new Constant(0), t, DataType.UINT8, 4),
            new ArrayLoadRo("arr", new Constant(0), t),
            new AugAssign(IrBinaryOp.Add, v, new Constant(1)),
        ];

        foreach (var instr in definingInstructions)
        {
            Optimizer.GetDst(instr).Should().NotBeNull(
                $"{instr.GetType().Name} defines a value and GetDst must return its destination");
        }
    }
}

