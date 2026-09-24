using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0009 phase 3 -- `-> Union[A, B]` / `-> Union[A, B, None]` on a real
/// subroutine. The tag byte carries the member index, the payload is the widest
/// member, and the decision-2 gate still applies: a union provably decidable at
/// compile time keeps the code it had before the tag existed.
/// </summary>
public class TaggedUnionReturnTests
{
    private const string Hdr =
        "from pymcu.types import uint8, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private static FunctionDef ParseFirst(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram().Functions[0];

    // ── member lists land on the FunctionDef ────────────────────────────────────

    [Fact]
    public void UnionOfTwoRealTypesHasAMemberList()
    {
        var f = ParseFirst("def f(a: uint8) -> Union[int, float]:\n    return a\n");
        Assert.Equal(new List<string> { "int", "float" }, f.ReturnMembers);
    }

    [Fact]
    public void UnionOfTwoRealTypesAndNoneHasAMemberList()
    {
        var f = ParseFirst("def f(a: uint8) -> Union[int, float, None]:\n    return a\n");
        Assert.Equal(new List<string> { "int", "float", "None" }, f.ReturnMembers);
    }

    [Fact]
    public void PipeSpellingHasAMemberList()
    {
        var f = ParseFirst("def f(a: uint8) -> int | float | None:\n    return a\n");
        Assert.Equal(new List<string> { "int", "float", "None" }, f.ReturnMembers);
    }

    [Fact]
    public void DuplicateMembersCollapse()
    {
        var f = ParseFirst("def f(a: uint8) -> Union[int, int, None]:\n    return a\n");
        Assert.Equal(new List<string> { "int", "None" }, f.ReturnMembers);
    }

    // ── a decidable union spends no tag (RFC decision 2) ────────────────────────

    [Fact]
    public void AUnionThatOnlyEverReturnsOneMemberKeepsByteIdenticalCode()
    {
        var ir = Gen(Hdr +
            "def pick(a: uint8) -> Union[int, float]:\n" +
            "    if a == 0:\n" +
            "        return 0\n" +
            "    return a + 1\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n" +
            "    GPIOR1.value = v\n");

        var f = ir.Functions.Single(fn => fn.Name == "pick");
        Assert.Null(f.ReturnMembers);
        Assert.DoesNotContain(f.Body, i => i is Return { Tag: not null });
        var call = ir.Functions.SelectMany(fn => fn.Body).OfType<Call>()
            .Single(c => c.FunctionName == "pick");
        Assert.Null(call.TagDst);
        Assert.DoesNotContain(ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>(),
            c => c.Dst is Variable v && v.Name.Contains("$tag"));
    }

    // ── a live union carries the member index ───────────────────────────────────

    [Fact]
    public void ATwoMemberUnionReturnCarriesMemberIndexTags()
    {
        var ir = Gen(Hdr +
            "def pick(a: uint8) -> Union[int, float]:\n" +
            "    if a == 0:\n" +
            "        return a + 1\n" +
            "    return 2.5\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n" +
            "    if v is not None:\n" +
            "        GPIOR1.value = 1\n");

        var f = ir.Functions.Single(fn => fn.Name == "pick");
        Assert.Equal(new List<string> { "int", "float" }, f.ReturnMembers);
        Assert.Contains(f.Body, i => i is Return { Tag: Constant { Value: 0 } });
        Assert.Contains(f.Body, i => i is Return { Tag: Constant { Value: 1 } });
        var call = ir.Functions.SelectMany(fn => fn.Body).OfType<Call>()
            .Single(c => c.FunctionName == "pick");
        Assert.NotNull(call.TagDst);
        // Widest member decides the payload width: float is 4 bytes.
        Assert.Equal(DataType.FLOAT, f.ReturnType);
    }

    [Fact]
    public void AUnionWithNoneTagsNoneWithTheLastIndex()
    {
        var ir = Gen(Hdr +
            "def pick(a: uint8) -> Union[int, float, None]:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    if a == 1:\n" +
            "        return a + 1\n" +
            "    return 2.5\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n" +
            "    if v is not None:\n" +
            "        GPIOR1.value = 1\n");

        var f = ir.Functions.Single(fn => fn.Name == "pick");
        Assert.Equal(new List<string> { "int", "float", "None" }, f.ReturnMembers);
        Assert.Contains(f.Body, i => i is Return { Tag: Constant { Value: 2 } });
        Assert.Contains(f.Body, i => i is Return { Tag: Constant { Value: 0 } });
        Assert.Contains(f.Body, i => i is Return { Tag: Constant { Value: 1 } });
    }

    // ── narrowing by isinstance and match ───────────────────────────────────────

    [Fact]
    public void IsinstanceOnALiveUnionIsATagCompare()
    {
        var ir = Gen(Hdr +
            "def pick(a: uint8) -> Union[int, float]:\n" +
            "    if a == 0:\n" +
            "        return a + 1\n" +
            "    return 2.5\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n" +
            "    if isinstance(v, int):\n" +
            "        GPIOR1.value = v\n");

        var main = ir.Functions.Single(fn => fn.Name == "main");
        Assert.Contains(main.Body.OfType<Binary>(),
            b => b.Op == PyMCU.IR.BinaryOp.Equal && b.Src2 is Constant { Value: 0 });
    }

    [Fact]
    public void AnIsinstanceArmReadsThePayloadAsTheMemberType()
    {
        var ir = Gen(Hdr +
            "def pick(a: uint8) -> Union[int, float]:\n" +
            "    if a == 0:\n" +
            "        return a + 1\n" +
            "    return 2.5\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n" +
            "    if isinstance(v, int):\n" +
            "        GPIOR1.value = v + 1\n");

        // Inside the arm the payload reads as int16 (member 0), not float.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Op == PyMCU.IR.BinaryOp.Add
                 && b.Src1 is Variable { Type: DataType.INT16 });
    }

    [Fact]
    public void AUnionPayloadReadOutsideNarrowingIsRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "def pick(a: uint8) -> Union[int, float]:\n" +
            "    if a == 0:\n" +
            "        return a + 1\n" +
            "    return 2.5\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n" +
            "    GPIOR1.value = v + 1\n"));
        Assert.Contains("'v'", ex.Message);
        Assert.Contains("narrow", ex.Message);
    }

    [Fact]
    public void MatchOnAUnionDispatchesOnTheTag()
    {
        var ir = Gen(Hdr +
            "def pick(a: uint8) -> Union[int, float, None]:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    if a == 1:\n" +
            "        return a + 1\n" +
            "    return 2.5\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n" +
            "    match v:\n" +
            "        case int():\n" +
            "            GPIOR1.value = v\n" +
            "        case float():\n" +
            "            GPIOR1.value = 9\n" +
            "        case None:\n" +
            "            GPIOR1.value = 0\n");

        var main = ir.Functions.Single(fn => fn.Name == "main");
        // Each typed arm compares the tag to that member's index.
        Assert.Contains(main.Body, i => i is JumpIfNotEqual { Src2: Constant { Value: 0 } });
        Assert.Contains(main.Body, i => i is JumpIfNotEqual { Src2: Constant { Value: 1 } });
        Assert.Contains(main.Body, i => i is JumpIfNotEqual { Src2: Constant { Value: 2 } });
    }

    // ── ceilings and out-of-scope members ───────────────────────────────────────

    [Fact]
    public void AFiveMemberUnionIsRefusedNamingTheCeiling()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "def pick(a: uint8) -> Union[uint8, uint16, int16, uint32, float]:\n" +
            "    return a\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n"));
        Assert.Contains("4", ex.Message);
        Assert.Contains("pick", ex.Message);
    }

    [Fact]
    public void AUnionOfInstanceTypesIsRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x: uint8 = 0\n\n" +
            "class B:\n" +
            "    def __init__(self):\n" +
            "        self.y: uint8 = 0\n\n" +
            "def pick(a: uint8) -> Union[A, B]:\n" +
            "    return A()\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n"));
        Assert.Contains("pick", ex.Message);
    }

    [Fact]
    public void AReturnNoneIntoANoneLessUnionIsRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "def pick(a: uint8) -> Union[int, float]:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    return a\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n"));
        Assert.NotEmpty(ex.Message);
    }

    // ── Optional[X] with a non-scalar payload is not a union ────────────────────

    [Fact]
    public void AnOptionalStrReturnIsNotAUnion()
    {
        // `-> Optional[str]` is a string that is sometimes absent, not a tagged
        // union: a buffer is a name, not a payload byte (RFC 0009 decision 4 --
        // the same line the parameter gate already draws). The member list is
        // dropped and the function keeps the marks-based None semantics it had
        // before the union work (adafruit_character_lcd's `message` getter).
        var ir = Gen(Hdr +
            "def pick(a: uint8) -> Optional[str]:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    return \"x\"\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n");

        var f = ir.Functions.Single(fn => fn.Name == "pick");
        Assert.Null(f.ReturnMembers);
        Assert.DoesNotContain(f.Body, i => i is Return { Tag: not null });
        var call = ir.Functions.SelectMany(fn => fn.Body).OfType<Call>()
            .Single(c => c.FunctionName == "pick");
        Assert.Null(call.TagDst);
    }

    [Fact]
    public void AnOptionalInstanceReturnIsNotAUnion()
    {
        // `-> Optional[Dev]` is an instance slot that is sometimes None: an
        // instance has no member slot, so this is the field-scan rule applied
        // to a return.
        var ir = Gen(Hdr +
            "class Dev:\n" +
            "    def __init__(self):\n" +
            "        self.x: uint8 = 0\n\n" +
            "def pick(a: uint8) -> Optional[Dev]:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    d = Dev()\n" +
            "    return d\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n");

        var f = ir.Functions.Single(fn => fn.Name == "pick");
        Assert.Null(f.ReturnMembers);
        Assert.DoesNotContain(f.Body, i => i is Return { Tag: not null });
    }

    [Fact]
    public void AUnionOfStrAndIntStillRefuses()
    {
        // Two REAL members, one a buffer: that is a genuine union a tag byte
        // cannot carry, and the refusal stands.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "def pick(a: uint8) -> Union[str, int]:\n" +
            "    if a == 0:\n" +
            "        return \"x\"\n" +
            "    return a\n\n" +
            "def main():\n" +
            "    v = pick(GPIOR0.value)\n"));
        Assert.Contains("pick", ex.Message);
    }
}
