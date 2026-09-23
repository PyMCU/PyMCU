using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `Optional[X]`, `X | None` and `Union[X, None]` are read as X.
///
/// The refusal they used to get was about storage: a union of two types has no width the two
/// share. That is true of two REAL types and false of this one, because None-ness is already a
/// COMPILE-TIME property in this compiler. A parameter bound to None is recorded, `p is None`
/// folds, `if p:` decides its branch without lowering the other side, and a field assigned None
/// keeps that knowledge per instance. So `Optional[X]` needs X's width and nothing else.
///
/// It was the largest single blocker of the twenty Adafruit libraries measured unmodified,
/// stopping nine of them.
///
/// The one position where the knowledge runs out is a RETURN: the caller asked for a number and
/// the path answers None. That is refused, in one sentence, at the return.
/// </summary>
public class OptionalIsTheTypeTests
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

    private static string ParamType(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram().Functions[0].Params[0].Type ?? "";

    // ── The three spellings read as the type ─────────────────────────────────────────────

    [Theory]
    [InlineData("Optional[uint8]")]
    [InlineData("Union[uint8, None]")]
    [InlineData("Union[None, uint8]")]
    [InlineData("uint8 | None")]
    [InlineData("None | uint8")]
    [InlineData("typing.Optional[uint8]")]
    public void EverySpellingOfOptionalUint8_IsUint8(string ann)
    {
        Assert.Equal("uint8", ParamType($"def take(v: {ann}):\n    pass\n"));
    }

    [Fact]
    public void OptionalOfABracketedFormKeepsTheBracketedForm()
    {
        Assert.Equal("uint8[4]", ParamType("def take(v: Optional[uint8[4]]):\n    pass\n"));
    }

    [Fact]
    public void AUnionOfTwoRealTypesIsStillRefused()
    {
        // RFC 0009 phase 2 lifted the refusal for members a tag can carry (scalars and
        // None): the argument run grows a tag byte that picks between them. What still
        // has no answer is a member the tag cannot carry -- a buffer travels as a name,
        // not a payload byte.
        foreach (string ann in new[] { "uint8 | bytearray", "Union[uint8, bytearray]" })
            Assert.Contains("union type annotation", Refusal(
                $"def take(v: {ann}) -> uint8:\n    return 1\n\ndef main():\n    y = take(1)\n"));
    }

    [Fact]
    public void AUnionOfThreeWithOneNoneIsStillRefused()
    {
        // Same: None is a member the tag carries, but bytearray is not.
        Assert.Contains("union type annotation", Refusal(
            "def take(v: Union[uint8, bytearray, None]) -> uint8:\n    return 1\n\n" +
            "def main():\n    y = take(1)\n"));
    }

    // ── None-ness decides the branch, and the other side leaves no code ──────────────────

    private const string Gain =
        Hdr +
        "@inline\n" +
        "def gain(a: uint8, b: Optional[uint8] = None) -> uint8:\n" +
        "    if b is None:\n" +
        "        return a\n" +
        "    return a + b\n\n";

    [Fact]
    public void AnOmittedOptionalArgumentFoldsToTheNonePath()
    {
        // The None path's value reaches the port. The other side is lowered as the dead code it
        // is and removed later; what this pins is which side was TAKEN, because taking the
        // wrong one is silent.
        var ir = Gen(Gain + "def main():\n    GPIOR0.value = gain(3)\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 3);
    }

    [Fact]
    public void APassedOptionalArgumentFoldsTheOtherWay()
    {
        var ir = Gen(Gain + "def main():\n    GPIOR0.value = gain(3, 4)\n");
        Assert.Empty(ir.Functions.SelectMany(f => f.Body).OfType<Binary>());
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 7);
    }

    [Fact]
    public void AFieldKeepsTheKnowledgePerInstance()
    {
        // Two instances of one class, one with a pin and one without, and the method that tests
        // the field folds differently in each. This is the shape every optional-peripheral
        // driver has, and the reason the knowledge has to survive being stored.
        var ir = Gen(Hdr +
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self, pin: Optional[uint8] = None):\n" +
            "        self._pin = pin\n" +
            "    @inline\n" +
            "    def go(self) -> uint8:\n" +
            "        if self._pin is not None:\n" +
            "            return self._pin\n" +
            "        return 99\n" +
            "d = Dev()\n" +
            "e = Dev(7)\n" +
            "def main():\n" +
            "    GPIOR0.value = d.go()\n" +
            "    GPIOR1.value = e.go()\n");

        var copies = ir.Functions.SelectMany(f => f.Body).OfType<Copy>().ToList();
        // The instance with no pin never gets a field written: the branch that would read it
        // was not lowered.
        Assert.DoesNotContain(copies, c => c.Dst is Variable v && v.Name == "d__pin");
        Assert.Contains(copies, c => c.Dst is Variable v && v.Name == "e__pin");
    }

    [Fact]
    public void ABareNoneBindingIsTrackedAndCleared()
    {
        // `x = None` then `if x is None: x = 5`. The binding used to record nothing, so the
        // test answered false, the assignment that gives `x` a value was dropped, and the read
        // below it reached a name nothing had written.
        var ir = Gen(Hdr +
            "def main():\n" +
            "    x = None\n" +
            "    if x is None:\n" +
            "        x = 5\n" +
            "    GPIOR0.value = x\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 5);
    }

    [Fact]
    public void ANoneInOneScopeDoesNotMakeTheSameNameNoneInAnother()
    {
        // The trap this nearly fell into. The None oracle's last resort is the BARE name, so
        // asking it whether a field's source is None made every `self.x = value` in the program
        // a None field as soon as any scope bound `value` to None -- and a ZCA bit index
        // recorded that way stops being a constant. Measured on fixtures/compat-cp-bitbangio-i2c,
        // which went from 272 bytes to "runtime bit index is only supported on a chip register".
        var ir = Gen(Hdr +
            "@inline\n" +
            "def maybe(value: Optional[uint8] = None) -> uint8:\n" +
            "    if value is None:\n" +
            "        return 1\n" +
            "    return 2\n" +
            "class Reg:\n" +
            "    @inline\n" +
            "    def __init__(self, value: uint8):\n" +
            "        self._bit = value\n" +
            "    @inline\n" +
            "    def get(self) -> uint8:\n" +
            "        return self._bit\n" +
            "r = Reg(5)\n" +
            "def main():\n" +
            "    GPIOR0.value = maybe()\n" +
            "    GPIOR1.value = r.get()\n");

        // The field kept its constant: 5 reaches the port, not a read of an unwritten slot.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 5);
    }

    // ── The one position where the knowledge runs out ────────────────────────────────────

    [Fact]
    public void AnInlineOptionalReturnWithAProvableArgStillFolds()
    {
        // `f(0)`: `a == 0` folds inside the expansion, so the result is a compile-time
        // None and storing it into a port is the ordinary "None is not a value" error --
        // no tag exists for a None the compiler can see (RFC 0009 decision 2).
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "@inline\n" +
            "def f(a: uint8) -> Optional[uint8]:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    return a\n\n" +
            "def main():\n" +
            "    GPIOR0.value = f(0)\n"));
        Assert.DoesNotContain("PyMCU reads Optional", ex.Message);
    }

    [Fact]
    public void AReturnOfNoneOnAPathTheCallerCannotReachIsFine()
    {
        // The guard folded, so the `return None` is not in the program the caller gets. That is
        // the whole point of deciding None-ness at compile time, and it is why the sentence
        // names the RETURN rather than the annotation.
        Assert.NotNull(Gen(Hdr +
            "@inline\n" +
            "def f(a: uint8) -> Optional[uint8]:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    return a\n\n" +
            "def main():\n" +
            "    GPIOR0.value = f(3)\n"));
    }

    [Fact]
    public void AReturnOfNoneFromAFunctionWithNoDeclaredResultIsUnchanged()
    {
        // An unannotated `def` gets a result slot allocated speculatively, and its implicit
        // return is the ordinary "returns nothing". Refusing that would refuse every class
        // whose __init__ delegates to a base.
        Assert.NotNull(Gen(
            "class Base:\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "class Child(Base):\n" +
            "    def __init__(self, v: uint8):\n" +
            "        super().__init__(v)\n" +
            "def main():\n" +
            "    c = Child(3)\n" +
            "    y = c.v\n"));
    }

    // ── The same sentence, when the callee is a REAL subroutine ─────────────────────────
    //
    // The checks above run while an @inline body is expanded into its caller. A plain `def`
    // lowers to a shared subroutine instead, where `return None` used to leave `ret` with
    // nothing in the return register: the caller read whatever R24 held (RFC 0009, decision
    // 5). Both paths refuse the shape now, in the same words.

    [Fact]
    public void ARealSubroutineReturnOfNoneOnAReachedPathIsRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "def read(a: uint8) -> uint8:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    return a\n\n" +
            "x: uint8 = read(GPIOR0.value)\n"));

        Assert.Contains("this return gives None at run time", ex.Message);
        Assert.Contains("'read'", ex.Message);
        Assert.Contains("declared to return uint8", ex.Message);
        Assert.Contains("Optional[uint8]", ex.Message);
        Assert.Equal(6, ex.Line);
        Assert.Equal(16, ex.Column);
    }

    [Fact]
    public void ARealSubroutineBareReturnOnAReachedPathIsRefused()
    {
        // `return` is `return None` to Python, so the same sentence covers it. Without the
        // literal the node carries no column, and the diagnostic lands on the line.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "def read(a: uint8) -> uint8:\n" +
            "    if a == 0:\n" +
            "        return\n" +
            "    return a\n\n" +
            "x: uint8 = read(GPIOR0.value)\n"));

        Assert.Contains("this return gives None at run time", ex.Message);
        Assert.Contains("'read'", ex.Message);
        Assert.Equal(6, ex.Line);
    }

    [Fact]
    public void ARealSubroutineReturnOfNoneInsideAFinallyIsRefused()
    {
        // A `return` inside a `try`/`finally` takes the early lowering that runs the pending
        // finally first, which is why the check sits ahead of every lowering in VisitReturn.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "def read(a: uint8) -> uint8:\n" +
            "    try:\n" +
            "        return None\n" +
            "    finally:\n" +
            "        GPIOR0.value = 0\n" +
            "    return a\n\n" +
            "x: uint8 = read(GPIOR1.value)\n"));

        Assert.Contains("this return gives None at run time", ex.Message);
        Assert.Equal(6, ex.Line);
    }

    [Fact]
    public void AnInlineBareReturnOnAReachedPathIsRefused()
    {
        // The @inline check judged only the literal: a bare `return` on a reached path left
        // the caller's result slot just as unwritten, so the shared check covers it.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Hdr +
            "@inline\n" +
            "def f(a: uint8) -> uint8:\n" +
            "    if a == 0:\n" +
            "        return\n" +
            "    return a\n\n" +
            "def main():\n" +
            "    GPIOR0.value = f(GPIOR1.value)\n"));

        Assert.Contains("this return gives None at run time", ex.Message);
        Assert.Contains("'f'", ex.Message);
    }

    [Fact]
    public void ARealSubroutineReturnOfNoneBehindAFoldedGuardCompiles()
    {
        // A real subroutine's parameters are run-time values, so `a == 0` cannot fold inside
        // one; a guard on a compile-time constant can. The arm is never visited and `read`
        // is an ordinary `return a` to its caller.
        var ir = Gen(Hdr +
            "MODE: const = 1\n" +
            "def read(a: uint8) -> uint8:\n" +
            "    if MODE == 0:\n" +
            "        return None\n" +
            "    return a\n\n" +
            "x: uint8 = read(GPIOR0.value)\n");

        var f = ir.Functions.Single(fn => fn.Name == "read");
        Assert.Contains(f.Body, i => i is Return { Value: not NoneVal });
        Assert.DoesNotContain(f.Body, i => i is Return { Value: NoneVal });
    }

    [Fact]
    public void ARealSubroutineDeclaredNoneKeepsItsReturnNone()
    {
        // `-> None` declares nothing to return, so its `return None` is the ordinary
        // "returns nothing", not the lie this check is about.
        Assert.NotNull(Gen(Hdr +
            "def touch(a: uint8) -> None:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    GPIOR0.value = a\n\n" +
            "touch(GPIOR1.value)\n"));
    }

    [Fact]
    public void AnUnannotatedMixedReturnIsAnInferredOptional()
    {
        // RFC 0009 section 6.1, N=2: `return a` mixed with `return None` on an
        // unannotated def is an inferred Optional[uint8] -- the payload joins to the
        // value type and the None ride-along is the tag.
        var ir = Gen(Hdr +
            "def read(a: uint8):\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    return a\n\n" +
            "def main():\n" +
            "    v = read(GPIOR0.value)\n" +
            "    if v is not None:\n" +
            "        GPIOR1.value = v\n");

        var f = ir.Functions.Single(fn => fn.Name == "read");
        Assert.Equal(new List<string> { "uint8", "None" }, f.ReturnMembers);
        Assert.Contains(f.Body, i => i is Return { Tag: not null });
        var call = ir.Functions.SelectMany(fn => fn.Body).OfType<Call>()
            .Single(c => c.FunctionName == "read");
        Assert.NotNull(call.TagDst);
    }

    // ══ RFC 0009 phase 1: the tag byte on the wire and in locals ════════════════════════
    //
    // A `-> Optional[X]` subroutine whose None can arrive at run time transports a
    // member-index tag next to the payload: the Return carries it, the Call receives
    // it, `is None` reads it, `or` picks on it. A compile-time-provable None keeps
    // byte-identical code -- the tag exists only where the run time can produce one.

    private const string OptRead =
        Hdr +
        "def read(a: uint8) -> Optional[uint8]:\n" +
        "    if a == 0:\n" +
        "        return None\n" +
        "    return a\n\n";

    [Fact]
    public void ARealSubroutineOptionalReturnCarriesATag()
    {
        var ir = Gen(OptRead +
            "def main():\n" +
            "    v = read(GPIOR0.value)\n" +
            "    if v is None:\n" +
            "        GPIOR1.value = 0\n" +
            "    else:\n" +
            "        GPIOR1.value = v\n");

        var f = ir.Functions.Single(fn => fn.Name == "read");
        Assert.Equal(new List<string> { "uint8", "None" }, f.ReturnMembers);
        Assert.Contains(f.Body, i => i is Return { Tag: Constant { Value: 1 } });
        Assert.Contains(f.Body, i => i is Return { Tag: Constant { Value: 0 } });

        var call = ir.Functions.SelectMany(fn => fn.Body).OfType<Call>()
            .Single(c => c.FunctionName == "read");
        Assert.NotNull(call.TagDst);
    }

    [Fact]
    public void IsNoneOnALiveOptionalReadsTheTag()
    {
        var ir = Gen(OptRead +
            "def main():\n" +
            "    v = read(GPIOR0.value)\n" +
            "    if v is None:\n" +
            "        GPIOR1.value = 1\n");

        // `v is None` is a tag compare against the None member index, not a payload test.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Op == PyMCU.IR.BinaryOp.Equal && b.Src2 is Constant { Value: 1 });
    }

    [Fact]
    public void OrDefaultSelectsOnTheTag()
    {
        var ir = Gen(OptRead +
            "def main():\n" +
            "    v = read(GPIOR0.value)\n" +
            "    GPIOR1.value = v or 0\n");

        var main = ir.Functions.Single(fn => fn.Name == "main");
        // tag == None jumps to the default; otherwise the payload is kept.
        Assert.Contains(main.Body, i => i is JumpIfEqual { Src2: Constant { Value: 1 } });
        Assert.Contains(main.Body, i => i is JumpIfNotZero);
        // `v or 0` is never None, so its result temp carries no tag onward -- only
        // v's own `v$tag` slot exists.
        Assert.DoesNotContain(main.Body.OfType<Copy>(),
            c => c.Dst is Variable v && v.Name.Contains("tmp_") && v.Name.Contains("$tag"));
    }

    [Fact]
    public void APayloadReadOutsideNarrowingIsRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(OptRead +
            "def main():\n" +
            "    v = read(GPIOR0.value)\n" +
            "    GPIOR1.value = v + 1\n"));
        Assert.Contains("'v' may be None here", ex.Message);
        Assert.Contains("is not None", ex.Message);
    }

    [Fact]
    public void APayloadReadInsideANarrowingArmCompiles()
    {
        var ir = Gen(OptRead +
            "def main():\n" +
            "    v = read(GPIOR0.value)\n" +
            "    if v is not None:\n" +
            "        GPIOR1.value = v + 1\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Op == PyMCU.IR.BinaryOp.Add);
    }

    [Fact]
    public void AnEarlyReturnNarrowsTheFallThrough()
    {
        var ir = Gen(OptRead +
            "def main():\n" +
            "    v = read(GPIOR0.value)\n" +
            "    if v is None:\n" +
            "        return\n" +
            "    GPIOR1.value = v + 1\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Op == PyMCU.IR.BinaryOp.Add);
    }

    [Fact]
    public void AJoinWhereOneArmLeftItNoneStaysOptional()
    {
        // `if v is None: <arm>` -- the arm ran with v None and the fall-through ran
        // with v present, so past the join v is optional again and the read refuses.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(OptRead +
            "def main():\n" +
            "    v = read(GPIOR0.value)\n" +
            "    if v is None:\n" +
            "        GPIOR1.value = 0\n" +
            "    GPIOR1.value = v + 1\n"));
        Assert.Contains("'v' may be None here", ex.Message);
    }

    [Fact]
    public void AProvableOptionalKeepsByteIdenticalCode()
    {
        // `return None` behind a guard that folds: decision 2 says no tag anywhere --
        // the function is an ordinary `return a` to its caller.
        var ir = Gen(Hdr +
            "MODE: const = 1\n" +
            "def read(a: uint8) -> Optional[uint8]:\n" +
            "    if MODE == 0:\n" +
            "        return None\n" +
            "    return a\n\n" +
            "def main():\n" +
            "    v = read(GPIOR0.value)\n" +
            "    GPIOR1.value = v\n");

        var f = ir.Functions.Single(fn => fn.Name == "read");
        Assert.Null(f.ReturnMembers);
        Assert.DoesNotContain(f.Body, i => i is Return { Tag: not null });
        var call = ir.Functions.SelectMany(fn => fn.Body).OfType<Call>()
            .Single(c => c.FunctionName == "read");
        Assert.Null(call.TagDst);
        Assert.DoesNotContain(ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>(),
            c => c.Dst is Variable v && v.Name.Contains("$tag"));
    }
}
