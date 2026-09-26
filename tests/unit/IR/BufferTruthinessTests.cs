using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A bytearray is true when it is non-empty. `if not buf:` lowered as a run-time `not` of
/// the NAME, which read the buffer's first byte: a `bytearray(4)` holding zero there
/// answered "empty", and `if not l_rom: l_rom = bytearray(8)` (adafruit_onewire) threw the
/// caller's buffer away. The length is known here, so the test folds, in every spelling.
/// </summary>
public class BufferTruthinessTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private static List<Instruction> Main(string src) =>
        Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Buf =
        "from pymcu.types import uint8\n" +
        "buf = bytearray(4)\n" +
        "r: uint8 = 0\n";

    private static bool ReadsBufAsScalar(List<Instruction> body) =>
        body.Any(i => i is Unary { Src: Variable { Name: "buf" } }
                      || i is JumpIfZero { Condition: Variable { Name: "buf" } }
                      || i is JumpIfNotZero { Condition: Variable { Name: "buf" } });

    [Theory]
    [InlineData("if not buf:\n    r = 1\nelse:\n    r = 2\n")]
    [InlineData("if buf:\n    r = 2\n")]
    [InlineData("r = 2 if buf else 1\n")]
    [InlineData("r = bool(buf)\n")]
    [InlineData("r = not buf\n")]
    public void ANonEmptyBufferIsTrueWithoutReadingItsFirstByte(string stmt)
    {
        var body = Main(Buf + stmt);

        ReadsBufAsScalar(body).Should().BeFalse(
            because: "the truth value is the length, which is 4, not the byte at buf[0]");
        body.Any(i => i is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "r" } }
                                      && stmt.Contains("r = 1")).Should().BeFalse(because: "the empty arm cannot run");
    }
}
