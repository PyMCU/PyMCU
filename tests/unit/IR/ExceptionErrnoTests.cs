using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `raise OSError(n)` with an integer n: the arg word `__exn_arg0` carries the integer
/// (that is `e.errno` and `e.args[0]`), the site id picks the rendering --
/// `[Errno n] NAME` for print(e), the bare integer for the args contexts -- and a
/// program that never raises one keeps byte-identical firmware: no arg word store,
/// no args printer, no errno piece.
/// </summary>
public class ExceptionErrnoTests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16, uint32, int32\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n" +
        "def uart_write_decimal_i32(v: int32):\n" +
        "    pass\n" +
        "def uart_write_decimal_u32(v: uint32):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Fn(ProgramIR ir, string name) =>
        ir.Functions.Single(f => f.Name == name).Body;

    private static string FlashText(ProgramIR ir, string name) =>
        new string(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
            .Single(fd => fd.Name == name).Bytes.TakeWhile(b => b != 0)
            .Select(b => (char)b).ToArray());

    [Fact]
    public void AnIntegerRaiseStoresItsArgumentInTheArgWord()
    {
        var main = Fn(Gen(
            "try:\n" +
            "    raise OSError(110)\n" +
            "except OSError as e:\n" +
            "    print(e)\n"), "main");

        main.Any(i => i is Copy { Src: Constant { Value: 110 }, Dst: Variable { Name: "__exn_arg0" } })
            .Should().BeTrue(because: "args[0] IS the arg word, so the integer lands there");
        main.Any(i => i is Copy { Dst: Variable { Name: "__exn_site" } })
            .Should().BeTrue(because: "the site id picks the [Errno n] NAME rendering");
    }

    [Fact]
    public void ErrnoReadsTheArgWord()
    {
        var main = Fn(Gen(
            "try:\n" +
            "    raise OSError(110)\n" +
            "except OSError as e:\n" +
            "    n: int32 = e.errno\n" +
            "    print(n)\n"), "main");

        main.Any(i => i is Copy { Src: Variable { Name: "__exn_arg0" } })
            .Should().BeTrue(because: "e.errno is the arg word the raise stored");
    }

    [Fact]
    public void PrintERendersErrnoNameText()
    {
        var ir = Gen(
            "try:\n" +
            "    raise OSError(110)\n" +
            "except OSError as e:\n" +
            "    print(e)\n");

        ir.Functions.Any(f => f.Name == "__pymcu_print_exn_msg").Should().BeTrue();
        ir.Functions.SelectMany(f => f.Body).OfType<FlashData>().Any(fd =>
                new string(fd.Bytes.TakeWhile(b => b != 0).Select(b => (char)b).ToArray())
                == "[Errno 110] ETIMEDOUT")
            .Should().BeTrue(because: "print(OSError(110)) is MicroPython's [Errno 110] ETIMEDOUT");
    }

    [Fact]
    public void PrintArgsItemCallsTheArgsPrinter()
    {
        var ir = Gen(
            "try:\n" +
            "    raise OSError(110)\n" +
            "except OSError as e:\n" +
            "    print(e.args[0])\n");
        var main = Fn(ir, "main");

        main.Any(i => i is Call { FunctionName: "__pymcu_print_exn_args" })
            .Should().BeTrue(because: "args[0] renders the bare integer, not [Errno n] NAME");
        ir.Functions.Any(f => f.Name == "__pymcu_print_exn_args").Should().BeTrue(
            because: "the printer exists because the program prints an args context");
    }

    [Fact]
    public void ErrnoOnANonOSErrorHandlerIsRefused()
    {
        var act = () => Gen(
            "try:\n" +
            "    raise ValueError(3)\n" +
            "except ValueError as e:\n" +
            "    n: int32 = e.errno\n");
        act.Should().Throw<PyMCU.Common.CompilerError>()
            .WithMessage("*the error code of an OSError*");
    }

    [Fact]
    public void ErrnoWithAStringRaiseTheHandlerCanCatchIsRefused()
    {
        var act = () => Gen(
            "try:\n" +
            "    raise OSError(\"texto\")\n" +
            "except OSError as e:\n" +
            "    n: int32 = e.errno\n");
        act.Should().Throw<PyMCU.Common.CompilerError>()
            .WithMessage("*every raise this handler can catch*");
    }

    [Fact]
    public void ExceptOSErrorAcceptsARaisedTimeoutError()
    {
        var main = Fn(Gen(
            "try:\n" +
            "    raise TimeoutError(110)\n" +
            "except OSError as e:\n" +
            "    print(e)\n"), "main");

        main.Count(i => i is Binary { Op: PyMCU.IR.BinaryOp.Equal }
            && ((Binary)i).Src2 is Constant { Value: 9 or 14 })
            .Should().Be(2, because: "the handler accepts OSError AND the TimeoutError a raise can deliver");
    }

    [Fact]
    public void ExceptOSErrorWithoutARaisedDescendantKeepsTheSingleComparison()
    {
        var main = Fn(Gen(
            "try:\n" +
            "    raise OSError(110)\n" +
            "except OSError as e:\n" +
            "    print(e)\n"), "main");

        main.Count(i => i is Binary { Op: PyMCU.IR.BinaryOp.Equal }
            && ((Binary)i).Src2 is Constant { Value: 9 or 14 })
            .Should().Be(1, because: "a TimeoutError nobody raises costs the handler nothing");
    }

    [Fact]
    public void AProgramWithoutErrnoArgsKeepsNoArgsPrinter()
    {
        var ir = Gen(
            "try:\n" +
            "    raise OSError(\"texto\")\n" +
            "except OSError as e:\n" +
            "    print(e)\n");

        ir.Functions.Any(f => f.Name == "__pymcu_print_exn_args").Should().BeFalse(
            because: "the args printer exists only while an integer OSError is raised");
        ir.Functions.SelectMany(f => f.Body)
            .Any(i => i is Copy { Dst: Variable { Name: "__exn_arg0" } }).Should().BeFalse();
    }

    [Fact]
    public void PrintErrnoDirectlyUsesTheU8PrinterForATableCode()
    {
        // print(e.errno) declares the printed value at the width the raise this handler
        // catches actually needs -- ExceptionArgPrintWidth, not a blanket guess. 110 (and
        // every code this module's own table names) fits a single byte, so the direct
        // print is not owed the 32-bit divider (uart_write_decimal_i32 -> __div32) a
        // wider declared type would call. `n: int32 = e.errno; print(n)` keeps the wide
        // word (ErrnoReadsTheArgWord above), because that path answers the LOCAL's own
        // declared type, not this call's.
        var main = Fn(Gen(
            "try:\n" +
            "    raise OSError(110)\n" +
            "except OSError as e:\n" +
            "    print(e.errno)\n"), "main");

        main.Any(i => i is Call { FunctionName: "uart_write_decimal_u8" })
            .Should().BeTrue(because: "110 fits a byte, and the direct print is not owed a wider writer");
        main.Any(i => i is Call { FunctionName: "uart_write_decimal_u16" or "uart_write_decimal_i32" })
            .Should().BeFalse(because: "neither writer is owed when the byte one already covers the value");
    }

    [Fact]
    public void PrintErrnoDirectlyUsesTheWidePrinterForAConstantOutsideTwoBytes()
    {
        // OSError(70000): CPython and MicroPython both print 70000 whole -- narrowing
        // the print to the table's usual width would silently truncate a code nobody
        // meant as a POSIX errno. ConstIntWidth reads the RAISE's own value, not an
        // assumption that every OSError argument is table-sized.
        var main = Fn(Gen(
            "try:\n" +
            "    raise OSError(70000)\n" +
            "except OSError as e:\n" +
            "    print(e.errno)\n"), "main");

        main.Any(i => i is Copy { Src: Constant { Value: 70000 }, Dst: Variable { Name: "__exn_arg0" } })
            .Should().BeTrue(because: "the arg word always holds the raise's real value, whatever prints it");
        main.Any(i => i is Call { FunctionName: "uart_write_decimal_u32" })
            .Should().BeTrue(because: "70000 does not fit sixteen bits, table code or not");
    }

    [Fact]
    public void PrintErrnoDirectlyUsesTheRaisingSitesDeclaredWidthForARuntimeValue()
    {
        // A runtime-seeded errno (GPIOR0.value forced into an int32 local, so the
        // compiler cannot fold it to a constant) is not a compile-time literal: the
        // print width has to come from the raise site's OWN declared type
        // (RaiseSiteArgWidth / LocalIntBindingWidth), or a genuinely wide runtime code
        // would print truncated instead of whole.
        var main = Fn(Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "def make_wide():\n" +
            "    x: int32 = 70000 + GPIOR0.value\n" +
            "    raise OSError(x)\n" +
            "try:\n" +
            "    make_wide()\n" +
            "except OSError as e:\n" +
            "    print(e.errno)\n"), "main");

        main.Any(i => i is Call { FunctionName: "uart_write_decimal_i32" })
            .Should().BeTrue(because: "the raising function declares x int32, and the seeded value needs every bit of it");
    }

    [Fact]
    public void AnUnknownErrnoCodeRendersTheBareInteger()
    {
        var ir = Gen(
            "try:\n" +
            "    raise OSError(999)\n" +
            "except OSError as e:\n" +
            "    print(e)\n");
        ir.Functions.SelectMany(f => f.Body).OfType<FlashData>().Any(fd =>
                new string(fd.Bytes.TakeWhile(b => b != 0).Select(b => (char)b).ToArray())
                == "999")
            .Should().BeTrue(because: "a code outside the errno table prints as itself, like MicroPython");
    }
}
