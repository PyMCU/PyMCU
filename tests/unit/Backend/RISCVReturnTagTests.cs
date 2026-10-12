using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0009: a function returning Optional[...] or a Union hands its caller the payload and a
/// member-tag byte. On RV32E the payload comes back in a0 and the tag in a1.
/// </summary>
public class RISCVReturnTagTests
{
    private static readonly DeviceConfig Ch32v003 = new() { Chip = "ch32v003", Arch = "ch32v" };

    private static string Compile(params Function[] functions)
    {
        var p = new ProgramIR();
        foreach (var f in functions) p.Functions.Add(f);
        var sw = new StringWriter();
        new RiscvCodeGen(Ch32v003).Compile(p, sw);
        return sw.ToString();
    }

    [Fact]
    public void AReturnWithATagPutsThePayloadInA0AndTheTagInA1()
    {
        var asm = Compile(new Function
        {
            Name = "find",
            ReturnMembers = ["uint8", "None"],
            Body = { new Return(new Constant(7), new Constant(0)) },
        });

        Assert.Matches(@"(?s)li\ta0, 7\s+li\ta1, 0\s", asm);
    }

    [Fact]
    public void ANoneReturnStillCarriesItsTag()
    {
        var asm = Compile(new Function
        {
            Name = "find",
            ReturnMembers = ["uint8", "None"],
            Body = { new Return(new NoneVal(), new Constant(1)) },
        });

        Assert.Contains("li\ta1, 1", asm);
    }

    [Fact]
    public void ACallReadsTheTagBackFromA1()
    {
        var asm = Compile(
            new Function
            {
                Name = "main",
                Body =
                {
                    new Call("find", new List<Val>(), new Temporary("v", DataType.UINT8),
                             new Temporary("tag", DataType.UINT8)),
                    new Return(new NoneVal()),
                },
            },
            new Function
            {
                Name = "find",
                ReturnMembers = ["uint8", "None"],
                Body = { new Return(new Constant(7), new Constant(0)) },
            });

        Assert.Matches(@"call\tfind[\s\S]*?sw\ta1,", asm);
    }

    [Fact]
    public void AnOrdinaryReturnLeavesA1Alone()
    {
        var asm = Compile(new Function { Name = "plain", Body = { new Return(new Constant(7)) } });

        Assert.DoesNotContain("a1", asm.Substring(asm.IndexOf("plain:", StringComparison.Ordinal)));
    }
}
