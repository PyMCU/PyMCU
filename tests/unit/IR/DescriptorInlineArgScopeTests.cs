using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An @inline constructor's tuple argument keeps the scope it was written in. The
/// elements of `S((expr, ...))` are the CALLER's AST, evaluated late -- from inside
/// `S.__init__`'s expansion, where the module, the inline prefix and `self` all
/// belong to the callee. Bound bare, `_h(...)` inside `__get__` (written in drv.py)
/// resolved against lib1's namespace ("call to undefined function '_h'"), and
/// `self.k` resolved against `lib1_S` instead of `Desc` ("'lib1_S' object has no
/// attribute 'k'").
///
/// The second shape pinned here is the binding `s = <expansion result>`: the result
/// temp aliases the constructed instance's anchor, and `s` must inherit that anchor
/// -- not the scratch temp, where member reads stop (Temporary.IsScratchName ends
/// the alias walk). Bound to the temp, `s.a` flattened to an `s_a` slot nothing
/// wrote and the descriptor read answered 0.
/// </summary>
public class DescriptorInlineArgScopeTests
{
    private const string Lib =
        "from pymcu.types import inline, int16\n" +
        "class S:\n" +
        "    @inline\n" +
        "    def __init__(self, t):\n" +
        "        self.a: int16 = t[0]\n" +
        "        self.b: int16 = t[1]\n";

    private const string Drv =
        "import lib1\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "def _h(v: int) -> int:\n" +
        "    return v + 1\n" +
        "class Desc:\n" +
        "    def __init__(self, k: int):\n" +
        "        self.k = k\n" +
        "    def __get__(self, obj, objtype=None):\n" +
        "        return lib1.S((_h(GPIOR0.value) + 2000, self.k))\n" +
        "class Dev:\n" +
        "    when = Desc(5)\n" +
        "    def __init__(self):\n" +
        "        pass\n";

    private const string DrvMethod =
        "import lib1\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "def _h(v: int) -> int:\n" +
        "    return v + 1\n" +
        "class W:\n" +
        "    def __init__(self):\n" +
        "        pass\n" +
        "    def make(self):\n" +
        "        return lib1.S((_h(GPIOR0.value) + 2000, 7))\n";

    private static ProgramIR Gen(string main, string drvSrc)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["lib1"] = new Parser(new Lexer(Lib).Tokenize()).ParseProgram(),
            ["drv"] = new Parser(new Lexer(drvSrc).Tokenize()).ParseProgram(),
        };
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "lib1", "drv" });
        return Optimizer.Optimize(ir);
    }

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    [Fact]
    public void ADescriptorResultAssignedToAName_KeepsCallerScopeAndInstance()
    {
        var ir = Gen(
            "import drv\n" +
            "buf = bytearray([0, 0])\n" +
            "d = drv.Dev()\n" +
            "s = d.when\n" +
            "buf[0] = s.a\n" +
            "buf[1] = s.b\n", Drv);
        var body = ir.Functions.SelectMany(f => f.Body).ToList();

        // `_h` is drv's helper: it must be CALLED (with a real destination), not
        // looked up inside lib1 and refused.
        Assert.Contains(body.OfType<Call>(),
            c => c.FunctionName == "drv__h" && c.Dst is Temporary);

        // `buf[0] = s.a` reads the field the ctor stored into the instance `s`
        // names -- a runtime value, so the read is a Variable of that anchor's
        // flattened field, never an `s_a` slot the ctor did not write.
        var stored = LastStored(ir, 0);
        var v = Assert.IsType<Variable>(stored);
        Assert.EndsWith("_a", v.Name);
        Assert.DoesNotContain("s_a", v.Name);

        // `buf[1] = s.b` is `self.k` on the descriptor instance (5), evaluated in
        // drv's scope -- a compile-time answer.
        Assert.Equal(new Constant(5), LastStored(ir, 1));
    }

    [Fact]
    public void AnInlineMethodResultAssignedToAName_AliasesTheConstructedAnchor()
    {
        var ir = Gen(
            "import drv\n" +
            "buf = bytearray([0, 0])\n" +
            "w = drv.W()\n" +
            "s = w.make()\n" +
            "buf[0] = s.a\n" +
            "buf[1] = s.b\n", DrvMethod);
        var body = ir.Functions.SelectMany(f => f.Body).ToList();

        Assert.Contains(body.OfType<Call>(),
            c => c.FunctionName == "drv__h" && c.Dst is Temporary);
        var stored = LastStored(ir, 0);
        var v = Assert.IsType<Variable>(stored);
        Assert.EndsWith("_a", v.Name);
        Assert.DoesNotContain("s_a", v.Name);
        Assert.Equal(new Constant(7), LastStored(ir, 1));
    }
}
