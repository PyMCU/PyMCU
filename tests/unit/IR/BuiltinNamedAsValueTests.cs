using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A builtin named without a call -- `print(len)` -- compiled to the integer 0: the name
/// check counted every builtin as known so a CALL could reach its own diagnostic, and a
/// value read fell through to a slot nobody writes. There is no function object to print,
/// store or pass on, so the read is refused and says so. A program that binds the name
/// itself is untouched.
/// </summary>
public class BuiltinNamedAsValueTests
{
    private const string Prelude =
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    [Theory]
    [InlineData("print(len)\n")]
    [InlineData("f = abs\n")]
    public void ABuiltinFunctionNamedWithoutACall_IsRefused(string src)
    {
        var ex = Assert.ThrowsAny<Exception>(() => Gen(src));
        Assert.Contains("builtin function, named here without being called", ex.Message);
    }

    [Fact]
    public void ABuiltinTypeNamedAsAValue_IsRefused()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Gen("t = int\n"));
        Assert.Contains("builtin type, named here as a value", ex.Message);
    }

    // The control: a program's own binding of a builtin's name is an ordinary name.
    [Fact]
    public void AProgramsOwnBindingOfABuiltinName_IsAName()
    {
        var ir = Gen(
            "sum = 5\n" +
            "print(sum)\n");
        Assert.NotNull(ir);
    }
}
