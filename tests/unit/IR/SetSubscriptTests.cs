using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// Subscripting a set literal did not fail. It COMPILED, into a bit test against a name
// nothing ever assigns:
//
//     s = {70, 7}
//     GPIOR1.value = s[1]
//     ->  bchk  source=var main.s  bit=1  dst=...        globals: []
//
// `s[i]` on a chip register is the supported PORTB[5] idiom, and nothing checked that the
// receiver was a register rather than a set binding. A set literal binds a compile-time
// membership table and no storage, so the firmware tested a bit of an undefined slot.
// CPython raises TypeError; a set is not subscriptable in any Python. Issue #208.
//
// This is the same defect the guard above it catches for class instances (#171), whose
// comment already records the shape: bits 0..7 are legal, so `a[0]` built clean and answered
// from an unassigned slot.
//
// WHAT DISCRIMINATES: the two refusal tests. Against the unfixed compiler the program builds
// and no error is raised at all.
//
// WHAT IS INVARIANT, and here on purpose: the four neighbours that share this code path and
// must keep reaching the backend. A guard placed in front of every subscript can silence a
// supported idiom as easily as an unsupported one, and `d[key]` in particular is one letter
// of source away from the thing being refused.
public class SetSubscriptTests
{
    private static void Build(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static PyMCU.Common.CompilerError Reject(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Build(src));

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n\n";

    private static string Program(string body) =>
        Preamble +
        "def main():\n" +
        "    seed: uint8 = GPIOR0.value\n" +
        body +
        "    while True:\n" +
        "        pass\n";

    // --- what is refused -------------------------------------------------------

    [Fact]
    public void ASetLiteralIsNotSubscriptable()
    {
        var ex = Reject(Program(
            "    s = {70, 7}\n" +
            "    GPIOR1.value = s[1] + seed\n"));

        Assert.Contains("set", ex.Message);
        Assert.Contains("not subscriptable", ex.Message);
    }

    [Fact]
    public void TheRefusalNamesTheReceiverAndWhatDoesWork()
    {
        var ex = Reject(Program(
            "    members = {70, 7}\n" +
            "    GPIOR1.value = members[0] + seed\n"));

        // The receiver by the name the program gave it, not a mangled one, and the two
        // operations that ARE supported, which are checked by the invariants below rather
        // than asserted here on trust.
        Assert.Contains("'members'", ex.Message);
        Assert.Contains("x in members", ex.Message);
        Assert.Contains("len(members)", ex.Message);
    }

    // --- invariants: these share the path and must keep working ----------------

    [Fact]
    public void MembershipOnASetStillWorks()
    {
        Build(Program(
            "    s = {70, 7}\n" +
            "    GPIOR1.value = uint8(seed in s)\n"));
    }

    [Fact]
    public void LenOfASetStillWorks()
    {
        Build(Program(
            "    s = {70, 7}\n" +
            "    GPIOR1.value = uint8(len(s)) + seed\n"));
    }

    [Fact]
    public void ADictSubscriptIsNotCaught()
    {
        // `d[key]` is supported and lowers to the constant lookup. A guard written one
        // binding-kind too wide would take this with it.
        Build(Program(
            "    d = {1: 70, 2: 7}\n" +
            "    GPIOR1.value = uint8(d[1]) + seed\n"));
    }

    [Fact]
    public void ARegisterBitSubscriptIsNotCaught()
    {
        // The PORTB[5] idiom, which is what this code path exists for.
        Build(Program(
            "    GPIOR1.value = uint8(GPIOR0[3]) + seed\n"));
    }
}
