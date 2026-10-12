using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An exception the compiler raises on its own -- a zero divisor, a missing dict key, a
/// miss in <c>list.index</c> -- is a raise with no message statement, so `print(e)` answered an
/// empty line where CPython 3.14 has text ("division by zero"). The text is now written into the
/// same message word a literal raise writes. It is written only where something reads it: a
/// handler that binds the exception (`except ZeroDivisionError as e`, or a catch-all one). A
/// program that never prints the exception carries neither the string nor the store.
/// </summary>
public class RuntimeExceptionTextTests
{
    private static List<Instruction> Body(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
        return ir.Functions.SelectMany(f => f.Body).ToList();
    }

    private static bool Interns(List<Instruction> body, string text) =>
        body.OfType<FlashData>().Any(fd => System.Text.Encoding.ASCII.GetString(
            fd.Bytes.Where(b => b != 0).Select(b => (byte)b).ToArray()) == text);

    private const string Divide = "def f(a: int, b: int) -> int:\n    return a // b\n\n";

    private static string Handler(string clause) =>
        Divide + "try:\n    x: int = f(8, 0)\n" + clause + "    pass\n";

    [Fact]
    public void ABoundHandlerForTheTypeGetsCPythonsText()
        => Assert.True(Interns(Body(Handler("except ZeroDivisionError as e:\n")), "division by zero"));

    [Fact]
    public void ACatchAllBoundHandlerGetsItToo()
        => Assert.True(Interns(Body(Handler("except Exception as e:\n")), "division by zero"));

    [Fact]
    public void AHandlerThatBindsNothingCarriesNoText()
        => Assert.False(Interns(Body(Handler("except ZeroDivisionError:\n")), "division by zero"));

    [Fact]
    public void AHandlerBoundForAnotherTypeCarriesNoText()
        => Assert.False(Interns(Body(Handler("except ValueError as e:\n")), "division by zero"));

    [Fact]
    public void AProgramWithNoHandlerCarriesNoTextAndNoStore()
    {
        var body = Body(Divide + "x: int = f(8, 2)\n");
        Assert.False(Interns(body, "division by zero"));
        Assert.DoesNotContain(body, i => i is Copy { Dst: Variable { Name: "__exn_msg" } });
    }

    [Fact]
    public void AZeroBaseToANegativePowerHasItsOwnText()
        => Assert.True(Interns(Body(
            "def p(a: float, b: float) -> float:\n    return a ** b\n\n" +
            "try:\n    y: float = p(0.0, -1.0)\nexcept ZeroDivisionError as e:\n    pass\n"),
            "zero to a negative power"));

    [Fact]
    public void AMissingDictKeyNamesTheKeyTheWayCPythonsReprDoes()
    {
        var str = Body("d = {'a': 1}\ntry:\n    y = d['zz']\nexcept KeyError as e:\n    pass\n");
        Assert.True(Interns(str, "'zz'"));
        var num = Body("d = {1: 1}\nk = 9\ntry:\n    y = d[k]\nexcept KeyError as e:\n    pass\n");
        Assert.True(Interns(num, "9"));
    }

    [Fact]
    public void ListIndexMissWordsItAsAListAndTupleIndexAsATuple()
    {
        var list = Body("xs = [1, 2]\nk = 99\ntry:\n    y = xs.index(k)\nexcept ValueError as e:\n    pass\n");
        Assert.True(Interns(list, "list.index(x): x not in list"));
        var tup = Body("ts = (1, 2)\nk = 99\ntry:\n    y = ts.index(k)\nexcept ValueError as e:\n    pass\n");
        Assert.True(Interns(tup, "tuple.index(x): x not in tuple"));
    }

    // The raise on a key the compiler already knows is missing never returns, so what follows it
    // must not read a temporary nothing writes: tools/verify_ir.py reports that as a slot in
    // somebody else's frame (`read-never-written`), the shape of a bit test on an unwritten slot.
    [Fact]
    public void AKnownMissingDictKeyInsideATryReadsNoUnwrittenTemporary()
    {
        var body = Body("d = {'a': 1, 'b': 2}\ntry:\n    z: int = d['zz']\nexcept KeyError as e:\n    pass\n");

        var written = new HashSet<string>();
        foreach (var ins in body)
            foreach (var prop in ins.GetType().GetProperties()
                         .Where(p => p.Name is "Dst" or "Target" && p.PropertyType == typeof(Val)))
                if (prop.GetValue(ins) is Temporary t)
                    written.Add(t.Name);

        var unwritten = body.OfType<Copy>()
            .Select(c => c.Src).OfType<Temporary>()
            .Where(t => !written.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();
        Assert.Empty(unwritten);
    }
}
