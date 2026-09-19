using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// adafruit_framebuf FrameBuffer.__init__ picks the format with
/// <c>if buf_format == MVLSB: self.format = MVLSBFormat()</c>.
/// When the constructor is expanded with MVLSB, only that branch
/// should run. If every branch is lowered, <c>self.format</c> is
/// the last class and GS2HMSBFormat.fill is compiled.
/// </summary>
public class FormatIfFoldsTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private static ProgramIR GenImported(string framebuf, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["adafruit_framebuf"] = new Parser(new Lexer(framebuf).Tokenize()).ParseProgram(),
        };
        return Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "adafruit_framebuf" }));
    }

    private static Val LastStored(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant { Value: 0 })
            .Select(s => s.Src)
            .Last();

    // A.get / B.get are @inline so the stored byte is the class constant,
    // not a Temporary from an outlined getter.
    private const string Classes =
        "from pymcu.types import uint8\n" +
        "MVLSB: uint8 = 0\n" +
        "OTHER: uint8 = 5\n" +
        "class A:\n" +
        "    def __init__(self):\n" +
        "        self.n: uint8 = 1\n" +
        "    @inline\n" +
        "    def get(self) -> uint8:\n" +
        "        return self.n\n" +
        "class B:\n" +
        "    def __init__(self):\n" +
        "        self.n: uint8 = 2\n" +
        "    @inline\n" +
        "    def get(self) -> uint8:\n" +
        "        return self.n\n";

    private const string FrameBuf =
        Classes +
        "class FrameBuffer:\n" +
        "    def __init__(self, buf_format: uint8 = MVLSB):\n" +
        "        if buf_format == MVLSB:\n" +
        "            self.format = A()\n" +
        "        elif buf_format == OTHER:\n" +
        "            self.format = B()\n" +
        "        else:\n" +
        "            self.format = B()\n";

    [Fact]
    public void AConstFormatArg_PicksOnlyThatBranch()
    {
        var ir = Gen(
            FrameBuf +
            "fb = FrameBuffer(MVLSB)\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.format.get()\n");

        LastStored(ir).Should().Be(new Constant(1),
            because: "fmt == MVLSB must keep A(), not the last elif's B()");
    }

    [Fact]
    public void SuperForwardsAModuleConst_PicksOnlyThatBranch()
    {
        var ir = GenImported(
            FrameBuf,
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "_FRAMEBUF_FORMAT = framebuf.MVLSB\n" +
            "class OLED(framebuf.FrameBuffer):\n" +
            "    def __init__(self):\n" +
            "        super().__init__(_FRAMEBUF_FORMAT)\n" +
            "fb = OLED()\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.format.get()\n");

        LastStored(ir).Should().Be(new Constant(1),
            because: "ssd1306's super().__init__(_FRAMEBUF_FORMAT) is MVLSB, so format is A");
    }

    // The real FrameBuffer.__init__ has buf/width/height/stride=None before
    // the format if. Those extra params used to leave buf_format unbound.
    private const string RealInit =
        Classes +
        "class FrameBuffer:\n" +
        "    def __init__(self, buf, width: uint8, height: uint8, buf_format: uint8 = MVLSB, stride=None):\n" +
        "        self.buf = buf\n" +
        "        self.width = width\n" +
        "        self.height = height\n" +
        "        self.stride = stride\n" +
        "        if self.stride is None:\n" +
        "            self.stride = width\n" +
        "        if buf_format == MVLSB:\n" +
        "            self.format = A()\n" +
        "        elif buf_format == OTHER:\n" +
        "            self.format = B()\n" +
        "        else:\n" +
        "            self.format = B()\n";

    [Fact]
    public void RealInitSignature_PicksOnlyTheMvlsbBranch()
    {
        var ir = Gen(
            RealInit +
            "buf = bytearray([0, 0, 0, 0, 0, 0, 0, 0])\n" +
            "fb = FrameBuffer(buf, 8, 8, MVLSB)\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.format.get()\n");

        LastStored(ir).Should().Be(new Constant(1),
            because: "FrameBuffer(buf, w, h, MVLSB) must keep A() after stride=None");
    }

    [Fact]
    public void SuperForwardsFormatThroughTheRealInitSignature()
    {
        var ir = GenImported(
            RealInit,
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "_FRAMEBUF_FORMAT = framebuf.MVLSB\n" +
            "class OLED(framebuf.FrameBuffer):\n" +
            "    def __init__(self):\n" +
            "        buf = bytearray([0, 0, 0, 0, 0, 0, 0, 0])\n" +
            "        super().__init__(buf, 8, 8, _FRAMEBUF_FORMAT)\n" +
            "fb = OLED()\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.format.get()\n");

        LastStored(ir).Should().Be(new Constant(1),
            because: "super().__init__(buf, w, h, _FRAMEBUF_FORMAT) is the ssd1306 call");
    }

    [Fact]
    public void SuperForwardsFormatThroughWidthHeightParams()
    {
        var ir = GenImported(
            RealInit,
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "_FRAMEBUF_FORMAT = framebuf.MVLSB\n" +
            "class OLED(framebuf.FrameBuffer):\n" +
            "    def __init__(self, width: uint8, height: uint8):\n" +
            "        buf = bytearray([0, 0, 0, 0, 0, 0, 0, 0])\n" +
            "        super().__init__(buf, width, height, _FRAMEBUF_FORMAT)\n" +
            "fb = OLED(8, 8)\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.format.get()\n");

        LastStored(ir).Should().Be(new Constant(1),
            because: "width/height params forwarded with _FRAMEBUF_FORMAT must still pick A()");
    }

    /// <summary>
    /// adafruit_ssd1306 opens with try import framebuf / except ImportError
    /// import adafruit_framebuf. The pipeline folds that try; _FRAMEBUF_FORMAT
    /// must stay a constant so the format if still picks A.
    /// </summary>
    [Fact]
    public void FoldedImportErrorFallback_KeepsTheFormatConst()
    {
        var framebufAst = new Parser(new Lexer(RealInit).Tokenize()).ParseProgram();
        var mainAst = new Parser(new Lexer(
            "from pymcu.types import uint8\n" +
            "try:\n" +
            "    import framebuf\n" +
            "    _FRAMEBUF_FORMAT = framebuf.MONO_VLSB\n" +
            "except ImportError:\n" +
            "    import adafruit_framebuf as framebuf\n" +
            "    _FRAMEBUF_FORMAT = framebuf.MVLSB\n" +
            "class OLED(framebuf.FrameBuffer):\n" +
            "    def __init__(self):\n" +
            "        buf = bytearray([0, 0, 0, 0, 0, 0, 0, 0])\n" +
            "        super().__init__(buf, 8, 8, _FRAMEBUF_FORMAT)\n" +
            "fb = OLED()\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.format.get()\n").Tokenize()).ParseProgram();

        foreach (var st in mainAst.GlobalStatements)
        {
            if (st is not TryStmt tryStmt) continue;
            foreach (var inner in tryStmt.Body)
            {
                if (inner is ImportStmt imp)
                {
                    imp.IsOptional = true;
                    imp.OptionalLoadFailed = true;
                    imp.FallbackImports.Add(new ImportStmt(
                        "adafruit_framebuf", new List<string>()) { ModuleAlias = "framebuf" });
                }
            }
        }

        new ConditionalCompilator(new DeviceConfig { Arch = "avr" }).Process(mainAst);
        var ir = Optimizer.Optimize(new IRGenerator().Generate(
            mainAst,
            new Dictionary<string, ProgramNode> { ["adafruit_framebuf"] = framebufAst },
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "adafruit_framebuf" }));

        LastStored(ir).Should().Be(new Constant(1),
            because: "except ImportError: _FRAMEBUF_FORMAT = framebuf.MVLSB must stay a constant");
    }

    // adafruit_framebuf's unused GS2HMSBFormat.rect calls set_pixel(framebuf, ...).
    // If rect stays an outlined subroutine it is compiled unused and
    // set_pixel sees a numeric framebuf.
    [Fact]
    public void UnusedFormatRectThatForwardsTheBuffer_IsNotCompiled()
    {
        var ir = GenImported(
            "from pymcu.types import uint8\n" +
            "MVLSB: uint8 = 0\n" +
            "class A:\n" +
            "    def fill(framebuf, color: uint8) -> uint8:\n" +
            "        return color\n" +
            "    def set_pixel(framebuf, x: uint8, y: uint8, color: uint8) -> uint8:\n" +
            "        return framebuf.stride\n" +
            "    def rect(framebuf, x: uint8, y: uint8, w: uint8, h: uint8, color: uint8) -> uint8:\n" +
            "        return A.set_pixel(framebuf, x, y, color)\n" +
            "class B:\n" +
            "    def fill(framebuf, color: uint8) -> uint8:\n" +
            "        framebuf.buf = [color for i in range(len(framebuf.buf))]\n" +
            "        return color\n" +
            "    def set_pixel(framebuf, x: uint8, y: uint8, color: uint8) -> uint8:\n" +
            "        return framebuf.stride\n" +
            "    def rect(framebuf, x: uint8, y: uint8, w: uint8, h: uint8, color: uint8) -> uint8:\n" +
            "        return B.set_pixel(framebuf, x, y, color)\n" +
            "class FrameBuffer:\n" +
            "    def __init__(self, buf_format: uint8 = MVLSB):\n" +
            "        self.buf: uint8[8] = [0, 0, 0, 0, 0, 0, 0, 0]\n" +
            "        self.stride: uint8 = 8\n" +
            "        if buf_format == MVLSB:\n" +
            "            self.format = A()\n" +
            "        else:\n" +
            "            self.format = B()\n" +
            "    def fill(self, color: uint8) -> uint8:\n" +
            "        return self.format.fill(self, color)\n",
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "fb = framebuf.FrameBuffer(framebuf.MVLSB)\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.fill(1)\n");

        ir.Should().NotBeNull(
            because: "unused B.rect -> B.set_pixel(framebuf) must not compile a numeric stride");
    }

    private const string FillClasses =
        "from pymcu.types import uint8\n" +
        "MVLSB: uint8 = 0\n" +
        "OTHER: uint8 = 5\n" +
        "class A:\n" +
        "    @inline\n" +
        "    def fill(framebuf, color: uint8) -> uint8:\n" +
        "        return color\n" +
        "class B:\n" +
        "    def fill(framebuf, color: uint8) -> uint8:\n" +
        "        framebuf.buf = [color for i in range(len(framebuf.buf))]\n" +
        "        return color\n";

    // Direct format.fill after the same super, no FrameBuffer.fill wrapper.
    // B.fill's listcomp is the discriminator: get() returning 1 is not, because
    // an unfolded if still runs A() first and leaves n=1 on the shared slot.
    [Fact]
    public void SuperForwardsFormatThenDirectFill_UsesTheFoldedFormat()
    {
        var ir = GenImported(
            FillClasses +
            "class FrameBuffer:\n" +
            "    def __init__(self, buf, width: uint8, height: uint8, buf_format: uint8 = MVLSB, stride=None):\n" +
            "        self.buf = buf\n" +
            "        self.width = width\n" +
            "        self.height = height\n" +
            "        self.stride = stride\n" +
            "        if self.stride is None:\n" +
            "            self.stride = width\n" +
            "        if buf_format == MVLSB:\n" +
            "            self.format = A()\n" +
            "        elif buf_format == OTHER:\n" +
            "            self.format = B()\n" +
            "        else:\n" +
            "            self.format = B()\n",
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "_FRAMEBUF_FORMAT = framebuf.MVLSB\n" +
            "class OLED(framebuf.FrameBuffer):\n" +
            "    def __init__(self):\n" +
            "        buf = bytearray([0, 0, 0, 0, 0, 0, 0, 0])\n" +
            "        super().__init__(buf, 8, 8, _FRAMEBUF_FORMAT)\n" +
            "fb = OLED()\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.format.fill(fb, 1)\n");

        LastStored(ir).Should().Be(new Constant(1),
            because: "fb.format.fill after subclass super().__init__(MVLSB) must be A.fill");
    }

    // ssd1306: after the I2C subclass's super().__init__, FrameBuffer.fill
    // does self.format.fill(self, color). That must expand A.fill, not
    // B.fill's listcomp (the last elif).
    [Fact]
    public void SuperForwardsFormatThenFill_UsesTheFoldedFormat()
    {
        var ir = GenImported(
            "from pymcu.types import uint8\n" +
            "MVLSB: uint8 = 0\n" +
            "OTHER: uint8 = 5\n" +
            "class A:\n" +
            "    @inline\n" +
            "    def fill(framebuf, color: uint8) -> uint8:\n" +
            "        return color\n" +
            "class B:\n" +
            "    def fill(framebuf, color: uint8) -> uint8:\n" +
            "        framebuf.buf = [color for i in range(len(framebuf.buf))]\n" +
            "        return color\n" +
            "class FrameBuffer:\n" +
            "    def __init__(self, buf, width: uint8, height: uint8, buf_format: uint8 = MVLSB, stride=None):\n" +
            "        self.buf = buf\n" +
            "        self.width = width\n" +
            "        self.height = height\n" +
            "        self.stride = stride\n" +
            "        if self.stride is None:\n" +
            "            self.stride = width\n" +
            "        if buf_format == MVLSB:\n" +
            "            self.format = A()\n" +
            "        elif buf_format == OTHER:\n" +
            "            self.format = B()\n" +
            "        else:\n" +
            "            self.format = B()\n" +
            "    def fill(self, color: uint8) -> uint8:\n" +
            "        return self.format.fill(self, color)\n",
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "_FRAMEBUF_FORMAT = framebuf.MVLSB\n" +
            "class OLED(framebuf.FrameBuffer):\n" +
            "    def __init__(self):\n" +
            "        buf = bytearray([0, 0, 0, 0, 0, 0, 0, 0])\n" +
            "        super().__init__(buf, 8, 8, _FRAMEBUF_FORMAT)\n" +
            "fb = OLED()\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.fill(1)\n");

        LastStored(ir).Should().Be(new Constant(1),
            because: "self.format.fill after subclass super().__init__(MVLSB) must be A.fill");
    }
}
