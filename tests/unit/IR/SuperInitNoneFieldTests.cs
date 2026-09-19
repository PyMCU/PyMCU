using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>super().__init__(reset=None)</c> then <c>if self.reset_pin:</c>.
/// Adafruit ssd1306 stores the Optional reset and guards every use;
/// the super binder used not to mark the None argument, so the then
/// branch lowered <c>Pin.low()</c> on a port that was never a register.
/// </summary>
public class SuperInitNoneFieldTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    private const string Src =
        "from pymcu.types import uint8\n" +
        "class Pin:\n" +
        "    def __init__(self, bit: uint8):\n" +
        "        self._bit: uint8 = bit\n" +
        "    def low(self):\n" +
        "        self._bit = 99\n" +
        "class Dio:\n" +
        "    def __init__(self, p: Pin):\n" +
        "        self._pin = p\n" +
        "    def switch_to_output(self, value: uint8 = 0):\n" +
        "        self._pin.low()\n" +
        "class Base:\n" +
        "    def __init__(self, reset=None):\n" +
        "        self.reset_pin = reset\n" +
        "        if self.reset_pin:\n" +
        "            self.reset_pin.switch_to_output(value=0)\n" +
        "        self.ok: uint8 = 1\n" +
        "class OLED(Base):\n" +
        "    def __init__(self):\n" +
        "        super().__init__(reset=None)\n" +
        "buf = bytearray([0])\n" +
        "o = OLED()\n" +
        "buf[0] = o.ok\n";

    [Fact]
    public void SuperInit_ResetNone_DoesNotLowerTheGuardedUse()
    {
        var ir = Gen(Src);

        LastStored(ir, 0).Should().Be(new Constant(1),
            because: "if self.reset_pin: must fold when super() forwarded None");
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Src is Constant k && k.Value == 99)
            .Should().BeEmpty(
                because: "Pin.low() lives only in the dead reset branch");
    }

    [Fact]
    public void SuperInit_ResetParamThatIsNone_IsTheSame()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Base:\n" +
            "    def __init__(self, reset=None):\n" +
            "        self.reset_pin = reset\n" +
            "        if self.reset_pin:\n" +
            "            self.ok: uint8 = 99\n" +
            "        else:\n" +
            "            self.ok: uint8 = 1\n" +
            "class OLED(Base):\n" +
            "    def __init__(self, reset=None):\n" +
            "        super().__init__(reset=reset)\n" +
            "buf = bytearray([0])\n" +
            "o = OLED()\n" +
            "buf[0] = o.ok\n");

        LastStored(ir, 0).Should().Be(new Constant(1),
            because: "adafruit_ssd1306 writes super().__init__(..., reset=reset) with reset defaulting to None");
    }
}
