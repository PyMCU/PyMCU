using System.Linq;
using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Adafruit sht4x/tmp117 CV.add_values: a @classmethod unpacks compile-time
/// tuples, setattr(cls, name, value), and fills class dicts. cls is the
/// receiver class, not a runtime object.
/// </summary>
public class ClassMethodCvTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));
        return ir;
    }

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    private const string Cv =
        "from pymcu.types import uint8\n" +
        "buf = bytearray([0, 0, 0, 0])\n" +
        "class CV:\n" +
        "    @classmethod\n" +
        "    def add_values(cls, value_tuples):\n" +
        "        cls.string = {}\n" +
        "        cls.delay = {}\n" +
        "        for value_tuple in value_tuples:\n" +
        "            name, value, string, delay = value_tuple\n" +
        "            setattr(cls, name, value)\n" +
        "            cls.string[value] = string\n" +
        "            cls.delay[value] = delay\n" +
        "    @classmethod\n" +
        "    def is_valid(cls, value: uint8) -> uint8:\n" +
        "        return 1 if value in cls.string else 0\n" +
        "class Mode(CV):\n" +
        "    pass\n" +
        "Mode.add_values((\n" +
        "    (\"NOHEAT_HIGHPRECISION\", 0xFD, \"No heat high precision\", 0.01),\n" +
        "    (\"NOHEAT_MEDPRECISION\", 0xF6, \"No heat med precision\", 0.004),\n" +
        "    (\"NOHEAT_LOWPRECISION\", 0xE0, \"No heat low precision\", 0.001),\n" +
        "    (\"HIGHHEAT_HIGHPRECISION\", 0x39, \"High heat high precision\", 1.1),\n" +
        "    (\"HIGHHEAT_MEDPRECISION\", 0x32, \"High heat med precision\", 1.1),\n" +
        "    (\"HIGHHEAT_LOWPRECISION\", 0x24, \"High heat low precision\", 1.1),\n" +
        "    (\"MEDHEAT_HIGHPRECISION\", 0x2F, \"Med heat high precision\", 1.1),\n" +
        "    (\"MEDHEAT_MEDPRECISION\", 0x24, \"Med heat med precision\", 1.1),\n" +
        "    (\"MEDHEAT_LOWPRECISION\", 0x16, \"Med heat low precision\", 1.1),\n" +
        "))\n";

    [Fact]
    public void AddValues_BindsClassAttributesAndDicts()
    {
        var ir = Gen(
            Cv +
            "buf[0] = Mode.NOHEAT_HIGHPRECISION\n" +
            "buf[1] = Mode.is_valid(0xFD)\n" +
            "buf[2] = Mode.is_valid(0)\n" +
            "buf[3] = uint8(Mode.delay[0xFD] * 100)\n");

        LastStored(ir, 0).Should().Be(new Constant(0xFD),
            because: "setattr(cls, \"NOHEAT_HIGHPRECISION\", 0xFD) is Mode.NOHEAT_HIGHPRECISION");
        LastStored(ir, 1).Should().Be(new Constant(1),
            because: "0xFD is a key of cls.string after add_values");
        LastStored(ir, 2).Should().Be(new Constant(0),
            because: "0 is not a Mode code, so is_valid is false");
        LastStored(ir, 3).Should().Be(new Constant(1),
            because: "cls.delay[0xFD] is 0.01 and 0.01 * 100 is 1");
    }

    [Fact]
    public void AddValues_Integer256_IsANumberNotTheFirstInternedString()
    {
        var ir = Gen(
            "from pymcu.types import uint8, uint16\n" +
            "buf = bytearray([0, 0])\n" +
            "class CV:\n" +
            "    @classmethod\n" +
            "    def add_values(cls, value_tuples):\n" +
            "        cls.string = {}\n" +
            "        for value_tuple in value_tuples:\n" +
            "            name, value, string = value_tuple\n" +
            "            setattr(cls, name, value)\n" +
            "            cls.string[value] = string\n" +
            "    @classmethod\n" +
            "    def is_valid(cls, value: uint16) -> uint8:\n" +
            "        return 1 if value in cls.string else 0\n" +
            "class Mode(CV):\n" +
            "    pass\n" +
            "Mode.add_values(((\"WIDE\", 256, \"wide duty\"),))\n" +
            "buf[0] = Mode.WIDE\n" +
            "buf[1] = Mode.is_valid(256)\n");

        LastStored(ir, 0).Should().Be(new Constant(256),
            because: "setattr(cls, \"WIDE\", 256) is the integer 256, not interned-string id 256");
        LastStored(ir, 1).Should().Be(new Constant(1),
            because: "256 remains a dict key after add_values, so is_valid is true");
    }

    [Fact]
    public void ClassmethodFactory_ReturnsAConstant()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "buf = bytearray([0])\n" +
            "class A:\n" +
            "    @classmethod\n" +
            "    def make(cls) -> uint8:\n" +
            "        return 77\n" +
            "buf[0] = A.make()\n");

        LastStored(ir, 0).Should().Be(new Constant(77),
            because: "A.make() expands with cls bound to A and returns 77");
    }

    [Fact]
    public void ClassmethodClsCall_ConstructsTheReceiverClass()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "buf = bytearray([0])\n" +
            "class Util:\n" +
            "    @classmethod\n" +
            "    def make(cls):\n" +
            "        return cls()\n" +
            "    def __init__(self):\n" +
            "        self.value = 5\n" +
            "m = Util.make()\n" +
            "buf[0] = m.value\n");

        LastStored(ir, 0).Should().Be(new Constant(5),
            because: "return cls() inside Util.make is Util()");
    }
}
