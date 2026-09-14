using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#306. `match x:` where x is None had no subject it could decide: None is not a
/// Constant, because it has no value to compare against, so every arm stayed a run-time
/// comparison and every arm was LOWERED -- including arms whose bodies refuse at compile time.
///
/// `pin.pull = None` is how CircuitPython spells "no pull", and it was refused with "Pull-down
/// resistor not supported on AVR", from the arm the program never selected.
///
/// Two halves. A None ARGUMENT now binds the parameter as None-valued, the way a None DEFAULT
/// already did, and `match` reads its subject's None-ness from the AST before lowering it --
/// a name bound to None has nothing to read.
/// </summary>
public class MatchOnNoneTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, inline\n" +
        "from pymcu.exceptions import CompileError\n\n" +
        "@inline\n" +
        "def act(n: uint8):\n" +
        "    if n == 2:\n" +
        "        raise CompileError('two is not supported here')\n\n";

    private const string Dispatch =
        "@inline\n" +
        "def dispatch(p):\n" +
        "    match p:\n" +
        "        case 1:\n" +
        "            act(1)\n" +
        "        case 2:\n" +
        "            act(2)\n" +
        "        case _:\n" +
        "            act(0)\n\n";

    [Fact]
    public void ANoneArgument_ReachesOnlyTheWildcardArm()
    {
        // The refusing arm belongs to a value this program never passes.
        var ir = Gen(Prelude + Dispatch + "def main():\n    dispatch(None)\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void ANoneArgument_SelectsACaseNoneArmOverTheWildcard()
    {
        var ir = Gen(Prelude +
            "@inline\n" +
            "def pick(p) -> uint8:\n" +
            "    match p:\n" +
            "        case None:\n" +
            "            return 7\n" +
            "        case _:\n" +
            "            return 3\n\n" +
            "def main() -> uint8:\n" +
            "    return pick(None)\n");

        // 7 is produced and 3 never is: the wildcard arm was not lowered at all.
        var main = ir.Functions.Single(f => f.Name == "main");
        var constants = main.Body.OfType<Copy>().Select(c => c.Src).OfType<Constant>()
            .Select(c => c.Value).ToList();
        Assert.Contains(7, constants);
        Assert.DoesNotContain(3, constants);
    }

    [Fact]
    public void AValueThatDoesSelectTheRefusingArm_IsStillRefused()
    {
        // The fold decides which arm runs; it does not make a refusal go away.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(
            () => Gen(Prelude + Dispatch + "def main():\n    dispatch(2)\n"));
        Assert.Contains("two is not supported", ex.Message);
    }

    [Fact]
    public void TheArmNoneDoesSelect_IsStillLowered()
    {
        // Folding decides WHICH arm runs; the arm it picks is as reachable as any other, so a
        // refusal written in it must still fire. Without this the fix would trade a false
        // refusal for a silently skipped one.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "@inline\n" +
            "def dispatch(p):\n" +
            "    match p:\n" +
            "        case 1:\n" +
            "            act(1)\n" +
            "        case _:\n" +
            "            act(2)\n\n" +
            "def main():\n" +
            "    dispatch(None)\n"));
        Assert.Contains("two is not supported", ex.Message);
    }

    [Fact]
    public void ARunTimeSubject_StaysARunTimeMatch()
    {
        // Nothing is decided here, so every arm is lowered and the refusal inside one of them
        // is a false positive that must stay suppressed, exactly as before.
        var ir = Gen(Prelude +
            "@inline\n" +
            "def dispatch(p: uint8):\n" +
            "    match p:\n" +
            "        case 1:\n" +
            "            act(1)\n" +
            "        case _:\n" +
            "            act(0)\n\n" +
            "def main(seed: uint8):\n" +
            "    dispatch(seed)\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void ANoneArgumentAtOneSite_DoesNotAnswerForTheNextSite()
    {
        // The parameter key is the inline prefix plus the name and is reused across call sites
        // at the same depth, so a None left behind by the first call would decide the second.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(
            () => Gen(Prelude + Dispatch +
                      "def main():\n    dispatch(None)\n    dispatch(2)\n"));
        Assert.Contains("two is not supported", ex.Message);
    }

    [Fact]
    public void ANoneAssignedThroughAPropertySetter_ReachesOnlyTheWildcardArm()
    {
        // The shape reported: the subject is the setter's own parameter, bound by the
        // property-setter expansion rather than by a call's argument list.
        var ir = Gen(Prelude +
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self._m = None\n" +
            "    @property\n" +
            "    def mode(self):\n" +
            "        return self._m\n" +
            "    @mode.setter\n" +
            "    def mode(self, p):\n" +
            "        self._m = p\n" +
            "        match p:\n" +
            "            case 1:\n" +
            "                act(1)\n" +
            "            case 2:\n" +
            "                act(2)\n" +
            "            case _:\n" +
            "                act(0)\n\n" +
            "d = Dev()\n" +
            "d.mode = None\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void ARefusalThroughAPropertySetter_KeepsTheArgumentsOwnPosition()
    {
        // One file, so the setter IS the reader's own code and its `act(2)` is a line they can
        // act on. The cross-file half of this -- a setter in an imported module, where that
        // position belongs to a file the reader never opened -- is pinned end to end in
        // tests/stdlib/test_a_setter_refusal_lands_on_the_assignment.py.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self._m = None\n" +
            "    @property\n" +
            "    def mode(self):\n" +
            "        return self._m\n" +
            "    @mode.setter\n" +
            "    def mode(self, p):\n" +
            "        match p:\n" +
            "            case 2:\n" +
            "                act(2)\n" +
            "            case _:\n" +
            "                act(0)\n\n" +
            "d = Dev()\n" +
            "d.mode = 2\n"));

        Assert.Contains("two is not supported", ex.Message);
        Assert.Equal(20, ex.Line);
    }
}
