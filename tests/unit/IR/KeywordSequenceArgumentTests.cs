using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A keyword argument binds exactly as the positional one it stands for. A tuple literal
/// passed by keyword to an @inline callee was refused as "tuples are not supported as runtime
/// values" while the same tuple passed by position compiled -- the shape of
/// `keypad.KeyMatrix(row_pins=(...), column_pins=(...))` as the CircuitPython docs write it.
/// </summary>
public class KeywordSequenceArgumentTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void ATupleLiteralByKeyword_BindsAsByPosition()
    {
        Gen("from pymcu.types import uint8, inline\n" +
            "total: uint8 = 0\n" +
            "@inline\n" +
            "def f(a, b):\n" +
            "    global total\n" +
            "    for x in a:\n" +
            "        total = total + x\n" +
            "    for y in b:\n" +
            "        total = total + y\n" +
            "f(a=(1, 2), b=(3, 4))\n");
    }

    [Fact]
    public void ATupleLiteralByKeywordToAConstructor_Binds()
    {
        Gen("from pymcu.types import uint8\n" +
            "class K:\n" +
            "    def __init__(self, row_pins, column_pins, *, interval: uint8 = 20):\n" +
            "        self.n: uint8 = interval\n" +
            "        for r in row_pins:\n" +
            "            self.n = self.n + r\n" +
            "        for c in column_pins:\n" +
            "            self.n = self.n + c\n" +
            "k = K(row_pins=(1, 2, 3), column_pins=(4, 5), interval=7)\n");
    }
}
