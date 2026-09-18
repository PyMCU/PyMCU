using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Descriptor <c>__set__</c> is force-inlined so <c>obj</c> can be the owning instance
/// (#419). The written value arrives as a compile-time constant. Adafruit's
/// <c>RWBits.__set__</c> then does <c>value &lt;&lt;= self.lowest_bit</c> and later
/// <c>reg |= value</c>. Binding <c>value</c> as a constant made the AugAssign drop
/// the name, so the next read said it was never assigned -- which is what stopped
/// adafruit_ina219 and adafruit_veml7700.
///
/// The numeric <c>3 &lt;&lt; 4 == 48</c> is the AVR fixture; these tests pin the
/// lowering: a real local seeded from the literal, then an in-place shift of it.
/// </summary>
public class DescriptorSetterValueAugAssignTests
{
    private const string Program =
        "from pymcu.types import uint8\n" +
        "buf = bytearray([0, 0, 0, 0])\n" +
        "class Field:\n" +
        "    def __init__(self, shift: uint8) -> None:\n" +
        "        self.shift = shift\n" +
        "        self.mask: uint8 = 0xF0\n" +
        "    def __set__(self, obj, value: uint8) -> None:\n" +
        "        value <<= self.shift\n" +
        "        obj.reg &= ~self.mask\n" +
        "        obj.reg |= value\n" +
        "class Dev:\n" +
        "    bits = Field(4)\n" +
        "    def __init__(self) -> None:\n" +
        "        self.reg: uint8 = 0\n" +
        "d = Dev()\n";

    private static ProgramIR Gen(string src)
    {
        return new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
    }

    [Fact]
    public void WritingADescriptorAttribute_SeedsValueThenShiftsIt()
    {
        var ir = Gen(Program + "d.bits = 3\nbuf[0] = d.reg\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();

        var seeds = body.OfType<Copy>()
            .Where(c => c.Src is Constant { Value: 3 }
                        && c.Dst is Variable v
                        && v.Name.Contains("value", StringComparison.Ordinal))
            .ToList();
        seeds.Should().NotBeEmpty(
            because: "d.bits = 3 must copy the literal into the setter's value parameter");

        var shifts = body.OfType<AugAssign>()
            .Where(a => a.Op == PyMCU.IR.BinaryOp.LShift && a.Target is Variable v
                        && v.Name.Contains("value", StringComparison.Ordinal))
            .ToList();
        shifts.Should().NotBeEmpty(
            because: "value <<= self.shift must RMW the seeded local, not drop the name");
    }

    [Fact]
    public void AnInlineCalleeThatShiftsItsOwnParameter_SeedsThenShiftsIt()
    {
        var ir = Gen(
            "from pymcu.types import uint8, inline\n" +
            "buf = bytearray([0, 0, 0, 0])\n" +
            "@inline\n" +
            "def shift_or(v: uint8, n: uint8) -> uint8:\n" +
            "    v <<= n\n" +
            "    return v\n" +
            "buf[0] = shift_or(3, 4)\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();

        body.OfType<Copy>()
            .Where(c => c.Src is Constant { Value: 3 } && c.Dst is Variable v
                        && v.Name.Contains(".v", StringComparison.Ordinal))
            .Should().NotBeEmpty(
                because: "shift_or(3, 4) must copy 3 into v because the callee writes v");

        body.OfType<AugAssign>()
            .Where(a => a.Op == PyMCU.IR.BinaryOp.LShift && a.Target is Variable v
                        && v.Name.Contains(".v", StringComparison.Ordinal))
            .Should().NotBeEmpty(
                because: "v <<= n must RMW the seeded local so the return still sees v");
    }

    [Fact]
    public void TheSetterMustNotReportValueAsUndefined()
    {
        var act = () => Gen(Program + "d.bits = 3\n");

        act.Should().NotThrow<CompilerError>(
            because: "value after value <<= self.shift is the same parameter, not an unbound name");
    }
}
