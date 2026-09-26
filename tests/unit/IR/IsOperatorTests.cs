using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `is` and `is None` answer what CPython answers, or refuse.
///
/// None-ness is a compile-time fact here, so `x is None` folds -- and it folded to False
/// wherever the None arrived through a position the fold did not ask about: the result of
/// a real subroutine that returns None, a local bound to such a call, a property or an
/// outlined method handing back a field marked None, and a parameter whose `= None` default
/// a shared subroutine cannot see. The first `a.p() is None` of a program also folded,
/// because the method's inferred union is registered by its first expansion.
///
/// Identity had the opposite fault: an instance has no run-time handle, so `n is t`
/// compared a number with a slot nothing writes and answered True for every n that read 0.
/// A value that is not an instance is never the same object as one; a value that may be an
/// instance flattened on the way in has no answer, and is refused.
/// </summary>
public class IsOperatorTests
{
    private const string Hdr =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<int> StoredConstants(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Select(c => c.Src).OfType<Constant>().Select(k => k.Value).ToList();

    // 7 on the `is` arm, 8 on the other one.
    private const string Branch =
        "    GPIOR0.value = 7\n" +
        "else:\n" +
        "    GPIOR1.value = 8\n";

    [Theory]
    [InlineData("    return None\n")]
    [InlineData("    return\n")]
    [InlineData("    GPIOR1.value = 1\n")]
    [InlineData("    x = None\n    return x\n")]
    public void ASubroutineThatReturnsNoneIsNone(string body)
    {
        // Two calls keep f a real subroutine. Its void call used to hand back the
        // return-register channel, which the fold read as "not None".
        var ir = Gen(Hdr + "def f():\n" + body +
            "f()\n" +
            "if f() is None:\n" + Branch);
        StoredConstants(ir).Should().Contain(7).And.NotContain(8);
    }

    [Fact]
    public void ALocalBoundToSuchACallIsNone()
    {
        var ir = Gen(Hdr + "def f():\n    return None\n" +
            "f()\n" +
            "x = f()\n" +
            "if x is None:\n" + Branch);
        StoredConstants(ir).Should().Contain(7).And.NotContain(8);
    }

    [Fact]
    public void APropertyReturningAFieldMarkedNoneIsNone()
    {
        // `a._p is None` already folded True; the getter's `return self._p` handed back
        // the field's never-written slot instead of the mark.
        var ir = Gen(Hdr +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self._p = None\n" +
            "    @property\n" +
            "    def p(self):\n" +
            "        return self._p\n" +
            "a = A()\n" +
            "if a.p is None:\n" + Branch);
        StoredConstants(ir).Should().Contain(7).And.NotContain(8);
    }

    [Fact]
    public void AnOutlinedMethodReturningAFieldMarkedNoneIsNone()
    {
        var ir = Gen(Hdr +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self._p = None\n" +
            "    def p(self):\n" +
            "        return self._p\n" +
            "a = A()\n" +
            "a.p()\n" +
            "if a.p() is None:\n" + Branch);
        StoredConstants(ir).Should().Contain(7).And.NotContain(8);
    }

    [Fact]
    public void TheFirstTestOfAMethodsInferredUnionReadsItsTag()
    {
        // Run-time None or 300: both arms stay. The first test used to fold to False.
        var ir = Gen(Hdr +
            "s = GPIOR0.value\n" +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self._p = 1\n" +
            "    def p(self):\n" +
            "        if s == 0:\n" +
            "            return None\n" +
            "        return 300\n" +
            "a = A()\n" +
            "if a.p() is None:\n" + Branch);
        StoredConstants(ir).Should().Contain(7).And.Contain(8);
    }

    [Fact]
    public void AParameterDefaultingToNoneIsTestedAtRunTime()
    {
        // g() and g(5) share one body: the None has to cross as a tag. The body folded
        // `p is None` to False for both calls.
        var ir = Gen(Hdr +
            "def g(p=None):\n" +
            "    if p is None:\n" +
            "        GPIOR0.value = 7\n" +
            "    else:\n" +
            "        GPIOR1.value = 8\n" +
            "g()\n" +
            "g(5)\n");
        StoredConstants(ir).Should().Contain(7).And.Contain(8);
    }

    [Fact]
    public void AnInlineReturningNoneOrAValueAtRunTimeReadsItsTag()
    {
        // No annotation: the union exists only because a run-time branch picks between
        // the two returns. Untagged, the test folded to False.
        var ir = Gen(Hdr +
            "from pymcu.types import inline\n" +
            "s = GPIOR0.value\n" +
            "@inline\n" +
            "def f():\n" +
            "    if s == 0:\n" +
            "        return None\n" +
            "    return 300\n" +
            "if f() is None:\n" + Branch);
        StoredConstants(ir).Should().Contain(7).And.Contain(8);
    }

    [Fact]
    public void PassingNoneToAnUnannotatedParameterIsNotARunTimeTypeError()
    {
        // `g(None)` used to raise TypeError at run time: the parameter was a plain uint8.
        var ir = Gen(Hdr +
            "def g(p):\n" +
            "    if p is None:\n" +
            "        GPIOR0.value = 7\n" +
            "    else:\n" +
            "        GPIOR1.value = 8\n" +
            "g(None)\n" +
            "g(5)\n");
        StoredConstants(ir).Should().Contain(7).And.Contain(8);
    }

    private const string Two =
        "class T:\n" +
        "    def __init__(self):\n" +
        "        self.x = 5\n" +
        "t = T()\n" +
        "u = T()\n";

    [Theory]
    [InlineData("n = GPIOR0.value\n", "n is t")]
    [InlineData("n = 0\n", "t is n")]
    [InlineData("", "t.x is t")]
    public void ANumberIsNeverAnInstance(string setup, string test)
    {
        var ir = Gen(Hdr + Two + setup + "if " + test + ":\n" + Branch);
        StoredConstants(ir).Should().Contain(8).And.NotContain(7);
    }

    [Fact]
    public void IdentityInsideAnExpansionSeesTheModuleInstance()
    {
        // `o` aliases u in the expansion; `t` is the module-level instance the prefixed
        // lookup did not see, so the two never-written handles compared equal.
        var ir = Gen(Hdr + Two +
            "def h(o: T):\n" +
            "    if o is t:\n" +
            "        GPIOR0.value = 7\n" +
            "    else:\n" +
            "        GPIOR1.value = 8\n" +
            "h(u)\n");
        StoredConstants(ir).Should().Contain(8).And.NotContain(7);
    }

    [Fact]
    public void AnUnannotatedParameterAgainstAnInstanceIsRefused()
    {
        // h(t) would be True in CPython and h(u) False; the parameter arrives as a plain
        // number either way, so there is nothing to compare.
        var act = () => Gen(Hdr + Two +
            "def h(o):\n" +
            "    return 1 if o is t else 0\n" +
            "GPIOR0.value = h(t)\n" +
            "GPIOR1.value = h(u)\n");
        act.Should().Throw<Exception>().WithMessage("*identity is lost*");
    }
}
