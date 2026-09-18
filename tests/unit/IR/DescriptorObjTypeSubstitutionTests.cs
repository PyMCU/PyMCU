using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#419. Descriptor protocol <c>__get__</c>/<c>__set__</c> receive <c>obj</c> as the
/// owning instance. Adafruit annotates that parameter with a typing-only placeholder
/// (<c>I2CDeviceDriver</c>); CPython still hands over the real <c>INA219</c>. The rewrite
/// must substitute the argument's class so <c>obj.i2c_device</c> / <c>obj.base</c> resolve.
/// </summary>
[Trait("Issue", "419")]
public class DescriptorObjTypeSubstitutionTests
{
    private const string Program =
        "from pymcu.types import uint8\n" +
        "buf = bytearray([0, 0, 0, 0])\n" +
        "if TYPE_CHECKING:\n" +
        "    from circuitpython_typing.device_drivers import I2CDeviceDriver\n" +
        "class Field:\n" +
        "    def __init__(self, addr: uint8) -> None:\n" +
        "        self.addr = addr\n" +
        "    def __get__(self, obj: I2CDeviceDriver, objtype=None) -> uint8:\n" +
        "        return self.addr + obj.base\n" +
        "    def __set__(self, obj: I2CDeviceDriver, value: uint8) -> None:\n" +
        "        obj.base = value\n" +
        "class Dev:\n" +
        "    reg = Field(9)\n" +
        "    def __init__(self, base: uint8) -> None:\n" +
        "        self.base = base\n" +
        "d = Dev(2)\n";

    private static ProgramIR Gen(string src)
    {
        var config = new DeviceConfig { Arch = "avr" };
        var program = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        var ir = new IRGenerator().Generate(
            program, new Dictionary<string, ProgramNode>(), config);
        return Optimizer.Optimize(ir);
    }

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    [Fact]
    public void ReadingADescriptorAttribute_CallsGetWithTheOwningInstance()
    {
        var ir = Gen(Program + "buf[0] = d.reg\n");

        LastStored(ir, 0).Should().Be(new Constant(11),
            because: "obj is the Dev instance, so obj.base is 2 and 9+2 is what CPython prints");
    }

    [Fact]
    public void WritingADescriptorAttribute_CallsSetWithTheOwningInstance()
    {
        var ir = Gen(Program +
            "d.reg = 5\n" +
            "buf[1] = d.base\n");

        LastStored(ir, 1).Should().Be(new Constant(5),
            because: "__set__ must write through obj.base, not refuse obj as I2CDeviceDriver");
    }

    [Fact]
    public void ANonInstanceArgumentStillRefusesAReadOfTheTypingOnlyParameter()
    {
        var act = () => Gen(
            "from pymcu.types import uint8\n" +
            "if TYPE_CHECKING:\n" +
            "    from circuitpython_typing.device_drivers import I2CDeviceDriver\n" +
            "class Field:\n" +
            "    def __init__(self, addr: uint8) -> None:\n" +
            "        self.addr = addr\n" +
            "    def __get__(self, obj: I2CDeviceDriver, objtype=None) -> uint8:\n" +
            "        return obj.base\n" +
            "f = Field(1)\n" +
            "def main() -> uint8:\n" +
            "    return f.__get__(0)\n");

        act.Should().Throw<CompilerError>()
            .Which.Message.Should().Contain("I2CDeviceDriver",
                because: "a direct call that does not pass an instance keeps #367's refusal");
    }
}
