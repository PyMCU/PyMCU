using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#361. `struct` is expanded at compile time from a literal format, and three shapes that
/// CircuitPython register drivers write every day are unreachable.
///
/// 1. `v = struct.unpack_from(fmt, buf, off)` then `v[0]`: refused, on the grounds that a tuple
///    needs a heap. Every element of that tuple is a scalar whose width the format already
///    fixed and the name is read only by constant index, so the tuple never has to exist at run
///    time. `i2c_struct.Struct.__get__` returns exactly that.
///
/// 2. `memoryview(buf)[1:]` as the buffer: refused with `Slice indexing is only supported on
///    named fixed-size arrays`, a sentence that never says memoryview and carries no caret. A
///    memoryview slice of a fixed buffer at a constant offset IS `unpack_from(fmt, buf, 1)`,
///    which already compiles.
///
/// 3. A multi-field format, whose refusal is the same tuple question as (1).
///
/// The last test is what already works, recorded so it is not re-implemented: a splat of a
/// compile-time tuple into pack_into already splices.
/// </summary>
public class StructBufferShapesTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static Val LastStored(ProgramIR ir, string array) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.ArrayName.EndsWith(array))
            .Select(s => s.Src).Last();

    private const string Head =
        "import struct\n" +
        "_BUFFER = bytearray([0, 0x12, 0x34, 0x56, 0x78])\n" +
        "out = bytearray([0, 0, 0, 0])\n";

    // The tuple lives only while compiling: the name is read once, by a constant index.
    [Fact]
    public void AnUnpackResultBoundToAName_IsIndexable()
    {
        var ir = Gen(Head +
            "def main() -> None:\n" +
            "    v = struct.unpack_from(\">H\", _BUFFER, 1)\n" +
            "    out[0] = v[0]\n");

        Assert.False(LastStored(ir, "out") is NoneVal);
    }

    // A two-field format, each field read by its own constant index.
    [Fact]
    public void AMultiFieldUnpackResult_IsIndexablePerField()
    {
        var ir = Gen(Head +
            "def main() -> None:\n" +
            "    v = struct.unpack_from(\">HH\", _BUFFER, 1)\n" +
            "    out[0] = v[0]\n" +
            "    out[1] = v[1]\n");

        Assert.False(LastStored(ir, "out") is NoneVal);
    }

    // A memoryview slice at a constant offset is the offset argument, spelled differently.
    [Fact]
    public void AMemoryviewSliceAsTheBuffer_IsAConstantOffset()
    {
        var ir = Gen(Head +
            "def main() -> None:\n" +
            "    out[0] = struct.unpack_from(\">H\", memoryview(_BUFFER)[1:])[0]\n");

        Assert.False(LastStored(ir, "out") is NoneVal);
    }

    // The two spellings must agree, because in the library they are the same read.
    [Fact]
    public void TheMemoryviewSpellingAndTheOffsetSpelling_ReadTheSameBytes()
    {
        var sliced = Gen(Head +
            "def main() -> None:\n" +
            "    out[0] = struct.unpack_from(\">H\", memoryview(_BUFFER)[1:])[0]\n");
        var offset = Gen(Head +
            "def main() -> None:\n" +
            "    out[0] = struct.unpack_from(\">H\", _BUFFER, 1)[0]\n");

        Assert.Equal(
            offset.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
                .Where(l => l.ArrayName.EndsWith("_BUFFER")).Select(l => l.Index.ToString()).ToList(),
            sliced.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
                .Where(l => l.ArrayName.EndsWith("_BUFFER")).Select(l => l.Index.ToString()).ToList());
    }

    // A format only known at run time still has nothing to expand, and keeps its sentence.
    [Fact]
    public void ARuntimeFormat_IsStillRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "import struct\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "_BUFFER = bytearray([0, 0, 0, 0])\n" +
            "fmts = [\"<H\", \">H\"]\n" +
            "def main() -> None:\n" +
            "    v = struct.unpack_from(fmts[GPIOR0.value], _BUFFER, 1)\n" +
            "    _BUFFER[0] = v[0]\n"));

        Assert.Contains("compile time", ex.Message);
    }
}
