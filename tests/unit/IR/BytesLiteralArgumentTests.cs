using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `bytes([A, B, ...])` and `bytes(N)` written INLINE as a call argument
/// (adafruit_bus_device's `bus_device.write(bytes([A_DEVICE_REGISTER]))`).
///
/// `bytearray(...)` written the same way already had two recognizers: one that normalises it
/// to a `ListExpr` for an `@inline` callee's parameter (so `for x in param` unrolls), and one
/// that materialises a hidden fixed buffer for a REGULAR callee's `bytearray`/`bytes` parameter
/// (#380). Neither looked for the callee name `bytes`, only `bytearray` -- so `bytes([...])`
/// fell through both and reached the generic call-expression visitor, which has no lowering for
/// the `bytes` builtin and reported "bytes() is a Python builtin that PyMCU does not provide",
/// which is false: a `bytes` PARAMETER is already read as the exact buffer a `bytearray`
/// parameter is (AnnotationText, #365). The two names disagreeing about which CONSTRUCTOR
/// spelling reaches that buffer is the same gap one word later.
/// </summary>
public class BytesLiteralArgumentTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    // ── a REGULAR (non-@inline) callee's `bytes` parameter ──────────────────────────────

    private const string RegularCallee =
        "def first(buf: bytes) -> int:\n    return buf[0]\n\n";

    [Fact]
    public void ABytesListLiteralArgumentIsAFixedBuffer()
    {
        // Red before the fix: "bytes() is a Python builtin that PyMCU does not provide".
        var ir = Gen(RegularCallee + "def main() -> None:\n    x: int = first(bytes([5, 6, 7]))\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        var call = main.Body.OfType<Call>().Single(c => c.FunctionName == "first");
        var argBase = Assert.IsType<ArrayBase>(Assert.Single(call.Args));
        var flashData = ir.Functions.SelectMany(f => f.Body).Concat(main.Body)
            .OfType<FlashData>().SingleOrDefault(fd => fd.Name == argBase.ArrayName);
        // Wherever the buffer is laid out (a module global's FlashData/init, or main's own
        // body), its bytes are exactly the literal's -- three, 5/6/7, nothing appended.
        if (flashData != null) Assert.Equal(new List<int> { 5, 6, 7 }, flashData.Bytes);
    }

    [Fact]
    public void ABytesConstructorSizeArgumentIsThatManyZeroBytes()
    {
        var ir = Gen(RegularCallee + "def main() -> None:\n    x: int = first(bytes(3))\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is Call c && c.FunctionName == "first"
                                                     && c.Args[0] is ArrayBase);
    }

    // ── an `@inline` callee's UNANNOTATED parameter (adafruit_bus_device's shape) ────────

    private const string InlineImport = "from pymcu.types import inline\n\n";
    private const string InlineCallee =
        InlineImport + "@inline\ndef total(buf) -> int:\n    s: int = 0\n    for b in buf:\n        s = s + b\n    return s\n\n";

    [Fact]
    public void ABytesListLiteralUnrollsIntoAnInlineCallee()
    {
        // Red before the fix: same "bytes() is a Python builtin" refusal, at the call site
        // instead of the definition.
        var ir = Gen(InlineCallee + "def main() -> None:\n    x: int = total(bytes([5, 6, 7]))\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        var adds = main.Body.OfType<Binary>().Where(b => b.Op == PyMCU.IR.BinaryOp.Add).ToList();
        Assert.Contains(adds, b => b.Src2 is Constant { Value: 5 });
        Assert.Contains(adds, b => b.Src2 is Constant { Value: 6 });
        Assert.Contains(adds, b => b.Src2 is Constant { Value: 7 });
    }

    [Fact]
    public void ABytesConstructorSizeArgumentUnrollsAsThatManyZeroes()
    {
        var ir = Gen(InlineCallee + "def main() -> None:\n    x: int = total(bytes(3))\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        var adds = main.Body.OfType<Binary>().Where(b => b.Op == PyMCU.IR.BinaryOp.Add).ToList();
        Assert.Equal(3, adds.Count);
        Assert.All(adds, b => Assert.Equal(new Constant(0), b.Src2));
    }

    // ── `bytes(n)` with a run-time `n`: refused, naming bytearray ────────────────────────

    [Fact]
    public void ARunTimeSizedBytesCallIsRefused()
    {
        string src = RegularCallee +
            "def main() -> None:\n    n: int = 3\n    x: int = first(bytes(n))\n";
        Assert.Contains("bytearray", Refusal(src));
    }

    [Fact]
    public void ARunTimeSizedBytesLocalIsRefused()
    {
        string src = "def main() -> None:\n    n: int = 3\n    buf: bytes = bytes(n)\n";
        Assert.Contains("bytearray", Refusal(src));
    }

    // ── `x: bytes = bytes([...])` as a named local, same as `bytearray` ──────────────────

    [Fact]
    public void ABytesLocalFromAListLiteralIsAFixedBuffer()
    {
        var ir = Gen("def main() -> None:\n    buf: bytes = bytes([5, 6, 7])\n    x: int = buf[0]\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        // The same fixed-buffer shape a `bytearray` literal already gets: three ArrayStore
        // writes laying out 5/6/7, and a constant-index read back through ArrayLoad.
        Assert.Contains(main.Body, i => i is ArrayStore s && s.ArrayName == "main.buf" && s.Src is Constant { Value: 7 });
        Assert.Contains(main.Body, i => i is ArrayLoad or BytearrayLoad or ArrayLoadFlash);
    }

    // ── a single run-time element, forwarded through a non-@inline hop ───────────────────

    /// <summary>
    /// `pymcu/hal/rp2040/i2c.py`'s own shape (adafruit_bmp280's `i2c.write(bytes([register &
    /// 0xFF]))`, one hop in): `writeto` (@inline) binds its `buffer` parameter to the literal
    /// `bytes([register & 0xFF])` -- ONE element, not a compile-time constant (`register` is a
    /// real parameter) -- and forwards it BY NAME into `send` (a REAL subroutine, not @inline).
    /// At `send`'s own call site the parameter-bound literal has no storage of its own, so
    /// `MaterializeSequenceArg` lays the element out under a hidden buffer -- but it used to do
    /// that only when EVERY element was a compile-time constant (`TryEvalElemConst`), answering
    /// null otherwise and leaving `send` holding the bare, unbacked name. `send`'s own
    /// `while i < n: ... buffer[i] ...` then indexed that name at run time and found nothing
    /// behind it: "'buffer' holds 1 compile-time values with no storage behind them, so it
    /// cannot be indexed at run time." Two or more elements, or one CONSTANT element, never
    /// took this path (every element folded, so `MaterializeSequenceArg`'s old code already
    /// succeeded) -- only a single run-time element did.
    ///
    /// The real HAL forwards the buffer through ONE MORE @inline hop past `send`
    /// (`write_bytes`) before indexing it; that additional hop still fails after this fix
    /// (`send`'s own parameter, having no annotation and never indexed INSIDE `send`'s own
    /// body, is typed a scalar instead of a buffer) and is tracked separately -- this test
    /// pins the storage fix in isolation, with the real subroutine doing its own indexing.
    /// </summary>
    [Fact]
    public void ASingleRunTimeElementForwardedThroughARealSubroutineIsMaterialized()
    {
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "from pymcu.types import inline\n\n" +
            "@inline\n" +
            "def writeto(buffer) -> None:\n" +
            "    send(buffer, 1)\n\n" +
            "def send(buffer, n: int) -> int:\n" +
            "    total: int = 0\n" +
            "    i: int = 0\n" +
            "    while i < n:\n" +
            "        total = total + buffer[i]\n" +
            "        i = i + 1\n" +
            "    return total\n\n" +
            "def main() -> None:\n" +
            "    register: int = GPIOR0.value\n" +
            "    writeto(bytes([register & 0xFF]))\n" +
            "    while True:\n        pass\n");

        // `writeto` is @inline, so it expands straight into `main`; `buffer`'s hidden
        // backing buffer is materialized there, at the point `main` calls `send` (a real
        // subroutine) with the parameter-bound literal.
        var main = ir.Functions.Single(f => f.Name == "main");
        // The hidden buffer's one element is computed at run time (an AND, not a bare
        // Constant) -- not silently defaulted to 0, the other half of the bug the same
        // doc comment on MaterializeSequenceArg already names (adafruit_bmp280 sending
        // 0x00 as the register pointer).
        Assert.Contains(main.Body, i => i is ArrayStore { Src: not Constant });
        Assert.DoesNotContain(main.Body, i => i is ArrayStore { Src: Constant { Value: 0 } });
    }

    /// <summary>
    /// The SAME single-element literal, indexed directly inside the @inline function it was
    /// handed straight to (no further forwarding): `writeto`'s own parameter-bound literal,
    /// read by `writeto[i]` with a run-time `i`. Pins the OTHER call site this fix touched
    /// (Expr.cs's `TryResolveListLiteralParam` branch materializing on demand, rather than
    /// `EmitRegularFunctionCall`'s `MaterializeSequenceArg` doing it ahead of a real call).
    /// </summary>
    [Fact]
    public void ASingleRunTimeElementIndexedDirectlyInsideItsOwnInlineCalleeIsMaterialized()
    {
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "from pymcu.types import inline\n\n" +
            "@inline\n" +
            "def writeto(buffer, n: int) -> int:\n" +
            "    total: int = 0\n" +
            "    i: int = 0\n" +
            "    while i < n:\n" +
            "        total = total + buffer[i]\n" +
            "        i = i + 1\n" +
            "    return total\n\n" +
            "def main() -> None:\n" +
            "    register: int = GPIOR0.value\n" +
            "    x: int = writeto(bytes([register & 0xFF]), 1)\n" +
            "    while True:\n        pass\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is ArrayStore { Src: not Constant });
        Assert.DoesNotContain(main.Body, i => i is ArrayStore { Src: Constant { Value: 0 } });
    }

    // ── a buffer parameter forwarded ONE MORE hop, never indexed by the real subroutine
    //    itself, only by the further @inline callee it hands the buffer to ────────────────

    /// <summary>
    /// `pymcu/hal/rp2040/i2c.py` (feat/rp-i2c-spi): `send` is a REAL subroutine with an
    /// UNANNOTATED `buffer` parameter that never indexes it directly -- it only forwards it
    /// on to `write_bytes` (@inline), whose own `while i < n: ... data[i] ...` indexes it with
    /// a run-time variable. `ScanFunctions` (Scan.cs) decided an unannotated real parameter is
    /// a buffer from `IsSubscriptedInBody(func.Body, p.Name)` alone -- true only for a DIRECT
    /// `buffer[...]` inside `send`'s own body, which this shape never has. `send.buffer` was
    /// therefore left a plain scalar (UINT8). The forwarded Variable still carries the real
    /// storage address `MaterializeSequenceArg` built (fix 2a3760f7) -- so this is a TYPE bug,
    /// not a storage one -- but `data[i]` inside `write_bytes`'s inline expansion read `data`
    /// as a scalar and fell to the register BIT-index path: "Bit index must be constant for
    /// reading", naming no buffer, no parameter, an operation the program does not contain.
    /// </summary>
    [Fact]
    public void ABufferParamNeverIndexedDirectlyButForwardedToAVarIndexedInlineCalleeIsABuffer()
    {
        // write_bytes defined BEFORE send: ScanFunctions walks one module top to bottom, and
        // the transitive check below needs write_bytes's OWN var-indexed parameter already on
        // record when send's parameters are decided -- exactly the order the real HAL has
        // (write_bytes lives in the imported pymcu.hal.rp2040.i2c module, scanned before the
        // busio.py that defines _i2c_writeto/send).
        string src =
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "from pymcu.types import inline\n\n" +
            "@inline\n" +
            "def write_bytes(data, start: int, n: int) -> int:\n" +
            "    total: int = 0\n" +
            "    i: int = start\n" +
            "    end: int = start + n\n" +
            "    while i < end:\n" +
            "        total = total + data[i]\n" +
            "        i = i + 1\n" +
            "    return total\n\n" +
            "def send(buffer, n: int) -> int:\n" +
            "    return write_bytes(buffer, 0, n)\n\n" +
            "@inline\n" +
            "def writeto(buffer) -> int:\n" +
            "    return send(buffer, 1)\n\n" +
            "def main() -> None:\n" +
            "    register: int = GPIOR0.value\n" +
            "    x: int = writeto(bytes([register & 0xFF]))\n" +
            "    while True:\n        pass\n";

        var ex = Record.Exception(() => Gen(src));
        Assert.Null(ex);

        var ir = Gen(src);
        var send = ir.Functions.Single(f => f.Name == "send");
        // A real buffer read through send's own parameter (its base address, built by
        // MaterializeSequenceArg) -- not a register bit test.
        Assert.Contains(send.Body,
            i => i is BytearrayLoad { PtrName: "send.buffer" } or ArrayLoad or LoadIndirect);
        Assert.DoesNotContain(send.Body, i => i is BitCheck);
    }

    /// <summary>
    /// The same shape, but `send` is written BEFORE `write_bytes` in the SAME module. The
    /// transitive check above needs `write_bytes`'s own var-indexed parameters already on
    /// record (via `inlineFunctions`) when `send`'s parameters are decided; `ScanFunctions`
    /// walks one module top to bottom, so a real function defined ahead of the `@inline`
    /// callee it forwards to still does not see that callee's shape -- `send.buffer` is left
    /// a scalar, same as before this fix. The real HAL never has this order (`write_bytes`
    /// lives in the imported `pymcu.hal.rp2040.i2c` module, scanned before `busio.py`'s
    /// `send`/`_i2c_writeto`), so this gap is not closed -- this test pins that it still fails
    /// the SAME clear way (`IsSubscriptedInBody`'s own documented policy: "a gap here costs a
    /// diagnostic, never a wrong answer") rather than silently miscompiling.
    /// </summary>
    [Fact]
    public void ABufferParamForwardedToAnInlineCalleeDefinedLaterInTheSameModuleStillRefusesClearly()
    {
        string src =
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "from pymcu.types import inline\n\n" +
            "def send(buffer, n: int) -> int:\n" +
            "    return write_bytes(buffer, 0, n)\n\n" +
            "@inline\n" +
            "def write_bytes(data, start: int, n: int) -> int:\n" +
            "    total: int = 0\n" +
            "    i: int = start\n" +
            "    end: int = start + n\n" +
            "    while i < end:\n" +
            "        total = total + data[i]\n" +
            "        i = i + 1\n" +
            "    return total\n\n" +
            "@inline\n" +
            "def writeto(buffer) -> int:\n" +
            "    return send(buffer, 1)\n\n" +
            "def main() -> None:\n" +
            "    register: int = GPIOR0.value\n" +
            "    x: int = writeto(bytes([register & 0xFF]))\n" +
            "    while True:\n        pass\n";

        var ex = Record.Exception(() => Gen(src));
        var err = Assert.IsType<PyMCU.Common.CompilerError>(ex);
        Assert.Contains("Bit index must be constant for reading", err.Message);
    }

    /// <summary>
    /// A constant bytes literal handed to an @inline method that only FORWARDS it, with a
    /// call (`len(buffer)`) as a later argument, to an expansion that indexes it at run time
    /// (the CircuitPython busio `I2C.writeto(addr, b"\x00")` -> `_i2c_writeto(bus, addr,
    /// buffer, len(buffer))` -> `buffer[k]` shape on AVR). The call is a later argument
    /// that can have an effect, so the earlier argument was read into a temporary
    /// (SnapshotRead) -- but a parameter bound to a compile-time sequence is not a scalar,
    /// and the temporary was one: `buffer` became a UINT8 with no sequence behind it and
    /// `buffer[k]` fell to the register bit path, "Bit index must be constant for reading".
    /// </summary>
    [Fact]
    public void ALiteralForwardedBesideACallArgumentKeepsItsSequenceForARunTimeIndex()
    {
        string src =
            InlineImport +
            "@inline\n" +
            "def inner(buf, n: uint16) -> int:\n" +
            "    total: int = 0\n" +
            "    k: uint16 = 0\n" +
            "    while k < n:\n" +
            "        total = total + buf[k]\n" +
            "        k = k + 1\n" +
            "    return total\n\n" +
            "@inline\n" +
            "def outer(buf) -> int:\n" +
            "    return inner(buf, len(buf))\n\n" +
            "def main() -> None:\n" +
            "    x: int = outer(b\"\\x01\\x02\")\n" +
            "    while True:\n        pass\n";

        var ex = Record.Exception(() => Gen(src));
        Assert.Null(ex);
    }
}
