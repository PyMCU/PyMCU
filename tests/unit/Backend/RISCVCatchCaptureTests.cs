using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A catch dispatcher reads the error code through the sentinel variable __exn_r22_capture, a
/// read-only alias of the error register (R22 on AVR, s1 here). The RISC-V backend used to treat
/// it as an ordinary stack variable nothing ever wrote, so every handler compared against
/// whatever the stack held and a caught exception fell through to the unhandled report.
/// </summary>
public class RISCVCatchCaptureTests
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

    private static Function Fn(string name, params Instruction[] body)
        => new() { Name = name, Body = body.ToList() };

    [Fact]
    public void TheCaptureAliasReadsTheErrorRegister()
    {
        var asm = Compile(Fn("main",
            new Copy(new Variable("__exn_r22_capture", DataType.UINT8),
                     new Variable("__exn_code_0", DataType.UINT8)),
            new Return(new NoneVal())));

        Assert.Contains("mv\tt0, s1", asm);
        Assert.DoesNotContain("la\tt2, __exn_r22_capture", asm);
    }

    [Fact]
    public void AComparisonAgainstTheCaptureAliasReadsTheErrorRegister()
    {
        var asm = Compile(Fn("main",
            new JumpIfEqual(new Variable("__exn_r22_capture", DataType.UINT8), new Constant(2), "L_h"),
            new Label("L_h"),
            new Return(new NoneVal())));

        Assert.Contains("mv\tt0, s1", asm);
        Assert.DoesNotContain("__exn_r22_capture", asm);
    }

    [Fact]
    public void AHappyReturnFromAFunctionThatCanFailClearsTheErrorRegister()
    {
        var asm = Compile(new Function { Name = "risky", CanFail = true, Body = { new Return(new Constant(3)) } });

        Assert.Matches(@"(?s)li\ta0, 3\s+li\ts1, 0\s+lw\ts0", asm);
    }

    [Fact]
    public void AReturnFromAFunctionThatCannotFailLeavesTheErrorRegisterAlone()
    {
        var asm = Compile(Fn("plain", new Return(new Constant(3))));

        Assert.DoesNotContain("li\ts1, 0", asm);
    }
}
