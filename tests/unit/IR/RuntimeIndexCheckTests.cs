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
}
