using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An expansion's result becomes a tagged union only when a `return` SAYS None.
///
/// The tag minted on demand for a `return None` that shares the result with a value
/// (IsOperatorTests) asked the lowered value, and a constructor's `return S(x)` lowers to
/// a NoneVal too -- the instance travels outside the value channel. So a factory whose
/// None arm raises was taken for an Optional, and the caller's `t = f(v)` was refused as
/// "'t' may be None here": `time.localtime(v)` in the CircuitPython layer and the
/// `compat-cp-time-calendar` fixture stopped building. An arm that raises gives the result
/// nothing; only a written None (or a name marked None) does.
/// </summary>
public class RaisingArmIsNotNoneTests
{
    private const string Hdr =
        "from typing import Optional\n" +
        "from pymcu.types import inline, uint32\n" +
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n\n" +
        "class S:\n" +
        "    @inline\n" +
        "    def __init__(self, a: uint32):\n" +
        "        self.a: uint32 = a\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Call =
        "v: uint32 = 7 + GPIOR0.value\n" +
        "t = f(v)\n" +
        "GPIOR1.value = t.a\n";

    [Fact]
    public void AMatchWhoseNoneArmRaisesReturnsTheObject()
    {
        var act = () => Gen(Hdr +
            "@inline\n" +
            "def f(x: Optional[uint32] = None) -> S:\n" +
            "    match x:\n" +
            "        case None:\n" +
            "            raise RuntimeError(\"no\")\n" +
            "        case _:\n" +
            "            return S(x)\n" + Call);
        act.Should().NotThrow();
    }

    [Fact]
    public void AnIfChainWithARaisingArmReturnsTheObject()
    {
        var act = () => Gen(Hdr +
            "@inline\n" +
            "def f(x: Optional[uint32] = None) -> S:\n" +
            "    if x is None:\n" +
            "        raise RuntimeError(\"no\")\n" +
            "    elif x > 100:\n" +
            "        return S(100)\n" +
            "    else:\n" +
            "        return S(x)\n" + Call);
        act.Should().NotThrow();
    }

    [Fact]
    public void AMatchWhoseNoneArmReturnsNoneIsStillOptional()
    {
        // The written `return None` against a value is the union the tag exists for.
        var ir = Gen(Hdr +
            "s = GPIOR0.value\n" +
            "@inline\n" +
            "def g(x):\n" +
            "    match x:\n" +
            "        case 0:\n" +
            "            return None\n" +
            "        case _:\n" +
            "            return 300\n" +
            "r = g(s)\n" +
            "if r is None:\n" +
            "    GPIOR0.value = 7\n" +
            "else:\n" +
            "    GPIOR1.value = 8\n");
        var stored = ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>()
            .Select(c => c.Src).OfType<Constant>().Select(k => k.Value).ToList();
        stored.Should().Contain(7).And.Contain(8);
    }
}
