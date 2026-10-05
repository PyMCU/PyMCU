using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Regression coverage for tuple-result slots of @inline expansions (the
/// "bBase.iret_depth_seq_k" names). Two expansions at the same depth used to share the
/// slot names: `pair(a)[0] + pair(b)[0]` bound the left operand to a Variable naming the
/// shared slot, the right expansion rewrote it, and the binary read b+b -- a silent
/// miscompile of a real program on main (16 where CPython says 11). Each expansion now
/// mints unique names, and every consumer of the result list empties it, so a
/// scalar-producing expression wrapped around the call cannot read the inner
/// expansion's slots back as its own result (`print(add2(pair(1)[0], pair(9)[0]))`
/// streamed "(9, 10)" instead of 10).
/// </summary>
public class InlineTupleResultSlotTests
{
    private const string Prelude =
        "from pymcu.types import uint8, inline, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n";

    private const string Pair =
        "@inline\n" +
        "def pair(v: uint8) -> (uint8, uint8):\n" +
        "    return v, v + 1\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body;

    private static bool IsTupleSlotName(string n) => n.Contains(".iret_");

    private static IEnumerable<string> VariableNamesOf(Instruction i)
    {
        foreach (var v in ValsOf(i))
            if (v is Variable { Name: { } n })
                yield return n;
    }

    private static IEnumerable<Val> ValsOf(Instruction i) => i switch
    {
        Copy c => new[] { c.Src, c.Dst },
        Binary b => new[] { b.Src1, b.Src2, b.Dst },
        Unary u => new[] { u.Src, u.Dst },
        Call c => c.Args.Append(c.Dst),
        Return r => new[] { r.Value },
        AugAssign a => new[] { a.Target, a.Operand },
        JumpIfEqual j => new[] { j.Src1, j.Src2 },
        JumpIfNotEqual j => new[] { j.Src1, j.Src2 },
        JumpIfLessThan j => new[] { j.Src1, j.Src2 },
        JumpIfLessOrEqual j => new[] { j.Src1, j.Src2 },
        JumpIfGreaterThan j => new[] { j.Src1, j.Src2 },
        JumpIfGreaterOrEqual j => new[] { j.Src1, j.Src2 },
        _ => Enumerable.Empty<Val>(),
    };

    // The shared-slot bug's shape at instruction level: one operation whose two operands
    // are the same tuple-result slot name -- the second expansion's value read twice.
    private static IEnumerable<Binary> SameSlotTwice(IEnumerable<Instruction> body) =>
        body.OfType<Binary>().Where(b =>
            b.Src1 is Variable { Name: { } n1 } && b.Src2 is Variable { Name: { } n2 }
            && n1 == n2 && IsTupleSlotName(n1));

    [Fact]
    public void SiblingSubscripts_ReadTwoDistinctResultSlots()
    {
        var body = Main(Gen(Pair +
            "def main():\n" +
            "    s: uint8 = G.value\n" +
            "    r: uint8 = pair(s + 3)[0] + pair(s + 8)[0]\n" +
            "    G.value = r\n"));

        // Both expansions emit their own slot writes: four distinct names now, two
        // shared ones before (main.iret_1_0, main.iret_1_1) -- the minimal surface the
        // miscompile needed.
        var written = body.OfType<Copy>()
            .Select(c => c.Dst).OfType<Variable>()
            .Select(v => v.Name).Where(IsTupleSlotName).Distinct().ToList();
        written.Should().HaveCountGreaterThanOrEqualTo(4,
            "each expansion mints its own tuple-result slots");

        SameSlotTwice(body).Should().BeEmpty(
            "`pair(a)[0] + pair(b)[0]` must not read one shared slot twice");
    }

    [Fact]
    public void ConstantFoldedSiblingSubscripts_StillReadDistinctSlots()
    {
        // Literal arguments take the constant-tracking path, where the stale binding for
        // the shared name folded to the OTHER call's value.
        var body = Main(Gen(Pair +
            "def main():\n" +
            "    r: uint8 = pair(3)[0] + pair(8)[0]\n" +
            "    G.value = r\n"));

        SameSlotTwice(body).Should().BeEmpty(
            "the folded path reads each expansion's own slot, not a shared name");
        body.OfType<Copy>()
            .Select(c => c.Dst).OfType<Variable>()
            .Select(v => v.Name).Where(IsTupleSlotName).Distinct()
            .Should().HaveCountGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void ScalarCallAroundTupleElements_LeavesNoResultListBehind()
    {
        // print(add2(...)): add2 is a real subroutine and produces a scalar. If the
        // tuple-expansion results inside its argument list stayed live, print streamed
        // them as a "(9, 10)" repr instead of the 10 add2 returned.
        var body = Main(Gen(Pair +
            "def add2(a: uint8, b: uint8) -> uint8:\n" +
            "    return a + b\n" +
            "def main():\n" +
            "    print(add2(pair(1)[0], pair(9)[0]))\n"));

        var decimalCalls = body.OfType<Call>()
            .Where(c => c.FunctionName == "uart_write_decimal_u8").ToList();
        decimalCalls.Should().ContainSingle("the scalar result prints once, as a number");
        decimalCalls[0].Args.OfType<Variable>().Select(v => v.Name)
            .Should().NotContain(n => IsTupleSlotName(n),
                "the printed value is add2's result, never an iret_ slot name");

        body.OfType<Call>().Where(c => c.FunctionName == "uart_write_str")
            .SelectMany(c => c.Args).OfType<Variable>()
            .Should().NotContain(v => IsTupleSlotName(v.Name),
                "no repr stream may read a tuple-result slot the scalar call did not produce");
    }

    [Fact]
    public void SplicedTupleArgs_BindFromDistinctExpansionSlots()
    {
        // add4(*pair(1), *pair(9)): the first splice names expansion A's slots, the
        // second names B's. Shared names handed add4 B's values twice.
        var body = Main(Gen(Pair +
            "def add4(a: uint8, b: uint8, c: uint8, d: uint8) -> uint8:\n" +
            "    return a + b + c + d\n" +
            "def main():\n" +
            "    r: uint8 = add4(*pair(1), *pair(9))\n" +
            "    G.value = r\n"));

        var slotNames = body.SelectMany(VariableNamesOf)
            .Where(IsTupleSlotName).Distinct().ToList();
        slotNames.Should().HaveCountGreaterThanOrEqualTo(4,
            "two *f() splices must see two different expansions' slots");
    }

    [Fact]
    public void SameNamedLocals_InlineAndRegular_StayDistinct()
    {
        // Control for the collision sweep: `acc` in a regular function is twice.acc, in
        // an @inline expansion it carries the expansion prefix -- the two never share a
        // name and never shared one before the fix.
        var ir = Gen(
            "@inline\n" +
            "def bump(v: uint8) -> uint8:\n" +
            "    acc = v + 1\n" +
            "    return acc\n" +
            "def twice(v: uint8) -> uint8:\n" +
            "    acc = v + 2\n" +
            "    return acc\n" +
            "def main():\n" +
            "    s: uint8 = G.value\n" +
            "    a: uint8 = bump(s)\n" +
            "    b: uint8 = twice(s)\n" +
            "    G.value = a + b\n");

        ir.Functions.Single(f => f.Name == "twice").Body
            .SelectMany(VariableNamesOf)
            .Should().Contain("twice.acc",
                "a regular function's local is qualified by its function");

        var inlineAccs = Main(ir).SelectMany(VariableNamesOf)
            .Where(n => n.EndsWith(".acc")).ToList();
        inlineAccs.Should().OnlyContain(n => n.StartsWith("inline") && n != "twice.acc",
            "the inlined `acc` is a different, expansion-prefixed name");
        inlineAccs.Should().NotBeEmpty();
    }
}
