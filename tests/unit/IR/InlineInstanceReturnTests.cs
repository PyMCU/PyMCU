using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#520. An <c>@inline</c> function whose <c>return</c> hands back an
/// instance: <c>w = one(a)</c> where <c>one</c> returns its parameter. The
/// result temporary only aliased the instance, and an alias through a scratch
/// temporary was not followed, so <c>w</c> bound to the temp and
/// <c>w.k</c> read <c>w_k</c>, a name nothing writes: 0 for the field's value.
/// When every reachable return names the same instance the call's value IS
/// that instance; when run time picks between two, the call refuses with a
/// diagnostic that names the limitation.
/// </summary>
public class InlineInstanceReturnTests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src, bool optimize = true)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private const string Shapes =
        "class K:\n" +
        "    def __init__(self, k: uint16):\n" +
        "        self.k: uint16 = k\n\n" +
        "@inline\n" +
        "def one(a: K) -> K:\n" +
        "    return a\n\n" +
        "a = K(GPIOR0.value + 300)\n";

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static string? ValName(Val v) => v switch
    {
        Variable vv => vv.Name,
        Temporary tt => tt.Name,
        _ => null,
    };

    [Fact]
    public void AnInlineReturnOfItsParameter_BindsTheCallersNameToThatInstance()
    {
        var ir = Gen(Shapes + "w = one(a)\nprint(w.k)\n");

        var write = Body(ir).OfType<Call>()
            .Where(c => c.FunctionName == "uart_write_decimal_u16")
            .ToList();
        write.Should().ContainSingle(
            because: "w.k is a's uint16 field, streamed by the 16-bit writer");
        ValName(write[0].Args[0]).Should().NotBeNull().And.NotEndWith("w_k",
            because: "w must alias the returned instance: w_k was a field slot " +
                     "nothing ever wrote and it read 0");
    }

    [Fact]
    public void AnInlineReturnChosenAtRunTime_RefusesNamingTheLimitation()
    {
        string msg = Refusal(Shapes +
            "b = K(7)\n" +
            "@inline\n" +
            "def pick(c: uint8, x: K, y: K) -> K:\n" +
            "    if c:\n" +
            "        return x\n" +
            "    return y\n" +
            "w = pick(GPIOR0.value, a, b)\n");

        msg.Should().Contain("different instance")
            .And.Contain("run-time")
            .And.Contain("pick");
    }
}
