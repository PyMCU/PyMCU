using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An instance is stored as byte cells (`d__slot`), so a 32-bit field can sit at an offset that is
/// not a multiple of four. RV32 loads and stores of a word or halfword trap on a misaligned
/// address on the CH32V003, so a pointer whose alignment is not known to suit the access goes
/// through byte accesses instead.
/// </summary>
public class RISCVUnalignedAccessTests
{
    private static readonly DeviceConfig Ch32v003 = new() { Chip = "ch32v003", Arch = "ch32v" };

    private static string Compile(params Instruction[] body)
    {
        var p = new ProgramIR();
        p.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        var sw = new StringWriter();
        new RiscvCodeGen(Ch32v003).Compile(p, sw);
        return sw.ToString();
    }

    private static Temporary T(string name, DataType t) => new(name, t);

    private static int Count(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void AWordLoadThroughAnOffsetOfTwoIntoASlotReadsFourBytes()
    {
        var asm = Compile(
            new Binary(BinaryOp.Add, new ArrayBase("d__slot"), new Constant(2), T("p", DataType.UINT32)),
            new LoadIndirect(T("p", DataType.UINT32), T("v", DataType.UINT32), DataType.UINT32));

        Assert.Equal(4, Count(asm, "lbu\tt1, 0(t0)") + Count(asm, "lbu\tt2, 1(t0)")
            + Count(asm, "lbu\tt2, 2(t0)") + Count(asm, "lbu\tt2, 3(t0)"));
        Assert.DoesNotContain("lw\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AWordLoadThroughAnOffsetOfFourIntoASlotIsOneLoad()
    {
        var asm = Compile(
            new Binary(BinaryOp.Add, new ArrayBase("d__slot"), new Constant(4), T("p", DataType.UINT32)),
            new LoadIndirect(T("p", DataType.UINT32), T("v", DataType.UINT32), DataType.UINT32));

        Assert.Contains("lw\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AWordLoadThroughThePlainArrayBaseIsOneLoad()
    {
        var asm = Compile(
            new LoadIndirect(new ArrayBase("d__slot"), T("v", DataType.UINT32), DataType.UINT32));

        Assert.Contains("lw\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AWordLoadThroughAPointerNothingComputedStaysOneLoad()
    {
        // Such a pointer is typically a peripheral register, which must be read at its width.
        var asm = Compile(
            new LoadIndirect(new Variable("p", DataType.UINT32), T("v", DataType.UINT32), DataType.UINT32));

        Assert.Contains("lw\tt1, 0(t0)", asm);
        Assert.DoesNotContain("lbu\tt2, 3(t0)", asm);
    }

    [Fact]
    public void ASignedHalfwordLoadAtAnOddSlotOffsetSignExtends()
    {
        var asm = Compile(
            new Binary(BinaryOp.Add, new ArrayBase("d__slot"), new Constant(1), T("p", DataType.UINT32)),
            new LoadIndirect(T("p", DataType.UINT32), T("v", DataType.INT16), DataType.INT16));

        Assert.Contains("lbu\tt2, 1(t0)", asm);
        Assert.Matches(@"(?s)slli\tt1, t1, 16\s+srai\tt1, t1, 16", asm);
        Assert.DoesNotContain("lh\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AByteLoadIsUntouched()
    {
        var asm = Compile(
            new LoadIndirect(new Variable("p", DataType.UINT32), T("v", DataType.UINT8), DataType.UINT8));

        Assert.Contains("lbu\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AWordStoreAtAnOffsetOfTwoIntoASlotWritesBytes()
    {
        var asm = Compile(
            new Binary(BinaryOp.Add, new ArrayBase("d__slot"), new Constant(2), T("p", DataType.UINT32)),
            new StoreIndirect(new Constant(0x12345678), T("p", DataType.UINT32), DataType.UINT32));

        Assert.DoesNotContain("sw\tt1, 0(t0)", asm);
        Assert.Contains("sb\tt1, 0(t0)", asm);
        Assert.Contains("sb\tt2, 3(t0)", asm);
    }

    [Fact]
    public void AWordStoreThroughAnAlignedSlotOffsetIsOneStore()
    {
        var asm = Compile(
            new Binary(BinaryOp.Add, new ArrayBase("d__slot"), new Constant(8), T("p", DataType.UINT32)),
            new StoreIndirect(new Constant(1), T("p", DataType.UINT32), DataType.UINT32));

        Assert.Contains("sw\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AWordLoadAtAnOffsetOfSixFromAnInstancePointerReadsBytes()
    {
        var asm = Compile(
            new Binary(BinaryOp.Add, new Variable("self", DataType.UINT32), new Constant(6), T("p", DataType.UINT32)),
            new LoadIndirect(T("p", DataType.UINT32), T("v", DataType.UINT32), DataType.UINT32));

        Assert.DoesNotContain("lw\tt1, 0(t0)", asm);
        Assert.Contains("lbu\tt2, 3(t0)", asm);
    }

    [Fact]
    public void AWordLoadAtAnOffsetOfEightFromAnInstancePointerIsOneLoad()
    {
        var asm = Compile(
            new Binary(BinaryOp.Add, new Variable("self", DataType.UINT32), new Constant(8), T("p", DataType.UINT32)),
            new LoadIndirect(T("p", DataType.UINT32), T("v", DataType.UINT32), DataType.UINT32));

        Assert.Contains("lw\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AWordStoreIntoAListElementTwoBytesPastTheBaseGoesByteByByte()
    {
        // The elements of a growable list start 2 bytes into the object, so element i is at
        // base + (i << 2) + 2 and a word there is never on a word boundary. The base is a variable
        // nothing here computed; the offset says what the address falls off.
        var asm = Compile(
            new Binary(BinaryOp.LShift, new Variable("i", DataType.UINT8), new Constant(2), T("o4", DataType.UINT32)),
            new Binary(BinaryOp.Add, T("o4", DataType.UINT32), new Constant(2), T("o", DataType.UINT32)),
            new Binary(BinaryOp.Add, new Variable("lst", DataType.UINT32), T("o", DataType.UINT32), T("p", DataType.UINT32)),
            new StoreIndirect(new Variable("v", DataType.UINT32), T("p", DataType.UINT32), DataType.UINT32));

        Assert.DoesNotContain("sw\tt1, 0(t0)", asm);
        Assert.Contains("sb\tt1, 0(t0)", asm);
        Assert.Contains("sb\tt2, 3(t0)", asm);
    }

    [Fact]
    public void AWordStoreAtABaseAndARunTimeOffsetNothingKnowsStaysOneStore()
    {
        var asm = Compile(
            new Binary(BinaryOp.Add, new Variable("base", DataType.UINT32), new Variable("off", DataType.UINT32), T("p", DataType.UINT32)),
            new StoreIndirect(new Variable("v", DataType.UINT32), T("p", DataType.UINT32), DataType.UINT32));

        Assert.Contains("sw\tt1, 0(t0)", asm);
    }
}
