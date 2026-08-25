using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Too many positional arguments in a call to a constructor or an @inline function (PyMCU#136).
///
/// The binder stopped at the end of the parameter list and dropped the rest, so `Box(3, 99)` for
/// `__init__(self, a)` built clean and the 99 vanished. A free function has always rejected this.
/// What it cost: `UART(0, 9600)`, the MicroPython spelling, bound baud to the 0 and dropped the
/// 9600, and a divisor of zero is 1 Mbaud on a 16 MHz part. Three programs in this project were
/// written that way and had been running at the wrong rate.
/// </summary>
public class ConstructorArityTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    private const string BoxClass =
        "class Box:\n" +
        "    def __init__(self, a: uint8):\n" +
        "        self.a = a\n" +
        "    def get(self) -> uint8:\n" +
        "        return self.a\n";

    [Fact]
    public void AnExtraArgumentToAConstructor_IsRejectedByTheNameTheUserWrote()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            BoxClass +
            "def main():\n" +
            "    b = Box(3, 99)\n" +
            "    print(b.get())\n"));

        Assert.Contains("too many arguments", ex.Message);
        Assert.Contains("'Box'", ex.Message);
        Assert.Contains("1 argument", ex.Message);
        Assert.Contains("2 were provided", ex.Message);
        // Not the mangled internal name, which is not something anyone typed.
        Assert.DoesNotContain("___init__", ex.Message);
    }

    [Fact]
    public void TheRightNumberOfArguments_StillCompiles()
    {
        var ir = Gen(
            BoxClass +
            "def main():\n" +
            "    b = Box(3)\n" +
            "    x: uint8 = b.get()\n");

        Assert.NotNull(ir);
    }

    [Fact]
    public void AnExtraArgumentToAnInlineFunction_IsRejected()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "@inline\n" +
            "def twice(v: uint8) -> uint8:\n" +
            "    return v + v\n" +
            "def main():\n" +
            "    print(twice(3, 4))\n"));

        Assert.Contains("too many arguments", ex.Message);
        Assert.Contains("'twice'", ex.Message);
    }

    [Fact]
    public void AMethodTakingNothingButSelf_IsNotCountedAgainstThatSelf()
    {
        // The receiver assumption gives a dotted call the offset a method would have. Counting
        // against a callee that does not actually take self turned a correct call into
        // "expects -1 argument(s)"; the compat-cp-alarm fixture in pymcu-avr is where that shape
        // shows up with real modules, which this harness has none of.
        var ir = Gen(
            "class Ticker:\n" +
            "    def __init__(self):\n" +
            "        self.n = 0\n" +
            "    def read(self) -> uint8:\n" +
            "        return self.n\n" +
            "def main():\n" +
            "    t = Ticker()\n" +
            "    x: uint8 = t.read()\n");

        Assert.NotNull(ir);
    }
}
