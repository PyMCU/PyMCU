using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0014 family 6: a chip fact bound through `from pymcu.chips import` is still a
/// name the program owns -- a later assignment rebinds it, and the descriptor's string
/// members carry their text into a variable like any other compile-time string.
/// </summary>
public class ChipFactBindingTests
{
    private static ProgramIR Gen(string src, string chip = "atmega328p")
    {
        var config = new DeviceConfig { Arch = "avr", Chip = chip };
        var program = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        return new IRGenerator().Generate(program, new Dictionary<string, ProgramNode>(), config);
    }

    private static bool EmitsString(ProgramIR ir, string text) =>
        ir.Functions.SelectMany(f => f.Body).Select(i => i.ToString())
            .Any(s => s.Contains(text));

    private static bool EmitsInt(ProgramIR ir, int value) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Any(c => c.Src is Constant k && k.Value == value) ||
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Any(s => s.Src is Constant k && k.Value == value);

    [Fact]
    public void ChipFact_ReboundAfterImport_ReadsTheUserValue()
    {
        // `from pymcu.chips import __FREQ__` then `__FREQ__ = 42`: the write is a rebind
        // the import cannot answer for -- the seed that copies the module's placeholder
        // into the bare name must not shadow it.
        var ir = Gen(
            "from pymcu.chips import __FREQ__\n" +
            "__FREQ__ = 42\n" +
            "buf = bytearray(4)\n" +
            "buf[0] = __FREQ__\n");
        Assert.True(EmitsInt(ir, 42));
        Assert.False(EmitsInt(ir, 16000000));
    }

    [Fact]
    public void ChipDescriptorMember_AsAValue_CarriesTheText()
    {
        // `v = __CHIP__.name` holds a compile-time string; the name must answer as the
        // text everywhere a string literal would, not as the interned id's integer.
        var ir = Gen(
            "from pymcu.chips import __CHIP__\n" +
            "v = __CHIP__.name\n");
        Assert.True(EmitsString(ir, "atmega328p"));
    }
}
