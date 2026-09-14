using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#349. A keyword argument to a base-class method reached VisitExpression as a value and
/// came out as "Unknown Expression type: KeywordArgExpr" -- the name of a class in this
/// compiler, about a program containing no such word. `super().__init__(pwm_out,
/// min_pulse=min_pulse, max_pulse=max_pulse)` is line 110 of adafruit_motor/servo.py.
///
/// Base methods now bind by name. Any other callee that still cannot says which argument it is
/// and what to write, so no path prints an AST class name.
///
/// The VALUES each parameter receives are asserted in avr8sharp by the AVR fixture.
/// </summary>
public class KeywordArgToMethodTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private const string Base3 =
        "class Base:\n" +
        "    def __init__(self, a: uint8, b: uint8 = 1, c: uint8 = 2):\n" +
        "        self.a = a\n" +
        "        self.b = b\n" +
        "        self.c = c\n\n";

    [Fact]
    public void SuperInitWithAKeyword_Compiles()
    {
        Assert.NotNull(Gen(Base3 +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        super().__init__(a, b=7)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
    }

    [Fact]
    public void AKeywordThatSkipsADefault_Compiles()
    {
        Assert.NotNull(Gen(Base3 +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        super().__init__(a, c=9)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
    }

    [Fact]
    public void TheUnboundSpellingTakesKeywordsToo()
    {
        Assert.NotNull(Gen(Base3 +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        Base.__init__(self, a, c=9)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
    }

    [Fact]
    public void AnUnknownKeyword_IsNamed()
    {
        Assert.Contains("unknown keyword argument 'zz'", Refusal(Base3 +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        super().__init__(a, zz=7)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
    }

    [Fact]
    public void ARepeatedKeyword_IsNamed()
    {
        Assert.Contains("repeated", Refusal(Base3 +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        super().__init__(a, b=7, b=8)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
    }

    [Fact]
    public void ATwiceFilledParameter_IsNamed()
    {
        Assert.Contains("multiple values for argument 'b'", Refusal(Base3 +
            "class Sub(Base):\n" +
            "    def __init__(self, a: uint8):\n" +
            "        super().__init__(a, 5, b=8)\n\n" +
            "def main():\n" +
            "    s = Sub(3)\n"));
    }

    [Fact]
    public void NoRefusalAnywhere_PrintsTheNameOfAnAstClass()
    {
        // The safety net. A callee with no binding path must still say which argument, so this
        // asserts the absence of the old sentence rather than the presence of a new one.
        string msg = Refusal(
            "class Base:\n" +
            "    def __init__(self):\n" +
            "        self.a: uint8 = 0\n\n" +
            "    def set(self, a: uint8, b: uint8 = 1):\n" +
            "        self.a = a + b\n\n" +
            "def main():\n" +
            "    o = Base()\n" +
            "    o.set(3, b=7)\n");
        Assert.DoesNotContain("KeywordArgExpr", msg);
        Assert.DoesNotContain("Unknown Expression type", msg);
        Assert.Contains("'b='", msg);
    }
}
