using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#350. `super().__init__(a)` left the base constructor's defaulted parameters unbound,
/// and the base body then read one of them: "name 'b' is not defined -- it is read here but
/// never assigned, imported, or received as a parameter", about a parameter, one line under
/// its own declaration.
///
/// The VALUES each default carries are asserted in avr8sharp by the AVR fixture.
/// </summary>
public class SuperInitDefaultsTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Base3 =
        "class Base:\n" +
        "    def __init__(self, a: uint8, b: uint8 = 1, c: uint8 = 2):\n" +
        "        self.a = a\n" +
        "        self.b = b\n" +
        "        self.c = c\n\n";

    [Fact]
    public void ForwardingOnlyTheRequiredArgument_Compiles()
    {
        Assert.NotNull(Gen(Base3 +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        super().__init__(a)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
    }

    [Fact]
    public void TheUnboundSpellingFillsThemToo()
    {
        Assert.NotNull(Gen(Base3 +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        Base.__init__(self, a)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
    }

    [Fact]
    public void EveryArgumentGivenPositionally_IsUnchanged()
    {
        Assert.NotNull(Gen(Base3 +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        super().__init__(a, 5, 6)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
    }

    [Fact]
    public void ARequiredParameterNoArgumentReaches_IsNamed()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "class Base:\n" +
            "    def __init__(self, a: uint8, b: uint8):\n" +
            "        self.a = a\n" +
            "        self.b = b\n\n" +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        super().__init__(a)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
        Assert.Contains("missing argument 'b'", ex.Message);
        Assert.DoesNotContain("never assigned, imported, or received as a parameter", ex.Message);
    }
}
