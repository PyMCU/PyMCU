using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A list declared inside an @inline callee (`v: list[uint8] = ...`) was also filed under
/// the CALLER's prefix by the array scan that runs at the expansion, so a caller-side name
/// spelled the same (`v = g(...)` at module level) resolved to that phantom `main.v`:
/// `print(v)` and `len(v)` read an empty list while `v[i]` read the real one. And a
/// function's local list that merely shares a module global's name was filed as the global.
/// </summary>
public class CalleeListNameCollisionTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AnInlineCalleesListLocal_DoesNotShadowTheCallersName()
    {
        var ir = Gen(
            "from pymcu.types import uint8, ptr, inline\n" +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "@inline\n" +
            "def g(k: uint8):\n" +
            "    v: list[uint8] = [k, k + 1]\n" +
            "    return v\n" +
            "v = g(GPIOR0.value)\n" +
            "n: uint8 = len(v)\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.DoesNotContain(body, i => i is LoadIndirect { SrcPtr: Variable { Name: "main.v" } });
        Assert.Contains(body, i => i is LoadIndirect { SrcPtr: Variable { Name: "v" } });
    }

    [Fact]
    public void AFunctionsListLocal_IsNotTheModuleGlobalOfTheSameName()
    {
        var ir = Gen(
            "from pymcu.types import uint8, ptr\n" +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "v: uint8 = GPIOR0.value\n" +
            "def g(k: uint8) -> uint8:\n" +
            "    v: list[uint8] = [k, k + 1]\n" +
            "    v.append(k)\n" +
            "    return len(v)\n" +
            "n: uint8 = g(GPIOR0.value)\n");
        var g = ir.Functions.Single(f => f.Name == "g");
        Assert.DoesNotContain(g.Body, i => i is Copy { Dst: Variable { Name: "v", Type: DataType.GC_REF } });
        Assert.Contains(g.Body, i => i is Copy { Dst: Variable { Name: "g.v" } });
    }
}
