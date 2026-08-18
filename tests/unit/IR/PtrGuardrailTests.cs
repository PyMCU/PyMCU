using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

public class PtrGuardrailTests
{
    private const string ChipModule =
        "from pymcu.types import uint8, ptr\n" +
        "PORTB: ptr[uint8] = ptr(0x25)\n" +
        "DDRB: ptr[uint8] = ptr(0x24)\n";

    private static ProgramIR Gen(string mainSrc)
    {
        var modAst = new Parser(new Lexer(ChipModule).Tokenize()).ParseProgram();
        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var modules = new Dictionary<string, ProgramNode> { ["pymcu.chips.fake"] = modAst };
        return new IRGenerator().Generate(mainAst, modules, new DeviceConfig { Arch = "avr" });
    }

    [Fact]
    public void ChipRegisterAssignedInsideAFunction_IsRejected()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.chips.fake import PORTB\n" +
            "def main():\n" +
            "    PORTB = 0x84\n"));

        Assert.Contains("never writes the register", ex.Message);
        Assert.Contains(".value", ex.Message);
    }

    [Fact]
    public void ChipRegisterAssignedAtModuleLevel_IsRejected()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.chips.fake import PORTB\n" +
            "PORTB = 0x84\n" +
            "def main():\n" +
            "    x: uint8 = 1\n"));

        Assert.Contains("never writes the register", ex.Message);
    }

    [Fact]
    public void WritingThroughValue_StillCompiles()
    {
        var ir = Gen(
            "from pymcu.chips.fake import PORTB\n" +
            "def main():\n" +
            "    PORTB.value = 0x84\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void BareRegisterAsScalarRvalue_IsRejected()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.chips.fake import PORTB\n" +
            "def main():\n" +
            "    x: uint8 = PORTB\n"));

        Assert.Contains("names a register, not its contents", ex.Message);
        Assert.Contains("PORTB.value", ex.Message);
    }

    [Fact]
    public void BareRegisterAsArithmeticOperand_IsRejected()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.chips.fake import PORTB\n" +
            "def main():\n" +
            "    x: uint8 = PORTB + 1\n"));

        Assert.Contains("PORTB.value", ex.Message);
    }

    [Fact]
    public void BareRegisterComparedWithAnInteger_IsRejected()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.chips.fake import PORTB\n" +
            "def main():\n" +
            "    if PORTB == 5:\n" +
            "        x: uint8 = 1\n"));

        Assert.Contains("PORTB.value", ex.Message);
    }

    [Fact]
    public void BareRegisterReturned_IsStillTheAddress()
    {
        var ir = Gen(
            "from pymcu.chips.fake import PORTB\n" +
            "def which() -> ptr[uint8]:\n" +
            "    return PORTB\n" +
            "def main():\n" +
            "    p = which()\n");

        Assert.Contains(ir.Functions, f => f.Name == "which");
    }

    [Fact]
    public void BareRegisterAsCallArgument_IsStillTheAddress()
    {
        var ir = Gen(
            "from pymcu.chips.fake import PORTB, DDRB\n" +
            "def poke(reg: ptr[uint8], v: uint8):\n" +
            "    reg.value = v\n" +
            "def main():\n" +
            "    poke(PORTB, 1)\n" +
            "    poke(DDRB, 2)\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void TwoBareRegistersCompared_IsStillTheAddress()
    {
        var ir = Gen(
            "from pymcu.chips.fake import PORTB, DDRB\n" +
            "def main():\n" +
            "    if PORTB == DDRB:\n" +
            "        x: uint8 = 1\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }
}
