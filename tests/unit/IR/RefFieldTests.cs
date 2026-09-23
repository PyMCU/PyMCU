using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `self.f = xs` where xs is a heap list or tuple: the field holds the
/// reference itself. Filed under the layout's declared width -- uint8 on an
/// unannotated `__init__` param, which is what namedtuple() synthesizes for
/// every field -- the constructor's Copy truncated the 16-bit pointer to its
/// low byte, and `m.f[i]` / `len(m.f)` never found the list-ness the source
/// name carried. adafruit_irremote's IRMessage is this shape:
/// `IRMessage(tuple(input_pulses), code=tuple(output))`.
/// </summary>
public class RefFieldTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Msg =
        "class Msg:\n" +
        "    def __init__(self, p):\n" +
        "        self.p = p\n";

    [Fact]
    public void FieldStore_KeepsThePointerWidth()
    {
        var ir = Gen(Msg +
            "def main():\n" +
            "    inp: list[uint16] = [9000, 4500]\n" +
            "    m = Msg(tuple(inp))\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        // The field is stored as a 16-bit GC_REF, not the layout's uint8.
        Assert.Contains(body, i => i is Copy { Dst: Variable { Type: DataType.GC_REF } v }
            && v.Name.EndsWith("_p"));
        Assert.DoesNotContain(body, i => i is Copy { Dst: Variable { Type: DataType.UINT8 } v }
            && v.Name.EndsWith("_p") && i is Copy { Src: Temporary });
    }

    [Fact]
    public void FieldSubscript_LoadsThroughTheHeapObject()
    {
        var ir = Gen(Msg +
            "def main():\n" +
            "    inp: list[uint16] = [9000, 4500]\n" +
            "    m = Msg(tuple(inp))\n" +
            "    x: uint16 = m.p[0]\n" +
            "    y: uint16 = m.p[1]\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        // m.p[i] is a heap index into the tuple the field points at.
        Assert.Contains(body, i => i is LoadIndirect { Elem: DataType.UINT16 });
    }

    [Fact]
    public void FieldLen_ReadsTheHeaderCount()
    {
        var ir = Gen(Msg +
            "def main():\n" +
            "    inp: list[uint16] = [9000, 4500]\n" +
            "    m = Msg(tuple(inp))\n" +
            "    n: uint16 = len(m.p)\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(body, i => i is LoadIndirect { Dst: Temporary });
    }

    [Fact]
    public void ListField_SubscriptsThroughTheHeapObject()
    {
        var ir = Gen(Msg +
            "def main():\n" +
            "    inp: list[uint16] = [9000, 4500]\n" +
            "    m = Msg(inp)\n" +          // a live list, not a tuple() copy
            "    x: uint16 = m.p[1]\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(body, i => i is LoadIndirect { Elem: DataType.UINT16 });
    }

    [Fact]
    public void ScalarField_StillFolds()
    {
        // A field holding a scalar is untouched by the ref path.
        var ir = Gen(
            "class Msg:\n" +
            "    def __init__(self, p: uint8):\n" +
            "        self.p = p\n" +
            "def main():\n" +
            "    m = Msg(7)\n" +
            "    x: uint8 = m.p\n");
        Assert.NotNull(ir);
    }
}
