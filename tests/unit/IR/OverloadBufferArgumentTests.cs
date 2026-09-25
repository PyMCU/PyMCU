using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Regression tests for buffer arguments in overload selection (PyMCU#503).
///
/// A buffer travels to a call as its BASE ADDRESS. Overload selection typed it by asking
/// <c>InferExprType</c>, which for a buffer held in an instance field answers with the
/// ELEMENT type, so a call with a scalar overload on offer took the scalar one and the
/// pointer was truncated to eight bits at the parameter. The same buffer in a local picked
/// the buffer overload, so the source gave the reader nothing to see: only where the
/// argument was stored decided which body ran.
///
/// What came out was measured on an emulated Uno through the TWI lines:
/// <c>i2c.writeto(self.addr, self.temp)</c> put <c>3C 02</c> on the wire where the program
/// says <c>3C 80ae</c>, and the <c>02</c> was <c>lo8(&amp;self.temp)</c> -- moving the buffer
/// to offset 5 and 9 with a module array in front of it moved the byte to <c>05</c> and
/// <c>09</c>. <c>self.buf = bytearray(n)</c> is the shape every MicroPython I2C driver is
/// written in, so the fault reached the bus in a driver that compiled and ran.
///
/// Each overload is given a DIFFERENT observable constant, so these assert which body ran
/// rather than how big the image came out.
/// </summary>
public class OverloadBufferArgumentTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static bool Uses(ProgramIR ir, int marker) =>
        ir.Functions.SelectMany(f => f.Body).Any(i =>
            i is Binary b && (b.Src1 is Constant c1 && c1.Value == marker
                              || b.Src2 is Constant c2 && c2.Value == marker));

    // 7 marks the scalar body, 100 the buffer one.
    private const string Preamble =
        "from pymcu.types import uint8, inline, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n" +
        "@inline\n" +
        "def sink(b: uint8) -> uint8:\n" +
        "    return b + 7\n" +
        "@inline\n" +
        "def sink(b: bytearray) -> uint8:\n" +
        "    return b[0] + 100\n";

    private static void AssertBufferBodyRan(ProgramIR ir)
    {
        Assert.True(Uses(ir, 100), "the bytearray body must run for a buffer argument");
        Assert.False(Uses(ir, 7), "the scalar body must not run for a buffer argument");
    }

    // DISCRIMINATING. This is the reported program: before the fix it ran the scalar body and
    // handed it the buffer's base address.
    [Fact]
    public void ABytearrayFieldTakesTheBytearrayOverload()
    {
        var ir = Gen(Preamble +
            "class D:\n" +
            "    def __init__(self):\n" +
            "        self.temp = bytearray(2)\n" +
            "    def go(self, c: uint8) -> uint8:\n" +
            "        self.temp[0] = c\n" +
            "        return sink(self.temp)\n" +
            "def main():\n" +
            "    d = D()\n" +
            "    G.value = d.go(G.value)\n");

        AssertBufferBodyRan(ir);
    }

    // DISCRIMINATING, and a second spelling of the same storage: a field declared with a size
    // rather than built by bytearray() was wrong in exactly the same way.
    [Fact]
    public void ADeclaredArrayFieldTakesTheBytearrayOverload()
    {
        var ir = Gen(Preamble +
            "class D:\n" +
            "    def __init__(self):\n" +
            "        self.temp: uint8[2] = [0, 0]\n" +
            "    def go(self, c: uint8) -> uint8:\n" +
            "        self.temp[0] = c\n" +
            "        return sink(self.temp)\n" +
            "def main():\n" +
            "    d = D()\n" +
            "    G.value = d.go(G.value)\n");

        AssertBufferBodyRan(ir);
    }

    // DISCRIMINATING. The driver shape: the method that fills the field is itself reached with
    // a buffer parameter, which is how a display library's write() is written.
    [Fact]
    public void AFieldFilledFromABufferParameterTakesTheBytearrayOverload()
    {
        var ir = Gen(Preamble +
            "class D:\n" +
            "    def __init__(self):\n" +
            "        self.temp = bytearray(2)\n" +
            "    def go(self, src: bytearray) -> uint8:\n" +
            "        self.temp[0] = src[0]\n" +
            "        return sink(self.temp)\n" +
            "def main():\n" +
            "    t = bytearray(2)\n" +
            "    t[0] = G.value\n" +
            "    d = D()\n" +
            "    G.value = d.go(t)\n");

        AssertBufferBodyRan(ir);
    }

    // DISCRIMINATING. A field of a field, which is how Adafruit's drivers reach their buffer
    // (`self.i2c_device.buffer`). Only the outermost member was read, so the argument fell to
    // InferExprType one hop further in and took the scalar body even after the direct field
    // stopped doing so.
    [Fact]
    public void ABufferOneObjectDownTakesTheBytearrayOverload()
    {
        var ir = Gen(Preamble +
            "class Inner:\n" +
            "    def __init__(self):\n" +
            "        self.temp = bytearray(2)\n" +
            "class D:\n" +
            "    def __init__(self):\n" +
            "        self.inner = Inner()\n" +
            "    def go(self, c: uint8) -> uint8:\n" +
            "        self.inner.temp[0] = c\n" +
            "        return sink(self.inner.temp)\n" +
            "def main():\n" +
            "    d = D()\n" +
            "    G.value = d.go(G.value)\n");

        AssertBufferBodyRan(ir);
    }

    // DISCRIMINATING, both spellings of one buffer. A class attribute has no per-instance
    // storage: it is registered once under the class-canonical name, which is neither the name
    // the instance spells nor the one the class spells, so both reads took the scalar body.
    [Theory]
    [InlineData("D.BUF")]
    [InlineData("self.BUF")]
    public void AClassAttributeBufferTakesTheBytearrayOverload(string spelling)
    {
        var ir = Gen(Preamble +
            "class D:\n" +
            "    BUF = bytearray(2)\n" +
            "    def go(self, c: uint8) -> uint8:\n" +
            $"        {spelling}[0] = c\n" +
            $"        return sink({spelling})\n" +
            "def main():\n" +
            "    d = D()\n" +
            "    G.value = d.go(G.value)\n");

        AssertBufferBodyRan(ir);
    }

    // DISCRIMINATING, and the one form here that was never about fields: a view over a
    // module-level buffer was wrong in the same way as a view over a field. The window has no
    // name until the argument is visited, so both typed as the element.
    [Theory]
    [InlineData("memoryview(g)")]
    [InlineData("memoryview(g)[0:2]")]
    public void AMemoryviewTakesTheBytearrayOverload(string spelling)
    {
        var ir = Gen(Preamble +
            "g = bytearray(4)\n" +
            "def main():\n" +
            "    g[0] = G.value\n" +
            $"    G.value = sink({spelling})\n");

        AssertBufferBodyRan(ir);
    }

    // DISCRIMINATING. A method that hands its buffer back. The call has not been visited when
    // the overload is chosen, so the declared return type is the only thing that can answer,
    // and it was not being read.
    [Fact]
    public void ABufferReturnedFromAMethodTakesTheBytearrayOverload()
    {
        var ir = Gen(Preamble +
            "class D:\n" +
            "    def __init__(self):\n" +
            "        self.temp = bytearray(2)\n" +
            "    def buf(self) -> bytearray:\n" +
            "        return self.temp\n" +
            "    def go(self, c: uint8) -> uint8:\n" +
            "        self.temp[0] = c\n" +
            "        return sink(self.buf())\n" +
            "def main():\n" +
            "    d = D()\n" +
            "    G.value = d.go(G.value)\n");

        AssertBufferBodyRan(ir);
    }

    // INVARIANT: a method declared to return a number must keep taking the scalar overload.
    // The check above reads declared return types, so a scalar one has to answer no.
    [Fact]
    public void AScalarReturnedFromAMethodStillTakesTheScalarOverload()
    {
        var ir = Gen(Preamble +
            "class D:\n" +
            "    def __init__(self):\n" +
            "        self.temp = bytearray(2)\n" +
            "    def first(self) -> uint8:\n" +
            "        return self.temp[0]\n" +
            "    def go(self, c: uint8) -> uint8:\n" +
            "        self.temp[0] = c\n" +
            "        return sink(self.first())\n" +
            "def main():\n" +
            "    d = D()\n" +
            "    G.value = d.go(G.value)\n");

        Assert.True(Uses(ir, 7), "the scalar body must run for a scalar return");
        Assert.False(Uses(ir, 100), "the bytearray body must not run for a scalar return");
    }

    // THE CASE THAT CANNOT BE DECIDED, and the reason the silence was the defect rather than
    // the choice. When no overload takes a buffer at all, there is nothing to select: what
    // used to happen was that the first key the registry enumerated won and read the buffer's
    // address as a number. The call is refused instead, at the line the caller can change.
    [Fact]
    public void ABufferWithNoBufferOverloadOnOfferIsRefusedAtTheCall()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(
            "from pymcu.types import uint8, inline, ptr\n" +
            "G: ptr[uint8] = ptr(0x3E)\n" +
            "@inline\n" +
            "def sink(b: uint8) -> uint8:\n" +
            "    return b + 7\n" +
            "@inline\n" +
            "def sink(b: float) -> uint8:\n" +
            "    return uint8(b) + 100\n" +
            "class D:\n" +
            "    def __init__(self):\n" +
            "        self.temp = bytearray(2)\n" +
            "    def go(self, c: uint8) -> uint8:\n" +
            "        self.temp[0] = c\n" +
            "        return sink(self.temp)\n" +
            "def main():\n" +
            "    d = D()\n" +
            "    G.value = d.go(G.value)\n"));

        Assert.Contains("takes a buffer as argument 1", ex.Message);
        Assert.Contains("sink", ex.Message);
    }

    // INVARIANT: widening one number into another is not an undecidable case and must keep
    // compiling. Every non-exact selection goes through the same check, so a uint8 argument
    // against a uint16 parameter -- which is what machine.PWM.freq takes in the MicroPython
    // layer -- has to pass it.
    [Fact]
    public void ANarrowerNumberStillWidensIntoAWiderParameter()
    {
        var ir = Gen(
            "from pymcu.types import uint8, uint16, inline, ptr\n" +
            "G: ptr[uint8] = ptr(0x3E)\n" +
            "@inline\n" +
            "def wide(v: uint16) -> uint16:\n" +
            "    return v + 100\n" +
            "@inline\n" +
            "def wide(v: float) -> uint16:\n" +
            "    return uint16(v) + 7\n" +
            "def main():\n" +
            "    n: uint8 = G.value\n" +
            "    G.value = uint8(wide(n))\n");

        Assert.True(Uses(ir, 100), "the uint16 body must run for a uint8 argument");
        Assert.False(Uses(ir, 7), "the float body must not run for a uint8 argument");
    }

    // INVARIANT, not discriminating: a buffer in a local already picked the bytearray overload
    // before the fix. It is the control that made the field the suspect rather than the
    // argument, and it is kept so that teaching the field branch cannot move the local one.
    [Fact]
    public void ALocalBufferStillTakesTheBytearrayOverload()
    {
        var ir = Gen(Preamble +
            "def main():\n" +
            "    t = bytearray(2)\n" +
            "    t[0] = G.value\n" +
            "    G.value = sink(t)\n");

        AssertBufferBodyRan(ir);
    }

    // INVARIANT: a scalar field must keep taking the scalar overload. The fix answers "is this
    // name a buffer?", and a field that is a plain number must answer no.
    [Fact]
    public void AScalarFieldStillTakesTheScalarOverload()
    {
        var ir = Gen(Preamble +
            "class D:\n" +
            "    def __init__(self):\n" +
            "        self.n: uint8 = 0\n" +
            "    def go(self, c: uint8) -> uint8:\n" +
            "        self.n = c\n" +
            "        return sink(self.n)\n" +
            "def main():\n" +
            "    d = D()\n" +
            "    G.value = d.go(G.value)\n");

        Assert.True(Uses(ir, 7), "the scalar body must run for a scalar field");
        Assert.False(Uses(ir, 100), "the bytearray body must not run for a scalar field");
    }
}
