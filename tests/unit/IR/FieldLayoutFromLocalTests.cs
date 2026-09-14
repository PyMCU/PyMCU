using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#294. A field is laid out from its first store in __init__: a parameter's annotation
/// gave the width, an annotated local did not, and the field came out a byte that every later
/// store truncated into (a uint16 duty read back 0).
/// </summary>
public class FieldLayoutFromLocalTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Variable> Field(ProgramIR ir, string suffix)
    {
        var found = new List<Variable>();
        void Note(Val? v) { if (v is Variable var && var.Name.EndsWith(suffix)) found.Add(var); }
        foreach (var f in ir.Functions)
            foreach (var ins in f.Body)
                switch (ins)
                {
                    case Copy c: Note(c.Src); Note(c.Dst); break;
                    case Binary b: Note(b.Src1); Note(b.Src2); Note(b.Dst); break;
                }
        return found;
    }

    private const string Box =
        "from pymcu.types import uint16\n\n" +
        "class Box:\n" +
        "    def __init__(self, v: uint16):\n" +
        "        w: uint16 = v\n" +
        "        self.x = w\n\n" +
        "    def put(self, v: uint16):\n" +
        "        self.x = v\n\n" +
        "    def get(self) -> uint16:\n" +
        "        return self.x\n\n" +
        "def main(n: uint16):\n" +
        "    b = Box(1000)\n" +
        "    b.put(n)\n" +
        "    y: uint16 = b.get()\n\n" +
        "main(300)\n";

    [Fact]
    public void AFieldFirstStoredFromAnAnnotatedLocal_HasTheLocalsWidth()
    {
        // The pipeline unifies the width of a name across its sites before the backend sees
        // it; with the layout right that union is uint16, with the old byte layout it stayed
        // a byte and the stores from put() and the read in get() were truncated.
        var ir = Gen(Box);
        Optimizer.UnifyVariableWidths(ir);
        var vars = Field(ir, "_x");
        Assert.NotEmpty(vars);
        Assert.All(vars, v => Assert.Equal(DataType.UINT16, v.Type));
    }
}
