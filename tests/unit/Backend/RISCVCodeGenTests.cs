using PyMCU.Backend;
using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

public class RISCVCodeGenTests
{
    private static readonly DeviceConfig Ch32v003 = new() { Chip = "ch32v003", Arch = "ch32v" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new RiscvCodeGen(config ?? Ch32v003);
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

    // ─── SimpleReturn ─────────────────────────────────────────────────────────

    [Fact]
    public void SimpleReturn()
    {
        var prog = MakeProgram("my_func", new Return(new Constant(42)));
        var asm = Compile(prog);
        Assert.Contains("li\ta0, 42", asm);
        Assert.Contains("ret", asm);
    }

    // ─── MainInfiniteLoop ─────────────────────────────────────────────────────

    [Fact]
    public void MainInfiniteLoop()
    {
        // main must not have ret — it loops forever
        var prog = MakeProgram("main", new Return(new Constant(0)));
        var asm = Compile(prog);
        Assert.DoesNotContain("ret", asm);
        Assert.Contains("j\tend_loop", asm);
        Assert.Contains("li\tsp, 0x20000800", asm);
    }

    // ─── NestedCallPrologue ───────────────────────────────────────────────────

    [Fact]
    public void NestedCallPrologue()
    {
        // caller is not a leaf → must save/restore ra
        var prog = MakeProgram("caller",
            new Call("other_func", [], new NoneVal()),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("sw\tra,", asm);
        Assert.Contains("lw\tra,", asm);
    }

    // ─── SoftwareMul ─────────────────────────────────────────────────────────

    [Fact]
    public void SoftwareMul()
    {
        // a = b * c — ch32v003 has no mul; must call __mulsi3
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Mul, new Variable("b"), new Variable("c"), new Variable("a")));
        var asm = Compile(prog);
        Assert.DoesNotContain("\tmul\t", asm);
        Assert.Contains("call\t__mulsi3", asm);
    }

    // ─── BinaryOps ────────────────────────────────────────────────────────────

    [Fact]
    public void BinaryOps()
    {
        // a = 10 + 20 → li t0, 10; addi t0, t0, 20; sw t0, -12(s0)
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Add, new Constant(10), new Constant(20), new Variable("a")),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("li\tt0, 10", asm);
        Assert.Contains("addi\tt0, t0, 20", asm);
        Assert.Contains("sw\tt0, -12(s0)", asm);
    }

    // ─── SubtractionOptimization ─────────────────────────────────────────────

    [Fact]
    public void SubtractionOptimization()
    {
        // a = b - 10 → addi t0, t0, -10
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Sub, new Variable("b"), new Constant(10), new Variable("a")),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("addi\tt0, t0, -10", asm);
    }

    // ─── BitManipulation ─────────────────────────────────────────────────────

    [Fact]
    public void BitManipulation()
    {
        // Set bit 5 of x → li t1, 32; or t0, t0, t1
        var prog = MakeProgram("main",
            new BitSet(new Variable("x"), 5),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("li\tt1, 32", asm);
        Assert.Contains("or\tt0, t0, t1", asm);
    }

    // ─── FactorySupport ──────────────────────────────────────────────────────

    [Fact]
    public void FactorySupport()
    {
        var codegen = CodeGenFactory.Create("ch32v003", Ch32v003);
        Assert.IsType<RiscvCodeGen>(codegen);
    }
}
