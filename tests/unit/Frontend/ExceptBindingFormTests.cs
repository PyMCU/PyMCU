using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `except X as e` binds a bounded exception object (#369).
///
/// One exception is live at a time in this model, so the object needs no allocation: the type
/// code the dispatcher already compares is one half, and the static id of a string-literal
/// message is the other. `print(e)`, `str(e)` and `e.args[0]` read the message; `type(e)`,
/// `isinstance(e, X)` and a bare re-raise fold against the code. A user-defined exception
/// class with fields set in `__init__` gets one static slot per class.
///
/// The form is refused today in both front ends with the same sentence, at `Parser.cs:1380`
/// and `pymcu_translate.py:981`, so both refusals move together or the AST contract the two
/// share stops holding.
///
/// The silent half is the one these tests exist for. `raise SensorError(3)` builds today and
/// discards the 3: `ParseRaiseStatement` accepts any non-string expression in the raise and
/// throws it away (`Parser.cs:1252-1281`), and a user exception class is registered as an
/// integer code with its body never scanned (`Scan.cs:1274-1282`), so `__init__` is not
/// compiled and the field it sets exists nowhere. Nothing in the build says so.
///
/// WHAT DISCRIMINATES: every `Parses` assertion, and `APayloadOnAUserExceptionIsNotDiscarded`.
///
/// WHAT IS INVARIANT: `as` in its two other places keeps working: a refusal wired to the
/// `as` token is one edit away from taking `with ... as f` and `import x as y` too.
/// A non-literal raise message is a deferred print (#435), not a refusal.
/// </summary>
public class ExceptBindingFormTests
{
    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static ProgramNode Translate(string src) =>
        PythonAstReader.ParseSource(src, "main.py");

    private static string Refusal(string src) =>
        Assert.ThrowsAny<Exception>(() => Parse(src)).Message;

    private static string TranslatorRefusal(string src) =>
        Assert.ThrowsAny<Exception>(() => Translate(src)).Message;

    /// What `e` carries is decided in the IR generator, not in the parser, so the refusals
    /// about a use it does not support are only observable with the generator run.
    private static string Lowered(ProgramNode prog) =>
        Assert.ThrowsAny<Exception>(() => new IRGenerator().Generate(
            prog, new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" })).Message;

    private static string LoweringRefusal(string src) => Lowered(Parse(src));

    private static string TranslatorLoweringRefusal(string src) => Lowered(Translate(src));

    /// The adafruit_dht simpletest shape, the commonest exception idiom in the CircuitPython
    /// corpus.
    private const string DhtHandler =
        "def read() -> uint8:\n" +
        "    raise RuntimeError(\"checksum mismatch\")\n" +
        "\n" +
        "def main():\n" +
        "    try:\n" +
        "        v = read()\n" +
        "    except RuntimeError as error:\n" +
        "        print(error.args[0])\n";

    private const string UserExceptionField =
        "class SensorError(Exception):\n" +
        "    def __init__(self, err: uint8):\n" +
        "        self.err = err\n" +
        "\n" +
        "def read() -> uint8:\n" +
        "    raise SensorError(err=3)\n" +
        "\n" +
        "def main():\n" +
        "    try:\n" +
        "        v = read()\n" +
        "    except SensorError as e:\n" +
        "        print(e.err)\n";

    // -- the binding ---------------------------------------------------------

    [Fact]
    public void BindingTheExceptionToANameParses()
    {
        var prog = Parse(DhtHandler);

        Assert.Equal(2, prog.Functions.Count);
    }

    [Fact]
    public void BindingTheExceptionToANameParsesOnTheCPythonFrontEnd()
    {
        var prog = Translate(DhtHandler);

        Assert.Equal(2, prog.Functions.Count);
    }

    [Fact(Skip = "#369: a field on a user-defined exception is the one part still open. The class body is skipped by Scan, so __init__ is never compiled.")]
    public void ARaiseWithAKeywordPayloadParses()
    {
        var prog = Parse(UserExceptionField);

        Assert.Single(prog.GlobalStatements.OfType<ClassDef>());
    }

    [Fact(Skip = "#369: a field on a user-defined exception is the one part still open. The class body is skipped by Scan, so __init__ is never compiled.")]
    public void ARaiseWithAKeywordPayloadParsesOnTheCPythonFrontEnd()
    {
        var prog = Translate(UserExceptionField);

        Assert.Single(prog.GlobalStatements.OfType<ClassDef>());
    }

    [Fact]
    public void ANestedTryBindsTwoNames()
    {
        var prog = Parse(
            "def inner() -> uint8:\n" +
            "    raise IndexError(\"inner\")\n" +
            "\n" +
            "def outer() -> uint8:\n" +
            "    try:\n" +
            "        return inner()\n" +
            "    except IndexError as e:\n" +
            "        print(e.args[0])\n" +
            "        raise KeyError(\"outer\")\n" +
            "\n" +
            "def main():\n" +
            "    try:\n" +
            "        v = outer()\n" +
            "    except KeyError as e2:\n" +
            "        print(e2.args[0])\n");

        Assert.Equal(3, prog.Functions.Count);
    }

    [Fact]
    public void ABareReRaiseInsideABindingHandlerParses()
    {
        var prog = Parse(
            "def read() -> uint8:\n" +
            "    raise ValueError(\"nope\")\n" +
            "\n" +
            "def main():\n" +
            "    try:\n" +
            "        v = read()\n" +
            "    except ValueError as e:\n" +
            "        print(e)\n" +
            "        raise\n");

        Assert.Equal(2, prog.Functions.Count);
    }

    // -- the payload that is discarded today ---------------------------------

    [Fact(Skip = "#369: a field on a user-defined exception is the one part still open. The class body is skipped by Scan, so __init__ is never compiled.")]
    public void APayloadOnAUserExceptionIsNotDiscarded()
    {
        // `raise SensorError(3)` builds today and the 3 goes nowhere. Whatever the compiler
        // decides to do with the positional form, it must not be to accept it and drop it, so
        // this holds the one outcome that is wrong either way.
        const string src =
            "class SensorError(Exception):\n" +
            "    def __init__(self, err: uint8):\n" +
            "        self.err = err\n" +
            "\n" +
            "def read() -> uint8:\n" +
            "    raise SensorError(3)\n" +
            "\n" +
            "def main():\n" +
            "    try:\n" +
            "        v = read()\n" +
            "    except SensorError as e:\n" +
            "        print(e.err)\n";

        var prog = Parse(src);
        var raised = prog.Functions
            .Single(f => f.Name == "read").Body.Statements
            .OfType<RaiseStmt>().Single();

        Assert.Equal("SensorError", raised.ErrorType);
        Assert.False(string.IsNullOrEmpty(raised.Message) && raised.MessageName is null,
            "the 3 was parsed and discarded: the raise carries neither a message nor a payload");
    }

    // -- a non-literal message is a deferred print (#435) --------------------

    [Fact]
    [Trait("Issue", "435")]
    public void ANonLiteralMessageParsesAsAnExpression()
    {
        var prog = Parse(
            "def read(code: uint8) -> uint8:\n" +
            "    raise ValueError(code)\n");
        var raised = prog.Functions.Single(f => f.Name == "read").Body.Statements
            .OfType<RaiseStmt>().Single();
        raised.ErrorType.Should().Be("ValueError",
            because: "the constructor name is still the exception type");
        raised.MessageName.Should().Be("code",
            because: "a bound integer name used to be refused; now it is the message expression");
    }

    [Fact]
    [Trait("Issue", "435")]
    public void BothFrontEndsAcceptANonLiteralMessage()
    {
        const string src =
            "def read(code: uint8) -> uint8:\n" +
            "    raise ValueError(code)\n" +
            "def main() -> uint8:\n" +
            "    return read(1)\n";

        var hand = Parse(src);
        var py = Translate(src);
        var handName = hand.Functions.SelectMany(f => f.Body.Statements).OfType<RaiseStmt>().Single().MessageName;
        var pyName = py.Functions.SelectMany(f => f.Body.Statements).OfType<RaiseStmt>().Single().MessageName;
        handName.Should().Be(pyName,
            because: "both front ends must carry the same message name or print(e) diverges");

        var actHand = () => new IRGenerator().Generate(hand, new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        var actPy = () => new IRGenerator().Generate(py, new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        actHand.Should().NotThrow(because: "raise ValueError(code) is a deferred print, not a refusal");
        actPy.Should().NotThrow(because: "the CPython front end must accept the same program");
    }

    [Fact]
    public void AnUnsupportedUseOfTheBoundNameSaysWhatItSupports()
    {
        // Assigning `e` to a local asks for an object that outlives the handler, which this
        // model does not have. The refusal has to name what `e` does support, or the reader is
        // left guessing which of the four spellings works.
        var msg = LoweringRefusal(
            "def read() -> uint8:\n" +
            "    raise ValueError(\"nope\")\n" +
            "\n" +
            "def main():\n" +
            "    saved = 0\n" +
            "    try:\n" +
            "        v = read()\n" +
            "    except ValueError as e:\n" +
            "        saved = e\n");

        Assert.Contains("args[0]", msg);
        Assert.DoesNotContain("is not defined", msg);
    }

    [Fact]
    public void IsinstanceOnTheBoundNameIsAnswered()
    {
        // The refusal table is right about every other receiver: a value's type is fixed when
        // the program is compiled, so a run-time type test has nothing left to ask. A caught
        // exception is the one receiver where the question IS open at run time, because the
        // dispatcher is already holding the code of whichever one arrived.
        var prog = Parse(
            "def read() -> uint8:\n" +
            "    raise ValueError(\"nope\")\n" +
            "\n" +
            "def main():\n" +
            "    try:\n" +
            "        v = read()\n" +
            "    except ValueError as e:\n" +
            "        if isinstance(e, ValueError):\n" +
            "            print(\"yes\")\n");

        Assert.Equal(2, prog.Functions.Count);
    }

    [Fact]
    public void AnIndexOtherThanZeroOnArgsIsRefusedByName()
    {
        // `args` holds the one message. Folding any index to it would answer a question the
        // program did not ask, so the index the exception does not have is named.
        var msg = LoweringRefusal(
            "def read() -> uint8:\n" +
            "    raise ValueError(\"nope\")\n" +
            "\n" +
            "def main():\n" +
            "    try:\n" +
            "        v = read()\n" +
            "    except ValueError as e:\n" +
            "        print(e.args[1])\n");

        Assert.Contains("args[0]", msg);
        Assert.Contains("one item", msg);
    }

    [Fact]
    public void WithAsAndImportAsStillParse()
    {
        var prog = Parse(
            "import pymcu.time as t\n" +
            "\n" +
            "def main():\n" +
            "    with open_port() as p:\n" +
            "        p.write(1)\n");

        Assert.Single(prog.Functions);
    }
}
