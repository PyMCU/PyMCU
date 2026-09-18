using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#466. <c>os.uname()</c> is a compile-time five-field record of the part
/// this firmware was built for. Adafruit DHT writes <c>"Linux" not in uname()</c>;
/// platformdetect writes <c>"RP2350" in uname().machine</c>. Both are facts the
/// compiler already has as <c>__CHIP__</c>, not an operating system.
///
/// The unit tests use a local <c>uname_result</c> of the same shape as
/// <c>lib/src/pymcu/os.py</c>: IR generation does not load the stdlib file.
/// The driver and AVR fixtures import the real module.
/// </summary>
[Trait("Issue", "466")]
public class OsUnameTests
{
    private const string UnameShape =
        "from pymcu.types import uint8, inline, const\n" +
        "class uname_result:\n" +
        "    def __init__(self, sysname, nodename, release, version, machine):\n" +
        "        self.sysname = sysname\n" +
        "        self.nodename = nodename\n" +
        "        self.release = release\n" +
        "        self.version = version\n" +
        "        self.machine = machine\n" +
        "    @inline\n" +
        "    def __contains__(self, item: const[str]) -> bool:\n" +
        "        return item == \"PyMCU\" or item == \"\" or item == self.machine\n" +
        "@inline\n" +
        "def uname():\n" +
        "    return uname_result(\"PyMCU\", \"\", \"\", \"\", \"rp2350 RP2350\")\n" +
        "buf = bytearray(1)\n";

    private static ProgramIR Gen(string src)
    {
        var config = new DeviceConfig { Arch = "avr", Chip = "atmega328p" };
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
    public void LinuxNotInUnameCall_IsTrue()
    {
        var ir = Gen(UnameShape + "buf[0] = 1 if \"Linux\" not in uname() else 0\n");

        LastStored(ir, 0).Should().Be(new Constant(1),
            because: "Adafruit DHT's 'Linux not in uname()' must fold: sysname is PyMCU, never Linux");
    }

    [Fact]
    public void PyMcuInUnameCall_IsTrue()
    {
        var ir = Gen(UnameShape + "buf[0] = 1 if \"PyMCU\" in uname() else 0\n");

        LastStored(ir, 0).Should().Be(new Constant(1),
            because: "uname() is iterable over its five fields, so sysname membership is True");
    }

    [Fact]
    public void LinuxNotInABoundUname_IsTheSame()
    {
        var ir = Gen(UnameShape +
            "u = uname()\n" +
            "buf[0] = 1 if \"Linux\" not in u else 0\n");

        LastStored(ir, 0).Should().Be(new Constant(1),
            because: "a name bound to the result already dispatched __contains__; that path must stay");
    }

    [Fact]
    public void Rp2350InUnameMachine_IsASubstring()
    {
        var ir = Gen(UnameShape +
            "buf[0] = 1 if \"RP2350\" in uname().machine else 0\n");

        LastStored(ir, 0).Should().Be(new Constant(1),
            because: "platformdetect writes 'RP2350 in uname().machine'; that is a compile-time substring");
    }
}
