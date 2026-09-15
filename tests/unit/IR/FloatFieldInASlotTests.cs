using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#404. A class with ONE field collapses to a scalar. With two or more it is boxed into a
/// byte slot in SRAM, and the constructor splits each field into its bytes to store it. That
/// split was an arithmetic shift -- `value >> 8`, `>> 16`, `>> 24` -- and a float's bytes are
/// its IEEE-754 representation, not an arithmetic quantity.
///
/// The AVR backend refused it, correctly and unhelpfully: "no float lowering for RShift...
/// an earlier pass rewrote a float operation into one, which is a bug in that pass". This was
/// that pass. The message named the constructor's line and a pass rather than a cause, and it
/// refused an ordinary class -- a float field alongside ANY second field is enough, which is
/// the shape of a driver that keeps a timeout beside a pin count.
///
/// The field is reinterpreted as 32 bits before the split now, which is what the READ side has
/// always done: a multi-byte field comes back through one typed load over the same four bytes.
///
/// Two sites emitted that split, the single instance and the `Class[N]` array, so both are
/// pinned. Values measured on the simulator: pymcu-avr fixture `float-field-slot`.
/// </summary>
public class FloatFieldInASlotTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static IEnumerable<Instruction> Body(ProgramIR ir, string fn)
        => ir.Functions.Single(f => f.Name == fn).Body;

    private const string TwoFieldClass =
        "from pymcu.types import uint16\n\n" +
        "class A:\n" +
        "    def __init__(self, t: float) -> None:\n" +
        "        self._t: float = t\n" +
        "        self._pad: uint16 = 3\n" +
        "    def gt(self) -> uint16:\n" +
        "        if self._t > 0.05:\n" +
        "            return 1\n" +
        "        return 0\n";

    [Fact]
    public void AFloatFieldIsNotShiftedAsAFloat()
    {
        // Red before: three Binary RShift instructions whose left operand was FLOAT, which is
        // what the backend refused.
        var ir = Gen(TwoFieldClass + "a = A(0.1)\n\ndef main() -> None:\n    x: uint16 = a.gt()\n");

        Assert.DoesNotContain(Body(ir, "main"),
            i => i is Binary { Op: PyMCU.IR.BinaryOp.RShift } b && TypeOf(b.Src1) == DataType.FLOAT);
    }

    [Fact]
    public void AFloatFieldIsReinterpretedBeforeItIsSplit()
    {
        var ir = Gen(TwoFieldClass + "a = A(0.1)\n\ndef main() -> None:\n    x: uint16 = a.gt()\n");

        Assert.Contains(Body(ir, "main"),
            i => i is Bitcast bc && TypeOf(bc.Src) == DataType.FLOAT
                                 && TypeOf(bc.Dst) == DataType.UINT32);
    }

    [Fact]
    public void AnIntegerFieldIsStillSplitDirectly()
    {
        // The control: the reinterpretation must reach floats only. A uint16 field is shifted
        // as it always was, with no Bitcast in front of it.
        var ir = Gen("from pymcu.types import uint16\n\n" +
                     "class C:\n" +
                     "    def __init__(self, n: uint16) -> None:\n" +
                     "        self._n: uint16 = n\n" +
                     "        self._m: uint16 = 5\n" +
                     "    def n(self) -> uint16:\n" +
                     "        return self._n\n" +
                     "c = C(300)\n\ndef main() -> None:\n    x: uint16 = c.n()\n");

        Assert.DoesNotContain(Body(ir, "main"), i => i is Bitcast);
    }

    [Fact]
    public void AnInstanceArrayOfAFloatFieldClassStillLowers()
    {
        // GREEN BEFORE AND AFTER, and here as a guard rather than as a regression test.
        //
        // `Class[N]` builds each element in place and carries its own copy of the byte split,
        // so it calls the same helper. No program that reaches that copy WITH A FLOAT could be
        // constructed: a constant element folds before it, and a runtime index changes only the
        // offset arithmetic. So the second site is covered by the shared helper and not by a
        // measurement, and this pins that the form keeps lowering at all.
        var ir = Gen("from pymcu.types import uint16\n\n" +
                     "class A:\n" +
                     "    def __init__(self, t: float) -> None:\n" +
                     "        self._t: float = t\n" +
                     "        self._pad: uint16 = 3\n" +
                     "    def gt(self) -> uint16:\n" +
                     "        if self._t > 0.05:\n" +
                     "            return 1\n" +
                     "        return 0\n\n" +
                     "def main() -> None:\n" +
                     "    xs: A[2] = [A(0.1), A(0.2)]\n" +
                     "    y: uint16 = xs[0].gt()\n");

        Assert.DoesNotContain(Body(ir, "main"),
            i => i is Binary { Op: PyMCU.IR.BinaryOp.RShift } b && TypeOf(b.Src1) == DataType.FLOAT);
    }

    private static DataType TypeOf(Val v) => v switch
    {
        Variable x => x.Type,
        Temporary x => x.Type,
        FloatConstant => DataType.FLOAT,
        _ => DataType.UNKNOWN,
    };
}
