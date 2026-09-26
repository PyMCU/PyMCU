using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A buffer handed to a parameter that holds one number.
///
/// `b"AB"` parses to a list of its byte values, and a buffer travels to a call as its base
/// address. With `def f(x: uint8)` the address landed in `x`: `print(x)` printed 0, and the
/// HAL's `UART.write(b"DE\n")` (whose only overloads take a `uint8` and a `const[str]`) put a
/// single 0x00 on the wire where the program asks for three bytes. The same literal bound to
/// a name first (`z = b"QR"; f(z)`) did not even reach the wire: it failed in the linker with
/// `undefined reference to main_z`, because a literal bound by name is a compile-time sequence
/// with no storage behind the base the call passed.
///
/// Now a buffer is never read as one number: overload selection takes a buffer overload when
/// there is one, a subroutine gets real storage for a named literal, and a parameter that
/// holds a number refuses the buffer at the argument.
/// </summary>
public class BufferToNumberParameterTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.types import uint8, inline, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    // The element the buffer body reads folds with its marker, so the body that ran is named
    // by the constant the program stores.
    private static bool Stores(ProgramIR ir, int value) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Any(c => c.Src is Constant k && k.Value == value);

    private static bool Uses(ProgramIR ir, int marker) =>
        ir.Functions.SelectMany(f => f.Body).Any(i =>
            i is Binary b && (b.Src1 is Constant c1 && c1.Value == marker
                              || b.Src2 is Constant c2 && c2.Value == marker));

    // DISCRIMINATING. A named literal passed to a subroutine's buffer parameter gets storage
    // of its own: before, the call passed the base of `main.z`, a label nothing defines.
    [Fact]
    public void ANamedBytesLiteralPassedToASubroutineHasStorage()
    {
        var ir = Gen(Preamble +
            "def f(x: bytes):\n" +
            "    G.value = x[1]\n" +
            "z = b\"QR\"\n" +
            "f(z)\n");

        var call = ir.Functions.Single(f => f.Name == "main").Body
            .OfType<Call>().Single(c => c.FunctionName == "f");
        var arg = Assert.IsType<ArrayBase>(call.Args[0]);
        Assert.NotEqual("main.z", arg.ArrayName);
        Assert.Contains(ir.Functions.Single(f => f.Name == "main").Body.OfType<ArrayStore>(),
            s => s.ArrayName == arg.ArrayName && s.Src is Constant { Value: 82 });
    }

    // Control: a buffer parameter keeps taking a bytes literal.
    [Fact]
    public void ABytesLiteralToABytesParameterStillCompiles()
    {
        var ir = Gen(Preamble +
            "def f(x: bytes):\n" +
            "    G.value = x[1]\n" +
            "f(b\"AB\")\n");
        Assert.Contains(ir.Functions.Single(f => f.Name == "main").Body.OfType<Call>(),
            c => c.FunctionName == "f" && c.Args[0] is ArrayBase);
    }
}
