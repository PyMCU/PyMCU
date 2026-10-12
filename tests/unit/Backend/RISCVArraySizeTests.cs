using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A 2 KB part cannot afford four bytes for every byte of an array. The IR says how many bytes
/// each fixed array occupies (ArrayByteSizes); the reservation follows it instead of assuming
/// a word per element.
/// </summary>
public class RISCVArraySizeTests
{
    private static readonly DeviceConfig Ch32v003 = new() { Chip = "ch32v003", Arch = "ch32v" };

    private static string Compile(ProgramIR p)
    {
        var sw = new StringWriter();
        new RiscvCodeGen(Ch32v003).Compile(p, sw);
        return sw.ToString();
    }

    private static ProgramIR Program(Dictionary<string, int> globalArrays, Dictionary<string, int> byteSizes)
    {
        var p = new ProgramIR { GlobalArrays = globalArrays, ArrayByteSizes = byteSizes };
        p.Functions.Add(new Function { Name = "main", Body = { new Return(new NoneVal()) } });
        return p;
    }

    [Fact]
    public void AByteArrayTakesItsByteSizeNotAWordPerElement()
    {
        var asm = Compile(Program(new() { ["data"] = 512 }, new() { ["data"] = 512 }));

        Assert.Matches(@"data:\s+\.zero\t512\b", asm);
    }

    [Fact]
    public void AWordArrayTakesItsByteSize()
    {
        var asm = Compile(Program(new() { ["w"] = 4 }, new() { ["w"] = 16 }));

        Assert.Matches(@"w:\s+\.zero\t16\b", asm);
    }

    [Fact]
    public void AnArrayWithNoRecordedSizeKeepsTheWordPerElementAssumption()
    {
        var asm = Compile(Program(new() { ["old"] = 4 }, new()));

        Assert.Matches(@"old:\s+\.zero\t16\b", asm);
    }

    [Fact]
    public void ALocalArrayOnlyEverPassedByAddressStillGetsStorage()
    {
        // Its stores folded away, so nothing indexes it: only the address is taken. The
        // recorded size is the one thing that says it needs space at all.
        var p = new ProgramIR { ArrayByteSizes = new() { ["f.digs"] = 24 } };
        p.Functions.Add(new Function
        {
            Name = "main",
            Body = { new Call("use", new List<Val> { new ArrayBase("f.digs") }, new NoneVal()), new Return(new NoneVal()) },
        });

        var asm = Compile(p);

        Assert.Matches(@"f\.digs:\s+\.zero\t24\b", asm);
    }

    [Fact]
    public void ARecordedArrayNothingReferencesTakesNoStorage()
    {
        var p = new ProgramIR { ArrayByteSizes = new() { ["unused"] = 24 } };
        p.Functions.Add(new Function { Name = "main", Body = { new Return(new NoneVal()) } });

        Assert.DoesNotContain("unused", Compile(p));
    }
}
