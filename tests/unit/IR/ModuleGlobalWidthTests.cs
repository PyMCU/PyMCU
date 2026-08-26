using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// pymcu-avr#6. A module-level global read through an @inline expansion carried UINT8 in the
/// IR while its write carried the widened type, so `b = 5` then `b = 300` handed the backend
/// a one-byte read of a two-byte variable and 300 arrived as 44.
///
/// The write side records the widened type in mutableGlobals (Assign.cs). The alias chain an
/// inline expansion resolves through took its type from variableTypes ONLY, which never holds
/// the module-global entry, and fell back to UINT8. A function local is unaffected because its
/// qualified name IS in variableTypes, which is why the same program inside `def main()` was
/// always correct.
///
/// The symptom was reported against the AVR backend. The backend was doing what the IR said.
/// </summary>
public class ModuleGlobalWidthTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    // Every Variable in the program that goes by `name`, whatever the instruction shape, so
    // the assertion does not depend on how the expansion happens to be lowered.
    private static List<Variable> VarsNamed(ProgramIR ir, string name)
    {
        var found = new List<Variable>();
        void Walk(object? o)
        {
            switch (o)
            {
                case Variable v when v.Name == name: found.Add(v); return;
                case Instruction ins:
                    foreach (var prop in ins.GetType().GetProperties())
                        if (typeof(Val).IsAssignableFrom(prop.PropertyType)
                            || typeof(IEnumerable<Val>).IsAssignableFrom(prop.PropertyType))
                            Walk(prop.GetValue(ins));
                    return;
                case IEnumerable<Val> vals:
                    foreach (var v in vals) Walk(v);
                    return;
            }
        }
        foreach (var f in ir.Functions)
            foreach (var ins in f.Body) Walk(ins);
        return found;
    }

    private const string WidenedGlobalThroughInline =
        "@inline\n" +
        "def show(v: uint16):\n" +
        "    z: uint16 = v + 1\n" +
        "\n" +
        "b = 5\n" +
        "b = 300\n" +
        "show(b)\n";

    [Fact]
    public void AWidenedModuleGlobal_IsReadAtItsWidenedWidth()
    {
        var reads = VarsNamed(Gen(WidenedGlobalThroughInline), "b");

        Assert.NotEmpty(reads);
        Assert.All(reads, v => Assert.True(v.Type.SizeOf() >= 2,
            $"every reference to the module global 'b' must be at least 2 bytes wide, saw {v.Type}"));
    }

    // The control that was always correct, kept so a future change cannot fix the global by
    // breaking the local.
    [Fact]
    public void AWidenedFunctionLocal_IsStillReadAtItsWidenedWidth()
    {
        var ir = Gen(
            "@inline\n" +
            "def show(v: uint16):\n" +
            "    z: uint16 = v + 1\n" +
            "\n" +
            "def main():\n" +
            "    b: uint16 = 5\n" +
            "    b = 300\n" +
            "    show(b)\n");

        var reads = VarsNamed(ir, "main.b");
        Assert.NotEmpty(reads);
        Assert.All(reads, v => Assert.True(v.Type.SizeOf() >= 2, $"saw {v.Type}"));
    }

    // A genuinely 8-bit global must NOT be widened by the fallback: the fallback reads the
    // recorded width, it does not invent one.
    [Fact]
    public void AnEightBitModuleGlobal_StaysEightBit()
    {
        var reads = VarsNamed(Gen(
            "@inline\n" +
            "def show(v: uint8):\n" +
            "    z: uint8 = v + 1\n" +
            "\n" +
            "a = 5\n" +
            "a = 200\n" +
            "show(a)\n"), "a");

        Assert.NotEmpty(reads);
        Assert.All(reads, v => Assert.Equal(1, v.Type.SizeOf()));
    }
}
