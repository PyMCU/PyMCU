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
    [InlineData("Sequence[uint8]")]
    [InlineData("Iterable[uint8]")]
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
}
