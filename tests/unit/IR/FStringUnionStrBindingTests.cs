using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// adafruit_ht16k33's print_hex shape: `self.print(f"{value:X}")` hands a formatted
/// f-string to a `Union[str, float]` parameter. Three separate bugs compounded into the
/// firmware displaying the interned string id (as decimal) instead of "FF23":
///
/// 1. VisitFStringExpr ignored part.FormatSpec, so `{value:X}` rendered decimal.
/// 2. A Constant arg's .Text survived only into parameters declared exactly `str` (or
///    unannotated); a Union[...] parameter dropped it, so `isinstance(value, str)` inside
///    print() folded False and the call fell into the _number branch.
/// 3. EmitStrBuiltin interned `c.Value.ToString()` -- for a constant that already is a
///    string, Value is the interned id, so str(s) produced the id's decimal digits.
///
/// The assertions mark which isinstance arm lowered (0x53 = str, 0x4E = number, 0x45 =
/// else) and capture character codes out of the bound parameter, so a build-success
/// assertion cannot pass on the unfixed compiler.
/// </summary>
public class FStringUnionStrBindingTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Seg =
        "marker: int = 0\n" +
        "m_first: int = 0\n" +
        "m_last: int = 0\n" +
        "m_len: int = 0\n\n" +
        "class Seg:\n" +
        "    def print(self, value: Union[str, float], decimal: int = 0) -> None:\n" +
        "        global marker, m_first, m_last, m_len\n" +
        "        if isinstance(value, str):\n" +
        "            marker = 0x53\n" +
        "            m_first = ord(value[0])\n" +
        "            m_len = len(value)\n" +
        "            for c in value:\n" +
        "                m_last = ord(c)\n" +
        "        elif isinstance(value, (int, float)):\n" +
        "            marker = 0x4E\n" +
        "        else:\n" +
        "            marker = 0x45\n\n" +
        "    def print_hex(self, value: Union[int, str]) -> None:\n" +
        "        if isinstance(value, int):\n" +
        "            self.print(f\"{value:X}\")\n" +
        "        else:\n" +
        "            self.print(value)\n\n" +
        "    def take(self, s: str) -> None:\n" +
        "        global m_first, m_len\n" +
        "        m_len = len(s)\n" +
        "        m_first = ord(s[0])\n\n" +
        "    def show(self, s: str) -> None:\n" +
        "        global m_last, m_len\n" +
        "        t = str(s)\n" +
        "        m_len = len(t)\n" +
        "        for c in t:\n" +
        "            m_last = ord(c)\n\n" +
        "d = Seg()\n";

    // The module-level `name: int = 0` initializer is the first constant write each
    // marker sees; the writes the test cares about start after it.
    private static int[] MarkerWrites(ProgramIR ir, string name) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: var n } && n == name
                        && c.Src is Constant)
            .Select(c => ((Constant)c.Src).Value)
            .Skip(1)
            .ToArray();

    [Fact]
    public void FStringFormatSpec_InCallArg_RendersUpperHex()
    {
        // The whole fixture path: print_hex(0xFF23) must reach the str arm of print()
        // with "FF23" bound -- first 'F' (0x46), last '3' (0x33), length 4.
        var ir = Gen(Seg + "d.print_hex(0xFF23)\n");

        Assert.Equal(new[] { 0x53 }, MarkerWrites(ir, "marker"));
        Assert.Equal(new[] { 0x46 }, MarkerWrites(ir, "m_first"));
        Assert.Equal(new[] { 0x46, 0x46, 0x32, 0x33 }, MarkerWrites(ir, "m_last"));
        Assert.Equal(new[] { 4 }, MarkerWrites(ir, "m_len"));
    }

    [Fact]
    public void StringLiteral_IntoUnionStrFloatParam_StaysAString()
    {
        // print("12:30"): the literal's text must cross the Union boundary or
        // isinstance(value, str) folds False and _number prints the interned id.
        var ir = Gen(Seg + "d.print(\"12:30\")\n");

        Assert.Equal(new[] { 0x53 }, MarkerWrites(ir, "marker"));
        Assert.Equal(new[] { (int)'1' }, MarkerWrites(ir, "m_first"));
        Assert.Equal(new[] { '1', '2', ':', '3', '0' }.Select(ch => (int)ch), MarkerWrites(ir, "m_last"));
        Assert.Equal(new[] { 5 }, MarkerWrites(ir, "m_len"));
    }

    [Fact]
    public void Int_IntoUnionStrFloatParam_TakesTheNumberArm()
    {
        var ir = Gen(Seg + "d.print(42)\n");

        Assert.Equal(new[] { 0x4E }, MarkerWrites(ir, "marker"));
    }

    [Fact]
    public void StrBoundVariable_IntoUnionStrFloatParam_StaysAString()
    {
        // print_hex's else branch hands `value` -- a Variable bound to a string
        // constant -- to print's Union[str, float] parameter.
        var ir = Gen(Seg + "d.print_hex(\"AB\")\n");

        Assert.Equal(new[] { 0x53 }, MarkerWrites(ir, "marker"));
        Assert.Equal(new[] { (int)'A' }, MarkerWrites(ir, "m_first"));
        Assert.Equal(new[] { 'A', 'B' }.Select(ch => (int)ch), MarkerWrites(ir, "m_last"));
    }

    [Fact]
    public void StrOfAStrConstant_IsTheTextNotTheInternedId()
    {
        // _number's `stnum = str(number)` shape: str() of a bound str must hand back
        // the string, not the decimal digits of its interned id.
        var ir = Gen(Seg + "d.show(\"AB9\")\n");

        Assert.Equal(new[] { 3 }, MarkerWrites(ir, "m_len"));
        Assert.Equal(new[] { 'A', 'B', '9' }.Select(ch => (int)ch), MarkerWrites(ir, "m_last"));
    }

    [Theory]
    [InlineData("d", '2', 3)]
    [InlineData("x", 'f', 2)]
    [InlineData("X", 'F', 2)]
    [InlineData("b", '1', 8)]
    [InlineData("o", '3', 3)]
    [InlineData("04X", '0', 4)]
    [InlineData("5d", ' ', 5)]
    public void IntFormatSpecs_FoldInValuePosition(string spec, char first, int len)
    {
        var ir = Gen(Seg + $"d.take(f\"{{255:{spec}}}\")\n");

        Assert.Equal(new[] { len }, MarkerWrites(ir, "m_len"));
        Assert.Equal(new[] { (int)first }, MarkerWrites(ir, "m_first"));
    }
}
