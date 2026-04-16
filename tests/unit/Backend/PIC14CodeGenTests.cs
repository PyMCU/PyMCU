using PyMCU.Backend.Targets.PIC14;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;
using IrUnaryOp = PyMCU.IR.UnaryOp;

namespace PyMCU.UnitTests;

public class PIC14CodeGenTests
{
    private static readonly DeviceConfig Pic16f84a = new() { Chip = "pic16f84a", Arch = "pic14" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new PIC14CodeGen(config ?? Pic16f84a);
        var sw = new StringWriter();
        codegen.Compile(program, sw);
        return sw.ToString();
    }

    private static ProgramIR GenerateIR(string source)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var ast = parser.ParseProgram();
        var irGen = new IRGenerator();
        return irGen.Generate(ast, new Dictionary<string, ProgramNode>(), Pic16f84a);
    }

    private static ProgramIR MakeProgram(string name, params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = name, Body = body.ToList() });
        return prog;
    }

    // ─── SimpleReturn ──────────────────────────────────────────────────────

    [Fact]
    public void SimpleReturn()
    {
        var prog = MakeProgram("main", new Return(new Constant(42)));
        var asm = Compile(prog);

        Assert.Contains("MOVLW\t0x2A", asm);  // 0x2A = 42
        Assert.Contains("RETURN", asm);
        Assert.Contains("main", asm);
    }

    // ─── MultipleFunctions ────────────────────────────────────────────────

    [Fact]
    public void MultipleFunctions()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "f1", Body = [new Return(new Constant(1))] });
        prog.Functions.Add(new Function { Name = "f2", Body = [new Return(new Constant(2))] });

        var asm = Compile(prog);

        Assert.Contains("f1", asm);
        Assert.Contains("f2", asm);
    }

    // ─── ControlFlow ──────────────────────────────────────────────────────

    [Fact]
    public void ControlFlow()
    {
        var prog = MakeProgram("f",
            new Label("L1"),
            new BitSet(new MemoryAddress(0x05), 0),
            new Jump("L1"));

        var asm = Compile(prog);

        Assert.Contains("GOTO", asm);
        Assert.Contains("L1", asm);
    }

    // ─── UnaryOps ─────────────────────────────────────────────────────────

    [Fact]
    public void UnaryOps()
    {
        var prog = MakeProgram("f",
            new Unary(IrUnaryOp.Neg, new Constant(5), new Variable("x")));

        var asm = Compile(prog);

        Assert.Contains("SUBLW\t0", asm);
    }

    // ─── BinaryOps ────────────────────────────────────────────────────────

    [Fact]
    public void BinaryOps()
    {
        var prog = MakeProgram("f",
            new Binary(IrBinaryOp.Add, new Constant(1), new Constant(2), new Variable("x")));

        var asm = Compile(prog);

        Assert.Contains("ADDLW\t0x02", asm);
    }

    // ─── ComparisonOps ────────────────────────────────────────────────────

    [Fact]
    public void ComparisonOps()
    {
        // x = (1 == 1) — result dst gets 1 if Z flag is set after XORWF.
        // PIC14 pattern: CLRF x; BTFSC STATUS, 2; INCF x, F
        var prog = MakeProgram("f",
            new Binary(IrBinaryOp.Equal, new Constant(1), new Constant(1), new Variable("x")));

        var asm = Compile(prog);

        Assert.Contains("BTFSC\tSTATUS, 2", asm);
    }

    // ─── BitManipulation ──────────────────────────────────────────────────

    [Fact]
    public void BitManipulation()
    {
        var prog = MakeProgram("f",
            new BitSet(new MemoryAddress(0x05), 0),
            new BitClear(new MemoryAddress(0x05), 1));

        var asm = Compile(prog);

        Assert.Contains("BSF\t0x05, 0", asm);
        Assert.Contains("BCF\t0x05, 1", asm);
    }

    // ─── Arguments ────────────────────────────────────────────────────────

    [Fact]
    public void CallWithArguments()
    {
        var ir = GenerateIR(
            "def add(a: int, b: int) -> int:\n    return a + b\n\n" +
            "def main():\n    x: int = add(1, 2)");

        Assert.Equal(2, ir.Functions.Count);
        var addFunc = ir.Functions.First(f => f.Name == "add");
        var mainFunc = ir.Functions.First(f => f.Name == "main");

        Assert.Equal(2, addFunc.Params.Count);
        Assert.Contains("add.a", addFunc.Params[0]);
        Assert.Contains("add.b", addFunc.Params[1]);

        var mainBody = mainFunc.Body;
        Assert.Contains(mainBody, i => i is Copy { Dst: Variable v } && v.Name.Contains("add.a"));
        Assert.Contains(mainBody, i => i is Copy { Dst: Variable v } && v.Name.Contains("add.b"));
        Assert.Contains(mainBody, i => i is Call { FunctionName: "add" });
    }

    [Fact]
    public void StackLayoutWithArguments()
    {
        var ir = GenerateIR(
            "def add(a: int, b: int) -> int:\n    return a + b\n\n" +
            "def main():\n    x: int = add(1, 2)");

        var asm = Compile(ir, new DeviceConfig { Chip = "pic16f84a", Arch = "pic14", Frequency = 4_000_000 });

        Assert.Contains("add.a EQU _stack_base +", asm);
        Assert.Contains("add.b EQU _stack_base +", asm);
        Assert.Contains("main.x EQU _stack_base +", asm);

        Assert.Contains("MOVLW\t0x01", asm);
        Assert.Contains("MOVWF\tadd.a", asm);
        Assert.Contains("MOVLW\t0x02", asm);
        Assert.Contains("MOVWF\tadd.b", asm);
        Assert.Contains("CALL\tadd", asm);
    }
}
