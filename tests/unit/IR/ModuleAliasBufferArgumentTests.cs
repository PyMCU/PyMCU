using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A module-level `x = r` is lowered in the synthesized main and files its alias as
/// `main.x`; handing the global `x` to an @inline carried the bare name, so the callee's
/// parameter took x's scalar slot and `buf[i]` compiled to a bit test of that byte. With a
/// constant index the program built and summed bits of a byte nothing wrote -- 0 where
/// CPython prints the buffer's contents.
/// </summary>
public class ModuleAliasBufferArgumentTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    [Fact]
    public void AnAliasOfAModuleBufferPassesTheBuffer()
    {
        var main = Gen(
            "from pymcu.types import uint8, inline\n" +
            "@inline\n" +
            "def s(buf) -> uint8:\n" +
            "    return buf[1] + buf[2]\n" +
            "r = bytearray(3)\n" +
            "x = r\n" +
            "n: uint8 = s(x)\n").Functions.Single(f => f.Name == "main").Body;

        main.OfType<BitCheck>().Should().BeEmpty(because: "buf is a buffer, not the bits of x");
    }
}
