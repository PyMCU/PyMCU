using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Adafruit <c>UnaryStruct.__set__(self, obj, value: Any)</c> then
/// <c>struct.pack_into(..., value)</c>. <c>Any</c> is accepted on an unread
/// parameter (#367) and was refused at the first read even when the written
/// value is an int with a width -- which is what stopped adafruit_ina219
/// after <c>_fit</c> kept the shared buffer.
/// </summary>
public class DescriptorSetterAnyValueTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Program =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "class Field:\n" +
        "    def __init__(self) -> None:\n" +
        "        pass\n" +
        "    def __set__(self, obj, value: Any) -> None:\n" +
        "        obj.reg = value\n" +
        "class Dev:\n" +
        "    bits = Field()\n" +
        "    def __init__(self) -> None:\n" +
        "        self.reg: uint8 = 0\n" +
        "d = Dev()\n";

    [Fact]
    public void ADescriptorWriteThroughAny_StoresTheWrittenValue()
    {
        var ir = Optimizer.Optimize(Gen(
            Program +
            "def main():\n" +
            "    d.bits = 7\n" +
            "    GPIOR0.value = d.reg\n"));

        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Select(c => c.Src)
            .Should().Contain(new Constant(7),
                because: "value: Any on __set__ is the written 7, not a missing width");
    }

    [Fact]
    public void ReadingATypingOnlyNameThatIsNotAny_IsStillRefused()
    {
        var act = () => Gen(
            "from pymcu.types import uint8\n" +
            "class Field:\n" +
            "    def __init__(self) -> None:\n" +
            "        pass\n" +
            "    def __set__(self, obj, value: Type[type]) -> None:\n" +
            "        obj.reg = value\n" +
            "class Dev:\n" +
            "    bits = Field()\n" +
            "    def __init__(self) -> None:\n" +
            "        self.reg: uint8 = 0\n" +
            "d = Dev()\n" +
            "def main():\n" +
            "    d.bits = 7\n");

        act.Should().Throw<PyMCU.Common.CompilerError>()
            .Which.Message.Should().Contain("Type[type]",
                because: "Any is the call-site type; Type[type] stays a missing width");
    }
}
