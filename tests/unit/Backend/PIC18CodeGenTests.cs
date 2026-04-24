using PyMCU.Backend;
using PyMCU.Backend.Targets.PIC18;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

public class PIC18CodeGenTests
{
    private static readonly DeviceConfig Pic18f45k50 = new() { Chip = "pic18f45k50", Arch = "pic18" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new PIC18CodeGen(config ?? Pic18f45k50);
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
        var prog = MakeProgram("main", new Return(new Constant(42)));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x2A", asm);
        Assert.Contains("RETURN", asm);
        Assert.Contains("p18f45k50.inc", asm);
    }

    // ─── MOVFF_Optimization ───────────────────────────────────────────────────

    [Fact]
    public void MOVFF_Optimization()
    {
        // x = y → MOVFF y, x
        var prog = MakeProgram("main",
            new Copy(new Variable("y"), new Variable("x")),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains("MOVFF\ty, x", asm);
    }

    // ─── Redundant_MOVFF_Removed ──────────────────────────────────────────────

    [Fact]
    public void Redundant_MOVFF_Removed()
    {
        // x = x — redundant copy, peephole must remove MOVFF x, x
        var prog = MakeProgram("main",
            new Copy(new Variable("x"), new Variable("x")),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.DoesNotContain("MOVFF\tx, x", asm);
    }

    // ─── Factory (throw-only) ─────────────────────────────────────────────────
    // CodeGenFactory is now throw-only: all backends are external plugins.

    [Fact]
    public void Factory_ThrowsForExternalBackend()
    {
        var ex = Assert.Throws<NotSupportedException>(() => CodeGenFactory.Create("pic18f45k50", Pic18f45k50));
        Assert.Contains("pymcuc-pic", ex.Message);
    }

    // ─── Arithmetic ──────────────────────────────────────────────────────────

    [Fact]
    public void Arithmetic()
    {
        // a = 10 + b → MOVLW 0x0A; ADDWF b, W; MOVWF a
        var codegen = Compile(MakeProgram("main",
            new Binary(BinaryOp.Add, new Constant(10), new Variable("b"), new Variable("a")),
            new Return(new NoneVal())));

        Assert.Contains("MOVLW\t0x0A", codegen);
        Assert.Contains("ADDWF\tb, W", codegen);
        Assert.Contains("MOVWF\ta", codegen);
    }

    // ─── BankedAccess ─────────────────────────────────────────────────────────

    [Fact]
    public void BankedAccess()
    {
        // 200 variables push allocation beyond access bank (0x60) → MOVLB 1 + BANKED suffix
        var body = Enumerable.Range(0, 200)
            .Select(i => (Instruction)new Copy(new Constant(0), new Variable($"v{i}")))
            .ToArray();
        var prog = MakeProgram("main", body);
        var asm = Compile(prog);

        Assert.Contains("MOVLB\t1", asm);
        Assert.Contains("MOVWF\tv199, BANKED", asm);
    }

    // ─── SubtractionCorrectness ───────────────────────────────────────────────

    [Fact]
    public void SubtractionCorrectness()
    {
        // a = b - c → MOVF c, W; SUBWF b, W; MOVWF a  (in that order)
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Sub, new Variable("b"), new Variable("c"), new Variable("a")));
        var asm = Compile(prog);

        var posC   = asm.IndexOf("MOVF\tc, W",  StringComparison.Ordinal);
        var posSub = asm.IndexOf("SUBWF\tb, W", StringComparison.Ordinal);
        var posA   = asm.IndexOf("MOVWF\ta",    StringComparison.Ordinal);

        Assert.NotEqual(-1, posC);
        Assert.NotEqual(-1, posSub);
        Assert.NotEqual(-1, posA);
        Assert.True(posC < posSub);
        Assert.True(posSub < posA);
    }

    // ─── Division ─────────────────────────────────────────────────────────────

    [Fact]
    public void Division()
    {
        // a = b / c — PIC18 uses software division loop
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Div, new Variable("b"), new Variable("c"), new Variable("a")));
        var asm = Compile(prog);

        Assert.Contains("div_loop", asm);
        Assert.Contains("SUBWF", asm);
        Assert.Contains("BN", asm);
    }

    // ─── AugAssign ────────────────────────────────────────────────────────────

    [Fact]
    public void AugAssign_Add()
    {
        var prog = MakeProgram("main",
            new AugAssign(BinaryOp.Add, new Variable("x"), new Constant(5)));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x05", asm);
        Assert.Contains("ADDWF\tx", asm);
    }

    [Fact]
    public void AugAssign_Sub()
    {
        var prog = MakeProgram("main",
            new AugAssign(BinaryOp.Sub, new Variable("x"), new Constant(3)));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x03", asm);
        Assert.Contains("SUBWF\tx", asm);
    }

    [Fact]
    public void AugAssign_Bitwise()
    {
        var prog = MakeProgram("main",
            new AugAssign(BinaryOp.BitAnd, new Variable("x"), new Constant(0x0F)));
        var asm = Compile(prog);

        Assert.Contains("ANDWF\tx", asm);
    }

    [Fact]
    public void AugAssign_LShift_Constant()
    {
        var prog = MakeProgram("main",
            new AugAssign(BinaryOp.LShift, new Variable("x"), new Constant(2)));
        var asm = Compile(prog);

        Assert.Contains("RLCF\tx", asm);
        Assert.Contains("BCF\tSTATUS, C, ACCESS", asm);
    }

    [Fact]
    public void LoadIndirect_StoreIndirect()
    {
        // ptr = 0x25; val = *ptr
        var prog = MakeProgram("main",
            new LoadIndirect(new Constant(0x25), new Variable("val")));
        var asm = Compile(prog);

        Assert.Contains("0xFE9", asm);  // FSR0L
        Assert.Contains("0xFEF", asm);  // INDF0
        Assert.Contains("MOVWF\tval", asm);
    }
}
