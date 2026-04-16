using PyMCU.Backend;
using PyMCU.Backend.Targets.PIO;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;
using IrUnaryOp = PyMCU.IR.UnaryOp;

namespace PyMCU.UnitTests;

public class PIOCodeGenTests
{
    private static readonly DeviceConfig PioCfg = new() { Chip = "rp2040", Arch = "pio" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new PIOCodeGen(config ?? PioCfg);
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

    // ─── SimpleMove ───────────────────────────────────────────────────────────

    [Fact]
    public void SimpleMove()
    {
        // x = 5 → SET X, 5
        var prog = MakeProgram("main", new Copy(new Constant(5), new Variable("x")));
        var asm = Compile(prog);
        Assert.Contains("SET X, 5", asm);
    }

    // ─── JumpIfZero ───────────────────────────────────────────────────────────

    [Fact]
    public void JumpIfZero()
    {
        // if x == 0 goto label → JMP !X, mylabel
        var prog = MakeProgram("main", new JumpIfZero(new Variable("x"), "mylabel"));
        var asm = Compile(prog);
        Assert.Contains("JMP !X, mylabel", asm);
    }

    // ─── BitNot ───────────────────────────────────────────────────────────────

    [Fact]
    public void BitNot()
    {
        // y = ~x → MOV Y, !X
        var prog = MakeProgram("main",
            new Unary(IrUnaryOp.BitNot, new Variable("x"), new Variable("y")));
        var asm = Compile(prog);
        Assert.Contains("MOV Y, !X", asm);
    }

    // ─── DecrementOnly ───────────────────────────────────────────────────────

    [Fact]
    public void DecrementOnly()
    {
        // x = x - 1 → JMP X--
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Sub, new Variable("x"), new Constant(1), new Variable("x")));
        var asm = Compile(prog);
        Assert.Contains("JMP X--", asm);
    }

    // ─── Intrinsics ───────────────────────────────────────────────────────────

    [Fact]
    public void Intrinsics()
    {
        var prog = MakeProgram("main",
            new Call("__pio_pull", [], new NoneVal()),
            new Call("__pio_wait", [new Constant(1), new MemoryAddress(1), new Constant(0)], new NoneVal()),
            new Call("delay", [new Constant(5)], new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("PULL BLOCK", asm);
        Assert.Contains("WAIT 1, PIN, 0 [5]", asm);
    }

    // ─── UnsupportedOpsThrow ─────────────────────────────────────────────────

    [Fact]
    public void UnsupportedOpsThrow()
    {
        // PIO has no general Add instruction — must throw
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Add, new Variable("x"), new Constant(1), new Variable("x")));
        Assert.Throws<NotSupportedException>(() => Compile(prog));
    }

    // ─── FullMapping ─────────────────────────────────────────────────────────

    [Fact]
    public void FullMapping()
    {
        const string src = @"
def ws2812():
    import pio
    x = 0
    while True:
        pull()
        out(pio.PINS, 1)
        delay(2)
        wait(1, pio.PIN, 0)
        pull(pio.NOBLOCK)
";
        const string pioPy = @"
PINS = PIORegister(0)
PIN = PIORegister(1)
NOBLOCK = 0
";
        var ast = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        var pioAst = new Parser(new Lexer(pioPy).Tokenize()).ParseProgram();

        var modules = new Dictionary<string, ProgramNode> { ["pio"] = pioAst };
        var ir = new IRGenerator().Generate(ast, modules, new DeviceConfig());

        var sw = new StringWriter();
        new PIOCodeGen(new DeviceConfig { Chip = "ws2812", Arch = "pio" }).Compile(ir, sw);
        var asm = sw.ToString();

        Assert.Contains("PULL BLOCK",    asm);
        Assert.Contains("OUT PINS, 1",   asm);
        Assert.Contains("WAIT 1, PIN, 0", asm);
        Assert.Contains("PULL NOBLOCK",  asm);
        Assert.Contains("[2]",           asm);
    }
}
