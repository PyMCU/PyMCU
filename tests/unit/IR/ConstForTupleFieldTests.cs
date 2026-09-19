using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>for cmd in (SET_DISP, 0x10 if self.page else 0x00, self.height - 1)</c>.
/// Adafruit ssd1306's init_display walks that tuple; BindUnrolledElement
/// used to accept only integer literals, so the first name already failed.
/// </summary>
public class ConstForTupleFieldTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private static IEnumerable<int> StoredBytes(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Src is Constant)
            .Select(s => ((Constant)s.Src).Value);

    [Fact]
    public void AForOverNamedConstsAndAFieldTernary_Unrolls()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "SET_DISP = const(0xAE)\n" +
            "SET_MUX = const(0xA8)\n" +
            "class OLED:\n" +
            "    def __init__(self, height: uint8, page: uint8):\n" +
            "        self.height = height\n" +
            "        self.page = page\n" +
            "        self.n: uint8 = 0\n" +
            "        buf = bytearray(8)\n" +
            "        for cmd in (\n" +
            "            SET_DISP,\n" +
            "            0x10 if self.page else 0x00,\n" +
            "            SET_MUX,\n" +
            "            self.height - 1,\n" +
            "            0x02 if self.height > 32 else 0x12,\n" +
            "        ):\n" +
            "            buf[self.n] = cmd\n" +
            "            self.n = self.n + 1\n" +
            "o = OLED(64, 0)\n");

        StoredBytes(ir).Should().Contain(new[] { 0xAE, 0x00, 0xA8, 63, 0x02 },
            because: "names, a field ternary, self.height - 1 and a comparison ternary fold");
    }
}
