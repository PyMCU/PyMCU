using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `if pull == 1:` with `pull` bound to None is decided where it is read: None equals nothing but
/// None. Left to the run-time path, a None operand has no storage to compare, and both arms of an
/// `if/elif` chain were emitted one after the other -- `Pin(7, Pin.IN, pull=None)` wrote the
/// pull-up and then the pull-down.
/// </summary>
public class NoneComparedToNumberTests
{
    private static List<int> Stores(string body)
    {
        var src =
            "from pymcu.types import uint8, const, inline, ptr\n" +
            "G: ptr[uint8] = ptr(0x3E)\n" +
            "@inline\n" +
            "def f(a: const = -1):\n" + body +
            "def main():\n" +
            "    f(None)\n";
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
        return ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>()
            .Where(c => c.Dst is MemoryAddress && c.Src is Constant)
            .Select(c => (int)((Constant)c.Src).Value).ToList();
    }

    [Fact]
    public void AnEqualityChainOverNone_RunsNoNumberedArm()
    {
        var s = Stores(
            "    if a == 1:\n        G.value = 11\n" +
            "    elif a == 2:\n        G.value = 22\n");
        s.Should().NotContain(11).And.NotContain(22);
    }

    [Fact]
    public void AnInequalityOverNone_IsTrue()
    {
        var s = Stores("    if a != 1:\n        G.value = 33\n");
        s.Should().Contain(33);
    }

    [Fact]
    public void NoneEqualToNone_IsTrue()
    {
        var s = Stores("    if a == None:\n        G.value = 44\n");
        s.Should().Contain(44);
    }
}
