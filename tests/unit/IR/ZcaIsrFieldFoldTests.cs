using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The routine `_set_irq_zca_arg(handler, self)` + `compile_isr` synthesizes inlines the
/// handler with the instance bound to its first parameter, while main is being lowered. The
/// fields of that instance are compile-time constants in main's lowering at that point, and
/// the handler read them as such: machine.I2CTarget's `if t._phase == 0` folded to the 0 its
/// constructor had just stored, so the data branch was deleted and every byte a controller
/// sent overwrote the memory address instead of reaching `mem`; `t._mem[t.memaddr]` read
/// `mem[0]` for every request. Silent, and it depended on the handler's parameter having a
/// different name from the instance: the scan that marks a module-level instance's written
/// fields matches `t.x` by the instance's name, so `def isr(t)` against `t = Tgt()` hid it.
/// </summary>
public class ZcaIsrFieldFoldTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Program(string handlerBody, string mainTail = "") =>
        "from pymcu.types import uint8, inline, compile_isr, _set_irq_zca_arg\n" +
        "\n" +
        "def _isr(h: \"Tgt\"):\n" +
        handlerBody +
        "\n" +
        "class Tgt:\n" +
        "    @inline\n" +
        "    def __init__(self, width: uint8):\n" +
        "        self.width: uint8 = width\n" +
        "        self.phase: uint8 = 0\n" +
        "        self.idx: uint8 = 0\n" +
        "        self.limit: uint8 = 0\n" +
        "        self.seen: uint8 = 0\n" +
        "        _set_irq_zca_arg(_isr, self)\n" +
        "        compile_isr(_isr, 0x0002)\n" +
        "\n" +
        "tgt = Tgt(8)\n" +
        mainTail;

    private static Function Isr(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name.StartsWith("_irq_synth_", StringComparison.Ordinal));

    [Fact]
    public void AFieldTheHandlerWrites_IsNotFoldedInsideTheHandler()
    {
        var ir = Gen(Program(
            "    if h.phase == 0:\n" +
            "        h.phase = 1\n" +
            "    else:\n" +
            "        h.idx = 5\n"));
        var isr = Isr(ir);

        // The test must stay a run-time branch on the field, and the else arm must survive.
        Assert.Contains(isr.Body, i => i is JumpIfNotEqual jne
            && jne.Src1 is Variable { Name: "tgt_phase" });
        Assert.Contains(isr.Body, i => i is Copy { Src: Constant { Value: 5 }, Dst: Variable { Name: "tgt_idx" } });
    }

    [Fact]
    public void AFieldMainWritesAfterConstruction_IsReadFromMemoryByTheHandler()
    {
        var ir = Gen(Program(
            "    h.seen = h.limit\n",
            "tgt.limit = 9\n"));
        var isr = Isr(ir);

        Assert.Contains(isr.Body, i => i is Copy { Src: Variable { Name: "tgt_limit" } });
        Assert.DoesNotContain(isr.Body, i => i is Copy { Src: Constant { Value: 0 }, Dst: Variable { Name: "tgt_seen" } });
    }

    [Fact]
    public void AFieldTheHandlerWrites_IsNotFoldedByMainAfterRegistration()
    {
        // The handler stores a constant; main's read must not take it as the field's value
        // (the interrupt may not have fired), nor the constructor's 0 (it may have).
        var ir = Gen(Program(
            "    h.phase = 3\n",
            "x: uint8 = tgt.phase\n"));
        var main = ir.Functions.Single(f => f.Name == "main");

        Assert.Contains(main.Body, i => i is Copy { Src: Variable { Name: "tgt_phase" } });
    }

    [Fact]
    public void AFieldNothingWritesAfterConstruction_StillFoldsInsideTheHandler()
    {
        // The zero-cost contract the synthesis exists for: a constructor-only field is the
        // constant the handler is expanded with (a Pin's port and bit build its masks).
        var ir = Gen(Program(
            "    h.idx = h.width + 1\n"));
        var isr = Isr(ir);

        Assert.DoesNotContain(isr.Body, i => i is Binary { Src1: Variable { Name: "tgt_width" } });
        Assert.Contains(isr.Body, i => i is Copy { Src: Constant { Value: 9 }, Dst: Variable { Name: "tgt_idx" } });
    }
}
