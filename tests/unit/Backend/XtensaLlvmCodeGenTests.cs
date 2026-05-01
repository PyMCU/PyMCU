using PyMCU.Backend.Targets.Xtensa;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;

namespace PyMCU.UnitTests;

public class XtensaLlvmCodeGenTests
{
    private static readonly DeviceConfig Esp32 = new() { Chip = "esp32", Arch = "xtensa" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new XtensaLlvmCodeGen(config ?? Esp32);
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

    // --- Basic emission ---

    [Fact]
    public void TargetTriple_Esp32()
    {
        var prog = MakeProgram("main", new Return(new Constant(0)));
        var ir = Compile(prog);
        Assert.Contains("xtensa-esp32-elf", ir);
    }

    [Fact]
    public void TargetTriple_Esp8266()
    {
        var cfg = new DeviceConfig { Chip = "esp8266", Arch = "xtensa" };
        var prog = MakeProgram("main", new Return(new Constant(0)));
        var ir = Compile(prog, cfg);
        Assert.Contains("xtensa-esp8266-none-elf", ir);
    }

    [Fact]
    public void TargetTriple_Esp32S3()
    {
        var cfg = new DeviceConfig { Chip = "esp32s3", Arch = "xtensa" };
        var prog = MakeProgram("main", new Return(new Constant(0)));
        var ir = Compile(prog, cfg);
        Assert.Contains("xtensa-esp32s3-elf", ir);
    }

    // --- Type narrowing (Phase 3A) ---

    [Fact]
    public void NarrowAlloca_Uint8_EmitsI8Alloca()
    {
        // A uint8 variable should get an i8 alloca, not i32.
        var v = new Variable("x", DataType.UINT8);
        var prog = MakeProgram("f",
            new Copy(new Constant(42), v),
            new Return(v));
        var ir = Compile(prog);
        Assert.Contains("alloca i8", ir);
    }

    [Fact]
    public void NarrowAlloca_Uint16_EmitsI16Alloca()
    {
        var v = new Variable("x", DataType.UINT16);
        var prog = MakeProgram("f",
            new Copy(new Constant(1000), v),
            new Return(v));
        var ir = Compile(prog);
        Assert.Contains("alloca i16", ir);
    }

    [Fact]
    public void NarrowAlloca_Int32_EmitsI32Alloca()
    {
        var v = new Variable("x", DataType.INT32);
        var prog = MakeProgram("f",
            new Copy(new Constant(1), v),
            new Return(v));
        var ir = Compile(prog);
        Assert.Contains("alloca i32", ir);
    }

    [Fact]
    public void NarrowAlloca_Uint8_StoreAndLoad_EmitsTruncAndZext()
    {
        var v = new Variable("x", DataType.UINT8);
        // Set ReturnType = INT32 so EmitLoad is called on the return value.
        var func = new Function
        {
            Name = "f",
            ReturnType = DataType.INT32,
            Body = new List<Instruction>
            {
                new Copy(new Constant(255), v),
                new Return(v)
            }
        };
        var prog = new ProgramIR();
        prog.Functions.Add(func);
        var ir = Compile(prog);
        // Store should trunc i32 to i8; load should zext i8 to i32.
        Assert.Contains("trunc i32", ir);
        Assert.Contains("zext i8", ir);
    }

    // --- Inline asm passthrough (Phase 3B) ---

    [Fact]
    public void InlineAsm_NoOperands_EmitsAsmSideeffect()
    {
        var prog = MakeProgram("f",
            new InlineAsm("rsr a2, CCOUNT"),
            new Return(new Constant(0)));
        var ir = Compile(prog);
        Assert.Contains("asm sideeffect", ir);
        Assert.Contains("rsr a2, CCOUNT", ir);
        Assert.Contains("~{memory}", ir);
    }

    [Fact]
    public void InlineAsm_NoOperands_IsNotComment()
    {
        var prog = MakeProgram("f",
            new InlineAsm("nop"),
            new Return(new Constant(0)));
        var ir = Compile(prog);
        // Must be a real LLVM instruction, not a comment.
        Assert.DoesNotContain("; inline asm", ir);
        Assert.Contains("call void asm sideeffect", ir);
    }

    [Fact]
    public void InlineAsm_WithOperands_EmitsConstraints()
    {
        var v = new Variable("result", DataType.UINT32);
        var prog = MakeProgram("f",
            new InlineAsm("rsr %0, CCOUNT", new List<Val> { v }),
            new Return(v));
        var ir = Compile(prog);
        Assert.Contains("asm sideeffect", ir);
        Assert.Contains("=r,r", ir);
    }

    // --- FloatBinary (Phase 1B / 3C) ---

    [Fact]
    public void FloatBinary_Add_EmitsFaddFloat()
    {
        var src1 = new Variable("a", DataType.FLOAT);
        var src2 = new Variable("b", DataType.FLOAT);
        var dst  = new Variable("c", DataType.FLOAT);
        var prog = MakeProgram("f",
            new FloatBinary(IrBinaryOp.Add, src1, src2, dst),
            new Return(dst));
        var ir = Compile(prog);
        Assert.Contains("fadd float", ir);
    }

    [Fact]
    public void FloatBinary_Mul_EmitsFmulFloat()
    {
        var src1 = new Variable("a", DataType.FLOAT);
        var src2 = new Variable("b", DataType.FLOAT);
        var dst  = new Variable("c", DataType.FLOAT);
        var prog = MakeProgram("f",
            new FloatBinary(IrBinaryOp.Mul, src1, src2, dst),
            new Return(dst));
        var ir = Compile(prog);
        Assert.Contains("fmul float", ir);
    }

    // --- Widen / Narrow (Phase 1D) ---

    [Fact]
    public void Widen_Unsigned_EmitsZext()
    {
        var src = new Variable("b", DataType.UINT8);
        var dst = new Variable("w", DataType.UINT32);
        var prog = MakeProgram("f",
            new Widen(src, DataType.UINT8, DataType.UINT32, dst),
            new Return(dst));
        var ir = Compile(prog);
        Assert.Contains("zext", ir);
    }

    [Fact]
    public void Widen_Signed_EmitsSext()
    {
        var src = new Variable("b", DataType.INT8);
        var dst = new Variable("w", DataType.INT32);
        var prog = MakeProgram("f",
            new Widen(src, DataType.INT8, DataType.INT32, dst),
            new Return(dst));
        var ir = Compile(prog);
        Assert.Contains("sext", ir);
    }

    [Fact]
    public void Narrow_EmitsTrunc()
    {
        var src = new Variable("w", DataType.INT32);
        var dst = new Variable("b", DataType.INT8);
        var prog = MakeProgram("f",
            new Narrow(src, DataType.INT32, DataType.INT8, dst),
            new Return(dst));
        var ir = Compile(prog);
        Assert.Contains("trunc", ir);
    }

    // --- RoData (Phase 1A) ---

    [Fact]
    public void RoData_EmitsConstantArray()
    {
        var prog = MakeProgram("f",
            new RoData("mystr", new List<int> { 72, 101, 108, 0 }),
            new ArrayLoadRo("mystr", new Constant(0), new Variable("c")),
            new Return(new Variable("c")));
        var ir = Compile(prog);
        Assert.Contains("@mystr", ir);
        Assert.Contains("constant", ir);
    }

    // --- Global variables ---

    [Fact]
    public void GlobalVar_EmittedAtModuleLevel()
    {
        var prog = MakeProgram("main",
            new Copy(new Constant(7), new Variable("gv", DataType.UINT32)),
            new Return(new Constant(0)));
        prog.Globals.Add(new Variable("gv", DataType.UINT32));
        var ir = Compile(prog);
        Assert.Contains("@gv = global i32", ir);
    }
}
