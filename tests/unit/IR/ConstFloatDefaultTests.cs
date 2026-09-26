using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A `const[float]` parameter's default is a compile-time constant.
//
// The default was accepted only when it lowered to an integer Constant, so
// `interval: const[float] = 0.020` was refused as "must be a compile-time constant" by every
// call that left the argument out -- which is how nearly every program writes keypad.Keys().
// The CircuitPython layer dropped `const` from the parameter to get its default to compile.
//
// WHAT DISCRIMINATES: the omitted-argument build, refused against the unfixed compiler.
// WHAT IS INVARIANT: a guard over the parameter still refuses a bad explicit argument.
public class ConstFloatDefaultTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Keys =
        "from pymcu.chips.atmega328p import GPIOR1\n" +
        "from pymcu.types import uint32, const, inline\n\n" +
        "@inline\n" +
        "def scan_ms(interval: const[float] = 0.020) -> uint32:\n" +
        "    if interval < 0.0:\n" +
        "        raise CompileError(\"negative interval\")\n" +
        "    return uint32(interval * 1000 + 0.5)\n";

    [Fact]
    public void AnOmittedConstFloatArgumentTakesItsDefault()
    {
        var main = Gen(Keys + "GPIOR1.value = scan_ms()\n").Functions.Last(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is Copy { Src: Constant { Value: 20 } });
    }

    [Fact]
    public void AnExplicitNegativeArgumentIsStillRefused()
    {
        var ex = Assert.Throws<ArchitectureError>(() => Gen(Keys + "GPIOR1.value = scan_ms(-1.0)\n"));
        Assert.Contains("negative interval", ex.Message);
    }
}
