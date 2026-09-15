using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#362. A module-level scratch buffer that starts at one byte and is grown to its final
/// size by the constructors that use it is the standard CircuitPython shape, and PyMCU refuses
/// it with a sentence about a construct the program does not contain:
///
///     '.extend()' requires a typed list; an untyped '[]' has no runtime list.
///
/// There is no `[]` in the program and no list. The receiver is a bytearray, which PyMCU sizes
/// at compile time and gives real SRAM; `.extend()` has no dispatch for it, so the call falls
/// through to the message written for an untyped list.
///
/// Nothing about it is dynamic: every `_fit(n)` a driver makes is called with `n` a literal, so
/// the buffer takes the largest size it is ever asked for, `len()` folds to that, and the guard
/// folds away with it. That is the `claim()` intrinsic's shape with `max` where claim has
/// `equal`.
///
/// The assertions are on the SIZE the compiled buffer takes and on what `len()` folds to. A
/// test that only checked the program compiles would pass with a buffer of one byte, and every
/// store past the first would land somewhere else.
///
/// adafruit_register/__init__.py is eleven lines and this is all of them.
/// </summary>
public class BufferGrownByExtendTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    /// <summary>
    /// The size the buffer is compiled at, read from what actually decides its storage: the
    /// element count every array instruction carries. ProgramIR.GlobalArrays is NOT it -- the
    /// IR generator never writes that dictionary, so a test asserting on it reads zero for a
    /// buffer that was allocated correctly, and passes for one that was not.
    /// </summary>
    private static int SizeOfGlobalArray(ProgramIR ir, string name) =>
        ir.Functions.SelectMany(f => f.Body)
            .Select(i => i switch
            {
                ArrayStore s when s.ArrayName.EndsWith(name) => s.Count,
                ArrayLoad l when l.ArrayName.EndsWith(name) => l.Count,
                _ => 0,
            })
            .DefaultIfEmpty(0).Max();

    private static Val LastStored(ProgramIR ir, string array) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.ArrayName.EndsWith(array))
            .Select(s => s.Src).Last();

    private const string Fit =
        "_BUFFER = bytearray(1)\n" +
        "out = bytearray([0, 0, 0, 0, 0, 0, 0, 0])\n" +
        "def _fit(size: uint8) -> None:\n" +
        "    if len(_BUFFER) < 1 + size:\n" +
        "        _BUFFER.extend(bytes(1 + size - len(_BUFFER)))\n";

    // The extend written where the size is already a literal, with no helper in between. It
    // isolates the growth itself from the rule that carries the size to it.
    [Fact]
    public void AnExtendAtModuleLevel_SizesTheBuffer()
    {
        var ir = Gen(
            "_BUFFER = bytearray(1)\n" +
            "_BUFFER.extend(bytes(2))\n");

        Assert.Equal(3, SizeOfGlobalArray(ir, "_BUFFER"));
    }

    // One grower: the buffer is 1 + 2 bytes and every index up to 2 is inside it.
    [Fact]
    public void OneExtendWithAConstantSize_SizesTheBuffer()
    {
        var ir = Gen(Fit + "_fit(2)\n");

        Assert.Equal(3, SizeOfGlobalArray(ir, "_BUFFER"));
    }

    // Several growers: the buffer takes the LARGEST, which is the whole point of the shape.
    // A buffer sized by the last caller instead would be 2 bytes here and the 4-byte descriptor
    // would write past it.
    [Fact]
    public void SeveralExtends_SizeTheBufferToTheLargest()
    {
        var ir = Gen(Fit +
            "_fit(4)\n" +
            "_fit(1)\n" +
            "_fit(2)\n");

        Assert.Equal(5, SizeOfGlobalArray(ir, "_BUFFER"));
    }

    // `len()` must fold to the size the buffer ended up with, not to the size it was declared
    // with, or the guard inside _fit() and every `in_end=1 + width` in a driver are wrong.
    [Fact]
    public void LenFoldsToTheGrownSize()
    {
        var ir = Gen(Fit +
            "_fit(3)\n" +
            "out[0] = len(_BUFFER)\n");

        Assert.Equal(new Constant(4), LastStored(ir, "out"));
    }

    // A size only known at run time has no compile-time answer and must be refused, naming the
    // buffer. Silently taking the declared size is how a driver writes past its own buffer.
    [Fact]
    public void AnExtendWithARuntimeSize_IsRefusedNamingTheBuffer()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "_BUFFER = bytearray(1)\n" +
            "_BUFFER.extend(bytes(GPIOR0.value))\n"));

        Assert.Contains("_BUFFER", ex.Message);
        Assert.DoesNotContain("untyped", ex.Message);
    }

    private static int ArrayStoreCount(ProgramIR ir, string name) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Count(s => s.ArrayName.EndsWith(name));

    /// <summary>
    /// PyMCU#411. A .extend() that grows a buffer by a large, compile-time-constant amount used
    /// to unroll into one ArrayStore per new slot regardless of how many: growing PulseCapture's
    /// ring from 1 to 70 entries cost 432 bytes of flash over a 754-byte program, on stores that
    /// zero SRAM the generic BSS-clear loop already zeros at boot.
    ///
    /// Below BufferExtendLoopThreshold the unrolled form is still smaller (measured on AVR: a
    /// two-word element crosses over between 13 and 14 added slots), so it must stay unrolled --
    /// every snapshot of a small extend() (#362's own shape) has to stay byte-identical.
    /// </summary>
    [Fact]
    public void AtTheThreshold_StaysUnrolled()
    {
        var ir = Gen("_BUFFER = bytearray(1)\n_BUFFER.extend(bytes(13))\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();

        // The declaration's own zero byte is one ArrayStore, the 13-slot extend is 13 more
        // (this is the unrolled form: no loop construct anywhere in the program).
        Assert.Equal(14, ArrayStoreCount(ir, "_BUFFER"));
        Assert.DoesNotContain(body, i => i is JumpIfGreaterOrEqual);
    }

    /// <summary>
    /// One slot past the threshold, growth is a counted loop instead: one ArrayStore whose
    /// index is the loop variable (not a compile-time constant), reached through a label a
    /// backward jump returns to -- the IR shape a `while` loop over a run-time bound already
    /// uses elsewhere in this compiler (ConstTables.cs's row-search loop). The declaration's own
    /// zero byte is still a separate, constant-indexed store -- only the GROWTH becomes a loop.
    /// </summary>
    [Fact]
    public void PastTheThreshold_UsesACountedLoop_NotOneStorePerSlot()
    {
        var ir = Gen("_BUFFER = bytearray(1)\n_BUFFER.extend(bytes(14))\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        var bufferStores = body.OfType<ArrayStore>().Where(s => s.ArrayName.EndsWith("_BUFFER")).ToList();

        Assert.Equal(2, bufferStores.Count);   // the declaration's store, and the loop's
        Assert.Contains(body, i => i is JumpIfGreaterOrEqual);
        Assert.Contains(body, i => i is Jump);
        var loopStore = Assert.Single(bufferStores, s => s.Index is not Constant);
        Assert.IsNotType<Constant>(loopStore.Index);
    }

    // The buffer still ends up the right size and reads back zero either side of the threshold
    // -- the loop form must zero exactly the new slots, not more and not fewer.
    [Fact]
    public void PastTheThreshold_TheBufferIsStillSizedAndZeroedCorrectly()
    {
        var ir = Gen(
            "_BUFFER = bytearray(1)\n" +
            "out = bytearray([0])\n" +
            "_BUFFER.extend(bytes(14))\n" +
            "out[0] = len(_BUFFER)\n");

        Assert.Equal(15, SizeOfGlobalArray(ir, "_BUFFER"));
        Assert.Equal(new Constant(15), LastStored(ir, "out"));
    }
}
