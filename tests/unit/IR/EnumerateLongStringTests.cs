using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>for i, c in enumerate(s)</c> over a compile-time string used to unroll up to
/// <c>ConstSequenceUnrollLimit</c> (8) and refuse past it -- the refusal is what
/// adafruit_framebuf's <c>for i, char in enumerate(chunk)</c> hits on any text line
/// longer than 8 characters. Past the cap the loop now iterates at run time over the
/// interned flash copy: a counter, an <see cref="ArrayLoadFlash"/> element read, and
/// one lowering of the body -- the same shape <c>for c in s</c> already takes past its
/// own cap, except the counter is the user's index variable and the char is a run-time
/// uint8 char code, which is what <c>ord(char)</c> and font-table arithmetic consume.
/// </summary>
public class EnumerateLongStringTests
{
    private const string Hdr =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n\n";

    private static ProgramIR Gen(string src, bool pyParser, bool optimize = true)
    {
        var ast = pyParser
            ? PythonAstReader.ParseSource(src, "main.py")
            : new Parser(new Lexer(src).Tokenize()).ParseProgram();
        var ir = new IRGenerator().Generate(
            ast,
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    public static TheoryData<bool> BothFrontEnds => new() { false, true };

    private static List<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).ToList();

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void ALongString_IteratesAtRunTimeOverTheFlashCopy(bool pyParser)
    {
        // optimize: false pins the LOWERING. The optimizer may still unroll a small
        // constant-trip loop afterwards -- same flash reads with constant indices --
        // which is correct but says nothing about which path emitted them.
        var ir = Gen(Hdr +
            "buf: uint8[16] = [0] * 16\n" +
            "def main():\n" +
            "    for i, c in enumerate(\"twelve chars\"):\n" +
            "        buf[i] = ord(c)\n" +
            "    GPIOR0.value = buf[3]\n", pyParser, optimize: false);

        var body = Body(ir);

        body.OfType<ArrayLoadFlash>().Should().ContainSingle(
            because: "a 12-character string is read by one loop body, not unrolled");
        var load = body.OfType<ArrayLoadFlash>().Single();
        load.Index.Should().BeOfType<Variable>(
            because: "the element read is indexed by the run-time counter (LPM at base+i)");

        body.OfType<JumpIfGreaterOrEqual>()
            .Where(j => j.Src1 is Variable && j.Src2 is Constant c && c.Value == 12)
            .Should().NotBeEmpty(
                because: "the counter is compared with the string's length on every pass");

        body.OfType<ArrayStore>()
            .Where(s => s.Index is Variable)
            .Should().NotBeEmpty(
                because: "the loop body's indexed store must share the run-time index");
    }

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void TheStringLandsInFlash_AsANullTerminatedTable(bool pyParser)
    {
        var ir = Gen(Hdr +
            "buf: uint8[16] = [0] * 16\n" +
            "def main():\n" +
            "    for i, c in enumerate(\"twelve chars\"):\n" +
            "        buf[i] = ord(c)\n" +
            "    GPIOR0.value = buf[3]\n", pyParser, optimize: false);

        var table = Body(ir).OfType<FlashData>()
            .Where(f => f.Name.StartsWith("__cstr_", StringComparison.Ordinal))
            .Should().ContainSingle(
                because: "the string is interned once as a flash byte table").Subject;
        table.Bytes.Should().Equal(
            "twelve chars".Select(c => (int)c).Append(0),
            "the table holds the ASCII bytes plus the NUL the print path expects");
    }

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void AShortString_StillUnrolls(bool pyParser)
    {
        var ir = Gen(Hdr +
            "buf: uint8[8] = [0] * 8\n" +
            "def main():\n" +
            "    for i, c in enumerate(\"abcde\"):\n" +
            "        buf[i] = ord(c)\n" +
            "    GPIOR0.value = buf[0]\n", pyParser, optimize: false);

        var body = Body(ir);

        body.OfType<ArrayLoadFlash>().Should().BeEmpty(
            because: "a 5-character string stays inside the unroll cap: each char folds");
        body.OfType<ArrayStore>()
            .Where(s => s.Index is Constant && s.Src is Constant cv && cv.Value != 0)
            .Select(s => ((Constant)s.Index).Value)
            .Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4 },
                because: "unrolling emits one constant-indexed store per character "
                       + "(the buffer's own zero-init stores carry Src=0 and are not the loop)");
    }

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void LenAndIndexing_ResolveInsideTheRuntimeLoop(bool pyParser)
    {
        var ir = Gen(Hdr +
            "buf: uint8[20] = [0] * 20\n" +
            "def main():\n" +
            "    s = \"twelve chars\"\n" +
            "    for i, c in enumerate(s):\n" +
            "        buf[i] = s[i]\n" +
            "        buf[16] = len(s)\n" +
            "    GPIOR0.value = buf[0]\n", pyParser, optimize: false);

        var body = Body(ir);

        // s[i] with a run-time i is a second flash load on the same table.
        body.OfType<ArrayLoadFlash>().Should().HaveCount(2,
            because: "the loop reads c = s[i] and s[i] itself, both off the flash copy");
        // len(s) folds: the store of 12 arrives as a constant, not a load.
        body.OfType<ArrayStore>()
            .Where(s => s.Index is Constant ix && ix.Value == 16
                     && s.Src is Constant v && v.Value == 12)
            .Should().NotBeEmpty(
                because: "len(s) still folds to the compile-time length inside the loop");
    }

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void ANameBoundToALongString_EnumeratesTheSameWay(bool pyParser)
    {
        var ir = Gen(Hdr +
            "buf: uint8[16] = [0] * 16\n" +
            "MSG = \"twelve chars\"\n" +
            "def main():\n" +
            "    for i, c in enumerate(MSG):\n" +
            "        buf[i] = ord(c)\n" +
            "    GPIOR0.value = buf[0]\n", pyParser, optimize: false);

        Body(ir).OfType<ArrayLoadFlash>().Should().ContainSingle()
            .Which.Index.Should().BeOfType<Variable>(
                because: "a name bound to the literal iterates the flash copy too");
    }

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void TheCounter_IsWideEnoughForTheString(bool pyParser)
    {
        string longStr = new('x', 300);
        var ir = Gen(Hdr +
            "buf: uint8[4] = [0] * 4\n" +
            "def main():\n" +
            "    for i, c in enumerate(\"" + longStr + "\"):\n" +
            "        buf[i & 3] = ord(c)\n" +
            "    GPIOR0.value = buf[0]\n", pyParser, optimize: false);

        Body(ir).OfType<ArrayLoadFlash>()
            .Select(l => l.Index).OfType<Variable>()
            .Select(v => v.Type)
            .Should().AllSatisfy(t => t.Should().Be(DataType.UINT16,
                because: "the counter must reach 300; a uint8 wraps at 255 and the loop never ends"));
    }

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void Break_ExitsTheRuntimeLoop(bool pyParser)
    {
        var ir = Gen(Hdr +
            "buf: uint8[16] = [0] * 16\n" +
            "def main():\n" +
            "    for i, c in enumerate(\"twelve chars\"):\n" +
            "        if i > 5:\n" +
            "            break\n" +
            "        buf[i] = ord(c)\n" +
            "    GPIOR0.value = buf[0]\n", pyParser, optimize: false);

        Body(ir).OfType<ArrayLoadFlash>().Should().ContainSingle(
            because: "the body is still lowered once; break exits the loop, not the unroll");
    }
}
