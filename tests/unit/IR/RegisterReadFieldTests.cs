using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#526. <c>self.c = REG.value</c> reads the register and keeps a copy of
/// what it held; only <c>self._port = PORTB</c> makes the field an alias of the
/// address. Both forms evaluated to the same MemoryAddress, and the read was
/// filed as an alias too, so <c>o.c = o.c + 300</c> read the register live and
/// wrote the register back: the field's own slot never saw the value, and
/// printing the register afterwards showed the field's arithmetic.
/// </summary>
public class RegisterReadFieldTests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16, ptr\n" +
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

    // The chip module spells a general-purpose register exactly this way
    // (lib/src/pymcu/chips/atmega328p.py: `GPIOR0: ptr[uint8] = ptr(0x3E)`).
    private const string Program =
        "REG: ptr[uint8] = ptr(0x3E)\n\n" +
        "class C:\n" +
        "    def __init__(self):\n" +
        "        self.c: uint16 = REG.value\n\n" +
        "o = C()\n" +
        "print(o.c)\n" +
        "o.c = o.c + 300\n" +
        "print(o.c)\n" +
        "print(REG.value)\n";

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    [Fact]
    public void ARegisterReadStoredInAField_SnapshotsIntoFieldStorage()
    {
        var ir = Gen(Program, optimize: false);

        Body(ir).OfType<Copy>()
            .Where(c => c.Src is MemoryAddress && c.Dst is Variable v && v.Name.EndsWith("_c"))
            .Should().ContainSingle(
                because: "self.c = REG.value loads the register once into o_c; " +
                         "filed as an alias there was no load at all");
    }

    [Fact]
    public void AFieldWrittenAfterARegisterRead_StaysFieldStorage()
    {
        var ir = Gen(Program, optimize: false);

        var writes16 = Body(ir).OfType<Call>()
            .Where(c => c.FunctionName == "uart_write_decimal_u16")
            .ToList();
        writes16.Should().HaveCount(2,
            because: "o.c prints twice, both at its declared uint16 width");
        writes16[1].Args[0].Should().NotBeOfType<MemoryAddress>(
            because: "the second print reads the field the arithmetic wrote; " +
                     "as a register alias it read the register live instead, " +
                     "and the register's own print just above shows the difference");
    }
}
