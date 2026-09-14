using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#316. Assigning to a name the class defines as a METHOD wrote a phantom field that
/// shadowed the method, and the write went nowhere.
///
/// `p.value = 1` on a HAL Pin is the CircuitPython spelling, and `Pin.value` is an overloaded
/// method here (value() reads, value(x) writes), so the program built clean and drove nothing:
/// measured on an Uno, firmware.gas.asm held the DDR bit from the constructor and no write to
/// the port at all. Reading it back hid it, because the read folded to the value just stored.
/// </summary>
public class AssignToAMethodTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Lamp =
        "class Lamp:\n" +
        "    def __init__(self, n: uint8):\n" +
        "        self.n = n\n" +
        "    def level(self, x: uint8):\n" +
        "        self.n = x\n\n";

    [Fact]
    public void AssigningToAMethodName_IsRefusedAndNamesTheCall()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Lamp +
            "def main():\n" +
            "    p = Lamp(1)\n" +
            "    p.level = 3\n"));

        Assert.Contains("'level' is a method on 'Lamp'", ex.Message);
        Assert.Contains("p.level(...)", ex.Message);
    }

    // The ordinary field write it would be easy to refuse by accident.
    [Fact]
    public void AssigningToAField_StillCompiles()
        => Assert.NotNull(Gen(Lamp +
            "def main():\n" +
            "    p = Lamp(1)\n" +
            "    p.n = 3\n"));

    // And the field written from inside the class, which is where every field is born.
    [Fact]
    public void AFieldWrittenInInit_StillCompiles()
        => Assert.NotNull(Gen(
            "class Box:\n" +
            "    def __init__(self, k: uint8):\n" +
            "        self.k = k\n" +
            "    def bump(self):\n" +
            "        self.k = self.k + 1\n" +
            "def main():\n" +
            "    b = Box(1)\n" +
            "    b.bump()\n"));
}
