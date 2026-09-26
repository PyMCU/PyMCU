using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The shape a signature's `*` and `/` give a call (PEP 3102, PEP 570), refused at compile
/// time with CPython's own sentences. A keyword-only parameter was bound by position --
/// `f(x, 7)` against `def f(size, *, order=3)` set `order` to 7 where CPython raises
/// TypeError -- and `/` was a syntax error in this front end while the other one accepted
/// it and did not enforce it (PyMCU#389).
/// </summary>
public class SignatureShapeTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Head =
        "from pymcu.types import uint8, ptr, inline\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n";

    private const string KwFunc = "def f(size: uint8, *, order: uint8 = 3) -> uint8:\n    return size + order\n";
    private const string PosFunc = "def g(a: uint8, b: uint8, /) -> uint8:\n    return a * 10 + b\n";

    [Fact]
    public void AKeywordOnlyParameterPassedByPosition_IsRefused()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            Gen(Head + KwFunc + "n: uint8 = f(GPIOR0.value, 7)\n"));
        Assert.Equal("f() takes 1 positional argument but 2 were given", ex.Message);
    }

    [Fact]
    public void AKeywordOnlyParameterPassedByName_Compiles() =>
        Gen(Head + KwFunc + "n: uint8 = f(GPIOR0.value, order=7)\n");

    [Fact]
    public void ThePositionalOnlyMarker_Parses() =>
        Gen(Head + PosFunc + "n: uint8 = g(GPIOR0.value, 2)\n");

    [Fact]
    public void APositionalOnlyParameterPassedByName_IsRefused()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            Gen(Head + PosFunc + "n: uint8 = g(a=GPIOR0.value, b=2)\n"));
        Assert.Equal("g() got some positional-only arguments passed as keyword arguments: 'a, b'",
            ex.Message);
    }

    [Fact]
    public void AnInlineFunction_IsChecked()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            Gen(Head + "@inline\n" + KwFunc + "n: uint8 = f(GPIOR0.value, 7)\n"));
        Assert.Equal("f() takes 1 positional argument but 2 were given", ex.Message);
    }

    [Fact]
    public void AConstructor_CountsSelfAsCPythonDoes()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(Head +
            "class F:\n" +
            "    def __init__(self, size: uint8, *, order: uint8 = 3):\n" +
            "        self.v = size + order\n" +
            "o = F(GPIOR0.value, 7)\n"));
        Assert.Equal("F.__init__() takes 2 positional arguments but 3 were given", ex.Message);
    }

    [Fact]
    public void AMethod_IsChecked()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(Head +
            "class C:\n" +
            "    def __init__(self):\n" +
            "        self.z = GPIOR0.value\n" +
            "    def g(self, a: uint8, b: uint8, /) -> uint8:\n" +
            "        return a * 10 + b + self.z\n" +
            "c = C()\n" +
            "n: uint8 = c.g(a=1, b=2)\n"));
        Assert.Equal("C.g() got some positional-only arguments passed as keyword arguments: 'a, b'",
            ex.Message);
    }

    [Fact]
    public void KeywordOnlyAfterVarArgs_TakesAnyPositionals() =>
        Gen(Head +
            "def h(a: uint8, *rest, k: uint8 = 1) -> uint8:\n" +
            "    return a + k\n" +
            "n: uint8 = h(GPIOR0.value, 2, 3, k=4)\n");

    [Fact]
    public void TheRangeForm_NamesTheDefaults()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(Head +
            "def f(a: uint8, b: uint8 = 2, *, c: uint8 = 3) -> uint8:\n    return a + b + c\n" +
            "n: uint8 = f(1, 2, 3)\n"));
        Assert.Equal("f() takes from 1 to 2 positional arguments but 3 were given", ex.Message);
    }
}
