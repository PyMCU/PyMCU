using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#438. Two string-identity bugs, one lookup table each.
///
/// 1. ResolveStrConstant's bare-name fallback asked the one flat strConstantVariables table
///    for `b` after the qualified `f.b` lookup had already come up empty -- so a uint8
///    parameter named `b` inside `def f(b: uint8)` answered with the text of a module-level
///    `b = "world"`, and `print(b)` refused as a string that varies by path. A scope that
///    knows the name holds no string shadows the global, exactly as Python scoping works.
///
/// 2. `a + b` of two string VARIABLES never folded: the gate asked for a StringLiteral on
///    one side, so the concat fell through to integer arithmetic on the interned ids and `c`
///    held the ids' sum for `a = "hello"; b = "world"` -- which is another string's id, so
///    `print(c)` wrote "world" where "helloworld" was meant. The text of a name the compiler
///    already holds (StaticStringOf, the same lookup f-strings and .join() use) is the test.
/// </summary>
public class StringIdentityShadowingTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static string FlashText(ProgramIR ir, string name) =>
        new string(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
            .Single(fd => fd.Name == name).Bytes.TakeWhile(b => b != 0)
            .Select(b => (char)b).ToArray());

    // Every text the program hands to uart_write_str.s: print()'s trailing newline is in the
    // list too, which is why the tests ask membership rather than uniqueness.
    private static List<string> StrWrites(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "uart_write_str.s" }
                        && c.Src is FlashStrAddr)
            .Select(c => FlashText(ir, ((FlashStrAddr)c.Src).Name))
            .ToList();

    [Fact]
    public void AParameterNamedLikeAModuleString_IsTheParameterNotTheString()
    {
        var ir = Gen(
            "b = \"world\"\n" +
            "def f(b: uint8):\n" +
            "    print(b)\n" +
            "f(65)\n");

        var f = ir.Functions.Single(fn => fn.Name == "f");
        Assert.Contains(f.Body, i => i is Call c && c.FunctionName.Contains("uart_write_decimal"));
        // print()'s own newline also goes through uart_write_str; what must not reach it is
        // the module-level string's text.
        Assert.DoesNotContain(f.Body,
            i => i is Copy c && c.Src is FlashStrAddr a && FlashText(ir, a.Name) == "world");
    }

    [Fact]
    public void ALocalNamedLikeAModuleString_IsTheLocalNotTheString()
    {
        var ir = Gen(
            "b = \"world\"\n" +
            "def f():\n" +
            "    b: uint8 = 65\n" +
            "    print(b)\n" +
            "f()\n");

        var f = ir.Functions.Single(fn => fn.Name == "f");
        Assert.Contains(f.Body, i => i is Call c && c.FunctionName.Contains("uart_write_decimal"));
        Assert.DoesNotContain(f.Body,
            i => i is Copy c && c.Src is FlashStrAddr a && FlashText(ir, a.Name) == "world");
    }

    [Fact]
    public void AddingTwoStringVariables_ConcatenatesTheirTexts()
    {
        var ir = Gen(
            "a = \"hello\"\n" +
            "b = \"world\"\n" +
            "c = a + b\n" +
            "print(c)\n");

        Assert.Contains("helloworld", StrWrites(ir));
    }

    [Fact]
    public void AStringVariablePlusALiteral_ConcatenatesTheirTexts()
    {
        var ir = Gen(
            "a = \"hello\"\n" +
            "c = a + \"!\"\n" +
            "print(c)\n");

        Assert.Contains("hello!", StrWrites(ir));
    }

    // 3. `if x == "abc":` on a module-level name deleted the `then` branch. A condition does
    //    not go through VisitBinary, where the two texts above are compared: it becomes a
    //    conditional jump over the VALUES, and the value of a name bound to a MULTI-character
    //    string is its storage slot. `str` is a one-byte slot and an interned id needs two, so
    //    the slot held 0 while the literal was 256, the two ranges were disjoint, and the
    //    always-false fold took the branch out of the image with nothing said. The
    //    one-character case, whose id IS its character code and so fits the byte, answered
    //    correctly the whole time, which is what kept this hidden.
    [Fact]
    public void ComparingAModuleStringToItsOwnText_KeepsTheThenBranch()
    {
        var ir = Gen(
            "x = \"abc\"\n" +
            "if x == \"abc\":\n" +
            "    print(\"yes\")\n" +
            "else:\n" +
            "    print(\"no\")\n");

        Assert.Contains("yes", StrWrites(ir));
        Assert.DoesNotContain("no", StrWrites(ir));
    }

    [Fact]
    public void ComparingAOneCharModuleStringToItsOwnText_KeepsTheThenBranch()
    {
        var ir = Gen(
            "y = \"a\"\n" +
            "if y == \"a\":\n" +
            "    print(\"yes\")\n" +
            "else:\n" +
            "    print(\"no\")\n");

        Assert.Contains("yes", StrWrites(ir));
        Assert.DoesNotContain("no", StrWrites(ir));
    }

    [Fact]
    public void ComparingAModuleStringToADifferentText_KeepsTheElseBranch()
    {
        var ir = Gen(
            "x = \"abc\"\n" +
            "if x == \"abd\":\n" +
            "    print(\"yes\")\n" +
            "else:\n" +
            "    print(\"no\")\n");

        Assert.Contains("no", StrWrites(ir));
        Assert.DoesNotContain("yes", StrWrites(ir));
    }

    // The run-time dispatch this fold must not swallow: `label` holds a different text on
    // each arm, so its text at the comparison is whatever the last arm lowered left behind
    // and the id test is the only honest answer. Both arms stay in the image.
    [Fact]
    public void ComparingARuntimeDecidedString_KeepsBothArms()
    {
        var ir = Gen(
            "k: uint8 = GPIOR0.value\n" +
            "if k == 0:\n" +
            "    label = \"mono\"\n" +
            "else:\n" +
            "    label = \"none\"\n" +
            "if label == \"mono\":\n" +
            "    print(\"is-mono\")\n" +
            "else:\n" +
            "    print(\"not-mono\")\n");

        Assert.Contains("is-mono", StrWrites(ir));
        Assert.Contains("not-mono", StrWrites(ir));
    }
}
