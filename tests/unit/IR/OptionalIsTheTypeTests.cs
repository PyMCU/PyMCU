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
        // The refusal was right about this one all along: two widths, and nothing to pick.
        foreach (string ann in new[] { "uint8 | bool", "Union[uint8, bool]" })
            Assert.Contains("union type annotation", Refusal(
                $"def take(v: {ann}) -> uint8:\n    return 1\n\ndef main():\n    y = take(1)\n"));
    }

    [Fact]
    public void AUnionOfThreeWithOneNoneIsStillRefused()
    {
        // Dropping the None leaves two, which is the case that has no answer.
        Assert.Contains("union type annotation", Refusal(
            "def take(v: Union[uint8, bool, None]) -> uint8:\n    return 1\n\n" +
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
    public void AReturnOfNoneThatTheCallerReachesIsRefused()
    {
        string msg = Refusal(Hdr +
            "@inline\n" +
            "def f(a: uint8) -> Optional[uint8]:\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    return a\n\n" +
            "def main():\n" +
            "    GPIOR0.value = f(0)\n");
        Assert.Contains("PyMCU reads Optional[X] as X", msg);
        Assert.Contains("this return gives None at run time", msg);
        Assert.Contains("'f'", msg);
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

        Assert.Contains("PyMCU reads Optional[X] as X", ex.Message);
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
    public void AnUnannotatedMixedReturnKeepsTodaysVoidSemantics()
    {
        // Measured, not endorsed: `read` files as void -- return-type inference gives up the
        // moment a `return None` is in the mix -- so the call asks for no result and `x` is
        // a copy of None; the value return never reaches it. That is the same hole as the
        // declared case, but refusing it would refuse the unannotated get/set pattern
        // unmodified CircuitPython code uses (FrameBuffer.pixel answers a value only on the
        // getter path). The pin stands until the language decides what an unannotated mixed
        // return means.
        var ir = Gen(Hdr +
            "def read(a: uint8):\n" +
            "    if a == 0:\n" +
            "        return None\n" +
            "    return a\n\n" +
            "x: uint8 = read(GPIOR0.value)\n");

        var f = ir.Functions.Single(fn => fn.Name == "read");
        Assert.Equal(DataType.VOID, f.ReturnType);
        var call = ir.Functions.SelectMany(fn => fn.Body).OfType<Call>()
            .Single(c => c.FunctionName == "read");
        Assert.IsType<NoneVal>(call.Dst);
    }
}
