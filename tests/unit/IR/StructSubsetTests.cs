using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The `struct` subset an ahead-of-time target can do without a heap.
///
/// The refusal used to be at the IMPORT: no heap, so no packed bytes to hand back. That is
/// exact for the half of `struct` that returns a tuple and describes nothing about the half
/// driver libraries write. Measured across the twelve most-used Adafruit libraries: every
/// format is a string literal, 21 of 21; the workhorse descriptors index the result on the
/// spot so no tuple ever escapes; every `pack_into` target is a buffer the caller owns. Four
/// type codes, five distinct format strings, no floats and no repeat counts.
///
/// EVERY ASSERTION HERE IS ON WHAT THE CODE READS OR WRITES, never on the build succeeding.
/// Endianness is the reason: `<H` and `>H` compile to the same instructions in a different
/// ORDER, so a test that only checked "struct works" would pass with the bytes reversed, and
/// a reversed 16-bit sensor reading is the hardest kind of wrong to notice on hardware.
/// </summary>
public class StructSubsetTests
{
    private const string Shim =
        "def calcsize(fmt):\n    pass\n" +
        "def unpack_from(fmt, buf, offset=0):\n    pass\n" +
        "def pack_into(fmt, buf, offset, value):\n    pass\n";

    private static ProgramIR Gen(string mainSrc) =>
        new IRGenerator().Generate(
            new Parser(new Lexer("import struct\n" + mainSrc).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>
                { ["struct"] = new Parser(new Lexer(Shim).Tokenize()).ParseProgram() },
            new DeviceConfig { Arch = "avr" });

    private static CompilerError Fails(string mainSrc) =>
        Assert.Throws<CompilerError>(() => Gen(mainSrc));

    /// <summary>The buffer byte indices the program READS, in the order it reads them.</summary>
    private static List<int> BytesRead(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Where(a => a.ArrayName.EndsWith("buf", StringComparison.Ordinal))
            .Select(a => ((Constant)a.Index).Value).ToList();

    /// <summary>The buffer byte indices the program WRITES, in order, skipping the zero-fill
    /// that `bytearray(n)` emits.</summary>
    private static List<int> BytesWritten(ProgramIR ir, int skip) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(a => a.ArrayName.EndsWith("buf", StringComparison.Ordinal))
            .Select(a => ((Constant)a.Index).Value).Skip(skip).ToList();

    private static IEnumerable<DataType> CopyTargetTypes(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Select(c => c.Dst).OfType<Temporary>().Select(t => t.Type);

    private const string Head =
        "def main() -> uint8:\n" +
        "    buf = bytearray(8)\n";

    // calcsize is a real compile-time value, so it can be asserted as one.
    [Theory]
    [InlineData("\"<B\"", 1)]
    [InlineData("\"b\"", 1)]
    [InlineData("\"<h\"", 2)]
    [InlineData("\">H\"", 2)]
    [InlineData("\"<hhh\"", 6)]
    [InlineData("\"<hhhh\"", 8)]
    [InlineData("\"<HH\"", 4)]
    public void CalcsizeFoldsToTheRecordSize(string fmt, int expected)
    {
        var ir = Gen(Head + $"    return struct.calcsize({fmt})\n");

        // The RETURNED value, so the buffer's own size and the zero-fill indices cannot stand
        // in for it.
        var returned = ir.Functions.SelectMany(f => f.Body).OfType<Return>()
            .Select(r => r.Value).OfType<Constant>().Select(c => c.Value).ToList();
        Assert.Contains(expected, returned);
    }

    // THE ENDIANNESS PAIR. Same two bytes, opposite order. The high byte is loaded first
    // because it is the one that gets shifted, so the list IS the byte order.
    [Fact]
    public void LittleEndianReadsTheHighByteFromTheHigherAddress()
    {
        var ir = Gen(Head + "    return struct.unpack_from(\"<H\", buf, 1)[0] & 0xFF\n");

        Assert.Equal(new[] { 2, 1 }, BytesRead(ir));
    }

    [Fact]
    public void BigEndianReadsTheHighByteFromTheLowerAddress()
    {
        var ir = Gen(Head + "    return struct.unpack_from(\">H\", buf, 1)[0] & 0xFF\n");

        Assert.Equal(new[] { 1, 2 }, BytesRead(ir));
    }

    // A one-byte field reads exactly one byte, at the offset the format and the call give it.
    [Fact]
    public void AOneByteFieldReadsOneByteAtItsOffset()
    {
        var ir = Gen(Head + "    return struct.unpack_from(\"B\", buf, 3)[0]\n");

        Assert.Equal(new[] { 3 }, BytesRead(ir));
    }

    // Field k of a multi-field format sits at the sum of the widths before it: field 1 of
    // '<hhh' read at offset 1 is bytes 3 and 4, not 1 and 2.
    [Fact]
    public void FieldKIsReadAtTheSumOfTheWidthsBeforeIt()
    {
        var ir = Gen(Head + "    return struct.unpack_from(\"<hhh\", buf, 1)[1] & 0xFF\n");

        Assert.Equal(new[] { 4, 3 }, BytesRead(ir));
    }

    // A signed code is the same bytes read as signed. The reinterpretation is visible as a
    // copy into a temp of the signed type; the unsigned spelling of the same field has none.
    [Fact]
    public void ASignedCodeProducesASignedValue()
    {
        Assert.Contains(DataType.INT16,
            CopyTargetTypes(Gen(Head + "    return struct.unpack_from(\"<h\", buf, 0)[0] & 0xFF\n")));
        Assert.Contains(DataType.INT8,
            CopyTargetTypes(Gen(Head + "    return struct.unpack_from(\"b\", buf, 0)[0]\n")));
    }

    [Fact]
    public void AnUnsignedCodeProducesNoSignedReinterpretation()
    {
        var types = CopyTargetTypes(Gen(Head + "    return struct.unpack_from(\"<H\", buf, 0)[0] & 0xFF\n")).ToList();

        Assert.DoesNotContain(DataType.INT16, types);
        Assert.DoesNotContain(DataType.INT8, types);
    }

    // pack_into writes the low byte at the low address for '<', the other way for '>'.
    // bytearray(8) zero-fills first, so the eight fill stores are skipped.
    [Fact]
    public void PackIntoWritesLittleEndianLowByteFirst()
    {
        var ir = Gen(Head + "    struct.pack_into(\"<H\", buf, 1, 0xBEEF)\n    return buf[0]\n");

        Assert.Equal(new[] { 1, 2 }, BytesWritten(ir, 8));
    }

    [Fact]
    public void PackIntoWritesBigEndianHighByteFirst()
    {
        var ir = Gen(Head + "    struct.pack_into(\">H\", buf, 1, 0xBEEF)\n    return buf[0]\n");

        Assert.Equal(new[] { 2, 1 }, BytesWritten(ir, 8));
    }

    // ---- the refusals. Each names which shape was out of scope, and none is a fallback. ----

    [Fact]
    public void AResultThatIsNotIndexedOnTheSpot_IsRefused()
    {
        var ex = Fails(Head + "    t = struct.unpack_from(\"<H\", buf, 0)\n    return buf[0]\n");

        Assert.Contains("returns a tuple", ex.Message);
        Assert.Contains("Index it on the spot", ex.Message);
    }

    [Fact]
    public void ANonLiteralFormat_IsRefused()
    {
        var ex = Fails(Head +
            "    f = \"<H\"\n" +
            "    if buf[0] > 0:\n" +
            "        f = \"<B\"\n" +
            "    return struct.unpack_from(f, buf, 0)[0] & 0xFF\n");

        Assert.Contains("must be a string known at compile time", ex.Message);
    }

    [Fact]
    public void ANonLiteralFieldIndex_IsRefused()
    {
        var ex = Fails(Head +
            "    k = buf[0]\n" +
            "    return struct.unpack_from(\"<HH\", buf, 0)[k] & 0xFF\n");

        Assert.Contains("the index must be known at compile time", ex.Message);
    }

    [Fact]
    public void AnUnsupportedCode_IsRefusedByName()
    {
        var ex = Fails(Head + "    return struct.unpack_from(\"<f\", buf, 0)[0]\n");

        Assert.Contains("'f' is not a supported struct code", ex.Message);
        Assert.Contains("B, b, H, h", ex.Message);
    }

    [Fact]
    public void ARepeatCount_IsRefusedAndSaysSo()
    {
        var ex = Fails(Head + "    return struct.unpack_from(\"<2H\", buf, 0)[0] & 0xFF\n");

        Assert.Contains("A repeat count is not supported", ex.Message);
    }

    // '=' and '@' mean native ORDER and native ALIGNMENT, which depend on the machine that ran
    // CPython. Guessing either would be a silent wrong value on a sensor reading.
    [Fact]
    public void ANativeByteOrderPrefix_IsRefused()
    {
        var ex = Fails(Head + "    return struct.unpack_from(\"=H\", buf, 0)[0] & 0xFF\n");

        Assert.Contains("native order AND native alignment", ex.Message);
    }

    // No prefix is accepted for ONE one-byte field, which is what `ROUnaryStruct(0x34, "b")`
    // writes, and refused for anything wider rather than guessed.
    [Fact]
    public void NoPrefixIsFineForOneByte_AndRefusedForTwo()
    {
        var ir = Gen(Head + "    return struct.unpack_from(\"b\", buf, 2)[0]\n");
        Assert.Equal(new[] { 2 }, BytesRead(ir));

        var ex = Fails(Head + "    return struct.unpack_from(\"H\", buf, 0)[0] & 0xFF\n");
        Assert.Contains("gives no byte order", ex.Message);
    }

    [Fact]
    public void PackingSeveralFieldsAtOnce_IsRefused()
    {
        var ex = Fails(Head + "    struct.pack_into(\"<HH\", buf, 0, 1)\n    return buf[0]\n");

        Assert.Contains("describes 2 fields", ex.Message);
        Assert.Contains("one call per field", ex.Message);
    }

    [Fact]
    public void AFieldIndexPastTheEndOfTheFormat_IsRefused()
    {
        var ex = Fails(Head + "    return struct.unpack_from(\"<H\", buf, 0)[3] & 0xFF\n");

        Assert.Contains("describes 1 field", ex.Message);
        Assert.Contains("no field 3", ex.Message);
    }
}
