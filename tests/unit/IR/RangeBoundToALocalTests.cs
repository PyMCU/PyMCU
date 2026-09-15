using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#363. A range() whose bounds fold is refused the moment it is given a name, even when
/// the name is only ever used as an iterable:
///
///     order = range(2, 0, -1)
///     for i in order:            # this IS using it as the iterable of a for loop
///         ...
///
/// with `range() is not a value in PyMCU: use it as the iterable of a for loop, in
/// 'x in range(...)', in reversed(range(...)) or in enumerate(range(...))`. The refusal is
/// about where the call appears, not about what the program does with it, and `reversed(order)`
/// -- a form the message itself offers -- is refused on the same line.
///
/// A name that escapes into any other position keeps the refusal, which is the last test here:
/// accepting the name as an iterable must not make range() a value.
///
/// adafruit_register.i2c_bits picks its byte order exactly this way, assigning a compile-time
/// sequence to one name from two branches and consuming it in one loop.
/// </summary>
public class RangeBoundToALocalTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    /// <summary>Every index the program loads from `buf`, in order.</summary>
    private static List<int> IndicesLoaded(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Where(l => l.ArrayName.EndsWith("buf") && l.Index is Constant)
            .Select(l => ((Constant)l.Index).Value).ToList();

    private const string Buf = "buf = bytearray([0, 1, 2, 3])\n";

    [Fact]
    public void ARangeBoundToAName_IsIterable()
    {
        var ir = Gen(Buf +
            "def main() -> None:\n" +
            "    order = range(2, 0, -1)\n" +
            "    for i in order:\n" +
            "        buf[0] = buf[i]\n");

        Assert.Equal(new[] { 2, 1 }, IndicesLoaded(ir));
    }

    [Fact]
    public void AReversedRangeBoundToAName_IsIterable()
    {
        var ir = Gen(Buf +
            "def main() -> None:\n" +
            "    order = range(1, 3)\n" +
            "    for i in reversed(order):\n" +
            "        buf[0] = buf[i]\n");

        Assert.Equal(new[] { 2, 1 }, IndicesLoaded(ir));
    }

    // The shape in the library: one name, two branches, one loop. The branch is decided at
    // compile time here, which is what makes both assignments compile-time sequences.
    [Fact]
    public void ANameReboundFromReversed_IsIterable()
    {
        var ir = Gen(Buf +
            "def main() -> None:\n" +
            "    lsb_first = False\n" +
            "    order = range(2, 0, -1)\n" +
            "    if not lsb_first:\n" +
            "        order = reversed(order)\n" +
            "    for i in order:\n" +
            "        buf[0] = buf[i]\n");

        Assert.Equal(new[] { 1, 2 }, IndicesLoaded(ir));
    }

    // range() must not become a value. A name that leaves the iterable positions keeps today's
    // refusal, and the sentence should be able to say which use it objects to.
    [Fact]
    public void ARangeNameUsedAsAValue_IsStillRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Buf +
            "def take(r: uint8) -> uint8:\n" +
            "    return r\n" +
            "def main() -> None:\n" +
            "    order = range(2, 0, -1)\n" +
            "    buf[0] = take(order)\n"));

        Assert.Contains("range()", ex.Message);
    }
}
