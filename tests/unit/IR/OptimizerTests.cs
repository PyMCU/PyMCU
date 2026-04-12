using PyMCU.Common;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;

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
