using PyMCU.Frontend;
using Xunit;

namespace PyMCU.Tests.Frontend;

/// <summary>
/// A trailing comma in a PARAMETER list, which the two front ends disagreed about.
///
/// `black` writes one on every signature it wraps, so it is not a corner of the grammar: it is
/// the default shape of formatted Python. The CPython bridge accepted it and the hand-written
/// parser said "Expected parameter name", which meant the same file compiled or did not
/// depending on PYMCU_PY_PARSER. That is the defect these tests exist for; the Adafruit
/// libraries that surfaced it are the symptom, not the reason.
///
/// The rule was already in the file four times over -- the tuple return annotation, the
/// @asm_pio keyword list, the bare-`*` branch of ParseParameters itself, and the call-site
/// path that learned it in #228, whose comment describes this same bug one arm away. Only the
/// parameter arm was missed.
///
/// Written as parity assertions rather than "the C# parser accepts it", because agreement is
/// the property that was broken. A future change that makes BOTH front ends reject it would
/// still be a language decision; a change that makes one of them reject it is a bug, and only
/// a parity test catches that.
/// </summary>
public class TrailingCommaParityTests
{
    private static void Csharp(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static void CPython(string src) =>
        PythonAstReader.ParseSource(src, "main.py");

    /// <summary>Parses under both front ends, or fails naming the one that refused.</summary>
    private static void BothAccept(string src)
    {
        var cs = Record.Exception(() => Csharp(src));
        var py = Record.Exception(() => CPython(src));

        Assert.True(cs is null,
            "the hand-written parser refused what the CPython front end accepts: " + cs?.Message);
        Assert.True(py is null,
            "the CPython front end refused what the hand-written parser accepts: " + py?.Message);
    }

    private const string MultiLine =
        "def f(\n" +
        "    a: uint8,\n" +
        "    b: uint8,\n" +
        ") -> uint8:\n" +
        "    return a\n";

    private const string SingleLine =
        "def f(a: uint8, b: uint8,) -> uint8:\n" +
        "    return a\n";

    private const string WithDefault =
        "def f(\n" +
        "    a: uint8,\n" +
        "    b: uint8 = 3,\n" +
        ") -> uint8:\n" +
        "    return a\n";

    private const string KeywordOnlySeparator =
        "def f(\n" +
        "    a: uint8,\n" +
        "    *,\n" +
        "    b: uint8 = 3,\n" +
        ") -> uint8:\n" +
        "    return a\n";

    private const string Method =
        "class C:\n" +
        "    def m(\n" +
        "        self,\n" +
        "        a: uint8,\n" +
        "    ) -> uint8:\n" +
        "        return a\n";

    [Theory]
    [InlineData(nameof(MultiLine))]
    [InlineData(nameof(SingleLine))]
    [InlineData(nameof(WithDefault))]
    [InlineData(nameof(KeywordOnlySeparator))]
    [InlineData(nameof(Method))]
    public void BothFrontEndsAcceptATrailingCommaInAParameterList(string which)
    {
        BothAccept(which switch
        {
            nameof(MultiLine) => MultiLine,
            nameof(SingleLine) => SingleLine,
            nameof(WithDefault) => WithDefault,
            nameof(KeywordOnlySeparator) => KeywordOnlySeparator,
            _ => Method,
        });
    }

    [Fact]
    public void TheCallSiteStillAcceptsItToo()
    {
        // #228's half of the same rule. Kept here so the two arms are asserted together and
        // neither can be tightened alone.
        BothAccept("def f(a: uint8, b: uint8) -> uint8:\n" +
                   "    return a\n" +
                   "def main():\n" +
                   "    x = f(1, 2,)\n");
    }

    [Fact]
    public void AParameterListWithNoParametersIsStillFine()
    {
        BothAccept("def f() -> uint8:\n    return 1\n");
    }

    [Fact]
    public void ALoneCommaIsStillARefusal()
    {
        // The fix must not turn `def f(,)` into a legal empty signature: the trailing comma is
        // only trailing when something precedes it. Both front ends refuse this, and that
        // agreement is as much the point as the acceptance above.
        Assert.NotNull(Record.Exception(() => Csharp("def f(,) -> uint8:\n    return 1\n")));
        Assert.NotNull(Record.Exception(() => CPython("def f(,) -> uint8:\n    return 1\n")));
    }
}
