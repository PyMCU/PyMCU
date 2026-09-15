using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#288. Three standard spellings of range() were refused -- `x in range(...)`,
/// reversed(range(...)) and enumerate(range(...)) over runtime bounds -- and range() as a
/// value was reported as a builtin PyMCU does not provide.
/// </summary>
public class RangeExpressionFormsTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) => Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Prelude = "from pymcu.types import uint8, uint16\n\n";

    // ---- x in range(...) ----

    [Theory]
    [InlineData("5 in range(10)", 1)]
    [InlineData("10 in range(10)", 0)]
    [InlineData("5 in range(6, 10)", 0)]
    [InlineData("12 not in range(0, 10, 3)", 1)]
    [InlineData("9 in range(0, 10, 3)", 1)]
    [InlineData("8 in range(0, 10, 3)", 0)]
    [InlineData("7 in range(10, 0, -1)", 1)]
    [InlineData("10 in range(10, 0, -1)", 1)]
    [InlineData("0 in range(10, 0, -1)", 0)]
    public void MembershipInAConstantRange_Folds(string test, int expected)
    {
        var body = Main(Prelude + "def main():\n" + $"    y: uint8 = 1 if {test} else 0\n\nmain()\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: var v }, Dst: Variable { Name: "main.y" } } && v == expected);
    }

    [Fact]
    public void MembershipInARuntimeRange_IsTwoComparisons()
    {
        var body = Main(Prelude +
            "def main(x: uint8, a: uint8, b: uint8):\n" +
            "    if x in range(a, b):\n" +
            "        y: uint8 = 1\n\n" +
            "main(1, 2, 3)\n");
        Assert.Contains(body, i => i is JumpIfGreaterOrEqual or JumpIfLessThan or JumpIfGreaterThan or JumpIfLessOrEqual or JumpIfZero or JumpIfNotZero);
    }

    [Fact]
    public void MembershipWithARuntimeStep_IsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Prelude +
            "def main(x: uint8, s: uint8):\n" +
            "    if x in range(0, 10, s):\n" +
            "        y: uint8 = 1\n\n" +
            "main(1, 2)\n"));
        Assert.Contains("constant step", ex.Message);
    }

    // ---- reversed(range(...)) ----

    [Fact]
    public void ReversedConstantRange_WalksDownFromTheLastValue()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    c: uint16 = 0\n" +
            "    for i in reversed(range(0, 20)):\n" +
            "        c = c + i\n\n" +
            "main()\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 19 }, Dst: Variable { Name: "main.i" } });
        Assert.Contains(body, i => i is JumpIfLessOrEqual { Src1: Variable { Name: "main.i" }, Src2: Constant { Value: -1 } });
        Assert.Contains(body, i => i is AugAssign { Target: Variable { Name: "main.i" }, Operand: Constant { Value: -1 } });
    }

    [Fact]
    public void ReversedShortRange_Unrolls()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    c: uint16 = 0\n" +
            "    for i in reversed(range(2, 8, 2)):\n" +
            "        c = c + i\n\n" +
            "main()\n");
        Assert.DoesNotContain(body, i => i is JumpIfLessOrEqual or JumpIfGreaterOrEqual);
        // 6, 4, 2 folded into the body
        Assert.Contains(body, i => i is Binary { Src2: Constant { Value: 6 } } or Copy { Src: Constant { Value: 6 } });
    }

    [Fact]
    public void ReversedRuntimeRange_CompilesAsADescendingLoop()
    {
        var body = Main(Prelude +
            "def main(n: uint8):\n" +
            "    c: uint16 = 0\n" +
            "    for i in reversed(range(n)):\n" +
            "        c = c + i\n\n" +
            "main(5)\n");
        Assert.Contains(body, i => i is JumpIfLessOrEqual { Src1: Variable { Name: "main.i" } });
    }

    [Fact]
    public void ReversedRuntimeRangeWithAWideStep_IsRefusedNamingTheRewrite()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Prelude +
            "def main(n: uint8):\n" +
            "    for i in reversed(range(0, n, 3)):\n" +
            "        pass\n\n" +
            "main(5)\n"));
        Assert.Contains("range(last, start - step, -step)", ex.Message);
    }

    // ---- enumerate(range(...)) ----

    [Fact]
    public void EnumerateRuntimeRange_KeepsAnIndexNextToTheCounter()
    {
        var body = Main(Prelude +
            "def main(n: uint8):\n" +
            "    c: uint16 = 0\n" +
            "    for k, v in enumerate(range(3, n)):\n" +
            "        c = c + k + v\n\n" +
            "main(9)\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 0 }, Dst: Variable { Name: "main.k" } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: "main.v" } });
        Assert.Contains(body, i => i is AugAssign { Target: Variable { Name: "main.k" }, Operand: Constant { Value: 1 } });
        Assert.Contains(body, i => i is JumpIfGreaterOrEqual { Src1: Variable { Name: "main.v" } });
    }

    [Fact]
    public void EnumerateShortConstantRange_StillUnrolls()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    c: uint16 = 0\n" +
            "    for k, v in enumerate(range(5, 8)):\n" +
            "        c = c + k * 100 + v\n\n" +
            "main()\n");
        Assert.DoesNotContain(body, i => i is JumpIfGreaterOrEqual);
    }

    // ---- range() as a value ----

    [Fact]
    public void RangeAsAValue_NamesTheSupportedSpellings()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen("r = range(4)\nprint(r)\n"));
        Assert.Contains("not a value", ex.Message);
    }

    // #363 changed where this is refused, not whether it is. Binding a range to a name is now
    // a compile-time sequence -- the shape `order = range(w, 0, -1)` then `for i in order` is
    // written in every register driver -- so the refusal moved from the assignment to the first
    // use of the name in a position that needs a value. A name only ever iterated is accepted.
    [Fact]
    public void ARangeBoundToAName_IsAcceptedAndIterable()
    {
        var ir = Gen("acc = 0\nr = range(4)\nfor i in r:\n    acc = acc + i\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Dst is Variable v && v.Name.EndsWith("acc"));
    }
}
