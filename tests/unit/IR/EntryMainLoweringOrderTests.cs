using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Which function of the ENTRY file is lowered first (issue #159). The entry file has no
/// `__module_init` of its own: its module level is injected into main's body, so lowering main
/// is what binds an object built there. A function defined above main was lowered before that
/// binding existed and read the instance's fields as run-time values, which the build refused
/// naming a bit index the program does not write.
///
/// The hoist is scoped to files that build an instance at module level, because lowering order
/// advances the shared label, temporary and string-literal counters: a hoist nobody needs
/// renumbers a program for nothing.
/// </summary>
public class EntryMainLoweringOrderTests
{
    private static ProgramIR GenerateIR(string source)
    {
        var parser = new Parser(new Lexer(source).Tokenize());
        return new IRGenerator().Generate(parser.ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    /// <summary>The lowest temporary number a function's body uses, or int.MaxValue.</summary>
    private static int FirstTempOf(ProgramIR ir, string function)
    {
        var numbers = ir.Functions.Where(f => f.Name == function)
            .SelectMany(f => f.Body)
            .SelectMany(i => i switch
            {
                Binary b => new[] { b.Src1, b.Src2, b.Dst },
                Copy c => new[] { c.Src, c.Dst },
                Unary u => new[] { u.Src, u.Dst },
                Call c => c.Args.Append(c.Dst).ToArray(),
                _ => Array.Empty<Val>(),
            })
            .OfType<Temporary>()
            .Select(t => int.TryParse(t.Name.AsSpan("tmp_".Length), out int n) ? n : int.MaxValue)
            .ToList();
        return numbers.Count == 0 ? int.MaxValue : numbers.Min();
    }

    private const string Box =
        "class Box:\n" +
        "    def __init__(self, n: uint8):\n" +
        "        self.n = n\n" +
        "    def bump(self) -> uint8:\n" +
        "        return self.n + 1\n";

    [Fact]
    public void EntryFileBuildingAnInstance_LowersMainFirst()
    {
        var ir = GenerateIR(Box +
            "obj = Box(5)\n" +
            "def helper(x: uint8) -> uint8:\n" +
            "    return x * 3 + 1\n" +
            "def main(seed: uint8) -> uint8:\n" +
            "    return helper(seed) + seed * 2\n");

        Assert.True(FirstTempOf(ir, "main") < FirstTempOf(ir, "helper"),
            "main is lowered before the functions that can read the instance it builds");
    }

    [Fact]
    public void EntryFileWithoutAnInstance_KeepsSourceOrder()
    {
        // Nothing to bind, so nothing moves: the program lowers exactly as it always did, and
        // its labels, temporaries and interned string ids keep the numbers they had.
        var ir = GenerateIR(
            "def helper(x: uint8) -> uint8:\n" +
            "    return x * 3 + 1\n" +
            "def main(seed: uint8) -> uint8:\n" +
            "    return helper(seed) + seed * 2\n");

        Assert.True(FirstTempOf(ir, "helper") < FirstTempOf(ir, "main"),
            "a file that builds no instance at module level is lowered in source order");
    }

    [Fact]
    public void ModuleLevelCallThatIsNotAConstructor_DoesNotMoveAnything()
    {
        // `x = f()` at module level binds no instance, so it is not a reason to reorder.
        var ir = GenerateIR(
            "def seedof() -> uint8:\n" +
            "    return 5\n" +
            "value = seedof()\n" +
            "def helper(x: uint8) -> uint8:\n" +
            "    return x * 3 + 1\n" +
            "def main(seed: uint8) -> uint8:\n" +
            "    return helper(seed) + seed * 2\n");

        Assert.True(FirstTempOf(ir, "helper") < FirstTempOf(ir, "main"),
            "a module-level call that constructs nothing leaves the order alone");
    }

    [Fact]
    public void AnnotatedModuleLevelInstance_AlsoLowersMainFirst()
    {
        // `obj: Box = Box(5)` is the same construction with the type written down.
        var ir = GenerateIR(Box +
            "obj: Box = Box(5)\n" +
            "def helper(x: uint8) -> uint8:\n" +
            "    return x * 3 + 1\n" +
            "def main(seed: uint8) -> uint8:\n" +
            "    return helper(seed) + seed * 2\n");

        Assert.True(FirstTempOf(ir, "main") < FirstTempOf(ir, "helper"),
            "the annotated spelling binds the same fields and needs the same order");
    }
}
