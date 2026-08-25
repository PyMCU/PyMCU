using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Regression tests for overload selection on a compile-time string that arrives through a
/// CALL (issue #152). Binding the call's result to a local first selected the const[str]
/// overload; passing the call straight in, or storing its result in a field first, selected
/// the NUMERIC one and reported nothing.
///
/// Two independent gaps, both here: the argument typer had no answer for a call (it types by
/// InferExprType, which has no string to report), and a field assigned a compile-time string
/// through a temporary kept only the interned id, not the string.
///
/// `k` comes from a ptr load, so the tag cannot fold to a constant and the two overloads stay
/// distinguishable in the IR: 20 + k is the string overload, 10 + k the numeric one.
/// </summary>
public class ConstStrOverloadTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static List<Instruction> MainBody(ProgramIR ir) =>
        ir.Functions.First(f => f.Name == "main").Body;

    private const string Preamble =
        "from pymcu.types import uint8, inline, const, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n" +
        "class Low:\n" +
        "    @inline\n" +
        "    def __init__(self, s: const[str], k: uint8):\n" +
        "        self.tag: uint8 = 20 + k\n" +
        "    @inline\n" +
        "    def __init__(self, n: const[uint8], k: uint8):\n" +
        "        self.tag: uint8 = 10 + k\n" +
        "@inline\n" +
        "def name_for(n: const[uint8]) -> const[str]:\n" +
        "    if n == 13:\n" +
        "        return \"PB5\"\n" +
        "    return \"PD2\"\n" +
        "@inline\n" +
        "def num_for(n: const[uint8]) -> const[uint8]:\n" +
        "    return n + 1\n";

    private static bool PickedStringOverload(List<Instruction> body) =>
        body.Any(i => i is Binary { Op: IR.BinaryOp.Add, Src1: Constant { Value: 20 } });

    private static bool PickedNumericOverload(List<Instruction> body) =>
        body.Any(i => i is Binary { Op: IR.BinaryOp.Add, Src1: Constant { Value: 10 } });

    // The shape that already worked, kept as the control: bind the call's result to a local,
    // then pass the local.
    [Fact]
    public void StringFromCall_BoundToLocal_PicksStringOverload()
    {
        var body = MainBody(Gen(Preamble +
            "class V:\n" +
            "    @inline\n" +
            "    def __init__(self, n: const[uint8], k: uint8):\n" +
            "        nm = name_for(n)\n" +
            "        self._low = Low(nm, k)\n" +
            "        self.tag: uint8 = self._low.tag\n" +
            "def main() -> None:\n" +
            "    k: uint8 = G.value\n" +
            "    G.value = V(13, k).tag\n"));

        Assert.True(PickedStringOverload(body));
        Assert.False(PickedNumericOverload(body));
    }

    [Fact]
    public void StringFromCall_PassedDirectly_PicksStringOverload()
    {
        var body = MainBody(Gen(Preamble +
            "class V:\n" +
            "    @inline\n" +
            "    def __init__(self, n: const[uint8], k: uint8):\n" +
            "        self._low = Low(name_for(n), k)\n" +
            "        self.tag: uint8 = self._low.tag\n" +
            "def main() -> None:\n" +
            "    k: uint8 = G.value\n" +
            "    G.value = V(13, k).tag\n"));

        Assert.True(PickedStringOverload(body));
        Assert.False(PickedNumericOverload(body));
    }

    [Fact]
    public void StringFromCall_StoredInFieldFirst_PicksStringOverload()
    {
        var body = MainBody(Gen(Preamble +
            "class V:\n" +
            "    @inline\n" +
            "    def __init__(self, n: const[uint8], k: uint8):\n" +
            "        self._name = name_for(n)\n" +
            "        self._low = Low(self._name, k)\n" +
            "        self.tag: uint8 = self._low.tag\n" +
            "def main() -> None:\n" +
            "    k: uint8 = G.value\n" +
            "    G.value = V(13, k).tag\n"));

        Assert.True(PickedStringOverload(body));
        Assert.False(PickedNumericOverload(body));
    }

    // The guard on the fix: a call that returns a NUMBER must still select the numeric
    // overload, in both of the positions above. Typing every call as a string would have
    // flipped the whole overload set the other way.
    [Fact]
    public void NumberFromCall_PassedDirectly_PicksNumericOverload()
    {
        var body = MainBody(Gen(Preamble +
            "class V:\n" +
            "    @inline\n" +
            "    def __init__(self, n: const[uint8], k: uint8):\n" +
            "        self._low = Low(num_for(n), k)\n" +
            "        self.tag: uint8 = self._low.tag\n" +
            "def main() -> None:\n" +
            "    k: uint8 = G.value\n" +
            "    G.value = V(13, k).tag\n"));

        Assert.True(PickedNumericOverload(body));
        Assert.False(PickedStringOverload(body));
    }

    [Fact]
    public void NumberFromCall_StoredInFieldFirst_PicksNumericOverload()
    {
        var body = MainBody(Gen(Preamble +
            "class V:\n" +
            "    @inline\n" +
            "    def __init__(self, n: const[uint8], k: uint8):\n" +
            "        self._num = num_for(n)\n" +
            "        self._low = Low(self._num, k)\n" +
            "        self.tag: uint8 = self._low.tag\n" +
            "def main() -> None:\n" +
            "    k: uint8 = G.value\n" +
            "    G.value = V(13, k).tag\n"));

        Assert.True(PickedNumericOverload(body));
        Assert.False(PickedStringOverload(body));
    }

    // The arm reached through the trailing `return`. `name_for`'s selecting `if` folds at
    // compile time, so exactly one return is visited whichever way it goes and the result is
    // still a compile-time string.
    //
    // This one pins the interaction with the multi-return fix for #132: read as two live
    // return paths, the differing constants would kill the string and the const[str]
    // parameter would then reject the call outright ("requires a compile-time string constant
    // value"). Reachability is what separates them -- a return at the expansion's own branch
    // depth ends the body, so what follows it is dead.
    [Fact]
    public void ConstSelectedString_SurvivesATrailingDeadReturn()
    {
        var body = MainBody(Gen(Preamble +
            "class V:\n" +
            "    @inline\n" +
            "    def __init__(self, n: const[uint8], k: uint8):\n" +
            "        self._low = Low(name_for(n), k)\n" +
            "        self.tag: uint8 = self._low.tag\n" +
            "def main() -> None:\n" +
            "    k: uint8 = G.value\n" +
            "    G.value = V(99, k).tag\n"));

        // n = 99 takes the trailing return, "PD2" -- still a string, still the str overload.
        Assert.True(PickedStringOverload(body));
        Assert.False(PickedNumericOverload(body));
    }

    // The field key is reused across call sites: a field of an instance built INSIDE an
    // @inline flattens under that expansion's own prefix (`inline1.build.r__x`), so a later
    // call to the same @inline lands on exactly the same name. Recording the string without
    // clearing it left the first call's string standing over the second call's numeric field,
    // which then selected const[str] and was rejected outright:
    //
    //   error: Parameter 's' is declared as const[str] and requires a compile-time
    //          string constant value
    //
    // The same hazard the @inline argument binder documents for its own maps (#144). Both
    // call sites are here, in this order, because one alone cannot show it.
    [Fact]
    public void FieldKeyReusedAcrossCallSites_DoesNotCarryThePreviousString()
    {
        var body = MainBody(Gen(Preamble +
            "class Slot:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self._x = 0\n" +
            "@inline\n" +
            "def build(n: const[uint8], k: uint8) -> uint8:\n" +
            "    r = Slot()\n" +
            "    if n == 13:\n" +
            "        r._x = name_for(n)\n" +
            "    else:\n" +
            "        r._x = num_for(n)\n" +
            "    return Low(r._x, k).tag\n" +
            "def main() -> None:\n" +
            "    k: uint8 = G.value\n" +
            "    G.value = build(13, k)\n" +
            "    G.value = build(7, k)\n"));

        // One of each: the string arm picked const[str], the numeric arm picked const[uint8].
        Assert.True(PickedStringOverload(body));
        Assert.True(PickedNumericOverload(body));
    }
}
