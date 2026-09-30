using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// P2 AVR gaps bundle, item 6: `s = f"{s}..."`, an f-string that interpolates the same name
/// it assigns. CPython admits it (strings are immutable and unbounded); PyMCU used to
/// refuse it outright, because the naive lowering reuses s's own buffer AND length
/// variable -- the length is reset to 0 before any part is emitted, so a self-referencing
/// part's read loop (bounded by that same length) sees zero bytes and the old text is
/// silently dropped before the refusal was added (#438-adjacent).
///
/// The fix: snapshot s's CURRENT bytes and length into a private temporary buffer BEFORE
/// touching s's own length or buffer, then every self-referencing part reads the snapshot
/// instead of s. s's own fixed-size buffer still governs how much a REASSIGNMENT can grow
/// by (same "assign the longest f-string first" rule every other f-string reassignment
/// follows) -- a self-reference that would need MORE bytes than s's buffer already has
/// still refuses, honestly, with the existing capacity message; it is the READ-before-
/// WRITE corruption that is fixed, not PyMCU's fixed-buffer model.
/// </summary>
public class FStringSelfReferenceTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8, uint16, uint32, int32\n" +
        "import pymcu.strfmt as _pymcu_strfmt\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "\n" +
        "def main() -> None:\n";

    private static readonly Dictionary<string, ProgramNode> StrfmtModule = new()
    {
        ["pymcu.strfmt"] = new Parser(new Lexer(
            "from pymcu.types import uint8, uint16, uint32, int32\n" +
            "def _fs_text(buf: bytearray, pos: uint16, s: const[str]) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_u32(buf: bytearray, pos: uint16, v: uint32) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_i32(buf: bytearray, pos: uint16, v: int32) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_frepr(buf: bytearray, pos: uint16, value: float) -> uint16:\n" +
            "    return pos\n").Tokenize()).ParseProgram(),
    };

    private static ProgramIR Gen(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + body).Tokenize()).ParseProgram(),
            StrfmtModule,
            new DeviceConfig { Arch = "avr" });

    // The snapshot's length var (VarDecl(snapLen, "uint16", oldInfo.LenVar)) lowers to a
    // Copy whose destination is a Variable named "__fsselflen_<target>" -- the IR-level
    // fingerprint of the snapshot this fix adds, independent of whatever the bytearray
    // allocation itself lowers to.
    private static bool EmittedASnapshot(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Any(c => c.Dst is Variable v && v.Name.Contains("__fsselflen_"));

    [Fact]
    public void SameLengthSelfReference_NoLongerRefuses()
    {
        // f"{s}" alone is the same length as s (nothing else in the f-string), so the
        // existing buffer's capacity always covers it -- the simplest case that must work.
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    s = f\"n={a}pad\"\n" +
            "    s = f\"{s}\"\n");
        // Reaching here without an exception is most of the assertion: this used to throw
        // "interpolates '...' itself" unconditionally. A snapshot buffer is also emitted.
        Assert.True(EmittedASnapshot(ir));
    }

    [Fact]
    public void SelfReferenceWithATrailingLiteral_ThatGrowsPastCapacity_StillRefuses_NotCrashes()
    {
        // The read-before-write corruption is fixed; PyMCU's fixed-buffer model is not
        // relaxed. Growing self-reference still needs the original capacity check to name
        // the real limit, the same one any other f-string reassignment gets.
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    s = f\"n={a}\"\n" +
            "    s = f\"{s}-longer-than-the-first-buffer-by-a-lot\"\n"));
        Assert.Contains("buffer was sized", ex.Message);
    }

    [Fact]
    public void RepeatedSelfReference_KeepsWorking()
    {
        // s = f"{s}" a second time, chained: each step is still exactly s's own length, so
        // it keeps fitting the buffer capacity established by the first, non-self-
        // referencing assignment.
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    s = f\"n={a}pad\"\n" +
            "    s = f\"{s}\"\n" +
            "    s = f\"{s}\"\n");
        Assert.Equal(2,
            ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
                .Count(c => c.Dst is Variable v && v.Name.Contains("__fsselflen_")));
    }
}
