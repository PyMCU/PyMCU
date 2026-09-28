using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#520. A zero-copy factory result passed straight as a call argument --
/// <c>rd(make(301))</c> where <c>make</c> returns a single-field class and
/// <c>rd(s)</c> reads <c>s.base</c>. The result temporary was tagged with no
/// handle, so the parameter bound by an ordinary Copy and <c>s.base</c>
/// flattened to <c>&lt;param&gt;_base</c>, a name nothing wrote: it printed 0
/// while <c>o = make(300); rd(o)</c> printed the right value only after the
/// field's own width reached the parameter.
/// </summary>
public class FactoryHandleArgumentTests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16\n" +
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

    private const string Shapes =
        "class Src:\n" +
        "    def __init__(self, base: uint16):\n" +
        "        self.base: uint16 = base\n\n" +
        "def make(n: uint16) -> Src:\n" +
        "    return Src(n + GPIOR0.value)\n\n" +
        "def rd(s: Src):\n" +
        "    print(s.base)\n\n";

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static string? ValName(Val v) => v switch
    {
        Variable vv => vv.Name,
        Temporary tt => tt.Name,
        _ => null,
    };

    [Fact]
    public void AFactoryResultPassedDirectly_StreamsItsFieldAtTheFieldsWidth()
    {
        var ir = Gen(Shapes + "rd(make(301))\n", optimize: false);

        var write = Body(ir).OfType<Call>()
            .Where(c => c.FunctionName == "uart_write_decimal_u16")
            .ToList();
        write.Should().ContainSingle(
            because: "s.base is a uint16 field, so the 16-bit writer streams it");
        ValName(write[0].Args[0]).Should().NotBeNull().And.NotEndWith("s_base",
            because: "the read must resolve to the factory result's storage; " +
                     "<param>_base was a flattened name nothing wrote");
    }

    [Fact]
    public void ABoundFactoryHandle_StillStreamsItsField()
    {
        var ir = Gen(Shapes + "o = make(300)\nrd(o)\n");

        Body(ir).OfType<Call>()
            .Where(c => c.FunctionName == "uart_write_decimal_u16")
            .Should().ContainSingle(
                because: "the named binding keeps the same handle resolution");
    }
}
