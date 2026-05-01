using PyMCU.Backend.Targets.Xtensa;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;

namespace PyMCU.UnitTests;

public class XtensaCodeGenTests
{
    private static readonly DeviceConfig Esp32 = new() { Chip = "esp32", Arch = "xtensa" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new XtensaCodeGen(config ?? Esp32);
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
        var prog = MakeProgram("my_func", new Return(new Constant(42)));
        var asm = Compile(prog);
        Assert.Contains("movi\ta2, 42", asm);
        Assert.Contains("ret.n", asm);
    }

    // ─── MainInfiniteLoop ────────────────────────────────────────────────────

    [Fact]
    public void MainInfiniteLoop()
    {
        // main must end in an infinite loop, not ret.n
        var prog = MakeProgram("main", new Return(new Constant(0)));
        var asm = Compile(prog);
        Assert.DoesNotContain("ret.n", asm);
        Assert.Contains("j\tend_loop_", asm);
    }

    // ─── StackPointerInit ────────────────────────────────────────────────────

    [Fact]
    public void StackPointerInit()
    {
        var prog = MakeProgram("main", new Return(new Constant(0)));
        var asm = Compile(prog);
        // main must initialise SP to top of DRAM (ESP32 default)
        Assert.Contains("movi\ta1,", asm);
    }

    // ─── AddBinary ───────────────────────────────────────────────────────────

    [Fact]
    public void AddBinary()
    {
        var prog = MakeProgram("f",
            new Binary(IrBinaryOp.Add,
                new Variable("x"), new Variable("y"), new Variable("z")),
            new Return(new Variable("z")));
        var asm = Compile(prog);
        Assert.Contains("add\t", asm);
    }

    // ─── CallPrologue ─────────────────────────────────────────────────────────

    [Fact]
    public void CallPrologue()
    {
        // Non-leaf function must save/restore ra (a0) on the stack
        var prog = MakeProgram("caller",
            new Call("callee", [], new NoneVal()),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("s32i\ta0,", asm);
        Assert.Contains("l32i\ta0,", asm);
    }

    // ─── CallInstr ────────────────────────────────────────────────────────────

    [Fact]
    public void CallInstr()
    {
        var prog = MakeProgram("f",
            new Call("some_func", [], new NoneVal()),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("call0\tsome_func", asm);
    }

    // ─── JumpLabel ────────────────────────────────────────────────────────────

    [Fact]
    public void JumpLabel()
    {
        var prog = MakeProgram("f",
            new Label("loop_top"),
            new Jump("loop_top"));
        var asm = Compile(prog);
        Assert.Contains("loop_top:", asm);
        Assert.Contains("j\tloop_top", asm);
    }

    // ─── ConditionalBranch ───────────────────────────────────────────────────

    [Fact]
    public void ConditionalBranch()
    {
        var prog = MakeProgram("f",
            new JumpIfZero(new Variable("cond"), "done"),
            new Label("done"),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("beqz\t", asm);
        Assert.Contains("done:", asm);
    }

    // ─── BranchFusion ─────────────────────────────────────────────────────────

    [Fact]
    public void BranchFusionBeq()
    {
        // Binary(Eq, a, b, tmp) + JumpIfNotZero(tmp, lbl) should fuse to beq a, b, lbl
        var prog = MakeProgram("f",
            new Binary(IrBinaryOp.Equal,
                new Variable("a"), new Variable("b"), new Variable("_t0")),
            new JumpIfNotZero(new Variable("_t0"), "eq_branch"),
            new Label("eq_branch"),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("beq\t", asm);
        Assert.DoesNotContain("beqz\t", asm);
    }

    // ─── BranchFusionBne ─────────────────────────────────────────────────────

    [Fact]
    public void BranchFusionBne()
    {
        var prog = MakeProgram("f",
            new Binary(IrBinaryOp.NotEqual,
                new Variable("a"), new Variable("b"), new Variable("_t0")),
            new JumpIfNotZero(new Variable("_t0"), "ne_branch"),
            new Label("ne_branch"),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("bne\t", asm);
    }

    // ─── RoDataLoad ──────────────────────────────────────────────────────────

    [Fact]
    public void RoDataLoad()
    {
        var prog = MakeProgram("f",
            new RoData("my_arr", [1, 2, 3]),
            new ArrayLoadRo("my_arr", new Constant(0), new Variable("v")),
            new Return(new Variable("v")));
        var asm = Compile(prog);
        Assert.Contains("my_arr", asm);
        Assert.Contains("l32r\t", asm);
    }

    // ─── FloatBinaryAdd ──────────────────────────────────────────────────────

    [Fact]
    public void FloatBinaryAddUsesLibcall()
    {
        var prog = MakeProgram("f",
            new FloatBinary(IrBinaryOp.Add,
                new Variable("x"), new Variable("y"), new Variable("z")),
            new Return(new Variable("z")));
        var asm = Compile(prog);
        Assert.Contains("__addsf3", asm);
    }

    // ─── WidenNarrow ─────────────────────────────────────────────────────────

    [Fact]
    public void WidenUsesSext()
    {
        var prog = MakeProgram("f",
            new Widen(new Variable("b"), DataType.INT8, DataType.INT32, new Variable("w")),
            new Return(new Variable("w")));
        var asm = Compile(prog);
        // sext or extui pattern
        Assert.True(asm.Contains("sext\t") || asm.Contains("extui\t"),
            $"Expected sext or extui in:\n{asm}");
    }
}
