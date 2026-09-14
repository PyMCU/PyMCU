using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#284. The IR generator types a name at each site from what it knows there, so one
/// name could reach the backend two bytes wide at one site and one byte at another. The
/// register allocator sizes a name once: `for k, v in enumerate(range(lo, lo + 3))` followed
/// by `for v in xs` put `main.v` in R8:R9 for the first loop and handed R9 to a callee, and
/// the counter printed 12288. Every occurrence of such a name is widened to the narrowest
/// type that covers all of them, before the optimizer and whether or not it runs.
/// </summary>
public class VariableWidthUnificationTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Variable> VarsNamed(ProgramIR ir, string name)
    {
        var found = new List<Variable>();
        void Note(Val? v) { if (v is Variable var && var.Name == name) found.Add(var); }
        foreach (var f in ir.Functions)
            foreach (var ins in f.Body)
                switch (ins)
                {
                    case Copy c: Note(c.Src); Note(c.Dst); break;
                    case Binary b: Note(b.Src1); Note(b.Src2); Note(b.Dst); break;
                    case AugAssign a: Note(a.Target); Note(a.Operand); break;
                    case JumpIfGreaterOrEqual j: Note(j.Src1); Note(j.Src2); break;
                    case JumpIfLessOrEqual j: Note(j.Src1); Note(j.Src2); break;
                    case Call cl: foreach (var a in cl.Args) Note(a); break;
                }
        return found;
    }

    private const string TwoWidths =
        "from pymcu.types import uint8, uint16\n\n" +
        "def main(lo: uint8):\n" +
        "    c: uint16 = 0\n" +
        "    for k, v in enumerate(range(lo, lo + 300)):\n" +
        "        c = c + v\n" +
        "    xs: uint8[3] = [1, 2, 3]\n" +
        "    for v in xs:\n" +
        "        c = c + v\n\n" +
        "main(5)\n";

    [Fact]
    public void ANameUsedAtTwoWidths_IsOneWidthAfterThePass()
    {
        var ir = Gen(TwoWidths);
        var before = VarsNamed(ir, "main.v").Select(v => v.Type).Distinct().ToList();
        Assert.True(before.Count > 1, "the generator types the counter uint16 and the array element uint8");

        Optimizer.UnifyVariableWidths(ir);

        var after = VarsNamed(ir, "main.v");
        Assert.NotEmpty(after);
        Assert.All(after, v => Assert.Equal(DataType.UINT16, v.Type));
    }

    [Fact]
    public void ANameUsedAtOneWidth_IsLeftAlone()
    {
        var ir = Gen("from pymcu.types import uint16\n\ndef main():\n    c: uint16 = 0\n    for i in range(300):\n        c = c + i\n\nmain()\n");
        Optimizer.UnifyVariableWidths(ir);
        Assert.All(VarsNamed(ir, "main.i"), v => Assert.Equal(DataType.UINT16, v.Type));
        Assert.All(VarsNamed(ir, "main.c"), v => Assert.Equal(DataType.UINT16, v.Type));
    }

    [Fact]
    public void SignedAndUnsignedWidths_MeetInASignedTypeThatHoldsBoth()
    {
        var ir = Gen(
            "from pymcu.types import uint8, uint16\n\n" +
            "def main(n: uint8):\n" +
            "    c: uint16 = 0\n" +
            "    for v in range(-3, 300):\n" +       // int16 counter
            "        c = c + v\n" +
            "    xs: uint8[3] = [1, 2, 3]\n" +
            "    for v in xs:\n" +                    // uint8 element
            "        c = c + v\n\n" +
            "main(5)\n");
        Optimizer.UnifyVariableWidths(ir);
        Assert.All(VarsNamed(ir, "main.v"), v => Assert.Equal(DataType.INT16, v.Type));
    }
}
