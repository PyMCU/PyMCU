using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Two folds that answered the wrong value in silence, both found through adafruit_onewire:
///
/// - a tuple-returning function with more than one `return a, b` left the constant of the LAST
///   return walked in the result slot, so the caller folded the second element of
///   `return 7, d - 1` / `return 7, 0` to 0 for every call;
/// - a property read on an instance held in a field (`self._c.value`) took the register shortcut
///   for `.value` and answered the field itself, so the getter never ran.
/// </summary>
public class SilentPreexistingFoldsTests
{
    private static ProgramIR Gen(string src, string arch) =>
        new IRGenerator().Generate(new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new System.Collections.Generic.Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = arch });

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void ATupleSlotTwoReturnsFillDifferently_IsNotFoldedToTheLastOne(string arch)
    {
        var ir = Gen(
            "def search(d: int) -> tuple[int, int]:\n" +
            "    if d > 1:\n" +
            "        return 7, d - 1\n" +
            "    return 7, 0\n" +
            "def run(d: int) -> int:\n" +
            "    x, y = search(d)\n" +
            "    return y\n", arch);
        var run = ir.Functions.Single(f => f.Name == "run").Body;
        Assert.DoesNotContain(run, i => i is Return { Value: Constant });
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void ATupleSlotEveryReturnFillsAlike_StillFolds(string arch)
    {
        var ir = Gen(
            "def search(d: int) -> tuple[int, int]:\n" +
            "    if d > 1:\n" +
            "        return 7, 4\n" +
            "    return 7, 4\n" +
            "def run(d: int) -> int:\n" +
            "    x, y = search(d)\n" +
            "    return y\n", arch);
        var run = ir.Functions.Single(f => f.Name == "run").Body;
        Assert.Contains(run, i => i is Return { Value: Constant { Value: 4 } });
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void APropertyOnAFieldInstance_RunsItsGetter(string arch)
    {
        var ir = Gen(
            "_g = 0\n" +
            "class Inner:\n" +
            "    @property\n" +
            "    def value(self) -> int:\n" +
            "        return _g\n" +
            "class Holder:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._c = Inner()\n" +
            "    def get(self) -> int:\n" +
            "        return self._c.value\n" +
            "h = Holder()\n" +
            "def run() -> int:\n" +
            "    return h.get()\n", arch);
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        // The getter's body reads the module global; the shortcut never reached it.
        Assert.Contains(body, i => i is Copy { Src: Variable { Name: var n } } && n.EndsWith("_g"));
    }
}
