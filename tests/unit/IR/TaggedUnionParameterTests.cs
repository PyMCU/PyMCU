using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0009 phase 2 -- `Optional[X]` / `Union[A, B, ...]` on a REAL subroutine's
/// parameter. The tag byte follows the payload in the argument run: the caller
/// stages the member index and the callee binds it as a sibling `p$tag` parameter
/// that `p is None` / narrowing reads. Decision 2 still applies: when every call
/// site proves one member the parameter stays tag-free and byte-identical.
/// </summary>
public class TaggedUnionParameterTests
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

    // ── member lists land on the Param ──────────────────────────────────────────

    [Fact]
    public void AnOptionalParameterHasAMemberList()
    {
        var f = ParseFirst("def f(a: Optional[uint8]) -> uint8:\n    return 0\n");
        Assert.Equal(new List<string> { "uint8", "None" }, f.Params[0].UnionMembers);
    }

    [Fact]
    public void APipeParameterHasAMemberList()
    {
        var f = ParseFirst("def f(a: int | float | None) -> uint8:\n    return 0\n");
        Assert.Equal(new List<string> { "int", "float", "None" }, f.Params[0].UnionMembers);
    }

    // ── the tag decision ────────────────────────────────────────────────────────

    [Fact]
    public void AParameterCalledWithBothMembersGetsATagParam()
    {
        var ir = Gen(Hdr +
            "def show(v: Optional[uint8]) -> uint8:\n" +
            "    if v is None:\n" +
            "        return 0\n" +
            "    return v\n\n" +
            "def main():\n" +
            "    GPIOR0.value = show(GPIOR1.value)\n" +
            "    GPIOR0.value = show(None)\n");

        var f = ir.Functions.Single(fn => fn.Name == "show");
        // TagParams names the TAG's own index in the parameter run: payload `v`
        // at 0, its tag sibling at 1.
        Assert.Equal(new List<int> { 1 }, f.TagParams);
        Assert.Equal("show.v", f.Params[0]);
        Assert.Equal("show.v$tag", f.Params[1]);
        // The caller stages one tag value per call: the argument list carries the
        // member index immediately after the payload, and the param-home copies
        // name show.v$tag as the tag's destination.
        var calls = ir.Functions.SelectMany(fn => fn.Body).OfType<Call>()
            .Where(c => c.FunctionName == "show").ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, calls[0].Args.Count);
        Assert.Equal(2, calls[1].Args.Count);
        Assert.Equal(new Constant(0), calls[0].Args[1]);
        Assert.Equal(new Constant(1), calls[1].Args[1]);
        Assert.Contains(ir.Functions.Single(fn => fn.Name == "main").Body.OfType<Copy>(),
            c => c.Dst is Variable { Name: "show.v$tag" });
    }

    [Fact]
    public void AParameterProvenValuedAtEverySiteKeepsByteIdenticalCode()
    {
        var ir = Gen(Hdr +
            "def show(v: Optional[uint8]) -> uint8:\n" +
            "    if v is None:\n" +
            "        return 0\n" +
            "    return v\n\n" +
            "def main():\n" +
            "    GPIOR0.value = show(GPIOR1.value)\n");

        var f = ir.Functions.Single(fn => fn.Name == "show");
        Assert.Null(f.TagParams);
        Assert.DoesNotContain(f.Params, n => n.Contains("$tag"));
        Assert.DoesNotContain(ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>(),
            c => c.Dst is Variable v && v.Name.Contains("$tag"));
    }

    [Fact]
    public void AParameterProvenNoneAtEverySiteKeepsByteIdenticalCode()
    {
        var ir = Gen(Hdr +
            "def show(v: Optional[uint8] = None) -> uint8:\n" +
            "    if v is None:\n" +
            "        return 7\n" +
            "    return v\n\n" +
            "def main():\n" +
            "    GPIOR0.value = show()\n");

        var f = ir.Functions.Single(fn => fn.Name == "show");
        Assert.Null(f.TagParams);
        Assert.DoesNotContain(f.Params, n => n.Contains("$tag"));
    }

    // ── callee-side binding ─────────────────────────────────────────────────────

    [Fact]
    public void ATagParamIsAUint8SiblingThePayloadNarrowsThrough()
    {
        var ir = Gen(Hdr +
            "def show(v: Optional[uint8]) -> uint8:\n" +
            "    if v is None:\n" +
            "        return 0\n" +
            "    return v\n\n" +
            "def main():\n" +
            "    GPIOR0.value = show(GPIOR1.value)\n" +
            "    GPIOR0.value = show(None)\n");

        var f = ir.Functions.Single(fn => fn.Name == "show");
        // `v is None` inside the callee is a compare of the tag parameter, not a
        // constant fold -- both members reach this callee at run time.
        Assert.Contains(f.Body.OfType<Binary>(),
            b => b.Op == PyMCU.IR.BinaryOp.Equal
                 && (b.Src1 is Variable { Name: "show.v$tag" }
                     || b.Src2 is Variable { Name: "show.v$tag" }));
    }

    [Fact]
    public void ATwoRealMemberParameterTagsByMemberIndex()
    {
        var ir = Gen(Hdr +
            "def emit(v: Union[int, float]) -> int:\n" +
            "    if isinstance(v, float):\n" +
            "        return 1\n" +
            "    return v\n\n" +
            "def main():\n" +
            "    GPIOR0.value = emit(3)\n" +
            "    GPIOR0.value = emit(1.5)\n");

        var f = ir.Functions.Single(fn => fn.Name == "emit");
        Assert.Equal(new List<int> { 1 }, f.TagParams);
        // int arg -> tag 0; float arg -> tag 1.
        var tagCopies = ir.Functions.Single(fn => fn.Name == "main").Body.OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "emit.v$tag" })
            .Select(c => c.Src).ToList();
        Assert.Contains(tagCopies, s => s is Constant { Value: 0 });
        Assert.Contains(tagCopies, s => s is Constant { Value: 1 });
    }

    // ── refusals the tag cannot carry ───────────────────────────────────────────

    [Fact]
    public void AUnionParameterOnAnExternFunctionKeepsItsRefusal()
    {
        var msg = Refusal(Hdr +
            "@extern(\"c_send\")\n" +
            "def send(v: Optional[uint8]) -> uint8:\n" +
            "    return 0\n\n" +
            "def main():\n" +
            "    GPIOR0.value = send(None)\n");
        Assert.Contains("fixed", msg);
    }

    [Fact]
    public void AUnionParameterOfInstancesKeepsItsRefusal()
    {
        var msg = Refusal(Hdr +
            "class A:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.x: uint8 = 0\n\n" +
            "class B:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.y: uint8 = 0\n\n" +
            "def take(v: Union[A, B]) -> uint8:\n" +
            "    return 0\n\n" +
            "def main():\n" +
            "    GPIOR0.value = take(A())\n");
        Assert.Contains("union", msg);
    }
}
