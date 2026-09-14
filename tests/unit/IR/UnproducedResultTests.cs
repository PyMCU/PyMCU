using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#302. A callee declared to return a value can reach the end of its body without
/// returning one. Python hands the caller None; there is no None here, so the caller read the
/// result temporary the expansion never wrote.
///
/// Measured on an Arduino Uno through the AVR PWM HAL: `pwm_prescaler_for_freq(pin, freq)
/// -> uint8` had lost its `return`, `PWM("PD6", 128, 500)` compiled to `MOV R4, R16` -- R16
/// holding the low byte of RAMEND from the reset prologue -- and TCCR0B came out 0x3F instead
/// of 3, which clocks Timer0 from the T0 pin. The firmware built clean.
///
/// The refusal is at the CALL and only when the result is read. A call written as a statement
/// throws the result away and produces no wrong value: the stdlib has accessors whose
/// one-argument path returns nothing and is always used that way.
/// </summary>
public class UnproducedResultTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Types = "from pymcu.types import uint8, uint16, const, inline\n\n";

    // The HAL's three levels: a selector whose `match` arm returns a call to a helper that
    // promises a uint8 and never produces one, reached from a constructor's annotated local.
    private const string ThreeLevels =
        Types +
        "@inline\n" +
        "def helper(pin: const, code: uint8) -> uint8:\n" +
        "    match pin:\n" +
        "        case \"PD6\" | \"PD5\":\n" +
        "            pass\n" +
        "        case _:\n" +
        "            pass\n\n" +
        "@inline\n" +
        "def select(pin: const) -> uint8:\n" +
        "    match pin:\n" +
        "        case \"PD6\" | \"PD5\":\n" +
        "            return helper(pin, 3)\n" +
        "        case _:\n" +
        "            return 0\n\n" +
        "class Dev:\n" +
        "    def __init__(self, pin: const):\n" +
        "        p: uint8 = 0\n" +
        "        p = select(pin)\n" +
        "        self.n = p\n\n" +
        "d = Dev(\"PD6\")\n";

    [Fact]
    public void AReturnedCallWhoseCalleeProducesNothing_IsRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(ThreeLevels));
        Assert.Contains("'helper'", ex.Message);
        Assert.Contains("without returning one", ex.Message);
        Assert.Equal(15, ex.Line);   // `return helper(pin, 3)`, the call that reads it
    }

    [Fact]
    public void ACalleeThatReturnsOnEveryArm_Compiles()
    {
        // The same three levels with the `return` the HAL was supposed to have.
        var ir = Gen(ThreeLevels.Replace(
            "        case \"PD6\" | \"PD5\":\n            pass\n        case _:\n            pass\n",
            "        case \"PD6\" | \"PD5\":\n            return code\n        case _:\n            return 0\n"));
        Assert.NotNull(ir);
    }

    [Fact]
    public void ACalleeThatReturnsUnderARunTimeCondition_IsRefused()
    {
        // The expansion DOES walk a `return` here, so only reading the body tells the compiler
        // that the other path falls through.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Types +
            "@inline\n" +
            "def pick(x: uint8) -> uint8:\n" +
            "    if x > 200:\n" +
            "        return 7\n\n" +
            "def main(seed: uint8) -> uint8:\n" +
            "    p: uint8 = pick(seed)\n" +
            "    return p\n"));
        Assert.Contains("'pick'", ex.Message);
    }

    [Fact]
    public void TheSameCalleeCalledAsAStatement_IsLeftAlone()
    {
        // Nothing reads the result, so nothing is wrong. This is the shape the stdlib's
        // `Pin.mode(m)` has at every one of its call sites.
        var ir = Gen(Types +
            "@inline\n" +
            "def touch(x: uint8) -> uint8:\n" +
            "    if x > 200:\n" +
            "        return 7\n\n" +
            "def main(seed: uint8):\n" +
            "    touch(seed)\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void ACallInAnArgumentIsRead_EvenInsideADiscardedStatement()
    {
        // `outer(inner())` as a statement discards OUTER's result. inner's is read by outer.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Types +
            "@inline\n" +
            "def inner(x: uint8) -> uint8:\n" +
            "    if x > 200:\n" +
            "        return 7\n\n" +
            "@inline\n" +
            "def outer(v: uint8) -> uint8:\n" +
            "    return v\n\n" +
            "def main(seed: uint8):\n" +
            "    outer(inner(seed))\n"));
        Assert.Contains("'inner'", ex.Message);
    }

    [Fact]
    public void AMatchWithACatchAllThatRaises_Compiles()
    {
        // `case _: raise` leaves the body on the arm that has no value, which is how every
        // selector in the AVR HAL is written.
        var ir = Gen(Types +
            "from pymcu.exceptions import CompileError\n" +
            "@inline\n" +
            "def sel(pin: const) -> uint8:\n" +
            "    match pin:\n" +
            "        case \"PD6\":\n" +
            "            return 1\n" +
            "        case _:\n" +
            "            raise CompileError(\"unsupported pin\")\n\n" +
            "def main() -> uint8:\n" +
            "    v: uint8 = sel(\"PD6\")\n" +
            "    return v\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void AnIfElseWhereBothArmsReturn_Compiles()
    {
        var ir = Gen(Types +
            "@inline\n" +
            "def pick(x: uint8) -> uint8:\n" +
            "    if x > 200:\n" +
            "        return 7\n" +
            "    else:\n" +
            "        return 3\n\n" +
            "def main(seed: uint8) -> uint8:\n" +
            "    p: uint8 = pick(seed)\n" +
            "    return p\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void AWhileTrueThatOnlyLeavesByReturning_Compiles()
    {
        // Control cannot reach the end of the body, so there is no path that produces nothing.
        var ir = Gen(Types +
            "@inline\n" +
            "def wait(x: uint8) -> uint8:\n" +
            "    while True:\n" +
            "        if x > 200:\n" +
            "            return 7\n\n" +
            "def main(seed: uint8) -> uint8:\n" +
            "    p: uint8 = wait(seed)\n" +
            "    return p\n");
        Assert.NotNull(ir);
    }
}
