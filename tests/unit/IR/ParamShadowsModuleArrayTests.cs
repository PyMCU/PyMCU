using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A subscripted name inside a function resolved at module scope even when the function
/// itself bound the name: `def f(s: const[str])` next to a module-level `s` array read
/// `main.s` for `s[i]`, so the callee streamed the caller's global and ignored the
/// argument it was handed (oracle probe 070: "ABC" repeated by every print, with "END"
/// and the newline never arriving). Python resolves the name to the local; the indexed
/// STORE path's ResolveArrayVar has ruled the module spelling out under LocalScopeBinds
/// since #458/#460 -- the indexed LOAD path gained the same module fallback without it.
/// </summary>
public class ParamShadowsModuleArrayTests
{
    private const string Prelude =
        "from pymcu.types import uint8\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AConstStrParamNamedLikeAModuleArray_ReadsTheArgument()
    {
        var ir = Gen(
            "s = bytearray(4)\n" +
            "def f(s: const[str]):\n" +
            "    i: uint8 = 0\n" +
            "    while i < 3:\n" +
            "        uart_write(s[i])\n" +
            "        i = i + 1\n" +
            "f(\"abc\")\n");

        var f = ir.Functions.Single(fn => fn.Name == "f");
        Assert.Contains(f.Body,
            i => i is FlashLoadPtr fl && fl.Ptr is Variable { Name: "f.s" });
        Assert.DoesNotContain(f.Body,
            i => i is ArrayLoad al && (al.ArrayName == "main.s" || al.ArrayName == "s"));
    }

    [Fact]
    public void ABytearrayParamNamedLikeAModuleArray_ReadsTheArgument()
    {
        var ir = Gen(
            "s = bytearray(4)\n" +
            "def f(s: bytearray):\n" +
            "    i: uint8 = 0\n" +
            "    while i < 3:\n" +
            "        uart_write(s[i])\n" +
            "        i = i + 1\n" +
            "f(s)\n");

        var f = ir.Functions.Single(fn => fn.Name == "f");
        Assert.Contains(f.Body,
            i => i is BytearrayLoad bl && bl.PtrName == "f.s");
        Assert.DoesNotContain(f.Body,
            i => i is ArrayLoad al && (al.ArrayName == "main.s" || al.ArrayName == "s"));
    }

    [Fact]
    public void AFunctionWithNoLocalBinding_StillReadsTheModuleArray()
    {
        // The module fallback is how a plain function reaches `cfg` in the first place;
        // only a local binding of the same name rules it out.
        var ir = Gen(
            "s = bytearray(4)\n" +
            "def f():\n" +
            "    i: uint8 = 0\n" +
            "    while i < 3:\n" +
            "        uart_write(s[i])\n" +
            "        i = i + 1\n" +
            "f()\n");

        var f = ir.Functions.Single(fn => fn.Name == "f");
        Assert.Contains(f.Body,
            i => i is ArrayLoad al && (al.ArrayName == "main.s" || al.ArrayName == "s"));
    }
}
