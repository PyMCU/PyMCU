using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The unhandled-exception runtime of the RISC-V backend. The frontend emits a jump to
/// __pymcu_unhandled_exn after any call that can fail and is not inside a try; the backend used
/// to define no such label, so the first program that could raise failed at the linker.
/// </summary>
public class RISCVUnhandledExnTests
{
    private static readonly DeviceConfig Ch32v003 = new() { Chip = "ch32v003", Arch = "ch32v" };

    private static string Compile(ProgramIR program)
    {
        var sw = new StringWriter();
        new RiscvCodeGen(Ch32v003).Compile(program, sw);
        return sw.ToString();
    }

    private static ProgramIR Program(params Function[] functions)
    {
        var p = new ProgramIR();
        foreach (var f in functions) p.Functions.Add(f);
        return p;
    }

    private static Function Fn(string name, params Instruction[] body)
        => new() { Name = name, Body = body.ToList() };

    private static readonly Instruction[] CallThenCheck =
    {
        new Call("risky", new List<Val>(), new NoneVal()),
        new BranchOnError("__pymcu_unhandled_exn"),
        new Return(new NoneVal()),
    };

    [Fact]
    public void ABranchOnErrorToTheReportGetsTheLabelItJumpsTo()
    {
        var asm = Compile(Program(
            Fn("main", CallThenCheck),
            Fn("risky", new SignalError(new Constant(1), null))));

        Assert.Contains("\tbnez\ts1, __pymcu_unhandled_exn", asm);
        Assert.Matches(@"(?m)^__pymcu_unhandled_exn:", asm);
    }

    [Fact]
    public void TheReportPrintsEAndTheNameOfTheCodeThroughUsart1()
    {
        var asm = Compile(Program(
            Fn("main", CallThenCheck),
            Fn("risky", new SignalError(new Constant(1), null))));

        Assert.Contains("0x4001380C", asm);                         // CTLR1: is UE set?
        Assert.Contains("li\ta0, 69", asm);                         // 'E'
        Assert.Contains("li\ta0, 58", asm);                         // ':'
        Assert.Contains("beq\ts1, t0, __pymcu_exn_name_1", asm);
        Assert.Matches(@"__pymcu_exn_str_1:\s+\.asciz ""[A-Za-z]+""", asm);
        Assert.Contains("0x40013800", asm);                         // STATR/DATAR window
    }

    [Fact]
    public void WithNoMessageMachineryItEndsTheLineItself()
    {
        var asm = Compile(Program(
            Fn("main", CallThenCheck),
            Fn("risky", new SignalError(new Constant(1), null))));

        Assert.DoesNotContain("__pymcu_exn_tail", asm);
        Assert.Contains("li\ta0, 13", asm);
        Assert.Contains("li\ta0, 10", asm);
    }

    [Fact]
    public void WhenTheProgramHasAMessageTailTheReportCallsIt()
    {
        var asm = Compile(Program(
            Fn("main", CallThenCheck),
            Fn("risky", new SignalError(new Constant(1), null)),
            Fn("__pymcu_exn_tail", new Return(new NoneVal()))));

        Assert.Contains("call\t__pymcu_exn_tail", asm);
    }

    [Fact]
    public void ARaiseNothingCatchesInMainReportsAndParks()
    {
        var asm = Compile(Program(
            Fn("main", new SignalError(new Constant(2), null))));

        Assert.Contains("j\t__pymcu_unhandled_exn", asm);
        Assert.Matches(@"(?m)^__pymcu_unhandled_exn:", asm);
    }

    [Fact]
    public void AProgramThatCannotFailCarriesNoReport()
    {
        var asm = Compile(Program(Fn("main", new Return(new Constant(0)))));

        Assert.DoesNotContain("__pymcu_unhandled_exn", asm);
        Assert.DoesNotContain("__pymcu_exn_putc", asm);
    }
}
