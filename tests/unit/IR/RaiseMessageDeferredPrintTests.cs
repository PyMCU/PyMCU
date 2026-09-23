using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#435. A non-literal raise message (f-string, concatenation, call) compiles as a
/// deferred print: runtime pieces are stored at the raise, and <c>print(e)</c> replays
/// them. A program that never binds <c>as e</c> emits neither the record nor the printer.
/// </summary>
[Trait("Issue", "435")]
public class RaiseMessageDeferredPrintTests
{
    // The printer is synthesised after lowering and calls the same UART writers print()
    // uses. Unit tests have no HAL, so one stub of each writer is enough.
    private const string Prelude =
        "from pymcu.types import uint8, uint16, int32\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n" +
        "def uart_write_decimal_i32(v: int32):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static Function Boom(ProgramIR ir) => ir.Functions.Single(f => f.Name == "boom");

    private static bool CopiesTo(IEnumerable<Instruction> body, string dst) =>
        body.Any(i => i is Copy { Dst: Variable { Name: var n } } && n == dst);

    private static bool StoresPositiveSite(IEnumerable<Instruction> body) =>
        body.Any(i => i is Copy { Src: Constant { Value: > 0 }, Dst: Variable { Name: "__exn_site" } });

    private static bool StoresFlashMessage(IEnumerable<Instruction> body) =>
        body.Any(i => i is Copy { Src: FlashStrAddr });

    [Fact]
    public void AnFStringRaiseWithABoundHandlerStoresTheSiteAndTheValue()
    {
        var ir = Gen(
            "def boom(n: uint8) -> uint8:\n" +
            "    raise ValueError(f\"bad {n}\")\n" +
            "def main() -> uint8:\n" +
            "    try:\n" +
            "        return boom(7)\n" +
            "    except ValueError as e:\n" +
            "        return 1\n");

        StoresPositiveSite(Boom(ir).Body).Should().BeTrue(
            because: "print(e) dispatches on a site id recorded at the raise");
        CopiesTo(Boom(ir).Body, "__exn_arg0").Should().BeTrue(
            because: "the interpolated n has to outlive boom(), so it is stored in the args record");
        ir.Functions.Select(f => f.Name).Should().Contain(
            "__pymcu_print_exn_msg",
            because: "a handler binds as e, so the deferred-print dispatcher must exist");
    }

    [Fact]
    public void ACallInsideAnFStringRaiseIsAccepted()
    {
        var ir = Gen(
            "def boom(n: uint8) -> uint8:\n" +
            "    xs: uint8[3] = [1, 2, 3]\n" +
            "    raise ValueError(f\"got {len(xs)}\")\n" +
            "def main() -> uint8:\n" +
            "    try:\n" +
            "        return boom(0)\n" +
            "    except ValueError as e:\n" +
            "        return 1\n");

        CopiesTo(Boom(ir).Body, "__exn_site").Should().BeTrue(
            because: "len(xs) in a raise used to be refused as a call that would never run");
    }

    [Fact]
    public void WithoutABoundHandlerTheRecordIsNotEmitted()
    {
        var ir = Gen(
            "def boom(n: uint8) -> uint8:\n" +
            "    raise ValueError(f\"bad {n}\")\n" +
            "def main() -> uint8:\n" +
            "    return boom(7)\n");

        CopiesTo(Boom(ir).Body, "__exn_site").Should().BeFalse(
            because: "no except binds a name, so the zero-cost gate of #369 still holds");
        ir.Functions.Should().NotContain(
            f => f.Name == "__pymcu_print_exn_msg",
            because: "a program that never reads the message must not grow a printer");
        ir.Globals.Should().NotContain(
            g => g.Name == "__exn_arg0",
            because: "the args record is only for a handler that can print it");
    }

    [Fact]
    public void AFoldedFStringUsesTheLiteralFlashPath()
    {
        var ir = Gen(
            "def boom() -> uint8:\n" +
            "    raise ValueError(f\"bad {1}\")\n" +
            "def main() -> uint8:\n" +
            "    try:\n" +
            "        return boom()\n" +
            "    except ValueError as e:\n" +
            "        return 1\n");

        var boom = Boom(ir).Body;
        StoresFlashMessage(boom).Should().BeTrue(
            because: "f\"bad {1}\" is the literal \"bad 1\", which #369 already stores as flash");
        CopiesTo(boom, "__exn_arg0").Should().BeFalse(
            because: "a fully folded message has no runtime piece to park in the args record");
    }

    [Fact]
    public void AnFStringRaiseWithAdjacentLiteralsStoresTheSiteAndTheValue()
    {
        // seesaw's `raise RuntimeError(f"...0x{chip_id:x} is not " "correct! ...")`:
        // CPython concatenates the f-string and the plain literal into one JoinedStr,
        // and the deferred print must carry the runtime piece through both front ends.
        var ir = Gen(
            "def boom(n: uint8) -> uint8:\n" +
            "    raise ValueError(\n" +
            "        f\"bad 0x{n:x} is not \"\n" +
            "        \"correct\"\n" +
            "    )\n" +
            "def main() -> uint8:\n" +
            "    try:\n" +
            "        return boom(7)\n" +
            "    except ValueError as e:\n" +
            "        return 1\n");

        StoresPositiveSite(Boom(ir).Body).Should().BeTrue(
            because: "the message is an f-string once the adjacent literal folds in");
        CopiesTo(Boom(ir).Body, "__exn_arg0").Should().BeTrue(
            because: "the interpolated n parks in the args record for print(e) to replay");
    }

    [Fact]
    public void AnIntPieceUnderAnFSpecParksInAFloatSlot()
    {
        // print(f"{n:.1f}") converts an int to float like CPython ("5.0"); the deferred
        // print must park it in a float slot so the replayed text matches.
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(Prelude +
                "def uart_write_float_fmt(v: float, prec: uint8, width: uint8, flags: uint8):\n" +
                "    pass\n" +
                "def boom(n: uint8) -> uint8:\n" +
                "    raise ValueError(f\"bad {n:.1f}\")\n" +
                "def main() -> uint8:\n" +
                "    try:\n" +
                "        return boom(7)\n" +
                "    except ValueError as e:\n" +
                "        return 1\n").Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

        CopiesTo(Boom(ir).Body, "__exn_farg0").Should().BeTrue(
            because: "an int under an f spec converts to float, exactly as print() does");
        CopiesTo(Boom(ir).Body, "__exn_arg0").Should().BeFalse(
            because: "parking the int would make the replay print '5' where print wrote '5.0'");
        ir.Functions.Single(f => f.Name == "__pymcu_print_exn_msg").Body
            .Any(i => i is Call { FunctionName: var n } && n.EndsWith("uart_write_float_fmt"))
            .Should().BeTrue(
                because: "the replay emits the spec'd float through the same writer print uses");
    }

    [Fact]
    public void CompileErrorStillNeedsACompileTimeString()
    {
        var act = () => Gen(
            "def boom() -> uint8:\n" +
            "    raise CompileError(NOPE)\n" +
            "def main() -> uint8:\n" +
            "    return boom()\n");

        act.Should().Throw<CompilerError>()
            .Which.Message.Should().Contain("string constant known at compile time",
                because: "CompileError is a diagnostic; RFC 0005 does not defer it");
    }
}

public class RaiseInstanceMessageTests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16, int32\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n" +
        "def uart_write_decimal_i32(v: int32):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    // Unmodified adafruit_irremote writes `raise FailedToDecode(msg)` where msg is
    // an UnparseableIRMessage namedtuple -- a payload that exists to be caught and
    // re-raised (`raise IRDecodeException from err`), never read. The message word
    // carries strings, so the payload's class name becomes the message: the type
    // dispatch is untouched and a handler that prints it sees what was raised.
    [Fact]
    public void AnInstanceRaiseMessageCompilesWithTheClassName()
    {
        var ir = Gen(
            "class Msg:\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v = v\n" +
            "def boom() -> uint8:\n" +
            "    m = Msg(3)\n" +
            "    raise ValueError(m)\n" +
            "def main() -> uint8:\n" +
            "    try:\n" +
            "        return boom()\n" +
            "    except ValueError as e:\n" +
            "        return 1\n");

        ir.Functions.Select(f => f.Name).Should().Contain(
            "__pymcu_print_exn_msg",
            because: "the handler binds as e, so the deferred printer still exists");
        BoomBody(ir).Any(
            i => i is Copy { Src: Constant { Value: > 0 }, Dst: Variable { Name: "__exn_site" } })
            .Should().BeTrue(
                because: "the raise records a site the printer can dispatch on");
    }

    [Fact]
    public void AnInstanceRaiseWithoutABoundHandlerCompiles()
    {
        var ir = Gen(
            "class Msg:\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v = v\n" +
            "def boom() -> uint8:\n" +
            "    m = Msg(3)\n" +
            "    raise ValueError(m)\n" +
            "def main() -> uint8:\n" +
            "    try:\n" +
            "        return boom()\n" +
            "    except ValueError:\n" +
            "        return 1\n");

        ir.Functions.Select(f => f.Name).Should().Contain("boom");
    }

    private static IEnumerable<Instruction> BoomBody(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "boom").Body;
}
