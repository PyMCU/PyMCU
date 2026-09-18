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
