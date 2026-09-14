using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#301. `main()` written at module level says WHERE the entry point's body runs. It was
/// dropped instead, which ran every module-level statement first: `main(); print("END")` put
/// END before main's own output, and nothing said so.
///
/// The call is still not lowered as a call -- that would make the cycle detector report
/// `main -> main` for the `if __name__ == "__main__":` idiom -- it is a split point. What is
/// written above it runs before main's body and what is written below it runs after. The two
/// shapes a splice cannot represent, a second call and a `return` that would skip the tail,
/// are refused where the call is written.
/// </summary>
public class EntryMainExplicitCallTests
{
    private static ProgramIR Gen(string src, bool foldCompileTimeIfs = false)
    {
        var config = new DeviceConfig { Arch = "avr" };
        var ast = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        // `__name__` is a compile-time value, so the guard folds away before IR generation.
        // Only the test that writes the guard needs that pass; the rest reach the IR as
        // written, which is what every other IR test does.
        if (foldCompileTimeIfs)
            new ConditionalCompilator(config) { ModuleName = "__main__" }.Process(ast);
        return new IRGenerator().Generate(
            ast, new Dictionary<string, ProgramNode>(), config);
    }

    /// The names of the functions main calls, in the order main calls them.
    private static List<string> CallOrder(ProgramIR ir) =>
        ir.Functions.Where(f => f.Name == "main")
            .SelectMany(f => f.Body)
            .OfType<Call>()
            .Select(c => c.FunctionName)
            .ToList();

    private const string Three =
        "from pymcu.types import uint8\n" +
        "def pre() -> uint8:\n" +
        "    return 1\n" +
        "def inside() -> uint8:\n" +
        "    return 2\n" +
        "def post() -> uint8:\n" +
        "    return 3\n";

    [Fact]
    public void ModuleCodeAfterTheExplicitCall_RunsAfterMainsBody()
    {
        var ir = Gen(Three +
            "def main():\n" +
            "    b: uint8 = inside()\n" +
            "a: uint8 = pre()\n" +
            "main()\n" +
            "c: uint8 = post()\n");

        Assert.Equal(new[] { "pre", "inside", "post" }, CallOrder(ir));
    }

    [Fact]
    public void ModuleCodeBeforeTheCallOnly_StillRunsFirst()
    {
        // The shape that already worked, and the one every fixture is written in: the call is
        // the last statement, so main's body closes the program exactly as before.
        var ir = Gen(Three +
            "def main():\n" +
            "    b: uint8 = inside()\n" +
            "a: uint8 = pre()\n" +
            "main()\n");

        Assert.Equal(new[] { "pre", "inside" }, CallOrder(ir));
    }

    [Fact]
    public void TheNameMainGuard_IsStillTheSameSplitPoint()
    {
        // `if __name__ == "__main__": main()` folds to a bare `main()` before IR generation,
        // so it splits the module level in exactly the same place.
        var ir = Gen(Three +
            "def main():\n" +
            "    b: uint8 = inside()\n" +
            "a: uint8 = pre()\n" +
            "if __name__ == \"__main__\":\n" +
            "    main()\n" +
            "c: uint8 = post()\n", foldCompileTimeIfs: true);

        Assert.Equal(new[] { "pre", "inside", "post" }, CallOrder(ir));
    }

    [Fact]
    public void NoExplicitCall_KeepsTheEntryConvention()
    {
        // No call at all: main is the entry point and its body runs after the module level.
        var ir = Gen(Three +
            "def main():\n" +
            "    b: uint8 = inside()\n" +
            "a: uint8 = pre()\n" +
            "c: uint8 = post()\n");

        Assert.Equal(new[] { "pre", "post", "inside" }, CallOrder(ir));
    }

    [Fact]
    public void ASecondExplicitCall_IsRefusedWhereItIsWritten()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Three +
            "def main():\n" +
            "    b: uint8 = inside()\n" +
            "main()\n" +
            "a: uint8 = pre()\n" +
            "main()\n"));

        Assert.Contains("cannot be called twice", ex.Message);
        Assert.Equal(12, ex.Line);   // the second call
    }

    [Fact]
    public void AMainThatReturnsEarly_IsRefusedWhenCodeFollowsTheCall()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Three +
            "def main():\n" +
            "    if True:\n" +
            "        return\n" +
            "    b: uint8 = inside()\n" +
            "main()\n" +
            "c: uint8 = post()\n"));

        Assert.Contains("main() returns", ex.Message);
        Assert.Equal(12, ex.Line);   // the call, which is where the two halves meet
    }

    [Fact]
    public void AMainThatReturnsEarly_IsFineWhenTheCallIsLast()
    {
        // Nothing follows the call, so there is no tail for the `return` to skip.
        var ir = Gen(Three +
            "def main():\n" +
            "    if True:\n" +
            "        return\n" +
            "    b: uint8 = inside()\n" +
            "a: uint8 = pre()\n" +
            "main()\n");

        Assert.Contains("pre", CallOrder(ir));
    }

    [Fact]
    public void ATrailingReturn_IsNotAnEarlyReturn()
    {
        // `return` as main's last statement skips nothing: the tail still lands after it.
        var ir = Gen(Three +
            "def main():\n" +
            "    b: uint8 = inside()\n" +
            "    return\n" +
            "main()\n" +
            "c: uint8 = post()\n");

        Assert.Equal(new[] { "inside", "post" }, CallOrder(ir));
    }
}
