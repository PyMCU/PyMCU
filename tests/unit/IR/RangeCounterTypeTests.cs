using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#284. The counter of a runtime `for i in range(...)` was an unconditional UINT8,
/// whatever the bounds said: range(300) compared its counter with 44, range(0, 256) never
/// ran, a descending range from 200 to -1 never ran, a uint16 stop variable and a uint16
/// annotation on the loop variable were both ignored, and a step that does not divide the
/// span wrapped the counter around.
///
/// The counter is now the program's annotation when there is one, otherwise the narrowest
/// integer type that holds every value it takes, including the one it stops on. The first
/// test is the zero-cost gate: a range that fits a byte is still an 8-bit loop.
/// </summary>
public class RangeCounterTypeTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static Function Main(ProgramIR ir) => ir.Functions.Single(f => f.Name == "main");

    // The counter of the (only) runtime range loop in main: the Src1 of its exit test.
    private static Variable Counter(ProgramIR ir, string name)
    {
        var jumps = Main(ir).Body
            .Select(i => i switch
            {
                JumpIfGreaterOrEqual j => (Val?)j.Src1,
                JumpIfLessOrEqual j => j.Src1,
                _ => null,
            })
            .OfType<Variable>()
            .Where(v => v.Name == name)
            .ToList();
        Assert.NotEmpty(jumps);
        return jumps[0];
    }

    private static string Loop(string header, string prelude = "") =>
        "from pymcu.types import uint8, uint16, int8\n\n" +
        "def main():\n" +
        "    c: uint16 = 0\n" +
        prelude +
        $"    for i in {header}:\n" +
        "        c = c + 1\n" +
        "\n" +
        "main()\n";

    [Fact]
    public void ARangeThatFitsAByte_IsStillAnEightBitLoop()
    {
        var ir = Gen(Loop("range(10)"));
        Assert.Equal(DataType.UINT8, Counter(ir, "main.i").Type);
        Assert.Contains(Main(ir).Body, i => i is JumpIfGreaterOrEqual { Src2: Constant { Value: 10 } });
    }

    [Theory]
    [InlineData("range(300)", DataType.UINT16)]
    [InlineData("range(0, 256)", DataType.UINT16)]
    [InlineData("range(0, 65500, 100)", DataType.UINT16)]      // the last step lands on 65500, not past uint16
    [InlineData("range(0, 250, 30)", DataType.UINT16)]         // stops on 270, past a byte
    [InlineData("range(200, -1, -1)", DataType.INT16)]
    [InlineData("range(100, -1, -1)", DataType.INT8)]
    [InlineData("range(-3, 30)", DataType.INT8)]
    public void ConstantBounds_SizeTheCounter(string header, DataType expected)
        => Assert.Equal(expected, Counter(Gen(Loop(header)), "main.i").Type);

    [Fact]
    public void ADescendingOvershoot_GoesSigned()
    {
        var ir = Gen(Loop("range(250, 0, -30)"));      // stops on -20
        Assert.Equal(DataType.INT16, Counter(ir, "main.i").Type);
        Assert.Contains(Main(ir).Body, i => i is JumpIfLessOrEqual);
    }

    [Theory]
    [InlineData("n: uint16 = 300", "range(n)", DataType.UINT16)]
    [InlineData("n: uint8 = 200", "range(n)", DataType.UINT8)]
    [InlineData("a: int8 = -5", "range(a, 5)", DataType.INT8)]
    [InlineData("a: int8 = -5\n    n: uint8 = 200", "range(a, n)", DataType.INT16)]
    [InlineData("n: uint8 = 200\n    s: uint8 = 30", "range(0, n, s)", DataType.UINT16)]   // may stop on n + s - 1
    public void RuntimeBounds_SizeTheCounterFromTheirTypes(string decls, string header, DataType expected)
        => Assert.Equal(expected, Counter(Gen(Loop(header, "    " + decls + "\n")), "main.i").Type);

    [Fact]
    public void ADeclaredLoopVariable_KeepsItsType()
    {
        var ir = Gen(Loop("range(10)", "    i: uint16 = 0\n"));
        Assert.Equal(DataType.UINT16, Counter(ir, "main.i").Type);
    }

    [Fact]
    public void AParameterReusedAsLoopVariable_KeepsItsType()
    {
        const string src =
            "from pymcu.types import uint16\n\n" +
            "def f(n: uint16) -> uint16:\n" +
            "    c: uint16 = 0\n" +
            "    for n in range(3, 20):\n" +
            "        c = c + n\n" +
            "    return c\n\n" +
            "def main():\n" +
            "    f(7)\n\n" +
            "main()\n";
        var f = Gen(src).Functions.Single(fn => fn.Name == "f");
        var counter = f.Body.OfType<JumpIfGreaterOrEqual>().Select(j => j.Src1).OfType<Variable>().Single(v => v.Name == "f.n");
        Assert.Equal(DataType.UINT16, counter.Type);
    }

    [Fact]
    public void ADeclaredTypeTheRangeCannotFit_IsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Loop("range(300)", "    i: uint8 = 0\n")));
        Assert.Contains("uint8", ex.Message);
        Assert.Contains("300", ex.Message);
        Assert.Contains("uint16", ex.Message);
    }

    [Fact]
    public void TheBodyReadsTheCounter_AtTheCounterWidth()
    {
        var ir = Gen(Loop("range(300)"));
        var reads = Main(ir).Body.OfType<Binary>()
            .SelectMany(b => new[] { b.Src1, b.Src2 })
            .OfType<Variable>().Where(v => v.Name == "main.i").ToList();
        // `c = c + 1` does not read i; make the body read it.
        var ir2 = Gen(Loop("range(300)").Replace("c = c + 1", "c = c + i"));
        reads = Main(ir2).Body.OfType<Binary>()
            .SelectMany(b => new[] { b.Src1, b.Src2 })
            .OfType<Variable>().Where(v => v.Name == "main.i").ToList();
        Assert.NotEmpty(reads);
        Assert.All(reads, v => Assert.Equal(DataType.UINT16, v.Type));
    }

    [Fact]
    public void TwoLoopsOverOneName_EachSizeTheirOwnCounter()
    {
        const string src =
            "from pymcu.types import uint16\n\n" +
            "def main():\n" +
            "    c: uint16 = 0\n" +
            "    for i in range(300):\n" +
            "        c = c + 1\n" +
            "    for i in range(0, 40, 3):\n" +
            "        c = c + 1\n\n" +
            "main()\n";
        var counters = Main(Gen(src)).Body.OfType<JumpIfGreaterOrEqual>()
            .Select(j => j.Src1).OfType<Variable>().Where(v => v.Name == "main.i").ToList();
        Assert.Equal(2, counters.Count);
        Assert.Equal(DataType.UINT16, counters[0].Type);
        // The second loop is not held to the first loop's inference: nothing declared uint16.
        Assert.Equal(DataType.UINT8, counters[1].Type);
    }

    [Fact]
    public void AFloatBound_IsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Loop("range(n)", "    n: float = 3.0\n")));
        Assert.Contains("integers", ex.Message);
    }

    // The optimizer's own unroller re-materialised the counter as a default UINT8 Variable,
    // which would have narrowed a declared uint16 counter the moment it was unrolled.
    [Fact]
    public void TheOptimizerUnroller_KeepsTheCounterType()
    {
        // 12 trips: past the IR generator's own unroll cap, inside the optimizer's.
        var ir = Optimizer.Optimize(Gen(Loop("range(12)", "    i: uint16 = 0\n").Replace("c = c + 1", "c = c + i")));
        Assert.DoesNotContain(Main(ir).Body, i => i is JumpIfGreaterOrEqual);
        var vars = Main(ir).Body
            .SelectMany(i => i switch
            {
                Copy c => new[] { c.Src, c.Dst },
                Binary b => new[] { b.Src1, b.Src2, b.Dst },
                _ => Array.Empty<Val>(),
            })
            .OfType<Variable>().Where(v => v.Name == "main.i").ToList();
        Assert.All(vars, v => Assert.Equal(DataType.UINT16, v.Type));
    }
}
