using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RAM is zero when main starts (the reset code clears .bss), so the stores of zero a program
/// opens with to fill a fixed array say nothing the machine does not already hold. A
/// 1957-byte zero array otherwise costs 20 KB of flash on a part that has 16.
/// </summary>
public class RISCVZeroInitTests
{
    private static readonly DeviceConfig Ch32v003 = new() { Chip = "ch32v003", Arch = "ch32v" };

    private static string Compile(ProgramIR p)
    {
        var sw = new StringWriter();
        new RiscvCodeGen(Ch32v003).Compile(p, sw);
        return sw.ToString();
    }

    private static ProgramIR Main(params Instruction[] body)
    {
        var p = new ProgramIR();
        p.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        return p;
    }

    private static ArrayStore Store(int index, int value, string name = "pad") =>
        new(name, new Constant(index), new Constant(value), DataType.UINT8, 8);

    private static int Stores(string asm) => asm.Split('\n').Count(l => l.Contains("sb\tt1, 0(t0)"));

    [Fact]
    public void ZeroStoresAtTheStartOfMainAreNotEmitted()
    {
        var asm = Compile(Main(Store(0, 0), Store(1, 0), Store(2, 0), new Return(new NoneVal())));

        Assert.Equal(0, Stores(asm));
    }

    [Fact]
    public void TheArrayStillGetsItsStorage()
    {
        var asm = Compile(Main(Store(0, 0), Store(7, 0), new Return(new NoneVal())));

        Assert.Matches(@"pad:\s+\.zero\t8\b", asm);
    }

    [Fact]
    public void ANonZeroStoreIsKept()
    {
        var asm = Compile(Main(Store(0, 5), Store(1, 0), new Return(new NoneVal())));

        Assert.Equal(1, Stores(asm));
    }

    [Fact]
    public void AZeroStoreAfterANonZeroStoreToTheSameCellIsKept()
    {
        var asm = Compile(Main(Store(3, 9), Store(3, 0), new Return(new NoneVal())));

        Assert.Equal(2, Stores(asm));
    }

    [Fact]
    public void AZeroStoreToAnotherCellOrArrayAfterANonZeroOneIsStillElided()
    {
        var asm = Compile(Main(Store(3, 9), Store(4, 0), Store(3, 0, "other"), new Return(new NoneVal())));

        Assert.Equal(1, Stores(asm));
    }

    [Fact]
    public void AZeroStoreAfterACallIsKept()
    {
        var asm = Compile(Main(new Call("f", new List<Val>(), new NoneVal()), Store(0, 0), new Return(new NoneVal())));

        Assert.Equal(1, Stores(asm));
    }

    [Fact]
    public void AZeroStoreAfterALabelIsKept()
    {
        var asm = Compile(Main(new Label("L_1"), Store(0, 0), new Return(new NoneVal())));

        Assert.Equal(1, Stores(asm));
    }

    [Fact]
    public void AZeroStoreAtARuntimeIndexIsKeptAndSoIsEveryStoreToThatArrayAfterIt()
    {
        var asm = Compile(Main(
            new ArrayStore("pad", new Variable("i", DataType.UINT8), new Constant(0), DataType.UINT8, 8),
            Store(0, 0),
            new Return(new NoneVal())));

        Assert.Equal(2, Stores(asm));
    }

    [Fact]
    public void ZeroStoresInAnotherFunctionAreKept()
    {
        var p = Main(new Return(new NoneVal()));
        p.Functions.Add(new Function { Name = "f", Body = { Store(0, 0), new Return(new NoneVal()) } });

        Assert.Equal(1, Stores(Compile(p)));
    }

    [Fact]
    public void WithAnInterruptHandlerTheStoresAreKept()
    {
        // The handler may run before main gets to them.
        var p = Main(Store(0, 0), new Return(new NoneVal()));
        p.Functions.Add(new Function { Name = "isr", IsInterrupt = true, InterruptVector = 12, Body = { new Return(new NoneVal()) } });

        Assert.Equal(1, Stores(Compile(p)));
    }

    [Fact]
    public void RootingAReferenceDoesNotEndTheOpening()
    {
        // A program with a growable list roots it before anything else.
        var asm = Compile(Main(new GcRoot(new Variable("y", DataType.GC_REF)), Store(0, 0), new Return(new NoneVal())));

        Assert.Equal(0, Stores(asm));
    }
}
