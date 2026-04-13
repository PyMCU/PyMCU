using PyMCU.Backend;
using PyMCU.Backend.Targets.PIC12;
using PyMCU.Common;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

public class PIC12CodeGenTests
{
    private static readonly DeviceConfig Pic10f200 = new() { Chip = "pic10f200", Arch = "pic12" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new PIC12CodeGen(config ?? Pic10f200);
        var sw = new StringWriter();
        codegen.Compile(program, sw);
        return sw.ToString();
    }

    private static ProgramIR MakeProgram(string name, params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = name, Body = body.ToList() });
        return prog;
    }

    // ─── SimpleReturn ────────────────────────────────────────────────────────

    [Fact]
    public void SimpleReturn()
    {
        var prog = MakeProgram("main", new Return(new Constant(42)));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x2A", asm);  // 42 = 0x2A
        Assert.Contains("RETLW\t0", asm);
    }

    // ─── NoAddLW_Optimization ────────────────────────────────────────────────

    [Fact]
    public void NoAddLW_Optimization()
    {
        // a = b + 10 — PIC12 baseline has no ADDLW, must use MOVLW + ADDWF
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Add, new Variable("b"), new Constant(10), new Variable("a")),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.DoesNotContain("ADDLW", asm);
        Assert.Contains("MOVLW\t0x0A", asm);
        Assert.Contains("ADDWF", asm);
    }

    // ─── FactorySupport ──────────────────────────────────────────────────────

    [Fact]
    public void FactorySupport()
    {
        var codegen = CodeGenFactory.Create("pic10f200", Pic10f200);
        Assert.IsType<PIC12CodeGen>(codegen);
    }

    // ─── TRISInstruction ─────────────────────────────────────────────────────

    [Fact]
    public void TRISInstruction()
    {
        // TRISGPIO (0x86) = 0 → TRIS 6 + shadow variable
        var prog = MakeProgram("main",
            new Copy(new Constant(0), new MemoryAddress(0x86)));
        var asm = Compile(prog);

        Assert.Contains("TRIS\t6", asm);
        Assert.Contains("__tris_shadow_6", asm);
    }

    // ─── NegationNoHardcoded ──────────────────────────────────────────────────

    [Fact]
    public void NegationNoHardcoded()
    {
        // x = -y — old code hardcoded MOVWF 0x07 (GPIO), must use __neg_temp instead
        var prog = MakeProgram("main",
            new Unary(UnaryOp.Neg, new Variable("y"), new Variable("x")));
        var asm = Compile(prog);

        Assert.DoesNotContain("MOVWF\t0x07", asm);
        Assert.Contains("__neg_temp", asm);
    }
}
