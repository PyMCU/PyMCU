using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0009 phases 2+3 -- a field that holds both None and scalars is a tagged
/// union: payload at the widest member's width plus a `<field>$tag` sibling.
/// The ctor's `None`, a method's scalar store and a caller's `is None` all ride
/// the same storage, so a MODULE-LEVEL instance's fields must be real globals --
/// a function-local tag dies as a dead store and folds the later test wrong.
/// </summary>
public class UnionFieldTests
{
    private const string Hdr = "from pymcu.types import uint8\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private static IEnumerable<Copy> Copies(ProgramIR ir) =>
        ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>();

    // ── storage: module-level fields are globals ────────────────────────────────

    [Fact]
    public void ADeclaredUnionFieldOnAModuleInstanceIsGlobal()
    {
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t: Union[uint8, None] = None\n" +
            "    def put(self, v: uint8) -> None:\n" +
            "        self.t = v\n" +
            "s = S()\n" +
            "s.put(3)\n");

        Assert.Contains(ir.Globals, g => g.Name == "s_t");
        Assert.Contains(ir.Globals, g => g.Name == "s_t$tag");
    }

    [Fact]
    public void AnEvidenceUnionFieldOnAModuleInstanceIsGlobal()
    {
        // No annotation: `None` in the ctor plus a scalar in a method is the
        // adafruit_dht shape -- the field's union membership is inferred.
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t = None\n" +
            "    def put(self, v: uint8) -> None:\n" +
            "        self.t = v\n" +
            "s = S()\n" +
            "s.put(3)\n");

        Assert.Contains(ir.Globals, g => g.Name == "s_t$tag");
    }

    [Fact]
    public void PipeAndOptionalSpellingsBothTagTheField()
    {
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t: uint8 | None = None\n" +
            "        self.u: Optional[uint8] = None\n" +
            "    def put(self, v: uint8) -> None:\n" +
            "        self.t = v\n" +
            "        self.u = v\n" +
            "s = S()\n" +
            "s.put(3)\n");

        Assert.Contains(ir.Globals, g => g.Name == "s_t$tag");
        Assert.Contains(ir.Globals, g => g.Name == "s_u$tag");
    }

    [Fact]
    public void ALocalInstancesUnionFieldStaysLocal()
    {
        // Only module-level instance storage is global: a function-local `s`
        // must not leak `s_t`/`s_t$tag` into the globals table.
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t: Optional[uint8] = None\n" +
            "    def put(self, v: uint8) -> None:\n" +
            "        self.t = v\n" +
            "def f() -> None:\n" +
            "    s = S()\n" +
            "    s.put(3)\n" +
            "f()\n");

        Assert.DoesNotContain(ir.Globals, g => g.Name == "s_t");
        Assert.DoesNotContain(ir.Globals, g => g.Name == "s_t$tag");
    }

    // ── member order and tag values ─────────────────────────────────────────────

    [Fact]
    public void DeclaredMembersKeepNoneLast()
    {
        // `Union[uint8, None]` normalizes to [uint8, None]: the ctor's None
        // store writes tag index 1, not the first-discovered 0.
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t: Union[uint8, None] = None\n" +
            "    def put(self, v: uint8) -> None:\n" +
            "        self.t = v\n" +
            "s = S()\n" +
            "s.put(3)\n");

        Assert.Contains(Copies(ir), c =>
            c.Dst is Variable { Name: "s_t$tag" } &&
            c.Src is Constant { Value: 1 });
    }

    // ── persistence across a real (bound-outlined) subroutine ──────────────────

    [Fact]
    public void ABoundMethodWritesTheGlobalTag()
    {
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t: Optional[uint8] = None\n" +
            "    def put(self, v: uint8) -> None:\n" +
            "        if v > 0:\n" +
            "            self.t = v\n" +
            "        else:\n" +
            "            self.t = None\n" +
            "s = S()\n" +
            "s.put(3)\n" +
            "s.put(4)\n" +
            "s.put(5)\n");

        var bound = ir.Functions.SingleOrDefault(f => f.Name == "_bound_s_put");
        Assert.NotNull(bound);
        Assert.Contains(bound!.Body.OfType<Copy>(), c =>
            c.Dst is Variable { Name: "s_t$tag" });
        Assert.Contains(bound.Body.OfType<Copy>(), c =>
            c.Dst is Variable { Name: "s_t" });
    }

    [Fact]
    public void IsNoneOnAModuleFieldReadsTheTag()
    {
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t: Optional[uint8] = None\n" +
            "    def put(self, v: uint8) -> None:\n" +
            "        self.t = v\n" +
            "s = S()\n" +
            "s.put(3)\n" +
            "if s.t is None:\n" +
            "    n = 0\n" +
            "else:\n" +
            "    n = s.t\n");

        // The check must NOT fold to a constant: the put() between the ctor's
        // None and the read makes the tag runtime-live -- `s.t is None` lowers
        // to a Binary on the tag byte feeding the branch.
        Assert.Contains(
            ir.Functions.SelectMany(fn => fn.Body).OfType<Binary>(),
            b => (b.Src1 is Variable v && v.Name == "s_t$tag") ||
                 (b.Src2 is Variable v2 && v2.Name == "s_t$tag"));
    }

    // ── compile-time-provable fields stay tag-free ─────────────────────────────

    [Fact]
    public void AnAlwaysNoneFieldKeepsNoTag()
    {
        // `self.t` is None forever: the provable case emits no tag sibling,
        // the byte-identical guarantee (RFC 0009 decision 2).
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t = None\n" +
            "s = S()\n");

        Assert.DoesNotContain(Copies(ir), c =>
            (c.Dst is Variable d && d.Name.Contains("$tag")) ||
            (c.Src is Variable s && s.Name.Contains("$tag")));
        Assert.DoesNotContain(ir.Globals, g => g.Name.Contains("$tag"));
    }

    [Fact]
    public void AnAlwaysScalarFieldKeepsNoTag()
    {
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t: uint8 = 0\n" +
            "    def put(self, v: uint8) -> None:\n" +
            "        self.t = v\n" +
            "s = S()\n" +
            "s.put(3)\n");

        Assert.DoesNotContain(ir.Globals, g => g.Name.Contains("$tag"));
    }

    // ── declared-member validation ──────────────────────────────────────────────

    [Fact]
    public void AStoreOutsideTheDeclaredMembersRefuses()
    {
        // `self.t + 1` widens to uint16 -- not a member of `Union[uint8, None]`.
        var msg = Refusal(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t: Optional[uint8] = None\n" +
            "    def bump(self) -> None:\n" +
            "        self.t = self.t + 1\n" +
            "s = S()\n" +
            "s.bump()\n");

        Assert.Contains("not a member", msg);
    }

    [Fact]
    public void ADottedAnnotatedAssignKeepsItsMemberList()
    {
        // The C# parser used to drop `Union[...]` metadata on `self.f: X = v`,
        // so the field's member list arrived as just [None]. The declared list
        // must reach the scan or `None` files as tag 0 instead of the last index.
        var ir = Gen(Hdr +
            "class S:\n" +
            "    def __init__(self):\n" +
            "        self.t: Union[uint8, int8, None] = None\n" +
            "    def put(self, v: uint8) -> None:\n" +
            "        self.t = v\n" +
            "s = S()\n" +
            "s.put(3)\n");

        // uint8 -> tag 0; None -> tag 2 (three declared members, None last).
        // The ctor's None store writes 2 directly; put()'s write-back returns
        // the field's runtime tag with the payload.
        Assert.Contains(Copies(ir), c =>
            c.Dst is Variable { Name: "s_t$tag" } &&
            c.Src is Constant { Value: 2 });
        Assert.Contains(
            ir.Functions.SelectMany(fn => fn.Body).OfType<Return>(),
            r => r.Tag is Variable { Name: "S_put.self_t$tag" });
    }
}
