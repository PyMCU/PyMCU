using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A tagged union parameter cannot ride an indirect call, so the scan refuses a function
/// whose name is also used as a value. The scan matched names program-wide, so a LOCAL of
/// the same spelling anywhere -- the float `f` of the stdlib's decimal printer -- refused
/// every program whose function with an Optional parameter was called `f`, pointing at 1:1.
/// </summary>
public class UnionParamFunctionRefScanTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Fn =
        "from typing import Optional\n" +
        "from pymcu.types import uint8\n" +
        "def f(b: Optional[uint8], k: uint8) -> uint8:\n" +
        "    if b is None:\n" +
        "        return k\n" +
        "    return b + k\n" +
        "def go(k: uint8) -> uint8:\n" +
        "    return f(None, k) + f(k, k)\n";

    [Fact]
    public void ALocalOfTheSameNameIsNotAFunctionReference()
    {
        var act = () => Gen(Fn +
            "def other(x: uint8) -> uint8:\n" +
            "    f = x + 1\n" +
            "    return f\n");

        act.Should().NotThrow(because: "`f` in other() is its own local, not the function");
    }

    [Fact]
    public void TheFunctionItselfUsedAsAValueIsStillRefused()
    {
        var act = () => Gen(Fn + "h = f\n");

        act.Should().Throw<Exception>().WithMessage("*called through a function reference*");
    }
}
