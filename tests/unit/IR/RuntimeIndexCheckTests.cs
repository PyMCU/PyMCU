using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A subscript with a RUN-TIME index into a fixed-size sequence used to read or write whatever
/// sat next to it and say nothing. It is now checked against the length and raises IndexError,
/// with a negative index counting from the end. The check is dropped only where the index is
/// proven to be in range by its shape: an unsigned type that cannot reach the length, `x & MASK`,
/// `x % N`, or the counter of an enclosing range() loop that stays inside it.
/// </summary>
public class RuntimeIndexCheckTests
{
    private static List<Instruction> Body(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
        return ir.Functions.SelectMany(f => f.Body).ToList();
    }

    private const string Array4 = "from pymcu.types import uint8\nxs: uint8[4] = [1, 2, 3, 4]\n\n";

    private static int IndexErrors(List<Instruction> b) =>
        b.OfType<SignalError>().Count(s => s.Code is Constant { Value: 3 });

    [Fact]
    public void ARunTimeSignedIndexIsCheckedAndCountsFromTheEnd()
    {
        var b = Body(Array4 + "def get(i: int) -> int:\n    return xs[i]\n\nx: int = get(2)\n");
        Assert.Equal(1, IndexErrors(b));
        // negative index: the length is added before the compare
        Assert.Contains(b, i => i is AugAssign { Op: PyMCU.IR.BinaryOp.Add, Operand: Constant { Value: 4 } });
    }

    [Fact]
    public void AnUnsignedIndexThatCanReachTheLengthIsChecked()
    {
        var b = Body(Array4 + "def get(i: uint8) -> int:\n    return xs[i]\n\nx: int = get(2)\n");
        Assert.Equal(1, IndexErrors(b));
        Assert.DoesNotContain(b, i => i is AugAssign { Op: PyMCU.IR.BinaryOp.Add, Operand: Constant { Value: 4 } });
    }

    [Fact]
    public void AnUnsignedByteIntoA256ElementArrayNeedsNoCheck()
    {
        var b = Body("from pymcu.types import uint8\nbig: uint8[256] = [0] * 256\n\n" +
                     "def get(i: uint8) -> int:\n    return big[i]\n\nx: int = get(2)\n");
        Assert.Equal(0, IndexErrors(b));
    }

    [Fact]
    public void AMaskedIndexNeedsNoCheck()
    {
        var b = Body(Array4 + "def get(i: int) -> int:\n    return xs[i & 3]\n\nx: int = get(2)\n");
        Assert.Equal(0, IndexErrors(b));
    }

    [Fact]
    public void AMaskThatIsTooWideIsStillChecked()
    {
        var b = Body(Array4 + "def get(i: int) -> int:\n    return xs[i & 7]\n\nx: int = get(2)\n");
        Assert.Equal(1, IndexErrors(b));
    }

    // Small constant ranges are unrolled into constant indexes; these loops are long enough to stay loops.
    private const string Array64 = "from pymcu.types import uint8\nbig: uint8[64] = [0] * 64\n\n";

    [Fact]
    public void ARangeCounterWithinTheLengthNeedsNoCheck()
    {
        var b = Body(Array64 + "def total() -> int:\n    s: int = 0\n    for i in range(64):\n" +
                     "        s = s + big[i]\n    return s\n\nx: int = total()\n");
        Assert.Equal(0, IndexErrors(b));
    }

    [Fact]
    public void ARangeCounterPastTheLengthIsChecked()
    {
        var b = Body(Array64 + "def total() -> int:\n    s: int = 0\n    for i in range(70):\n" +
                     "        s = s + big[i]\n    return s\n\nx: int = total()\n");
        Assert.Equal(1, IndexErrors(b));
    }

    [Fact]
    public void ARangeCounterTheBodyReassignsIsChecked()
    {
        var b = Body(Array64 + "def total() -> int:\n    s: int = 0\n    for i in range(40):\n" +
                     "        i = i + 3\n        s = s + big[i]\n    return s\n\nx: int = total()\n");
        Assert.Equal(1, IndexErrors(b));
    }

    [Fact]
    public void ACounterOfARangeWithARunTimeBoundIsChecked()
    {
        var b = Body(Array64 + "def total(n: int) -> int:\n    s: int = 0\n    for i in range(n):\n" +
                     "        s = s + big[i]\n    return s\n\nx: int = total(3)\n");
        Assert.Equal(1, IndexErrors(b));
    }

    [Fact]
    public void AStoreIsCheckedToo()
    {
        var b = Body(Array4 + "def put(i: int) -> None:\n    xs[i] = 9\n\nput(1)\n");
        Assert.Equal(1, IndexErrors(b));
    }

    [Fact]
    public void ATextIsStoredOnlyWhereAHandlerBindsTheException()
    {
        string guarded = Array4 + "def get(i: int) -> int:\n    return xs[i]\n\n" +
            "try:\n    x: int = get(9)\nexcept IndexError as e:\n    pass\n";
        Assert.Contains(Body(guarded), i => i is FlashData fd
            && System.Text.Encoding.ASCII.GetString(fd.Bytes.Where(c => c != 0).Select(c => (byte)c).ToArray())
               == "list index out of range");
        var plain = Body(Array4 + "def get(i: int) -> int:\n    return xs[i]\n\nx: int = get(2)\n");
        Assert.DoesNotContain(plain, i => i is FlashData fd
            && System.Text.Encoding.ASCII.GetString(fd.Bytes.Where(c => c != 0).Select(c => (byte)c).ToArray())
               .Contains("out of range"));
    }

    [Fact]
    public void ARunTimeIndexIntoAStringIsCheckedWithTheStringWording()
    {
        string src = "s: str = \"abc\"\n\ndef at(i: int) -> int:\n    return s[i]\n\nx: int = at(1)\n" ;
        var b = Body("try:\n    pass\nexcept IndexError as e:\n    pass\n" + src);
        Assert.Equal(1, IndexErrors(b));
    }

    [Fact]
    public void ACounterOfARangeWithARunTimeStopIsNeverNegativeSoItSkipsTheNegativePath()
    {
        var b = Body(Array64 + "def total(n: int) -> int:\n    s: int = 0\n    for i in range(n):\n" +
                     "        s = s + big[i + 1]\n    return s\n\nx: int = total(3)\n");
        Assert.Equal(1, IndexErrors(b));
        // checked against the length, but without adding the length to a negative value
        Assert.DoesNotContain(b, i => i is AugAssign { Op: PyMCU.IR.BinaryOp.Add, Operand: Constant { Value: 64 } });
    }

    [Fact]
    public void AnIndexThatCanBeNegativeKeepsTheNegativePath()
    {
        var b = Body(Array64 + "def get(i: int, j: int) -> int:\n    return big[i - j]\n\nx: int = get(5, 2)\n");
        Assert.Contains(b, i => i is AugAssign { Op: PyMCU.IR.BinaryOp.Add, Operand: Constant { Value: 64 } });
    }

    [Fact]
    public void ReadingAndWritingTheSameIndexExpressionChecksItOnce()
    {
        var b = Body(Array64 + "def bump(i: int, j: int) -> None:\n    big[i + j] = big[i + j] + 1\n\nbump(1, 2)\n");
        // one compare against the length for two accesses (the negative path is the same single check)
        Assert.Equal(1, b.OfType<JumpIfGreaterOrEqual>().Count(j => j.Src2 is Constant { Value: 64 }));
    }

    [Fact]
    public void AWriteBetweenTwoAccessesForcesASecondCheck()
    {
        var b = Body(Array64 + "def bump(i: int, j: int) -> int:\n    a: int = big[i + j]\n    i = i + 5\n" +
                     "    return a + big[i + j]\n\nx: int = bump(1, 2)\n");
        Assert.Equal(2, b.OfType<JumpIfGreaterOrEqual>().Count(j => j.Src2 is Constant { Value: 64 }));
    }

    [Fact]
    public void ARoundWithMoreDigitsThanAFloatHoldsIsTheValueItself()
    {
        var b = Body("def f(x: float) -> float:\n    return round(x, 20)\n\nr: float = f(1.5)\n");
        Assert.DoesNotContain(b, i => i is Call c && c.FunctionName.Contains("round2"));
    }

    [Fact]
    public void ARoundBelowTheSupportedNegativeRangeIsStillRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(
            () => Body("def f(x: float) -> float:\n    return round(x, -20)\n\nr: float = f(1.5)\n"));
        Assert.Contains("below the supported range", ex.Message);
    }

    // PulseIn's ring is declared with one entry and grown with .extend() by a function lowered after
    // the reader; the reader's check must bound the length the buffer ends with.
    private static int Compares(List<Instruction> b, int n) =>
        b.OfType<JumpIfGreaterOrEqual>().Count(j => j.Src2 is Constant { Value: var v } && v == n)
        + b.OfType<JumpIfLessThan>().Count(j => j.Src2 is Constant { Value: var v } && v == n);

    private const string RingReader =
        "from pymcu.types import uint16, uint8\nring: uint16[1] = [0]\nother: uint8[4] = [1, 2, 3, 4]\n\n" +
        "def get(i: int) -> int:\n    return ring[i]\n\n" +
        "def get_other(i: int) -> int:\n    return other[i]\n\n";

    [Fact]
    public void ABufferExtendedAfterItsReaderIsCheckedAgainstTheFinalLength()
    {
        var b = Body(RingReader + "def grow():\n    ring.extend(bytes(7))\n\ngrow()\nx: int = get(3) + get_other(1)\n");
        Assert.Equal(1, Compares(b, 8));
        Assert.Equal(0, Compares(b, 1));
    }

    [Fact]
    public void ExtendOnAnotherBufferLeavesThisOnesCheckAlone()
    {
        var b = Body(RingReader + "def grow():\n    other2.extend(bytes(7))\n\n" +
                     "other2: uint8[1] = [0]\ngrow()\nx: int = get(0) + get_other(1)\n");
        Assert.Equal(1, Compares(b, 1));
        Assert.Equal(1, Compares(b, 4));
    }

    [Fact]
    public void ExtendInAStringOrACommentChangesNothing()
    {
        var b = Body(RingReader + "# ring.extend(bytes(7))\ns: str = \"ring.extend(x)\"\nx: int = get(0) + get_other(1)\n");
        Assert.Equal(1, Compares(b, 1));
        Assert.Equal(0, Compares(b, 8));
    }
}
