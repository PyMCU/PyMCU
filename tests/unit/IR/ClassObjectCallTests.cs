using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// Calling a method through the class object, `A.f(x)`, issue #201.
//
// It emitted a call to `A_f` into a build that defines no `A_f`, so the two halves disagreed
// inside one stage and the reader was shown a symbol and a byte offset by avr-ld. The
// `@staticmethod` decorator had nothing to do with it: it is dropped by the parser, and the
// same program without it failed the same way.
//
// What decided the shape of the fix is that ONE spelling already worked, and is in the AVR
// suite: `@staticmethod @inline` on `Math.clamp(...)`. `@inline` expands the body at the call
// site, so no symbol is needed. Without it the method reached `IsOutlineSafe`, which refuses
// an empty field layout, and a class with no fields has one; so the method was registered for
// expansion only and never compiled, while the call site went on naming it.
//
// A method with no `self` parameter has no receiver, so the layout that decision turns on says
// nothing about it. It is compiled as an ordinary function under the class prefix, which is
// the name the call site already forms.
//
// A method that DOES take self cannot be called that way at all, and now says so where it is
// written instead of arriving as an arity count on a mangled name.
//
// WHAT DISCRIMINATES: the four cases below the first heading. Against the unfixed compiler the
// first two build a call to a function the same program does not contain, and the last two
// report `Function 'A_f' expects 2 arguments, but 1 were provided`.
//
// WHAT IS INVARIANT: the `@inline` spelling that already worked, the ordinary instance call,
// and the unbound base call from #131, whose argument count matches and which must keep
// reaching the body `super()` reaches.
public class ClassObjectCallTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static PyMCU.Common.CompilerError Reject(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src));

    private static List<string> CalleesIn(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>().Select(c => c.FunctionName).ToList();

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8, inline\n\n\n";

    private static string Program(string cls, string body) =>
        Preamble + cls + "\n\ndef main():\n    seed: uint8 = GPIOR0.value\n" + body;

    private const string StaticShaped =
        "class A:\n" +
        "    @staticmethod\n" +
        "    def f(x: uint8) -> uint8:\n" +
        "        return x + 7\n";

    // --- what the fix makes true ------------------------------------------------

    [Fact]
    public void AMethodCalledThroughTheClassIsAlsoDefined()
    {
        // The whole defect in one assertion: the call and the definition have to be in the
        // same image. Asserting only that it builds passes on the bug, which built too.
        var ir = Gen(Program(StaticShaped, "    GPIOR1.value = A.f(seed)\n"));

        Assert.Contains("A_f", CalleesIn(ir));
        Assert.Contains(ir.Functions, f => f.Name == "A_f");
    }

    [Fact]
    public void TheDecoratorIsNotWhatMakesItWork()
    {
        // @staticmethod is dropped by the parser, so the same class without it is the same
        // program. It failed identically before and has to work identically now.
        var ir = Gen(Program(
            "class A:\n    def f(x: uint8) -> uint8:\n        return x + 7\n",
            "    GPIOR1.value = A.f(seed)\n"));

        Assert.Contains(ir.Functions, f => f.Name == "A_f");
    }

    [Fact]
    public void OnAnInstanceTheArgumentIsTheArgument()
    {
        // The second half of the issue: the receiver used to be bound to the first parameter,
        // so `x` took the role of self and the argument had nowhere to go, reported as
        // "name 'x' is not defined" about a parameter declared one line above.
        var ir = Gen(Program(
            "class A:\n" +
            "    def __init__(self):\n        pass\n" +
            "    @staticmethod\n" +
            "    def f(x: uint8) -> uint8:\n        return x + 7\n",
            "    a = A()\n    GPIOR1.value = a.f(seed)\n"));

        var call = ir.Functions.SelectMany(f => f.Body).OfType<Call>().Single(c => c.FunctionName == "A_f");
        Assert.Single(call.Args);
    }

    [Fact]
    public void AMethodThatTakesSelfSaysSoInsteadOfCountingArguments()
    {
        var ex = Reject(Program(
            "class A:\n" +
            "    def __init__(self):\n        self.n: uint8 = 0\n" +
            "    def f(self, x: uint8) -> uint8:\n        return x + 7\n",
            "    GPIOR1.value = A.f(seed)\n"));

        Assert.Contains("'A.f(...)'", ex.Message);
        Assert.Contains("takes 'self'", ex.Message);
        Assert.DoesNotContain("expects 2 arguments", ex.Message);
    }

    [Fact]
    public void TheRefusalOffersBothWaysOut()
    {
        var ex = Reject(Program(
            "class A:\n" +
            "    def __init__(self):\n        self.n: uint8 = 0\n" +
            "    def f(self, x: uint8) -> uint8:\n        return x + 7\n",
            "    GPIOR1.value = A.f(seed)\n"));

        Assert.Contains("obj.f(...)", ex.Message);
        Assert.Contains("drop 'self'", ex.Message);
    }

    // --- invariants -------------------------------------------------------------

    [Fact]
    public void TheInlineSpellingStillExpandsRatherThanCalling()
    {
        // fixtures/static-method in pymcu-avr is built on this and runs on silicon. @inline
        // means the body is expanded at the call site, so there is no call at all, and that
        // has to stay true: compiling it standalone as well would be a second copy.
        var ir = Gen(Program(
            "class A:\n" +
            "    @staticmethod\n    @inline\n" +
            "    def f(x: uint8) -> uint8:\n        return x + 7\n",
            "    GPIOR1.value = A.f(seed)\n"));

        Assert.DoesNotContain("A_f", CalleesIn(ir));
    }

    [Fact]
    public void AnOrdinaryInstanceMethodIsUnchanged()
    {
        var ir = Gen(Program(
            "class A:\n" +
            "    def __init__(self):\n        self.n: uint8 = 3\n" +
            "    def f(self, x: uint8) -> uint8:\n        return x + self.n\n",
            "    a = A()\n    GPIOR1.value = a.f(seed)\n"));

        Assert.Contains(ir.Functions, f => f.Name == "A_f");
    }

    [Fact]
    public void TheUnboundBaseCallIsUntouched()
    {
        // #131: `Base.__init__(self, x)` is the same shape as the call being refused above,
        // and it is legal Python that reaches the body super() reaches. Its argument count
        // matches, which is what tells the two apart.
        Gen(Preamble +
            "class Base:\n" +
            "    def __init__(self, offset: uint8):\n        self.offset: uint8 = offset\n" +
            "    def read(self, raw: uint8) -> uint8:\n        return raw + self.offset\n" +
            "class Child(Base):\n" +
            "    def __init__(self, offset: uint8):\n        Base.__init__(self, offset)\n" +
            "\n" +
            "def main():\n" +
            "    seed: uint8 = GPIOR0.value\n" +
            "    c = Child(10)\n" +
            "    GPIOR1.value = c.read(seed)\n");
    }
}
