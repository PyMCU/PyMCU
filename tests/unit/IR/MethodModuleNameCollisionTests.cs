using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A method may share its name with a module-level function and CALL that function by its
/// bare name -- Python resolves bare names to module scope, never to sibling methods
/// (those need `self.`). CircuitPython's adafruit_irremote does exactly this:
/// `GenericDecode.decode_bits` wraps the module-level `decode_bits`.
///
/// Before the fix the bare call was resolved through the class prefix left over by the
/// method's inline expansion (`GenericDecode_` + `decode_bits`), which named the method
/// itself -- and the compile died on a false RecursionError:
///
///     function 'decode_bits' is recursive; PyMCU has no call frame for inlined or ZCA
///     methods, so recursion is not supported -- rewrite it as a loop
///
/// The discriminator is the call target: it must be the module function `decode_bits`,
/// not `GenericDecode_decode_bits`.
/// </summary>
public class MethodModuleNameCollisionTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private const string Src =
        "def decode_bits(pulses: list[uint16]) -> uint8:\n" +
        "    return 1\n" +
        "class D:\n" +
        "    def decode_bits(self, pulses: list[uint16]) -> uint8:\n" +
        "        return decode_bits(pulses)\n" +
        "d = D()\n" +
        "xs: list[uint16] = [1, 2]\n" +
        "x = d.decode_bits(xs)\n";

    [Fact]
    public void ABareCallInsideASameNamedMethod_ReachesTheModuleFunction()
    {
        // Before the fix this threw RecursionError naming `decode_bits`. Resolving to the
        // module function is what lets it compile at all; a self-call would be the bug.
        var ir = Gen(Src);
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.DoesNotContain(main.Body, i => i is Call c && c.FunctionName == "D_decode_bits");
    }

    [Fact]
    public void ARealSelfRecursion_StillRefuses()
    {
        // `self.decode_bits` is genuinely recursive -- the refusal must still fire.
        var ex = Assert.Throws<RecursionError>(() => Gen(
            "class D:\n" +
            "    def decode_bits(self, pulses: list[uint16]) -> uint8:\n" +
            "        return self.decode_bits(pulses)\n" +
            "d = D()\n" +
            "xs: list[uint16] = [1, 2]\n" +
            "x = d.decode_bits(xs)\n"));
        Assert.Contains("recursive", ex.Message);
    }

    [Fact]
    public void ASameNamedMethodInAnImportedModule_StillResolves()
    {
        // The vendored shape: `import adafruit_irremote` -- the method lives under the
        // module prefix AND the class prefix. Bare `decode_bits` must reach
        // `mod_decode_bits`, skipping `mod_GenericDecode_decode_bits`.
        var lib = new Parser(new Lexer(
            "def decode_bits(pulses: list[uint16]) -> uint8:\n" +
            "    return 1\n" +
            "class GenericDecode:\n" +
            "    def decode_bits(self, pulses: list[uint16]) -> uint8:\n" +
            "        return decode_bits(pulses)\n").Tokenize()).ParseProgram();
        var main = new Parser(new Lexer(
            "import mod\n" +
            "xs: list[uint16] = [1, 2]\n" +
            "d = mod.GenericDecode()\n" +
            "x = d.decode_bits(xs)\n").Tokenize()).ParseProgram();
        var ir = new IRGenerator().Generate(main,
            new Dictionary<string, ProgramNode> { ["mod"] = lib },
            new DeviceConfig { Arch = "avr" });
        var mainFn = ir.Functions.Single(f => f.Name == "main");
        Assert.DoesNotContain(mainFn.Body,
            i => i is Call c && c.FunctionName == "mod_GenericDecode_decode_bits");
    }
}
