using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The file half of a diagnostic raised while a DIFFERENT module's code is on the stack.
///
/// Every expansion that lowers a callee's body has to move <c>currentSourcePath</c> with it,
/// or the body's own node lines are reported under the caller's file -- a location that does
/// not exist. EmitInlineFunctionCall already makes the switch; these pin the three places
/// that did not: the super()/Base.m(self) expansion, the dunder expansion, and the pre-scan
/// transforms, which see a bare AST with no file at all.
///
/// Measured on the real programs: `bytearray(17 * len(self.i2c_device))` in
/// adafruit_ht16k33/ht16k33.py reported segments.py:60, `isinstance(index, slice)` in
/// adafruit_pixelbuf.py reported main.py:293, and `yield` inside a method of
/// adafruit_irremote.py reported main.py:226.
/// </summary>
public class ImportedCalleeDiagnosticFileTests
{
    private static PyMCU.Common.CompilerError GenError(
        string entrySrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        var paths = new Dictionary<string, string>();
        foreach (var (name, source) in modules)
        {
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();
            paths[name] = "/proj/src/" + name + ".py";
        }

        return Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => new IRGenerator().Generate(
            new Parser(new Lexer(entrySrc).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string>(paths.Keys),
            modulePaths: paths));
    }

    // A runtime-sized bytearray inside a base-class __init__ reached through super(): the
    // line came from the base method's node while the file stayed the intermediate module's.
    [Fact]
    public void ARuntimeBytearrayInASuperInit_NamesTheFileThatDefinesIt()
    {
        var ex = GenError(
            "from pymcu.types import uint8\n" +
            "from sub import Sub\n" +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "while True:\n" +
            "    s = Sub(GPIOR0.value)\n",
            ("sub",
             "from pymcu.types import uint8\n" +
             "from base import Base\n" +
             "class Sub(Base):\n" +
             "    def __init__(self, n: uint8) -> None:\n" +
             "        super().__init__(n)\n"),
            ("base",
             "from pymcu.types import uint8\n" +
             "class Base:\n" +
             "    def __init__(self, n: uint8) -> None:\n" +
             "        self.buf = bytearray(n * 4)\n"));

        Assert.Equal("/proj/src/base.py", ex.File);
        Assert.Equal(4, ex.Line);
    }

    // isinstance() inside an imported class's __setitem__, expanded at `pix[i] = v` in the
    // entry file: the module's line under the entry file's name.
    [Fact]
    public void AnIsinstanceInsideAnImportedDunder_NamesTheFileThatDefinesIt()
    {
        var ex = GenError(
            "from pymcu.types import uint8\n" +
            "from pix import Pix\n" +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "pix = Pix()\n" +
            "i: uint8 = GPIOR0.value\n" +
            "pix[i] = 3\n",
            ("pix",
             "from typing import Union\n" +
             "from pymcu.types import uint8\n" +
             "class Pix:\n" +
             "    def __init__(self) -> None:\n" +
             "        self.n: uint8 = 0\n" +
             "    def __setitem__(self, index: Union[int, slice], val: uint8) -> None:\n" +
             "        if isinstance(index, slice):\n" +
             "            self.n = 0\n" +
             "        self.n = val\n"));

        Assert.Equal("/proj/src/pix.py", ex.File);
        Assert.Equal(7, ex.Line);
        Assert.Equal(12, ex.Column);
    }

    // `yield` in a method is refused by AsyncTransform, which runs on a bare ProgramNode
    // before the scan exists: the error kept the module's line and fell back to the entry
    // file's name. The diagnostic names the METHOD, whose def is at gen.py line 3.
    [Fact]
    public void AYieldInsideAnImportedMethod_NamesTheFileThatDefinesIt()
    {
        var ex = GenError(
            "from pymcu.types import uint8\n" +
            "from gen import Decoder\n" +
            "d = Decoder()\n",
            ("gen",
             "from pymcu.types import uint8\n" +
             "class Decoder:\n" +
             "    def read(self, n: uint8) -> uint8:\n" +
             "        yield n\n"));

        Assert.Equal("/proj/src/gen.py", ex.File);
        Assert.Equal(3, ex.Line);
        Assert.Equal(5, ex.Column);
    }

    // A module the loader has no path for cannot be named. Falling back to the entry file
    // there is the same fiction with a different mechanism, so the entry file keeps it.
    [Fact]
    public void AModuleWithNoRecordedPath_KeepsTheEntryFile()
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["gen"] = new Parser(new Lexer(
                "class Decoder:\n" +
                "    def read(self):\n" +
                "        yield 1\n").Tokenize()).ParseProgram(),
        };

        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => new IRGenerator().Generate(
            new Parser(new Lexer("from gen import Decoder\nd = Decoder()\n").Tokenize())
                .ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: ["gen"]));

        Assert.Null(ex.File);
    }
}
