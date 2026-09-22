using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The compile-time 2-D grid: `[[v] * W for _ in range(H)]`, `[bytearray(W) for _ in
/// range(H)]` and the `self.g = <same>` field form all lower to ONE flat array of W*H
/// elements, `g[y][x]` to `g[y*W + x]`. Nothing else about the construct is a value:
/// a row is a view, not a list, so every use that would need one is a located refusal.
/// </summary>
public class Grid2dTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static IEnumerable<Instruction> AllBody(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static CompilerError Refused(string src) =>
        Assert.ThrowsAny<CompilerError>(() => Gen(src));

    // ── accepted shapes ─────────────────────────────────────────────────────

    [Fact]
    public void LocalListOfLists_LowersToOneFlatArray()
    {
        var ir = Gen(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    g[1][2] = 7\n" +
            "    v = g[1][2]\n");

        var stores = AllBody(ir).OfType<ArrayStore>()
            .Where(s => s.ArrayName == "main.g").ToList();
        // 12 fill stores (all zero) plus the g[1][2] = 7 store at flat index 6.
        Assert.Equal(13, stores.Count);
        Assert.Contains(stores, s => s.Index is Constant { Value: 6 }
            && s.Src is Constant { Value: 7 });
        Assert.Contains(AllBody(ir).OfType<ArrayLoad>(), l => l.ArrayName == "main.g");
    }

    [Fact]
    public void NonzeroFill_FillsEveryElement()
    {
        var ir = Gen(
            "def main() -> None:\n" +
            "    g = [[5] * 4 for _ in range(3)]\n" +
            "    x = g[0][0]\n");

        var stores = AllBody(ir).OfType<ArrayStore>()
            .Where(s => s.ArrayName == "main.g").ToList();
        Assert.Equal(12, stores.Count);
        Assert.All(stores, s => Assert.Equal(5, ((Constant)s.Src).Value));
    }

    [Fact]
    public void BytearrayRows_LowerToTheSameFlatArray()
    {
        var ir = Gen(
            "def main() -> None:\n" +
            "    g = [bytearray(4) for _ in range(3)]\n" +
            "    g[2][1] = 9\n");

        Assert.Equal(13, AllBody(ir).OfType<ArrayStore>()
            .Count(s => s.ArrayName == "main.g"));
    }

    [Fact]
    public void ModuleLevel_IsAFlatSramArray()
    {
        var ir = Gen(
            "g = [[0] * 4 for _ in range(3)]\n" +
            "def main() -> None:\n" +
            "    g[1][1] = 3\n" +
            "    x = g[2][0]\n");

        Assert.Equal(13, AllBody(ir).OfType<ArrayStore>().Count(s => s.ArrayName == "g"));
    }

    [Fact]
    public void CtorArgs_FoldAsDimensions()
    {
        var ir = Gen(
            "class Life:\n" +
            "    def __init__(self, width, height):\n" +
            "        self.cells = [[0] * width for _ in range(height)]\n" +
            "    def get(self, x, y):\n" +
            "        return self.cells[y][x]\n" +
            "    def set(self, x, y, v):\n" +
            "        self.cells[y][x] = v\n" +
            "\n" +
            "life = Life(32, 8)\n" +
            "def main() -> None:\n" +
            "    life.set(3, 4, 1)\n" +
            "    x = life.get(3, 4)\n");

        var stores = AllBody(ir).OfType<ArrayStore>()
            .Where(s => s.ArrayName == "life_cells").ToList();
        // 256 fill stores; the life.set() call is inlined and adds one more at 4*32+3.
        Assert.Equal(257, stores.Count);
        Assert.Contains(stores, s => s.Index is Constant { Value: 131 });
    }

    [Fact]
    public void AnnotatedArray_WeighedAgainstTheGrid()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "def main() -> None:\n" +
            "    g: uint8[12] = [[0] * 4 for _ in range(3)]\n" +
            "    g[0][0] = 1\n");

        Assert.NotNull(ir);
        Assert.Equal(13, AllBody(ir).OfType<ArrayStore>()
            .Count(s => s.ArrayName == "main.g"));
    }

    [Fact]
    public void AnnotationMismatch_IsRefused()
    {
        var ex = Refused(
            "from pymcu.types import uint8\n" +
            "def main() -> None:\n" +
            "    g: uint8[10] = [[0] * 4 for _ in range(3)]\n");
        Assert.Contains("12", ex.Message);
    }

    [Fact]
    public void NestedWrite_AndAugmentedWrite_Compile()
    {
        var ir = Gen(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    y = 1\n" +
            "    g[y][0] = 2\n" +
            "    g[y][1] += 3\n");

        Assert.NotNull(ir);
    }

    // ── len() ───────────────────────────────────────────────────────────────

    [Fact]
    public void Len_OfGridIsHeight_OfRowIsWidth()
    {
        var ir = Gen(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    h = len(g)\n" +
            "    w = len(g[0])\n" +
            "    y = 1\n" +
            "    w2 = len(g[y])\n");

        var consts = AllBody(ir).OfType<Copy>()
            .Where(c => c.Src is Constant).Select(c => ((Constant)c.Src).Value).ToList();
        Assert.Contains(3, consts);
        Assert.Contains(4, consts);
    }

    // ── iteration and aliases ───────────────────────────────────────────────

    [Fact]
    public void ForRowIn_LowersToARowIndexLoop()
    {
        var ir = Gen(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    s = 0\n" +
            "    for row in g:\n" +
            "        s = s + row[0]\n");

        Assert.NotNull(ir);
        Assert.Contains(AllBody(ir).OfType<ArrayLoad>(), l => l.ArrayName == "main.g");
    }

    [Fact]
    public void RowAlias_SameBlockReadsAndWrites_Compile()
    {
        var ir = Gen(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    y = 2\n" +
            "    r = g[y]\n" +
            "    r[0] = 1\n" +
            "    x = r[1]\n" +
            "    n = len(r)\n");

        Assert.NotNull(ir);
        Assert.Contains(AllBody(ir).OfType<ArrayStore>(),
            s => s.ArrayName == "main.g" && s.Src is Constant { Value: 1 });
    }

    [Fact]
    public void ForElementInRow_IteratesTheRow()
    {
        var ir = Gen(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    y = 1\n" +
            "    s = 0\n" +
            "    for x in g[y]:\n" +
            "        s = s + x\n");

        Assert.NotNull(ir);
        Assert.Contains(AllBody(ir).OfType<ArrayLoad>(), l => l.ArrayName == "main.g");
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public void RepeatedListOfLists_IsAnAliasingRefusal()
    {
        var ex = Refused(
            "def main() -> None:\n" +
            "    g = [[0] * 4] * 3\n");
        Assert.Contains("alias", ex.Message);
        Assert.Contains("for _ in range(", ex.Message);
    }

    [Fact]
    public void NonConstantDims_AreRefused()
    {
        var ex = Refused(
            "def f(n):\n" +
            "    g = [[0] * n for _ in range(3)]\n" +
            "def main() -> None:\n" +
            "    f(4)\n");
        Assert.Contains("compile-time constant", ex.Message);
    }

    [Fact]
    public void GridAppend_IsRefused()
    {
        var ex = Refused(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    g.append([0] * 4)\n");
        Assert.Contains("append", ex.Message);
    }

    [Fact]
    public void RowRebind_IsRefused()
    {
        var ex = Refused(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    g[1] = [9] * 4\n");
        Assert.Contains("row", ex.Message);
    }

    [Fact]
    public void RowPassedToAFunction_EscapesAndIsRefused()
    {
        var ex = Refused(
            "def f(r):\n" +
            "    return r[0]\n" +
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    f(g[1])\n");
        Assert.Contains("row", ex.Message);
    }

    [Fact]
    public void RowAliasPassedToAFunction_EscapesAndIsRefused()
    {
        var ex = Refused(
            "def f(r):\n" +
            "    return r[0]\n" +
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    r = g[1]\n" +
            "    f(r)\n");
        Assert.Contains("row", ex.Message);
    }

    [Fact]
    public void RowReturned_EscapesAndIsRefused()
    {
        var ex = Refused(
            "def pick(g, y):\n" +
            "    return g[y]\n" +
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    r = pick(g, 1)\n");
        Assert.Contains("row", ex.Message);
    }

    [Fact]
    public void RowStoredInAField_EscapesAndIsRefused()
    {
        var ex = Refused(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self.f = 0\n" +
            "    def grab(self, g, y):\n" +
            "        self.f = g[y]\n" +
            "b = Box()\n" +
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    b.grab(g, 1)\n");
        Assert.Contains("row", ex.Message);
    }

    [Fact]
    public void RowAppend_IsRefused()
    {
        var ex = Refused(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    g[1].append(9)\n");
        Assert.Contains("row", ex.Message);
    }

    [Fact]
    public void RowSlice_IsRefused()
    {
        var ex = Refused(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    s = g[1][0:2]\n");
        Assert.Contains("slice", ex.Message);
    }

    [Fact]
    public void GridSlice_IsRefused()
    {
        var ex = Refused(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    s = g[0:2]\n");
        Assert.Contains("slice", ex.Message);
    }

    [Fact]
    public void RowComparison_IsRefused()
    {
        var ex = Refused(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    if g[0] == g[1]:\n" +
            "        x = 1\n");
        Assert.Contains("row", ex.Message);
    }

    [Fact]
    public void MembershipOnARow_IsRefused()
    {
        var ex = Refused(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    if 0 in g[0]:\n" +
            "        x = 1\n");
        Assert.Contains("row", ex.Message);
    }

    [Fact]
    public void BareRowRead_IsRefused()
    {
        var ex = Refused(
            "def main() -> None:\n" +
            "    g = [[0] * 4 for _ in range(3)]\n" +
            "    x = g[1]\n");
        Assert.Contains("row", ex.Message);
    }
}
