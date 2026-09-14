using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#319. A constant declared inside a class that is itself inside a class could not be
/// read: one level worked and two did not. Two things were missing, and each on its own would
/// have kept the refusal -- the scan never registered a nested class body's attributes, and
/// the read resolved `Outer.Inner.A` one hop at a time, so it asked for `Inner` as an
/// attribute of `Outer` and was told the object has none.
///
/// It is how CircuitPython spells the UART parity: `busio.UART.Parity.ODD`, which puts a
/// module hop in front of the same shape, so the canonical spelling did not compile and the
/// layer had to carry a name CircuitPython does not have.
/// </summary>
public class NestedClassConstantTests
{
    private static ProgramIR Gen(string mainSrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();

        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        foreach (var (name, _) in modules) ctx.ProjectModules.Add(name);

        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
    }

    private static List<int> RegisterWrites(ProgramIR ir)
    {
        var main = ir.Functions.Last(f => f.Name == "main");
        return main.Body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Select(c => c.Src is Constant k ? k.Value : -1)
            .ToList();
    }

    private const string Prelude =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n";

    [Fact]
    public void AConstantTwoClassNamesDeep_IsRead()
    {
        var ir = Gen(Prelude +
            "class Outer:\n" +
            "    class Inner:\n" +
            "        A = 7\n" +
            "    B = 3\n" +
            "\n" +
            "def main():\n" +
            "    GPIOR0.value = Outer.B\n" +
            "    GPIOR0.value = Outer.Inner.A\n");

        Assert.Equal(new List<int> { 3, 7 }, RegisterWrites(ir));
    }

    [Fact]
    public void ThreeDeep_IsReadToo()
    {
        var ir = Gen(Prelude +
            "class A:\n" +
            "    class B:\n" +
            "        class C:\n" +
            "            V = 9\n" +
            "\n" +
            "def main():\n" +
            "    GPIOR0.value = A.B.C.V\n");

        Assert.Equal(new List<int> { 9 }, RegisterWrites(ir));
    }

    [Fact]
    public void ThroughTheModuleThatDeclaresIt_IsReadAsWell()
    {
        // busio.UART.Parity.EVEN: a module hop in front of the two class hops.
        const string Busio =
            "class UART:\n" +
            "    class Parity:\n" +
            "        EVEN = 1\n" +
            "        ODD = 2\n";

        var ir = Gen(Prelude +
            "import busio\n" +
            "\n" +
            "def main():\n" +
            "    GPIOR0.value = busio.UART.Parity.ODD\n",
            ("busio", Busio));

        Assert.Equal(new List<int> { 2 }, RegisterWrites(ir));
    }

    [Fact]
    public void AnInstanceFieldReadIsUnaffected()
    {
        var ir = Gen(Prelude +
            "class Inner:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self.n = n\n" +
            "\n" +
            "class Outer:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self.inner = Inner(n)\n" +
            "\n" +
            "def main():\n" +
            "    o = Outer(5)\n" +
            "    GPIOR0.value = o.inner.n\n");

        Assert.Equal(new List<int> { 5 }, RegisterWrites(ir));
    }
}
