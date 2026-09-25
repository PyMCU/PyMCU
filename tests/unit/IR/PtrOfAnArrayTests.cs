using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#413. `ptr(buf)` on an ordinary SRAM array compiled, and compiled to reading the
/// array's FIRST BYTE.
///
/// ptr()'s constant-base recognition exists for a hardware register -- `ptr(PORTB + x)` --
/// where the base is a MemoryAddress with an address this pass can resolve. An array name is
/// an ordinary variable, so it fell past that arm into the runtime-address path, which
/// evaluates its argument: `buf` read as a scalar is `buf[0]`, widened into a 16-bit temp.
/// The pointer then held a small number that looks plausible, and a write through it landed
/// that many bytes up from address 0 -- register space, another global, the stack -- with no
/// diagnostic anywhere.
///
/// An SRAM array is addressed by a label the assembler assigns, not by a number this pass
/// can compute, so the fix is the refusal: located, naming the array, and saying what ptr()
/// does accept.
/// </summary>
public class PtrOfAnArrayTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    [Fact]
    public void PtrOfAModuleArray_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.types import uint16, ptr\n" +
            "buf: bytearray = bytearray(16)\n" +
            "pos: uint16 = 3\n" +
            "p: ptr = ptr(buf) + pos\n"));

        Assert.Contains("ptr() cannot take the array 'buf'", ex.Message);
    }

    [Fact]
    public void PtrOfAMemberArray_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.types import uint8, uint16, ptr, inline\n" +
            "class D:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self.buf: uint8[4] = [0, 0, 0, 0]\n" +
            "    @inline\n" +
            "    def go(self) -> uint16:\n" +
            "        p: ptr = ptr(self.buf)\n" +
            "        return 1\n" +
            "d = D()\n" +
            "n: uint16 = d.go()\n"));

        Assert.Contains("ptr() cannot take the array 'self.buf'", ex.Message);
    }

    [Fact]
    public void PtrOfARegisterOrANumber_StillCompiles()
    {
        // The arms the refusal must not touch: the register base it was written for, a
        // register base plus an offset, a runtime offset off a numeric base, and a plain
        // number.
        Assert.NotNull(Gen(
            "from pymcu.types import uint8, uint16, ptr\n" +
            "PORTB: ptr[uint8] = ptr(0x25)\n" +
            "x: uint8 = 2\n" +
            "def main():\n" +
            "    a: ptr[uint8] = ptr(PORTB)\n" +
            "    b: ptr[uint8] = ptr(PORTB + 1)\n" +
            "    c: ptr[uint8] = ptr(0x100 + x)\n" +
            "    d: ptr[uint8] = ptr(0x100)\n" +
            "    a.value = 1\n" +
            "    b.value = 2\n" +
            "    c.value = 3\n" +
            "    d.value = 4\n"));
    }
}
