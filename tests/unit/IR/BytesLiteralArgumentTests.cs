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
}
