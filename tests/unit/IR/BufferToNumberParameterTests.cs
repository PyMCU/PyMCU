using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A buffer handed to a parameter that holds one number.
///
/// `b"AB"` parses to a list of its byte values, and a buffer travels to a call as its base
/// address. With `def f(x: uint8)` the address landed in `x`: `print(x)` printed 0, and the
/// HAL's `UART.write(b"DE\n")` (whose only overloads take a `uint8` and a `const[str]`) put a
/// single 0x00 on the wire where the program asks for three bytes. The same literal bound to
/// a name first (`z = b"QR"; f(z)`) did not even reach the wire: it failed in the linker with
/// `undefined reference to main_z`, because a literal bound by name is a compile-time sequence
/// with no storage behind the base the call passed.
///
/// Now a buffer is never read as one number: overload selection takes a buffer overload when
/// there is one, a subroutine gets real storage for a named literal, and a parameter that
/// holds a number refuses the buffer at the argument.
/// </summary>
public class BufferToNumberParameterTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.types import uint8, inline, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    // The element the buffer body reads folds with its marker, so the body that ran is named
    // by the constant the program stores.
    private static bool Stores(ProgramIR ir, int value) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Any(c => c.Src is Constant k && k.Value == value);

    private static bool Uses(ProgramIR ir, int marker) =>
        ir.Functions.SelectMany(f => f.Body).Any(i =>
            i is Binary b && (b.Src1 is Constant c1 && c1.Value == marker
                              || b.Src2 is Constant c2 && c2.Value == marker));

    // DISCRIMINATING. The reported program: before the fix it compiled and passed the
    // literal's address to `x`.
    [Fact]
    public void ABytesLiteralToAUint8ParameterOfASubroutineIsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Preamble +
            "def f(x: uint8):\n" +
            "    G.value = x\n" +
            "f(b\"AB\")\n"));
        Assert.Contains("bytes or list literal", ex.Message);
        Assert.Contains("'uint8'", ex.Message);
    }

    // DISCRIMINATING. The same through an @inline callee, which binds the literal as a
    // compile-time sequence and read it as 0.
    [Fact]
    public void ABytesLiteralToAUint8ParameterOfAnInlineFunctionIsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Preamble +
            "@inline\n" +
            "def f(x: uint8):\n" +
            "    G.value = x\n" +
            "f(b\"AB\")\n"));
        Assert.Contains("'uint8'", ex.Message);
    }

    // DISCRIMINATING. Bound to a name first: this one failed in the linker.
    [Fact]
    public void ANamedBytesLiteralToAUint8ParameterIsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Preamble +
            "def f(x: uint8):\n" +
            "    G.value = x\n" +
            "z = b\"QR\"\n" +
            "f(z)\n"));
        Assert.Contains("'z'", ex.Message);
    }

    // DISCRIMINATING. A parameter written without a type and never indexed is one byte, and
    // the address was cut to its low byte.
    [Fact]
    public void ABytesLiteralToAnUnindexedUntypedParameterIsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Preamble +
            "def f(x):\n" +
            "    G.value = x\n" +
            "f(b\"AB\")\n"));
        Assert.Contains("never indexed", ex.Message);
    }

    // DISCRIMINATING. With a buffer overload on offer, a bytes literal takes it: 7 marks the
    // scalar body, and the buffer one stores its second byte plus 100. The scalar overload is declared FIRST, which is the
    // order the HAL's UART declares write() in.
    [Fact]
    public void ABytesLiteralTakesTheBufferOverload()
    {
        var ir = Gen(Preamble +
            "@inline\n" +
            "def sink(b: uint8) -> uint8:\n" +
            "    return b + 7\n" +
            "@inline\n" +
            "def sink(b: bytes) -> uint8:\n" +
            "    return b[1] + 100\n" +
            "G.value = sink(b\"AB\")\n");

        Assert.True(Stores(ir, 66 + 100), "the bytes body must run for a bytes literal");
        Assert.False(Uses(ir, 7), "the uint8 body must not run for a bytes literal");
    }

    // DISCRIMINATING. The same with the literal bound to a name.
    [Fact]
    public void ANamedBytesLiteralTakesTheBufferOverload()
    {
        var ir = Gen(Preamble +
            "@inline\n" +
            "def sink(b: uint8) -> uint8:\n" +
            "    return b + 7\n" +
            "@inline\n" +
            "def sink(b: bytes) -> uint8:\n" +
            "    return b[1] + 100\n" +
            "z = b\"QR\"\n" +
            "G.value = sink(z)\n");

        Assert.True(Stores(ir, 82 + 100), "the bytes body must run for a named bytes literal");
        Assert.False(Uses(ir, 7), "the uint8 body must not run for a named bytes literal");
    }

    // DISCRIMINATING. A named literal passed to a subroutine's buffer parameter gets storage
    // of its own: before, the call passed the base of `main.z`, a label nothing defines.
    [Fact]
    public void ANamedBytesLiteralPassedToASubroutineHasStorage()
    {
        var ir = Gen(Preamble +
            "def f(x: bytes):\n" +
            "    G.value = x[1]\n" +
            "z = b\"QR\"\n" +
            "f(z)\n");

        var call = ir.Functions.Single(f => f.Name == "main").Body
            .OfType<Call>().Single(c => c.FunctionName == "f");
        var arg = Assert.IsType<ArrayBase>(call.Args[0]);
        Assert.NotEqual("main.z", arg.ArrayName);
        Assert.Contains(ir.Functions.Single(f => f.Name == "main").Body.OfType<ArrayStore>(),
            s => s.ArrayName == arg.ArrayName && s.Src is Constant { Value: 82 });
    }

    // Control: a parameter holding a number shadows a module-level bytes literal of the same
    // name. The storage lookups fall back to the bare spelling, and the first cut of this fix
    // refused the HAL's own `uart_write(b)` inside `uart_write_byte_repr(b: uint8)` for any
    // program with a global `b = b"AZ"`.
    [Fact]
    public void ANumberParameterShadowsAModuleLevelBytesLiteral()
    {
        var ir = Gen(Preamble +
            "b = b\"AZ\"\n" +
            "@inline\n" +
            "def put(data: uint8):\n" +
            "    G.value = data\n" +
            "def h(b: uint8):\n" +
            "    put(b)\n" +
            "h(G.value)\n" +
            "h(G.value)\n" +
            "G.value = b[1]\n");
        Assert.Contains(ir.Functions, f => f.Name == "h");
    }

    // DISCRIMINATING. The same shadow through a subroutine call, which was a SILENT fault
    // before any of this: the marshalling step found the module-level array under the bare
    // name and passed its address for the parameter, so `g` printed 0 for 5.
    [Fact]
    public void ANumberParameterPassedOnIsItsValueNotAShadowedArray()
    {
        var ir = Gen(Preamble +
            "b = bytearray(2)\n" +
            "def g(x: uint8):\n" +
            "    G.value = x\n" +
            "def h(b: uint8):\n" +
            "    g(b)\n" +
            "h(G.value)\n" +
            "h(G.value)\n" +
            "G.value = b[1]\n");

        var call = ir.Functions.Single(f => f.Name == "h").Body.OfType<Call>()
            .Single(c => c.FunctionName == "g");
        Assert.IsNotType<ArrayBase>(call.Args[0]);
    }

    // DISCRIMINATING. A store through an @inline callee's buffer-declared parameter bound
    // to a bytes literal: the compile-time sequence has no writable slot, and on the dead
    // name the store compiled to a register bit set -- `b[0] = 65` emitted `bset put.b, 0`
    // and the 65 went nowhere. Refused with the same "no storage" diagnostic the deferred
    // flash-table veto raises on the read path, whether or not a run-time read follows.
    [Fact]
    public void AStoreThroughAnInlineBufferParameterIsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Preamble +
            "@inline\n" +
            "def put(b: bytearray):\n" +
            "    b[0] = 0x55\n" +
            "put(b\"AB\")\n"));
        Assert.Contains("compile-time values with no storage", ex.Message);
    }

    // A run-time read needs no storage of its own: the values are constants and nothing
    // writes them, so the deferred `__cttab` flash table answers `b[i]` -- the binding
    // stays the literal, and the literal's own store must not appear.
    [Fact]
    public void ARunTimeReadOnAnInlineBufferParameterStaysAFlashTable()
    {
        var ir = Gen(Preamble +
            "@inline\n" +
            "def put(b: bytearray):\n" +
            "    i: uint8 = 0\n" +
            "    while i < 2:\n" +
            "        G.value = b[i]\n" +
            "        i = i + 1\n" +
            "put(b\"AB\")\n");

        Assert.DoesNotContain(ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>(),
            s => s.ArrayName.Contains("__inline_bytes_arg"));
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>(),
            t => t.Name.Contains("__cttab") && t.Bytes.Count == 2);
    }

    // A run-time read over an element that is not a compile-time constant folds nothing
    // and the `__cttab` table holds constants only: the literal materializes under a
    // hidden name and the parameter aliases that storage.
    [Fact]
    public void ARunTimeReadOverANonConstantElementGetsStorage()
    {
        var ir = Gen(Preamble +
            "@inline\n" +
            "def put(b: bytearray):\n" +
            "    i: uint8 = 0\n" +
            "    while i < 2:\n" +
            "        G.value = b[i]\n" +
            "        i = i + 1\n" +
            "put([G.value, 2])\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>(),
            s => s.ArrayName.Contains("__inline_bytes_arg"));
    }

    // Control: a buffer parameter keeps taking a bytes literal.
    [Fact]
    public void ABytesLiteralToABytesParameterStillCompiles()
    {
        var ir = Gen(Preamble +
            "def f(x: bytes):\n" +
            "    G.value = x[1]\n" +
            "f(b\"AB\")\n");
        Assert.Contains(ir.Functions.Single(f => f.Name == "main").Body.OfType<Call>(),
            c => c.FunctionName == "f" && c.Args[0] is ArrayBase);
    }
}
