using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#105: an annotation naming no known type used to fall back to uint8 in silence, so a
/// one-character typo truncated the arithmetic to 8 bits and the program printed a different
/// number than the same line without the annotation.
///
/// PyMCU#278: that check was applied to a LOCAL and a MODULE-LEVEL GLOBAL only. Three
/// positions were missed and stayed silent -- a function parameter, a return type, and an
/// instance field written `self.v: T = x`. There is still ONE check and ONE sentence for all
/// of them, because the reader's mistake is the same mistake wherever they made it, and the
/// tests below assert that same sentence in every position.
///
/// The instance field slipped through for a reason worth keeping: a BARE name annotation on
/// an assignment never becomes an AnnAssign at all -- both front ends build one only when the
/// annotation contains a '[' -- so it arrives as AssignStmt.AnnotatedType, which nothing was
/// checking.
///
/// The signature check does NOT live in VisitFunction, where it belongs by shape. Measured: a
/// force-inlined function never reaches VisitFunction, so for a program with one
/// `def take(v: Bogus)` and one call, VisitFunction runs for `main` alone and sees no
/// parameter at all. It runs over every DEFINITION instead, after the scans, because the
/// check asks whether a name is a class.
/// </summary>
public class UnknownAnnotationTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    [Fact]
    public void AMisspelledScalar_IsRejectedAndTheRealNameSuggested()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    x: unit8 = 300\n"));

        Assert.Contains("unknown type 'unit8'", ex.Message);
        Assert.Contains("uint8", ex.Message);
    }

    [Fact]
    public void TheSpellingsThatMeanSomething_StillCompile()
    {
        Gen("def main():\n" +
            "    a: uint16 = 300\n" +
            "    b: int8 = -1\n" +
            "    c: float = 1.5\n" +
            "    d: bool = True\n" +
            "    e: uint8[4] = [1, 2, 3, 4]\n");
    }

    [Fact]
    public void AClassOfTheProgramsOwn_IsAType()
    {
        Gen("class Thing:\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "\n" +
            "def main():\n" +
            "    t: Thing = Thing(3)\n");
    }

    private static CompilerError Fails(string src) =>
        Assert.Throws<CompilerError>(() => Gen(src));

    private const string Unknown = "unknown type 'Bogus' in the annotation";

    // ---- the two that already worked. Kept so the fix cannot be made by loosening them. ----

    [Fact]
    public void ALocal_IsStillRefused()
    {
        Assert.Contains(Unknown, Fails(
            "def main() -> uint8:\n" +
            "    v: Bogus = 300\n" +
            "    return v >> 8\n").Message);
    }

    [Fact]
    public void AModuleLevelGlobal_IsStillRefused()
    {
        Assert.Contains(Unknown, Fails(
            "g: Bogus = 300\n" +
            "def main() -> uint8:\n" +
            "    return g >> 8\n").Message);
    }

    // ---- the three that were silent ----

    [Fact]
    public void AFunctionParameter_IsRefused()
    {
        Assert.Contains(Unknown, Fails(
            "def take(v: Bogus) -> uint8:\n" +
            "    return v >> 8\n" +
            "def main() -> uint8:\n" +
            "    return take(300)\n").Message);
    }

    [Fact]
    public void AReturnType_IsRefused()
    {
        Assert.Contains(Unknown, Fails(
            "def give() -> Bogus:\n" +
            "    return 300\n" +
            "def main() -> uint8:\n" +
            "    return give() >> 8\n").Message);
    }

    [Fact]
    public void AnInstanceField_IsRefused()
    {
        Assert.Contains(Unknown, Fails(
            "class D:\n" +
            "    def __init__(self, seed):\n" +
            "        self.v: Bogus = seed\n" +
            "d = D(300)\n" +
            "def main() -> uint8:\n" +
            "    return d.v >> 8\n").Message);
    }

    // A method's parameter is a definition the sweep has to reach too: methods live in a class
    // body, not in ast.Functions.
    [Fact]
    public void AMethodParameter_IsRefused()
    {
        Assert.Contains(Unknown, Fails(
            "class D:\n" +
            "    def __init__(self):\n" +
            "        self.n = 1\n" +
            "    def take(self, v: Bogus) -> uint8:\n" +
            "        return v >> 8\n" +
            "d = D()\n" +
            "def main() -> uint8:\n" +
            "    return d.take(300)\n").Message);
    }

    // The near-miss half of the message survives in the new positions: the whole point is a
    // one-character typo, and naming the type meant is what makes it a two-second fix.
    [Fact]
    public void ANearMissIsSuggested_InTheNewPositionsToo()
    {
        Assert.Contains("did you mean 'uint8'", Fails(
            "def take(v: unit8) -> uint8:\n" +
            "    return v\n" +
            "def main() -> uint8:\n" +
            "    return take(3)\n").Message);
    }

    // ---- controls: refusing everything would pass every test above ----

    [Fact]
    public void RealTypeNames_StillCompileInAllThreeNewPositions()
    {
        Gen("from pymcu.types import uint16\n" +
            "class D:\n" +
            "    def __init__(self, seed: uint16):\n" +
            "        self.v: uint16 = seed\n" +
            "def take(v: uint16) -> uint16:\n" +
            "    return v >> 8\n" +
            "def main() -> uint16:\n" +
            "    return take(300)\n");
    }

    // A user's own class is a type. The check asks the class tables, which is why it runs
    // after every module is scanned rather than during one.
    [Fact]
    public void AUserClassName_IsAcceptedAsAParameterAndReturnType()
    {
        Gen("class Box:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self.n = n\n" +
            "def make(n: uint8) -> Box:\n" +
            "    return Box(n)\n" +
            "def read(b: Box) -> uint8:\n" +
            "    return b.n\n" +
            "def main() -> uint8:\n" +
            "    return read(make(7))\n");
    }

    // `void` and `None` are both spellings of "returns nothing" and neither is a typo.
    [Theory]
    [InlineData("None")]
    [InlineData("void")]
    public void TheNothingReturnTypes_AreAccepted(string ret)
    {
        Gen($"def go() -> {ret}:\n" +
            "    pass\n" +
            "def main() -> None:\n" +
            "    go()\n");
    }

    // THE VALUE SIDE, and the shape it has to take.
    //
    // A local's annotation decides the width, so it can be asserted as a value: uint16 reaches
    // the IR as a two-byte variable. The other positions cannot be asserted this way today,
    // and two shapes that LOOK like they discriminate do not:
    //
    //   self.v: T = 300 read through an 8-bit sink   the value folds and the high byte is
    //                                                legitimately dead, so uint16 emits one
    //                                                instruction FEWER than uint8
    //   self.v: T = seed compared with `> 255`       the comparison forces a 16-bit compare
    //                                                on its own, whatever T says
    //
    // A discriminator has to CONSUME the high byte of a value that is not known at compile
    // time, AND the field has to be written again later: with only its __init__ initialiser
    // the layout is taken from the right-hand side and the annotation is not consulted at
    // all, so a CORRECT `uint16` is ignored too. That is PyMCU#282, a layout decision rather
    // than a missing check, and it is why there is no value assertion for the field position
    // here. The absence is deliberate, not an oversight.
    [Fact]
    public void AnAnnotatedLocalIsAsWideAsItSays()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n" +
            "def main(seed: uint8) -> uint8:\n" +
            "    v: uint16 = seed + 300\n" +
            "    return v >> 8\n");

        var widths = ir.Functions.SelectMany(f => f.Body)
            .SelectMany(i => i switch
            {
                Copy c => new[] { c.Src, c.Dst },
                Binary b => new[] { b.Src1, b.Src2, b.Dst },
                _ => Array.Empty<Val>(),
            })
            .OfType<Variable>().Where(x => x.Name.EndsWith("v", StringComparison.Ordinal))
            .Select(x => x.Type).ToList();

        Assert.NotEmpty(widths);
        Assert.All(widths, t => Assert.True(t.SizeOf() >= 2,
            $"'v: uint16' must reach the IR at two bytes, saw {t}"));
    }

    // ---- bracketed annotations: the head name is a type name too ----
    //
    // This check used to return early for anything containing a '[', on the grounds that a
    // bracketed form's own handling reports what it cannot make sense of. True for the forms
    // that mean something and false for every other head, so `Optional[uint8]`,
    // `Tuple[uint8, uint8]`, `List[uint8]`, `Dict[uint8, uint8]` and `Bogus[uint8, bool]` were
    // all accepted in silence -- and those are the spellings a typing-annotated library
    // writes, so the hole was closed for the rare spelling and open for the common one.

    [Fact]
    public void AnUnknownBracketedHead_IsRefusedAndNamed()
    {
        var ex = Fails(
            "def take(v: Bogus[uint8, bool]) -> uint8:\n" +
            "    return 1\n" +
            "def main() -> uint8:\n" +
            "    return take(1)\n");

        Assert.Contains("unknown type 'Bogus' in the annotation", ex.Message);
    }

    // The head is reported, not the whole form, and the near-miss points at the spelling that
    // works: PyMCU has `list[...]`, so `List[...]` is one capital letter away from compiling.
    [Fact]
    public void ATypingSpelling_IsRefusedAndTheWorkingSpellingSuggested()
    {
        var ex = Fails(
            "def take(v: List[uint8]) -> uint8:\n" +
            "    return 1\n" +
            "def main() -> uint8:\n" +
            "    return take(1)\n");

        Assert.Contains("unknown type 'List'", ex.Message);
        Assert.Contains("did you mean 'list'", ex.Message);
    }

    // ONE IDEA, ONE ANSWER. `Optional[X]` IS `Union[X, None]` and `Union[a, b]` IS `a | b`, so
    // all three get the sentence the `|` spelling already got, word for word. "unknown type
    // 'Union'" would be true and useless.
    [Theory]
    [InlineData("Union[uint8, bool]")]
    [InlineData("Optional[uint8]")]
    public void TheTypingSpellingsOfAUnion_GetTheUnionMessage(string ann)
    {
        var ex = Fails(
            $"def take(v: {ann}) -> uint8:\n" +
            "    return 1\n" +
            "def main() -> uint8:\n" +
            "    return take(1)\n");

        Assert.Contains("a union type annotation is not supported", ex.Message);
        Assert.DoesNotContain("unknown type", ex.Message);
    }

    // The forms that mean something must keep meaning it. `tuple` is the one this list was
    // missing when it was first written, and the corpus said so with one fixture and seven
    // unit tests rather than with an argument.
    [Fact]
    public void TheBracketedFormsThatMeanSomething_StillCompile()
    {
        Gen("from pymcu.types import uint8, const, ptr, inline\n" +
            "@inline\n" +
            "def pair() -> tuple[uint8, uint8]:\n" +
            "    return 1, 2\n" +
            "def main() -> uint8:\n" +
            "    a: uint8[4] = [1, 2, 3, 4]\n" +
            "    b, c = pair()\n" +
            "    return a[0] + b + c\n");
    }
}
