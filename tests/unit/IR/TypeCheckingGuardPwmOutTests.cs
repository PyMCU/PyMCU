using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#480. After the nested Adafruit TYPE_CHECKING guard, <c>PWMOut</c> is
/// the class imported from pwmio, so <c>def __init__(self, h: PWMOut)</c> is a
/// real type. The inner <c>except NotImplementedError</c> used to drop it.
/// </summary>
[Trait("Issue", "480")]
public class TypeCheckingGuardPwmOutTests
{
    private static ProgramIR Gen(string main, string pwmio)
    {
        var config = new DeviceConfig { Arch = "avr" };
        var program = new Parser(new Lexer(main).Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        var imported = new Dictionary<string, ProgramNode>
        {
            ["pwmio"] = new Parser(new Lexer(pwmio).Tokenize()).ParseProgram(),
        };
        return new IRGenerator().Generate(
            program, imported, config, projectModules: new HashSet<string> { "pwmio" });
    }

    [Fact]
    public void PwmOutInTheAnnotation_IsTheImportedClass()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "try:\n" +
            "    from typing import Optional, Type\n" +
            "    try:\n" +
            "        from pwmio import PWMOut\n" +
            "    except NotImplementedError:\n" +
            "        from circuitpython_typing.pwmio import PWMOut\n" +
            "except ImportError:\n" +
            "    pass\n" +
            "def take(h: PWMOut) -> uint8:\n" +
            "    return 1\n" +
            "buf = bytearray([0])\n" +
            "buf[0] = 1\n",
            "from pymcu.types import uint8\n" +
            "class PWMOut:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.n: uint8 = 0\n");

        ir.Functions.Should().NotBeEmpty(
            because: "PWMOut after except NotImplementedError is a class in the annotation, not an unknown type");
    }
}
