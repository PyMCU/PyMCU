using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#305. `with C(...) as v:` binds v as a pure alias of the context manager: nothing is
/// registered under v, because v IS the manager. Reading a field through v worked, because
/// that path walks the alias chain. A METHOD call did not walk it, looked the bare name up in
/// the instance registry, found nothing, and degraded into a free function named `v_method`.
///
/// `with digitalio.DigitalInOut(board.D6) as pin:` then `pin.switch_to_output(True)` -- the
/// CircuitPython idiom, and the whole point of the `with` form -- was refused as a call to an
/// undefined `pin_switch_to_output`.
/// </summary>
public class WithAsMethodCallTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Box =
        "from pymcu.types import uint8, inline\n\n" +
        "class Box:\n" +
        "    def __init__(self, n: uint8):\n" +
        "        self.n = n\n" +
        "    @inline\n" +
        "    def __enter__(self):\n" +
        "        return self\n" +
        "    @inline\n" +
        "    def __exit__(self, a=None, b=None, c=None):\n" +
        "        self.n = 0\n" +
        "    @inline\n" +
        "    def bump(self):\n" +
        "        self.n = self.n + 1\n\n";

    /// A call to a function whose name was built from the receiver's own name is the defect:
    /// `b.bump()` must not become `b_bump`.
    private static void AssertNoCallNamedAfterTheAlias(ProgramIR ir, string alias)
    {
        foreach (var f in ir.Functions)
            foreach (var i in f.Body)
                if (i is Call c)
                    Assert.False(c.FunctionName.StartsWith(alias + "_"),
                        $"the method resolved to '{c.FunctionName}', built from the alias name");
    }

    [Fact]
    public void AMethodOnTheAsName_ResolvesAtModuleLevel()
    {
        var ir = Gen(Box +
            "out: uint8 = 0\n" +
            "with Box(5) as b:\n" +
            "    b.bump()\n" +
            "    out = b.n\n");

        Assert.NotNull(ir);
        AssertNoCallNamedAfterTheAlias(ir, "b");
    }

    [Fact]
    public void AMethodOnTheAsName_ResolvesInsideAFunction()
    {
        var ir = Gen(Box +
            "out: uint8 = 0\n" +
            "def main():\n" +
            "    with Box(5) as b:\n" +
            "        b.bump()\n" +
            "        b.bump()\n" +
            "        out = b.n\n");

        Assert.NotNull(ir);
        AssertNoCallNamedAfterTheAlias(ir, "b");
    }

    [Fact]
    public void AFieldOnTheAsName_StillResolves()
    {
        // The path that always worked, kept here so a fix that moved the alias lookup would
        // have to keep both halves working rather than swap which one is broken.
        var ir = Gen(Box +
            "out: uint8 = 0\n" +
            "with Box(7) as b:\n" +
            "    out = b.n\n");

        Assert.NotNull(ir);
    }

    [Fact]
    public void AMethodOnTheManagersOwnName_IsUnaffected()
    {
        // `with m as b:` over a name that is already an instance: the receiver was never an
        // alias and resolved before this change.
        var ir = Gen(Box +
            "out: uint8 = 0\n" +
            "m = Box(5)\n" +
            "with m as b:\n" +
            "    m.bump()\n" +
            "    out = m.n\n");

        Assert.NotNull(ir);
        AssertNoCallNamedAfterTheAlias(ir, "m");
    }
}
