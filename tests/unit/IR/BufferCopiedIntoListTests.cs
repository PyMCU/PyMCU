using System.Linq;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0016: `xs.append(C(buf))` copies the buffer into a heap record. CPython shares the object,
/// so a store into the buffer afterwards would change the list's element there and not here; the
/// store is refused by name instead of meaning something different. A fresh buffer bound to the
/// name is a new object and may be written.
/// </summary>
public class BufferCopiedIntoListTests
{
    private static ProgramIR Gen(string src, string arch) =>
        new IRGenerator().Generate(new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new System.Collections.Generic.Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = arch });

    private const string Addr =
        "class Addr:\n" +
        "    def __init__(self, rom: bytearray) -> None:\n" +
        "        self._rom = rom\n" +
        "    @property\n" +
        "    def rom(self) -> bytearray:\n" +
        "        return self._rom\n";

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void AStoreIntoABufferAfterItWasAppended_IsRefused(string arch)
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Addr +
            "devs = []\n" +
            "buf = bytearray(4)\n" +
            "devs.append(Addr(buf))\n" +
            "buf[0] = 2\n", arch));
        Assert.Contains("was copied into a list", ex.Message);
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void ABufferBoundAfreshAfterTheAppend_MayBeWrittenAgain(string arch)
    {
        var ir = Gen(Addr +
            "devs = []\n" +
            "buf = bytearray(4)\n" +
            "devs.append(Addr(buf))\n" +
            "buf = bytearray(4)\n" +
            "buf[0] = 2\n", arch);
        Assert.NotEmpty(ir.Functions);
    }
}
