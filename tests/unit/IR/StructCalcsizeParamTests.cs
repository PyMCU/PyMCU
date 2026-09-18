using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>struct.calcsize(fmt)</c> folds a format held in a <c>str</c> parameter.
/// Adafruit StructArray writes <c>_fit(struct.calcsize(struct_format))</c> with
/// <c>struct_format: str</c> and a class-body <c>StructArray(0x06, "&lt;HH", 16)</c>.
/// The literal reached the parameter as an interned id; calcsize refused a format
/// the compiler was holding.
/// </summary>
public class StructCalcsizeParamTests
{
    private const string StructShim =
        "def calcsize(fmt):\n    pass\n" +
        "def unpack_from(fmt, buf, offset=0):\n    pass\n" +
        "def pack_into(fmt, buf, offset, value):\n    pass\n";

    private const string Fit =
        "import struct\n" +
        "_BUFFER = bytearray(1)\n" +
        "out = bytearray([0])\n" +
        "def _fit(size: uint8) -> None:\n" +
        "    if len(_BUFFER) < 1 + size:\n" +
        "        _BUFFER.extend(bytes(1 + size - len(_BUFFER)))\n";

    private static ProgramIR Gen(string mainSrc) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>
                { ["struct"] = new Parser(new Lexer(StructShim).Tokenize()).ParseProgram() },
            new DeviceConfig { Arch = "avr" }));

    private static ProgramIR GenImported(string pack, string sensor, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["struct"] = new Parser(new Lexer(StructShim).Tokenize()).ParseProgram(),
            ["pack"] = new Parser(new Lexer(pack).Tokenize()).ParseProgram(),
            ["sensor"] = new Parser(new Lexer(sensor).Tokenize()).ParseProgram(),
        };
        return Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "pack", "sensor" }));
    }

    private static int SizeOfGlobalArray(ProgramIR ir, string name) =>
        ir.Functions.SelectMany(f => f.Body)
            .Select(i => i switch
            {
                ArrayStore s when s.ArrayName.EndsWith(name) => s.Count,
                ArrayLoad l when l.ArrayName.EndsWith(name) => l.Count,
                _ => 0,
            })
            .DefaultIfEmpty(0).Max();

    private static Val LastStored(ProgramIR ir, string array) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.ArrayName.EndsWith(array))
            .Select(s => s.Src).Last();

    [Fact]
    public void CalcsizeOfAStrParameter_FoldsTheLiteral()
    {
        var ir = Gen(
            "import struct\n" +
            "buf = bytearray([0])\n" +
            "@inline\n" +
            "def size_of(fmt: str) -> uint8:\n" +
            "    return struct.calcsize(fmt)\n" +
            "buf[0] = size_of(\"<HH\")\n");

        LastStored(ir, "buf").Should().Be(new Constant(4),
            because: "calcsize(\"<HH\") through a str parameter is 4, not a runtime format");
    }

    [Fact]
    public void CalcsizeOfAKeywordStrParameter_FoldsTheLiteral()
    {
        var ir = Gen(
            "import struct\n" +
            "buf = bytearray([0])\n" +
            "@inline\n" +
            "def size_of(fmt: str) -> uint8:\n" +
            "    return struct.calcsize(fmt)\n" +
            "buf[0] = size_of(fmt=\"<HH\")\n");

        LastStored(ir, "buf").Should().Be(new Constant(4),
            because: "calcsize of a keyword str parameter still folds \"<HH\" to 4");
    }

    [Fact]
    public void CalcsizeOfANamedStrPassedThrough_FoldsTheLiteral()
    {
        var ir = Gen(
            "import struct\n" +
            "buf = bytearray([0])\n" +
            "@inline\n" +
            "def size_of(fmt: str) -> uint8:\n" +
            "    return struct.calcsize(fmt)\n" +
            "f = \"<HH\"\n" +
            "buf[0] = size_of(f)\n");

        LastStored(ir, "buf").Should().Be(new Constant(4),
            because: "a name bound to \"<HH\" passed to a str parameter is still the format");
    }

    [Fact]
    public void AClassBodyConstructorCalcsize_GrowsTheScratchBuffer()
    {
        var ir = Gen(Fit +
            "class Field:\n" +
            "    def __init__(self, register_address: uint8, struct_format: str, count: uint8) -> None:\n" +
            "        self.format = struct_format\n" +
            "        self.address = register_address\n" +
            "        self.count = count\n" +
            "        _fit(struct.calcsize(struct_format))\n" +
            "class Dev:\n" +
            "    regs = Field(0x06, \"<HH\", 16)\n" +
            "    def __init__(self) -> None:\n" +
            "        self.x: uint8 = 0\n" +
            "d = Dev()\n" +
            "out[0] = len(_BUFFER)\n");

        SizeOfGlobalArray(ir, "_BUFFER").Should().Be(5,
            because: "calcsize(\"<HH\") is 4, so _fit grows the 1-byte scratch buffer to 5");
        LastStored(ir, "out").Should().Be(new Constant(5),
            because: "len(_BUFFER) folds to the grown size, not the declared 1");
    }

    [Fact]
    public void AnImportedClassBodyConstructorCalcsize_GrowsTheScratchBuffer()
    {
        const string pack =
            "import struct\n" +
            "_BUFFER = bytearray(1)\n" +
            "def _fit(size: uint8) -> None:\n" +
            "    if len(_BUFFER) < 1 + size:\n" +
            "        _BUFFER.extend(bytes(1 + size - len(_BUFFER)))\n" +
            "class Field:\n" +
            "    def __init__(self, register_address: uint8, struct_format: str, count: uint8) -> None:\n" +
            "        self.format = struct_format\n" +
            "        _fit(struct.calcsize(struct_format))\n";
        const string sensor =
            "from pack import Field\n" +
            "class Dev:\n" +
            "    regs = Field(0x06, \"<HH\", 16)\n" +
            "    def __init__(self) -> None:\n" +
            "        self.x: uint8 = 0\n";
        var ir = GenImported(pack, sensor,
            "from sensor import Dev\n" +
            "from pack import _BUFFER\n" +
            "out = bytearray([0])\n" +
            "d = Dev()\n" +
            "out[0] = len(_BUFFER)\n");

        SizeOfGlobalArray(ir, "_BUFFER").Should().Be(5,
            because: "an imported class-body Field(\"<HH\") still grows the package _BUFFER to 5");
        LastStored(ir, "out").Should().Be(new Constant(5),
            because: "len of the imported grown buffer folds to 5, not bytearray(1)");
    }
}
