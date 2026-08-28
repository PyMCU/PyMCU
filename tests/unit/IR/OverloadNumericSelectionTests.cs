using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Regression tests for numeric overload selection (PyMCU#182).
///
/// Two faults, in two different places, both reached by giving an @inline an integer and a
/// float overload:
///
/// 1. The arity fallback's shape test asked only "is this an instance?", which both numeric
///    candidates answered the same way, so the winner was whichever key the registry happened
///    to enumerate first. An integer argument took the float body and the image paid for the
///    software float routines, silently.
/// 2. The module prefix for an expansion was derived as callee-minus-name, which for an
///    overloaded callee leaves the MANGLED KEY (`mm_floor___`). Every name inside the body
///    that spelled a parameter type then resolved to that overload: `float(t)` inside
///    `floor(x: float)` became a call to `floor` and the build died reporting recursion in a
///    function that does not call itself.
///
/// Each body is given a DIFFERENT observable constant, so these assert which body ran rather
/// than how big the image came out. Program size is not a reliable proxy here: a float body
/// that performs no float arithmetic costs nothing, so the wrong selection can be invisible in
/// the byte count while being plainly visible in the IR.
/// </summary>
public class OverloadNumericSelectionTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static bool Uses(ProgramIR ir, int marker) =>
        ir.Functions.SelectMany(f => f.Body).Any(i =>
            i is Binary b && (b.Src1 is Constant c1 && c1.Value == marker
                              || b.Src2 is Constant c2 && c2.Value == marker));

    private const string Preamble =
        "from pymcu.types import uint8, int32, inline, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    // 7 marks the integer body, 100 the float one.
    private const string IntFirst =
        "@inline\n" +
        "def pick(x: int32) -> int32:\n" +
        "    return x + 7\n" +
        "@inline\n" +
        "def pick(x: float) -> int32:\n" +
        "    return int32(x) + 100\n";

    private const string FloatFirst =
        "@inline\n" +
        "def pick(x: float) -> int32:\n" +
        "    return int32(x) + 100\n" +
        "@inline\n" +
        "def pick(x: int32) -> int32:\n" +
        "    return x + 7\n";

    private const string CallInt =
        "def main():\n" +
        "    seed: uint8 = G.value\n" +
        "    G.value = uint8(pick(seed))\n";

    // DISCRIMINATING. Before the fix this ran the float body for both declaration orders in
    // which float enumerated first, which is the reported defect.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnIntegerArgumentTakesTheIntegerOverload(bool intDeclaredFirst)
    {
        var ir = Gen(Preamble + (intDeclaredFirst ? IntFirst : FloatFirst) + CallInt);

        Assert.True(Uses(ir, 7), "the integer body must run for an integer argument");
        Assert.False(Uses(ir, 100), "the float body must not run for an integer argument");
    }

    // DISCRIMINATING, and the sharpest of the set: the answer must not depend on the order the
    // two overloads are written in. Before the fix, swapping the declarations swapped which
    // body ran, with the source otherwise identical.
    [Fact]
    public void DeclarationOrderDoesNotDecideWhichOverloadRuns()
    {
        var a = Gen(Preamble + IntFirst + CallInt);
        var b = Gen(Preamble + FloatFirst + CallInt);

        Assert.Equal(Uses(a, 7), Uses(b, 7));
        Assert.Equal(Uses(a, 100), Uses(b, 100));
    }

    // INVARIANT, not discriminating: a float argument already selected the float overload
    // before the fix, because float enumerated first. It is here so that teaching the shape
    // test about integers cannot quietly send floats the other way.
    [Fact]
    public void AFloatArgumentStillTakesTheFloatOverload()
    {
        var ir = Gen(Preamble + IntFirst +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    G.value = uint8(pick(float(seed) + 0.5))\n");

        Assert.True(Uses(ir, 100), "the float body must run for a float argument");
        Assert.False(Uses(ir, 7), "the integer body must not run for a float argument");
    }

    // THE ARRIVAL THAT THE INVARIANT ABOVE CANNOT SEE, and the one that cost a revert.
    //
    // `AFloatArgumentStillTakesTheFloatOverload` passes a float EXPRESSION written at the call.
    // A float LOCAL is a different arrival: `InferExprType` looked the name up only under the
    // inline prefix, never under the enclosing function, so `main.x` was never found and every
    // plain local inferred UINT8. The suffix for `x: float` was therefore "uint8", the exact key
    // missed, and selection fell to the arity fallback.
    //
    // That was true BEFORE any of this work. The old fallback ignored the suffix and picked by
    // enumeration order, which for `floor` happened to land on the float overload, so the wrong
    // suffix was invisible. Teaching the fallback to respect the numeric family made it decide
    // on that wrong suffix, and `math.floor(x)` on a float local started returning the integer
    // answer: floor(-1.5) came back -1 instead of -2, across five AVR cases.
    //
    // So the repair is to make the suffix right, not to weaken the family rule. With the local
    // found, the suffix is "float", the EXACT key hits, and the fallback is never reached.
    //
    // Kept as a unit case because the failure only ever appeared in the AVR integration suite,
    // four minutes away, on a stdlib function reached through a class. This runs in
    // milliseconds and fails for the same reason.
    [Fact]
    public void AFloatLOCALTakesTheFloatOverload()
    {
        var ir = Gen(Preamble + IntFirst +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    x: float = (float(seed) - 4.0) / 2.0\n" +
            "    G.value = uint8(pick(x))\n");

        Assert.True(Uses(ir, 100), "a float local must reach the float body");
        Assert.False(Uses(ir, 7), "the integer body must not run for a float local");
    }

    // DISCRIMINATING. Before the fix this threw RecursionError naming `pick`, because the
    // expansion's module prefix was the mangled key `pick___` and `float(t)` resolved to
    // `pick___float`. `int32` escaped only by being an intrinsic, which returns earlier in
    // ResolveCallee, so the defect reached exactly the names that are not intrinsics.
    [Fact]
    public void ACastInsideAnOverloadDoesNotResolveToTheOverload()
    {
        var ir = Gen(Preamble +
            "@inline\n" +
            "def pick(x: int32) -> int32:\n" +
            "    return x + 7\n" +
            "@inline\n" +
            "def pick(x: float) -> int32:\n" +
            "    t: int32 = int32(x)\n" +
            "    if float(t) != x:\n" +
            "        t = t - 1\n" +
            "    return t + 100\n" +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    G.value = uint8(pick(float(seed) + 0.5))\n");

        Assert.NotEmpty(ir.Functions);
        Assert.True(Uses(ir, 100), "the float body must run, and its float() cast must be a cast");
    }
}
