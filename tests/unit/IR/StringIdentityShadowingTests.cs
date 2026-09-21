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
}
