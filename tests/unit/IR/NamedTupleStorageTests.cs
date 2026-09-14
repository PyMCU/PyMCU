using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#297 and PyMCU#298. A tuple of constants bound to a name compiled up to eight
/// elements and was refused at nine ("tuples are not supported as runtime values"), while
/// the same elements written as a list compiled at any length -- and once past eight, the
/// storage both of them get was one byte per element whatever the values were.
/// </summary>
public class NamedTupleStorageTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) => Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Prelude =
        "from pymcu.types import uint8, uint16, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n\n";

    // Since PyMCU#331 a read of a local whose value the compiler tracks folds to that value, so
    // an accumulator seeded with 0 makes every add in an unrolled loop a compile-time sum and
    // leaves one constant behind. A test that reads the unrolled arithmetic seeds it from G, a
    // register the compiler cannot read, so the per-element adds survive.
    private const string RuntimeSeed = "G.value";

    private static string Program(string literal, string accInit = "0") =>
        Prelude +
        "def main():\n" +
        $"    T = {literal}\n" +
        $"    acc: uint16 = {accInit}\n" +
        "    for d in T:\n" +
        "        acc = acc + d\n\n" +
        "main()\n";

    private const string Nine = "0, 7, 14, 21, 28, 35, 42, 49, 56";
    private const string Eight = "0, 7, 14, 21, 28, 35, 42, 49";

    // ---- #297: past the unroll limit a tuple gets the storage a list gets ----

    [Fact]
    public void ATupleOfNineConstants_GetsElementStorage()
    {
        var body = Main(Program($"({Nine})"));
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 0 }, Dst: Variable { Name: "main.T__0" } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 56 }, Dst: Variable { Name: "main.T__8" } });
    }

    [Fact]
    public void ATupleOfNineConstants_LowersExactlyLikeTheSameList()
    {
        var tuple = Main(Program($"({Nine})"));
        var list = Main(Program($"[{Nine}]"));
        Assert.Equal(
            list.Select(i => i.ToString()).ToList(),
            tuple.Select(i => i.ToString()).ToList());
    }

    // The other direction of the gate: at or below the limit the binding is the whole
    // statement, nothing is stored and the loop folds. Storage appearing here would mean
    // the short form had silently changed shape.
    [Fact]
    public void ATupleOfEightConstants_StillUnrollsWithNoStorage()
    {
        var body = Main(Program($"({Eight})", RuntimeSeed));
        Assert.DoesNotContain(body, i => i is Copy { Dst: Variable { Name: "main.T__0" } });
        Assert.Contains(body, i => i is Binary { Src2: Constant { Value: 49 }, Dst: Temporary });
    }

    [Fact]
    public void AnInlineTupleOfNineConstants_StillUnrolls()
    {
        var body = Main(Prelude +
            "def main():\n" +
            $"    acc: uint16 = {RuntimeSeed}\n" +
            $"    for d in ({Nine}):\n" +
            "        acc = acc + d\n\n" +
            "main()\n");
        Assert.DoesNotContain(body, i => i is Copy { Dst: Variable { Name: "main.T__0" } });
        Assert.Contains(body, i => i is Binary { Src2: Constant { Value: 56 }, Dst: Temporary });
    }

    // ---- #298: the element width comes from the widest element, not from uint8 ----

    [Theory]
    [InlineData("256, 383, 512, 16384, 16639, 32768, 32895, 33024, 49152, 65280", DataType.UINT16)]
    [InlineData("1, 2, 3, 4, 5, 6, 7, 8, 9", DataType.UINT8)]
    [InlineData("-1, -2, -3, -4, -5, -6, -7, -8, -9", DataType.INT8)]
    [InlineData("-1, -2, -3, -4, -5, -6, -7, -8, -300", DataType.INT16)]
    public void AnUnannotatedSequence_TakesTheWidthOfItsWidestElement(string elems, DataType expected)
    {
        foreach (string literal in new[] { $"({elems})", $"[{elems}]" })
        {
            var body = Main(Program(literal));
            Assert.Contains(body, i => i is Copy { Dst: Variable { Name: "main.T__0", Type: var t } } && t == expected);
            Assert.Contains(body, i => i is Copy { Dst: Variable { Name: "main.T__8", Type: var t } } && t == expected);
        }
    }

    [Fact]
    public void AWideSequenceKeepsItsValues()
    {
        var body = Main(Program("(256, 383, 512, 16384, 16639, 32768, 32895, 33024, 49152, 65280)"));
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 256 }, Dst: Variable { Name: "main.T__0" } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 65280 }, Dst: Variable { Name: "main.T__9" } });
    }
}
