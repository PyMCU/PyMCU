using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The message word of `except X as e` is zero for a raise with no argument, and nothing
/// read it that way. `print(e)` handed zero to the flash string writer, which streamed the
/// interrupt vector table ("3" and a control byte), and `e.args[0]` did the same where
/// CPython raises IndexError, args being `()`. `E("")` is not `E()`: its args is `('',)`,
/// so the parser records whether the call had an argument.
/// </summary>
public class ExceptionArgsTests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16, int32\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n" +
        "def uart_write_decimal_i32(v: int32):\n" +
        "    pass\n" +
        "class E1(Exception):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Fn(ProgramIR ir, string name) =>
        ir.Functions.Single(f => f.Name == name).Body;

    private const string Boom =
        "def boom(k: uint8) -> uint8:\n" +
        "    if k == 0:\n" +
        "        raise E1()\n" +
        "    return k\n";

    private static bool NameEndsWith(Val v, string suffix) =>
        v is Variable { Name: var n } && n.EndsWith(suffix, StringComparison.Ordinal);

    [Fact]
    public void PrintOfTheMessageSkipsTheWriterOnZero()
    {
        var main = Fn(Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    print(e)\n"), "main");

        main.Any(i => i is JumpIfZero { Condition: Variable c } && NameEndsWith(c, "_msg")).Should().BeTrue();
    }

    [Fact]
    public void ArgsZeroOfAnArgumentlessRaiseIsAnIndexError()
    {
        var main = Fn(Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    print(e.args[0])\n"), "main");

        main.Any(i => i is JumpIfNotZero { Condition: Variable c } && NameEndsWith(c, "_msg")).Should().BeTrue();
        main.Any(i => i is Copy { Src: FlashStrAddr, Dst: Variable { Name: "__exn_msg" } }).Should().BeTrue(because: "the IndexError carries CPython's 'tuple index out of range'");
    }

    [Fact]
    public void PrintOfArgsZeroDoesNotAskTwiceWhetherThereWasAMessage()
    {
        // fix/b1-size: EmitExceptionArgsIndexCheck already raises IndexError on the zero
        // case and never falls through from it, so by the time print(e.args[0]) goes to
        // stream the message, __exn_msg is proven non-zero. A second JumpIfZero guarding
        // that write asked the same question the index check just answered -- 6 bytes on
        // pymcu-circuitpython's `42_except_as_e_args.py` corpus fixture for every build.
        var main = Fn(Gen(
            "try:\n" +
            "    raise E1(\"boom\")\n" +
            "except E1 as e:\n" +
            "    print(e.args[0])\n"), "main");

        main.Count(i => i is JumpIfNotZero { Condition: Variable c } && NameEndsWith(c, "_msg"))
            .Should().Be(1, because: "EmitExceptionArgsIndexCheck asks it once");
        main.Any(i => i is JumpIfZero { Condition: Variable c } && NameEndsWith(c, "_msg"))
            .Should().BeFalse(because: "the value is already proven non-zero past the index check");
    }

    private static string FlashText(ProgramIR ir, string name) =>
        new string(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
            .Single(fd => fd.Name == name).Bytes.TakeWhile(b => b != 0)
            .Select(b => (char)b).ToArray());

    private static List<string> StrWritesOf(ProgramIR ir, string fn) =>
        ir.Functions.Single(f => f.Name == fn).Body
            .OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "uart_write_str.s" } && c.Src is FlashStrAddr)
            .Select(c => FlashText(ir, ((FlashStrAddr)c.Src).Name))
            .ToList();

    [Fact]
    public void PrintArgsStreamsTheTupleBehindTheRuntimeArgumentCheck()
    {
        var ir = Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    print(e.args)\n");
        var main = Fn(ir, "main");

        // `e.args` is `()` for the argument-less raise and `('x',)` for one with a
        // message -- which of the two is a run-time fact, so the parens are written
        // unconditionally and the element plus its one-element comma behind
        // `__exn_msg != 0` (the bound name's snapshot word).
        main.Any(i => i is JumpIfNotZero { Condition: Variable c } && NameEndsWith(c, "_msg"))
            .Should().BeTrue(because: "() or (msg,) is decided by the message word at run time");
        StrWritesOf(ir, "main").Should().Contain("(").And.Contain(",").And.Contain(")");
    }

    [Fact]
    public void LenArgsIsTheRuntimeArgumentCount()
    {
        var ir = Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    x = len(e.args)\n" +
            "    print(x)\n");
        var main = Fn(ir, "main");

        main.Any(i => i is Binary { Op: PyMCU.IR.BinaryOp.NotEqual, Src1: Variable s }
                     && NameEndsWith(s, "_msg"))
            .Should().BeTrue(because: "args is () or (msg,) -- len is 0 or 1, read off the word");
    }

    [Fact]
    public void ArgsBoundToANameIsRefused()
    {
        var act = () => Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    t = e.args\n");
        act.Should().Throw<PyMCU.Common.CompilerError>()
            .WithMessage("*the tuple of the raise's argument*");
    }

    [Fact]
    public void ForOverArgsIsRefused()
    {
        var act = () => Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    for a in e.args:\n" +
            "        print(a)\n");
        act.Should().Throw<PyMCU.Common.CompilerError>()
            .WithMessage("*no sequence here to step through*");
    }

    [Fact]
    public void AWritePastTheRaiseDoesNotRetypeTheRaiseArgument()
    {
        // The local-binding scan used to take the last TEXTUAL write in the whole
        // function body, so the unreachable `code: uint8 = 5` retyped the "oops"
        // binding the raise actually sees -- the raise stored a dead word in
        // __exn_arg0 and e.args[0] answered it instead of the message.
        var ir = Gen(
            "def fail():\n" +
            "    code = \"oops\"\n" +
            "    raise E1(code)\n" +
            "    code: uint8 = 5\n" +
            "try:\n" +
            "    fail()\n" +
            "except E1 as e:\n" +
            "    print(e)\n");
        var fail = Fn(ir, "fail");

        fail.Any(i => i is Copy { Src: FlashStrAddr, Dst: Variable { Name: "__exn_msg" } })
            .Should().BeTrue(because: "code is bound to \"oops\" at the raise, so the message word stores the text");
        fail.Any(i => i is Copy { Dst: Variable { Name: "__exn_arg0" } })
            .Should().BeFalse(because: "the dead uint8 write must not turn the raise into an int-arg one");
    }

    [Fact]
    public void AReachableIntBindingStillAnswersArgsItem()
    {
        // The same rule read forward: `code: uint8 = 9` reaches the raise, so the
        // argument is the integer and e.args[0] is allowed -- the dead string write
        // after the raise must not retype it back into a refusal either.
        var ir = Gen(
            "def fail():\n" +
            "    code: uint8 = 9\n" +
            "    raise E1(code)\n" +
            "    code = \"oops\"\n" +
            "try:\n" +
            "    fail()\n" +
            "except E1 as e:\n" +
            "    v = e.args[0]\n" +
            "    print(v)\n");
        var fail = Fn(ir, "fail");

        fail.Any(i => i is Copy { Dst: Variable { Name: "__exn_arg0" } })
            .Should().BeTrue(because: "the reachable uint8 binding makes the raise an int-arg one");
    }

    [Fact]
    public void ArgsItemReadOnAStringMessageStillRefuses()
    {
        // Once the raise is classified right, `v = e.args[0]` is a value read of a
        // string message -- which has no integer to answer and must refuse rather
        // than hand back whatever the arg word happens to hold.
        var act = () => Gen(
            "def fail():\n" +
            "    code = \"oops\"\n" +
            "    raise E1(code)\n" +
            "    code: uint8 = 5\n" +
            "try:\n" +
            "    fail()\n" +
            "except E1 as e:\n" +
            "    v = e.args[0]\n");
        act.Should().Throw<PyMCU.Common.CompilerError>()
            .WithMessage("*integer argument*");
    }

    [Fact]
    public void ANestedHandledRaiseStillAnswersTheOuterArgumentsItem()
    {
        // outer.args[0] read the single live __exn_arg0 word, which a nested
        // raise-and-catch under the handler had overwritten with the inner raise's
        // argument -- the read answered 2 where the caught OSError carried 5. The
        // matched handler now snapshots the delivered record into __exh_<id>_* words
        // and the value read answers the snapshot.
        var main = Fn(Gen(
            "try:\n" +
            "    raise OSError(5)\n" +
            "except OSError as outer:\n" +
            "    try:\n" +
            "        raise ValueError(2)\n" +
            "    except ValueError:\n" +
            "        pass\n" +
            "    v = outer.args[0]\n" +
            "    print(v)\n"), "main");

        main.Any(i => i is Copy { Src: Variable { Name: "__exn_arg0" },
                                  Dst: Variable s } && NameEndsWith(s, "_arg0")
                       && s.Name.StartsWith("__exh_", StringComparison.Ordinal))
            .Should().BeTrue(because: "the matched handler saves the delivered record");
        main.Any(i => i is Copy { Src: Variable s, Dst: Variable }
                       && s.Name.StartsWith("__exh_", StringComparison.Ordinal)
                       && s.Name.EndsWith("_arg0", StringComparison.Ordinal))
            .Should().BeTrue(because: "the bound read answers the snapshot word, not the shared one");
    }

    [Fact]
    public void ANestedHandledRaiseRestoresTheRecordForTheArgsPrinter()
    {
        // print(outer.args[0]) goes through the deferred-print printer, which replays
        // the LIVE record -- so the read restores this binding's snapshot into the
        // shared words before the call, or the printer would replay the inner raise.
        var main = Fn(Gen(
            "try:\n" +
            "    raise OSError(5)\n" +
            "except OSError as outer:\n" +
            "    try:\n" +
            "        raise ValueError(2)\n" +
            "    except ValueError:\n" +
            "        pass\n" +
            "    print(outer.args[0])\n"), "main");

        int printerCall = main.FindIndex(i => i is Call { FunctionName: "__pymcu_print_exn_args" });
        printerCall.Should().BeGreaterOrEqualTo(0);
        main.Take(printerCall).Any(i => i is Copy
                { Src: Variable s, Dst: Variable { Name: "__exn_arg0" } }
                && s.Name.StartsWith("__exh_", StringComparison.Ordinal))
            .Should().BeTrue(because: "the snapshot is restored before the printer replays it");
    }

    [Fact]
    public void AReraiseInsideAHandlerRestoresTheCaughtRecord()
    {
        // A bare `raise` propagates the record of the exception being re-raised; with a
        // nested handled raise in between, the shared words hold the inner record and the
        // unwind must restore the handler's snapshot before it signals outward.
        var main = Fn(Gen(
            "try:\n" +
            "    try:\n" +
            "        raise OSError(5)\n" +
            "    except OSError as outer:\n" +
            "        try:\n" +
            "            raise ValueError(2)\n" +
            "        except ValueError:\n" +
            "            pass\n" +
            "        raise\n" +
            "except OSError:\n" +
            "    print(\"caught\")\n"), "main");

        int reraise = main.FindIndex(i => i is SignalError
            { Code: Variable { Name: var n } } && n.StartsWith("__exn_code_", StringComparison.Ordinal));
        reraise.Should().BeGreaterOrEqualTo(0, because: "the bare raise re-signals the saved code");
        main.Take(reraise).Any(i => i is Copy
                { Src: Variable s, Dst: Variable d }
                && s.Name.StartsWith("__exh_", StringComparison.Ordinal)
                && d.Name.StartsWith("__exn_", StringComparison.Ordinal))
            .Should().BeTrue(because: "the snapshot restores the caught record ahead of the signal");
    }

    [Fact]
    public void AHandlerThatNeverReraisesKeepsNoSnapshot()
    {
        // The snapshot guards the caught record against nested handled raises; an
        // unbound handler whose raises are all fresh (`raise X(...)`) never observes
        // it, so the save is dead weight -- and it was 46 bytes of it on an unmodified
        // CircuitPython fixture that sits exactly at the flash limit. The first try's
        // bound handler keeps the record machinery linked so the absence is measured,
        // not assumed.
        var main = Fn(Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as x:\n" +
            "    print(x.args[0])\n" +
            "try:\n" +
            "    boom(0)\n" +
            "except E1:\n" +
            "    try:\n" +
            "        raise ValueError(2)\n" +
            "    except ValueError:\n" +
            "        raise ValueError(3)\n" +
            "    finally:\n" +
            "        pass\n"), "main");

        main.Any(i => i is Copy { Src: Variable s, Dst: Variable d }
                  && (s.Name.StartsWith("__exh_1", StringComparison.Ordinal)
                      || s.Name.StartsWith("__exh_2", StringComparison.Ordinal)
                      || d.Name.StartsWith("__exh_1", StringComparison.Ordinal)
                      || d.Name.StartsWith("__exh_2", StringComparison.Ordinal)))
            .Should().BeFalse(because: "nothing in either body re-signals a caught record");
        main.Any(i => i is Copy { Src: Variable s, Dst: Variable d }
                  && (s.Name.StartsWith("__exh_0", StringComparison.Ordinal)
                      || d.Name.StartsWith("__exh_0", StringComparison.Ordinal)))
            .Should().BeTrue(because: "the bound handler still snapshots its record");
    }

    [Fact]
    public void AnUnboundHandlerWithABareRaiseStillSnapshots()
    {
        // No `as e`, but the bare `raise` re-signals the record this handler caught:
        // a nested handled raise has overwritten the shared words by then, so the
        // snapshot must stay. The leading bound handler keeps the record fields live.
        var main = Fn(Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as x:\n" +
            "    print(x.args[0])\n" +
            "try:\n" +
            "    try:\n" +
            "        boom(0)\n" +
            "    except E1:\n" +
            "        try:\n" +
            "            raise ValueError(2)\n" +
            "        except ValueError:\n" +
            "            pass\n" +
            "        raise\n" +
            "except E1:\n" +
            "    print(\"caught\")\n"), "main");

        main.Any(i => i is Copy { Src: Variable s, Dst: Variable d }
                 && s.Name.StartsWith("__exh_2", StringComparison.Ordinal)
                 && d.Name.StartsWith("__exn_", StringComparison.Ordinal))
            .Should().BeTrue(because: "the bare raise restores the caught record from the snapshot");
    }

    [Fact]
    public void ABareRaiseInsideAnInlineRestoresTheCatchingHandlerRecord()
    {
        // f3: `rer()` is @inline, so its bare `raise` lowers inside the handler that
        // called it. By then a nested handled ValueError has overwritten the shared
        // record, and the unwind must restore this handler's snapshot before it
        // re-signals -- on main there was no snapshot and the outer read saw 2 where
        // the caught OSError carried 5.
        var main = Fn(Gen(
            "from pymcu.types import inline\n" +
            "@inline\n" +
            "def rer():\n" +
            "    raise\n" +
            "try:\n" +
            "    try:\n" +
            "        raise OSError(5)\n" +
            "    except OSError:\n" +
            "        try:\n" +
            "            raise ValueError(2)\n" +
            "        except ValueError:\n" +
            "            pass\n" +
            "        rer()\n" +
            "except OSError as e:\n" +
            "    v = e.args[0]\n" +
            "    print(v)\n"), "main");

        int reraise = main.FindIndex(i => i is SignalError
            { Code: Variable { Name: var n } } && n.StartsWith("__exn_code_", StringComparison.Ordinal));
        reraise.Should().BeGreaterOrEqualTo(0, because: "the inlined bare raise re-signals the saved code");
        main.Take(reraise).Any(i => i is Copy
                { Src: Variable s, Dst: Variable d }
                && s.Name.StartsWith("__exh_", StringComparison.Ordinal)
                && d.Name.StartsWith("__exn_", StringComparison.Ordinal))
            .Should().BeTrue(because: "the snapshot of the handler that called rer() is restored first");
    }

    [Fact]
    public void AnEmptyStringArgumentIsAnArgument()
    {
        var ir = Gen(
            "def boom(k: uint8) -> uint8:\n" +
            "    if k == 0:\n" +
            "        raise E1(\"\")\n" +
            "    return k\n" +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    print(e.args[0])\n");

        Fn(ir, "boom").Any(i =>
            i is Copy { Src: FlashStrAddr, Dst: Variable { Name: "__exn_msg" } }).Should().BeTrue(because: "E(\"\") has args ('',): the word points at an empty string, not zero");
    }
}
