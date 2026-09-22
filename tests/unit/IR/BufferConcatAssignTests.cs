using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `buf += src` on a fixed buffer is the in-place concat CPython gives the spelling: the
/// buffer's compile-time size grows by the source's length and the source's bytes are
/// stored into the new tail -- the same stores `buf[len(buf):] = src` emits. The shape
/// that demanded it is adafruit_seesaw.write:
///
///     full_buffer = bytearray([reg_base, reg])
///     if buf is not None:
///         full_buffer += buf
///     i2c.write(full_buffer)
///
/// where `write(reg_base, reg)` callers must send two bytes and `write(r, b, cmd)`
/// callers six, from one function body.
///
/// The subtlety is that sibling @inline expansions of `write` share the
/// `inlineN.write.full_buffer` storage key, so the bump cannot live on the storage
/// size: one expansion's += would grow every sibling's len(). A declaration re-bases
/// the logical length; a += moves it; the storage key keeps the max.
///
/// A += is refused unless it runs in exactly the run-time branch context the buffer
/// was declared in -- otherwise it can fire a different number of times than the
/// declaration, and a size fixed at compile time cannot say how long the buffer is.
/// </summary>
public class BufferConcatAssignTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(
                "from pymcu.chips.atmega328p import GPIOR0\n" +
                "from pymcu.types import uint8\n\n" + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static CompilerError Fails(string src) =>
        Assert.Throws<CompilerError>(() => Gen(src));

    /// <summary>Byte indices stored into the buffer, in order.</summary>
    private static List<int> StoresInto(ProgramIR ir, string nameSuffix) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(a => a.ArrayName.EndsWith(nameSuffix, StringComparison.Ordinal)
                        && a.Index is Constant)
            .Select(a => ((Constant)a.Index).Value).ToList();

    /// <summary>Every constant returned by the program, in order.</summary>
    private static List<int> ReturnedConstants(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Return>()
            .Where(r => r.Value is Constant)
            .Select(r => ((Constant)r.Value).Value).ToList();

    [Fact]
    public void ConcatGrowsTheBufferAndStoresTheTail()
    {
        var ir = Gen(
            "def main() -> uint8:\n" +
            "    a = bytearray([1, 2])\n" +
            "    b = bytearray([3])\n" +
            "    a += b\n" +
            "    return len(a)\n");

        // len(a) folds to 3, and the appended byte lands at index 2 -- not index 0,
        // which is what the old scalar-AugAssign lowering produced.
        Assert.Contains(3, ReturnedConstants(ir));
        Assert.Contains(2, StoresInto(ir, ".a"));
    }

    [Fact]
    public void InlinedCalleeBufferConcatsItsArgument()
    {
        // The seesaw shape: `write` is @inline, `buf` is a per-call argument, and
        // `if buf is not None` folds at each expansion so only the buffer call
        // site lowers the +=.
        var ir = Gen(
            "@inline\n" +
            "def write(reg_base: uint8, reg: uint8, buf = None) -> uint8:\n" +
            "    full_buffer = bytearray([reg_base, reg])\n" +
            "    if buf is not None:\n" +
            "        full_buffer += buf\n" +
            "    return len(full_buffer)\n" +
            "\n" +
            "def main() -> uint8:\n" +
            "    cmd = bytearray([9, 9, 9, 9])\n" +
            "    x = write(0, 1)          # buf=None: two bytes\n" +
            "    y = write(0, 2, cmd)     # buf given: six bytes\n" +
            "    return x * 10 + y\n");

        // The two len() calls fold to 2 and 6: the buf=None expansion keeps the
        // declared length while the cmd expansion's += grows it. Both expansions
        // share the `inline1.write.full_buffer` storage key, so the cmd one's bump
        // would leak into the None one's len() if the size were read off storage.
        var copiedConstants = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Src is Constant).Select(c => ((Constant)c.Src).Value).ToList();
        Assert.Contains(2, copiedConstants);
        Assert.Contains(6, copiedConstants);
        // And the cmd expansion's appended bytes land in the tail, not at index 0.
        var fullBufferStores = StoresInto(ir, "write.full_buffer");
        Assert.Contains(5, fullBufferStores);
    }

    [Fact]
    public void ConcatInsideTheLoopThatDeclaredItIsAllowed()
    {
        // `b` is re-created each iteration, so one bump covers every execution.
        var ir = Gen(
            "def main() -> uint8:\n" +
            "    src = bytearray([7])\n" +
            "    out = bytearray(4)\n" +
            "    i: uint8 = 0\n" +
            "    while i < 2:\n" +
            "        b = bytearray([1])\n" +
            "        b += src\n" +
            "        out[i] = b[1]\n" +
            "        i += 1\n" +
            "    return out[1]\n");

        Assert.Contains(1, StoresInto(ir, ".b"));
    }

    [Fact]
    public void ConcatInALoopOnALoopExternalBufferIsRefused()
    {
        // The declaration runs once; the += would run per iteration, and a fixed
        // size cannot express growth that happens at run time.
        var err = Fails(
            "def main() -> uint8:\n" +
            "    b = bytearray([1])\n" +
            "    src = bytearray([7])\n" +
            "    i: uint8 = 0\n" +
            "    while i < 4:\n" +
            "        b += src\n" +
            "        i += 1\n" +
            "    return b[0]\n");

        Assert.Contains("different context", err.Message);
    }

    [Fact]
    public void ConcatUnderARuntimeConditionalIsRefused()
    {
        // `if` on a runtime value does not fold, so the += can be skipped while
        // the size stays bumped -- len() would answer grown either way.
        var err = Fails(
            "def main() -> uint8:\n" +
            "    b = bytearray([1])\n" +
            "    src = bytearray([7])\n" +
            "    if GPIOR0.value > 3:\n" +
            "        b += src\n" +
            "    return len(b)\n");

        Assert.Contains("different context", err.Message);
    }

    [Fact]
    public void ConcatInAnInlinedCallInsideALoopIsAllowed()
    {
        // The demandant's real shape: the CALL is inside the runtime loop, but the
        // buffer is a callee local -- declared and grown inside the same inlined
        // body each iteration, so the bump is once per construction.
        var ir = Gen(
            "@inline\n" +
            "def write(reg_base: uint8, reg: uint8, buf = None) -> uint8:\n" +
            "    full_buffer = bytearray([reg_base, reg])\n" +
            "    if buf is not None:\n" +
            "        full_buffer += buf\n" +
            "    return len(full_buffer)\n" +
            "\n" +
            "def main() -> uint8:\n" +
            "    cmd = bytearray([9, 9, 9, 9])\n" +
            "    acc: uint8 = 0\n" +
            "    i: uint8 = 0\n" +
            "    while i < 2:\n" +
            "        acc += write(0, 2, cmd)\n" +
            "        i += 1\n" +
            "    return acc\n");

        Assert.NotEmpty(ir.Functions);
    }
}
