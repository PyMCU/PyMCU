using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `for i, v in enumerate(self.header)` where the field holds the list a constructor was handed
/// (adafruit_irremote's GenericTransmit). `for v in self.header` already walked it; enumerate()
/// refused it with a message that asked for a constant literal the field's list is.
/// </summary>
public class EnumerateFieldListTests
{
    private const string Prelude =
        "from pymcu.types import uint8, const, inline, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n" +
        "class Frame:\n" +
        "    def __init__(self, header: list[int]):\n" +
        "        self.header = header\n" +
        "\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<int> Stores(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is MemoryAddress && c.Src is Constant)
            .Select(c => (int)((Constant)c.Src).Value).ToList();

    [Fact]
    public void AFieldListIsEnumeratedElementByElement()
    {
        var ir = Gen(
            "    def put(self):\n" +
            "        for i, v in enumerate(self.header):\n" +
            "            G.value = v + i\n" +
            "def main():\n" +
            "    Frame([9, 4, 6]).put()\n");
        Stores(ir).Should().Contain(new[] { 9, 5, 8 }, "9+0, 4+1, 6+2");
    }

    [Fact]
    public void AStartShiftsTheIndexOfAFieldList()
    {
        var ir = Gen(
            "    def put(self):\n" +
            "        for i, v in enumerate(self.header, 10):\n" +
            "            G.value = v + i\n" +
            "def main():\n" +
            "    Frame([9, 4]).put()\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        body.OfType<Copy>().Where(c => c.Dst is Variable { Name: var n } && n.EndsWith(".i") && c.Src is Constant)
            .Select(c => (int)((Constant)c.Src).Value).Should().Equal(10, 11);
        body.OfType<Binary>().Select(b => b.Src1).OfType<Constant>().Select(c => (int)c.Value)
            .Should().Equal(9, 4);
    }

    [Fact]
    public void ARunTimeElementIsRefusedInWords()
    {
        var act = () => Gen(
            "    def put(self):\n" +
            "        for i, v in enumerate(self.header):\n" +
            "            G.value = v + i\n" +
            "def main():\n" +
            "    x = G.value\n" +
            "    Frame([x, 4]).put()\n");
        act.Should().Throw<Exception>().WithMessage("*must be compile-time integer constants*");
    }
}
