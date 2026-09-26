using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `with M(k):` and `with self.dev:` with no `as` ran the body alone: nothing constructed
/// the manager, and neither __enter__ nor __exit__ ran, with no diagnostic. Only the `as`
/// form gave the manager a name first. The lock idiom (`with self.i2c_device:`) is this.
/// </summary>
public class WithWithoutAsTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Src(string dec, string with) =>
        "from pymcu.types import uint8, uint16, ptr, inline\n" +
        "class M:\n" +
        "    def __init__(self, k: uint16):\n" +
        "        self.k: uint16 = k\n" +
        dec + "    def __enter__(self):\n" +
        "        p: ptr[uint8] = ptr(self.k)\n" +
        "        p.value = 0x33\n" +
        "        return self\n" +
        dec + "    def __exit__(self, a, b, c):\n" +
        "        p: ptr[uint8] = ptr(self.k)\n" +
        "        p.value = 0x44\n" +
        "class Owner:\n" +
        "    def __init__(self):\n" +
        "        self.m = M(0x0610)\n" +
        "    def run(self):\n" +
        "        with self.m:\n" +
        "            pass\n" +
        with;

    // Both halves ran: the program stores __enter__'s 0x33 and __exit__'s 0x44.
    private static void AssertEnterAndExitRan(ProgramIR ir)
    {
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        bool enter = body.Any(i => i.ToString()!.Contains("Value = 51"));
        bool exit = body.Any(i => i.ToString()!.Contains("Value = 68"));
        Assert.True(enter && exit, $"enter={enter} exit={exit}");
    }

    [Theory]
    [InlineData("    @inline\n")]
    [InlineData("")]
    public void AManagerConstructedInTheStatement_Enters(string dec) =>
        AssertEnterAndExitRan(Gen(Src(dec, "with M(0x0601):\n    pass\n")));

    [Theory]
    [InlineData("    @inline\n")]
    [InlineData("")]
    public void AManagerHeldInAField_Enters(string dec) =>
        AssertEnterAndExitRan(Gen(Src(dec, "o = Owner()\no.run()\n")));
}
