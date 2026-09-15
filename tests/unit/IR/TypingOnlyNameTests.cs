using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#367. A name that stands for nothing at run time here is ACCEPTED on a value the
/// program never touches, and REFUSED at the first line that touches it.
///
/// The canonical case is a context manager's `__exit__(self, exc_type, exc_val, exc_tb)`, whose
/// three parameters are annotated with `typing` names and never read. Refusing the signature
/// stopped eight of the twenty Adafruit libraries measured unmodified on the Uno, on three
/// lines of `adafruit_bus_device` that the bodies ignore.
///
/// #278's guarantee is relocated, not weakened: nothing becomes uint8 behind the reader's back.
/// A value with such an annotation gets no width and no storage, and reading it is refused by
/// name, so nothing the program touches has an unknown width and no computation is deleted.
/// </summary>
public class TypingOnlyNameTests
{
    private const string Hdr =
        "from pymcu.types import uint8, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    // Runs the AST through ConditionalCompilator first, the way the real pipeline does
    // (Pipeline/Phases/Processors/ConditionalCompilationProcessor) and the bare Gen() above
    // does not. A `try: ... except ImportError: pass` or `if TYPE_CHECKING:` guard is folded
    // there -- Gen() alone never populates ProgramNode.TypingOnlyNames at all, so a test
    // relying on a GUARDED name (rather than the small hardcoded TypingModuleNames set) needs
    // this one instead.
    private static ProgramIR GenWithGuardFolding(string src)
    {
        var config = new DeviceConfig { Arch = "avr" };
        var program = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        return new IRGenerator().Generate(
            program, new Dictionary<string, ProgramNode>(), config);
    }

    private static string GuardedRefusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => GenWithGuardFolding(src)).Message;

    [Fact]
    public void AnUnreadTypingOnlyParameterIsAccepted()
    {
        // The __exit__ shape, with the three annotations the libraries actually write.
        Assert.NotNull(Gen(Hdr +
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def close(self, exc_type: Optional[Type[type]], exc_val: Optional[BaseException],\n" +
            "              exc_tb: Optional[TracebackType]) -> uint8:\n" +
            "        return 1\n" +
            "d = Dev()\n" +
            "def main():\n" +
            "    GPIOR0.value = d.close(0, 0, 0)\n"));
    }

    [Theory]
    [InlineData("Type[type]")]
    [InlineData("Sequence")]
    [InlineData("Iterable")]
    [InlineData("Any")]
    [InlineData("TracebackType")]
    [InlineData("BaseException")]
    public void EveryTypingOnlySpellingIsAcceptedWhereNothingReadsIt(string ann)
    {
        Assert.NotNull(Gen(Hdr +
            $"@inline\ndef take(v: {ann}) -> uint8:\n    return 1\n\n" +
            "def main():\n    GPIOR0.value = take(0)\n"));
    }

    [Fact]
    public void ReadingItIsRefusedAtTheLineThatReadsIt()
    {
        string msg = Refusal(Hdr +
            "@inline\ndef take(v: Type[type]) -> uint8:\n    return v\n\n" +
            "def main():\n    GPIOR0.value = take(0)\n");
        Assert.Contains("'v'", msg);
        Assert.Contains("Type[type]", msg);
        Assert.Contains("no width", msg);
    }

    [Fact]
    public void ATypoIsStillATypo()
    {
        // The whole point of #278 is that an unreadable annotation never silently becomes
        // uint8. A name that is not a typing name keeps its refusal, at the signature, even on
        // a parameter nothing reads.
        Assert.Contains("unknown type 'Bogus'", Refusal(Hdr +
            "@inline\ndef take(v: Bogus) -> uint8:\n    return 1\n\n" +
            "def main():\n    GPIOR0.value = take(0)\n"));
    }

    [Theory]
    [InlineData("Sequence[uint8]")]
    [InlineData("Iterable[uint8]")]
    [InlineData("List[uint8]")]
    public void ASubscriptedSequenceNameIsTheCompileTimeListForm(string ann)
    {
        // These say what their elements are, so they are the list parameter this compiler
        // already has: the elements bind against the name and `xs[0]`, `for v in xs` and
        // `len(xs)` answer inside the callee as they do outside it. Reading one is NOT
        // refused, which is the difference from a bare `Sequence`.
        var ir = Gen(Hdr +
            $"@inline\ndef total(xs: {ann}) -> uint8:\n" +
            "    n: uint8 = 0\n" +
            "    for v in xs:\n" +
            "        n = n + v\n" +
            "    return n\n\n" +
            "def main():\n    GPIOR0.value = total([1, 2, 3])\n");

        // Three additions, one per element: the elements bound against the name and the `for`
        // unrolled over them, which is what "the compile-time list form" means. Whether the
        // additions then fold is a separate question (#331), and not this test's.
        Assert.Equal(3, ir.Functions.SelectMany(f => f.Body).OfType<Binary>()
            .Count(b => b.Op == PyMCU.IR.BinaryOp.Add));
    }

    [Fact]
    public void ASubscriptedSequenceIsReadAsTheListSpelling()
    {
        string t = new Parser(new Lexer("def take(xs: Sequence[uint8]):\n    pass\n").Tokenize())
            .ParseProgram().Functions[0].Params[0].Type ?? "";
        Assert.Equal("list[uint8]", t);
    }

    [Fact]
    public void AnUnreadParameterCostsNothing()
    {
        var ir = Gen(Hdr +
            "@inline\ndef take(v: Type[type], n: uint8) -> uint8:\n    return n + 1\n\n" +
            "def main():\n    GPIOR0.value = take(0, 5)\n");
        // The call folds to 6, so the ignored parameter left no instruction behind.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 6);
    }

    // ── PyMCU#417: `if TYPE_CHECKING:` populates typingOnlyNames the same way a folded ────
    // ── `try/except ImportError` already does ─────────────────────────────────────────────

    [Fact]
    public void AnIfTypeCheckingGuard_AlsoPopulatesTheTypingOnlyName()
    {
        // The other spelling of the guard: `if TYPE_CHECKING:` around the import, no
        // try/except involved. CompileTimeEvaluator has no notion of TYPE_CHECKING, so
        // before #417 this `if` was left as ordinary (unresolvable) control flow and its
        // import's name never reached TypingOnlyNames at all.
        Assert.NotNull(GenWithGuardFolding(Hdr +
            "if TYPE_CHECKING:\n" +
            "    from circuitpython_typing import ReadableBuffer\n\n" +
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def take(self, buf: ReadableBuffer) -> uint8:\n" +
            "        return 1\n" +
            "d = Dev()\n" +
            "def main():\n    GPIOR0.value = d.take(0)\n"));
    }

    [Fact]
    public void AnIfTypeCheckingGuardedName_ARealMemberRead_IsRefused()
    {
        // A real use still has to be refused -- accepting the annotation is only half of
        // #367/#417's guarantee. The message is not required to be the tailored typing-only
        // sentence here (member access has its own refusal path); what matters is that it
        // does NOT silently compile as an unknown-width read.
        GuardedRefusal(Hdr +
            "if TYPE_CHECKING:\n" +
            "    from circuitpython_typing import ReadableBuffer\n\n" +
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def take(self, buf: ReadableBuffer) -> uint8:\n" +
            "        return buf.something\n" +
            "d = Dev()\n" +
            "def main():\n    GPIOR0.value = d.take(0)\n");
    }
}
