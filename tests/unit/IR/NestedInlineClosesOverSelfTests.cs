using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#427. A nested function decorated `@inline`, defined inside a method and reading or
/// writing `self` of the enclosing method, is the documented way to write a closure
/// (docs/language/limitations.md:287) -- valid without a `nonlocal self` declaration, since
/// only self's ATTRIBUTE is mutated; `self` itself is never rebound.
///
/// Nothing forwarded the enclosing method's own `self` binding (its `variableAliases` /
/// `instanceClasses` entry) into the nested function's own fresh inline prefix, so `self`
/// inside the nested body resolved to an ordinary, never-aliased local of that new frame
/// (`inline2.bump.self`) instead of the real instance. Every write to it was invisible outside
/// the nested call -- calling `bump()` had no observable effect at all, not even a wrong value.
/// </summary>
public class NestedInlineClosesOverSelfTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import inline, uint8\n\n\n";

    [Fact]
    public void ANestedInlineFunctionWithNoSelfParamWritesTheEnclosingInstance()
    {
        // A GPIOR0-seeded start, not a literal, so the assertion measures the runtime value
        // thread rather than a compile-time fold ([[medir-no-es-compilar]]).
        var ir = Gen(Preamble +
            "class Counter:\n" +
            "    def __init__(self, start: uint8):\n" +
            "        self.value = start\n\n" +
            "    def bump_twice(self):\n" +
            "        @inline\n" +
            "        def bump():\n" +
            "            self.value = self.value + 1\n" +
            "        bump()\n" +
            "        bump()\n" +
            "        return self.value\n\n" +
            "c = Counter(GPIOR0.value)\n" +
            "GPIOR1.value = c.bump_twice()\n");

        // The broken symptom by name: a nested-frame "self" that nothing ever aliased.
        var allNames = ir.Functions.SelectMany(f => f.Body)
            .SelectMany(CollectVariableNames)
            .ToList();
        Assert.DoesNotContain(allNames, n => n.EndsWith(".self", StringComparison.Ordinal));

        // The two increments must land on the SAME storage bump_twice's own (unnested)
        // self.value write would use -- the instance's own flattened name, `c` (Counter has
        // exactly one field, so RFC 0001 collapses the instance onto the field itself).
        var cWrites = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(cpy => cpy.Dst is Variable v && v.Name == "c")
            .ToList();
        // One store from the constructor plus one per bump() call.
        Assert.Equal(3, cWrites.Count);
    }

    private static IEnumerable<string> CollectVariableNames(Instruction instr)
    {
        foreach (var val in instr switch
        {
            Copy c => new[] { c.Src, c.Dst },
            Binary b => new[] { b.Src1, b.Src2, b.Dst },
            _ => Array.Empty<Val>(),
        })
        {
            if (val is Variable v) yield return v.Name;
            if (val is Temporary t) yield return t.Name;
        }
    }
}
