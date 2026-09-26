using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// An argument bound to a FLOAT parameter of a real subroutine crosses as a float.
//
// The Call marshals each argument by its own type, and the callee reads its parameter as a
// float, so an integer argument arrived as whatever its bytes spell as a float: `half(6)` for
// `def half(a: float)` computed 0.0. An outlined method was worse off: its argument lowering
// rounded every compile-time float to an int whatever the parameter was, so even
// `p.scale(-3.0)` passed the integer -3 and the method read 0.0.
//
// WHAT DISCRIMINATES: every assertion below. Against the unfixed compiler the arguments are
// integer Constants (or an int-typed variable) in a float slot.
public class FloatParameterArgumentTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Call> CallsTo(ProgramIR ir, string suffix) =>
        ir.Functions.Last(f => f.Name == "main").Body
            .OfType<Call>().Where(c => c.FunctionName.EndsWith(suffix)).ToList();

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR1\n" +
        "from pymcu.types import int16\n\n";

    [Fact]
    public void AnIntegerArgumentToAFloatParameterIsAFloat()
    {
        var ir = Gen(Preamble +
            "def half(a: float) -> float:\n" +
            "    return a * 0.5\n" +
            "k: int16 = int16(GPIOR1.value) - 5\n" +
            "x: float = half(6)\n" +
            "y: float = half(-6)\n" +
            "z: float = half(k)\n" +
            "GPIOR1.value = uint8(x + y + z)\n");

        var args = CallsTo(ir, "half").Select(c => c.Args[0]).ToList();
        Assert.Equal(3, args.Count);
        Assert.Equal(new FloatConstant(6.0), args[0]);
        Assert.Equal(new FloatConstant(-6.0), args[1]);
        Assert.Equal(DataType.FLOAT, args[2] switch
        {
            Temporary t => t.Type,
            Variable v => v.Type,
            _ => DataType.UNKNOWN
        });
    }

    [Fact]
    public void AFloatLiteralToAnOutlinedMethodStaysAFloat()
    {
        var ir = Gen(Preamble +
            "class P:\n" +
            "    def __init__(self, s: float):\n" +
            "        self.s = s\n" +
            "    def scale(self, a: float) -> float:\n" +
            "        return a * self.s\n" +
            "p = P(2.5)\n" +
            "x: float = p.scale(-3.0)\n" +
            "y: float = p.scale(-3)\n" +
            "GPIOR1.value = uint8(x + y)\n");

        var calls = CallsTo(ir, "scale");
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(new FloatConstant(-3.0), c.Args[^1]));
    }

    [Fact]
    public void AnIntegerForwardedToASiblingMethodsFloatParameterIsAFloat()
    {
        // `self.scale(-2)` inside another outlined method takes the forwarding path, which
        // narrowed integer arguments to their parameter width and left a float parameter
        // an integer.
        var ir = Gen(Preamble +
            "class P:\n" +
            "    def __init__(self, s: float):\n" +
            "        self.s = s\n" +
            "    def scale(self, a: float) -> float:\n" +
            "        return a * self.s\n" +
            "    def twice(self, k: int16) -> float:\n" +
            "        return self.scale(-2) + self.scale(k)\n" +
            "p = P(2.5)\n" +
            "x: float = p.twice(int16(GPIOR1.value))\n" +
            "GPIOR1.value = uint8(x)\n");

        var twice = ir.Functions.Single(f => f.Name.EndsWith("twice"));
        var args = twice.Body.OfType<Call>().Where(c => c.FunctionName.EndsWith("scale"))
            .Select(c => c.Args[^1]).ToList();
        Assert.Equal(2, args.Count);
        Assert.Equal(new FloatConstant(-2.0), args[0]);
        Assert.Equal(DataType.FLOAT, args[1] switch
        {
            Temporary t => t.Type,
            Variable v => v.Type,
            _ => DataType.UNKNOWN
        });
    }
}
