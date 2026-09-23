using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0007's introspection facts substituted at every read site, in MicroPython's
/// u-spellings too: `sys`/`usys`, `os`/`uos`. The compat layer's own files carry
/// IDE placeholders; the table answers the real per-board value.
/// </summary>
public class IntrospectionFoldTests
{
    private static ProgramIR Gen(string src, string stdlib = "micropython", string chip = "atmega328p")
    {
        var config = new DeviceConfig { Arch = "avr", Chip = chip, Stdlib = stdlib };
        var program = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        return new IRGenerator().Generate(program, new Dictionary<string, ProgramNode>(), config);
    }

    private static bool EmitsString(ProgramIR ir, string text) =>
        ir.Functions.SelectMany(f => f.Body).Select(i => i.ToString())
            .Any(s => s.Contains(text));

    private static bool EmitsInt(ProgramIR ir, int value) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Any(c => c.Src is Constant k && k.Value == value) ||
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Any(s => s.Src is Constant k && k.Value == value);

    [Fact]
    public void UsysPlatform_InACondition_FoldsToTheChipFallback()
    {
        var ir = Gen("buf = bytearray(1)\nbuf[0] = 1 if usys.platform == \"atmega328p\" else 0\n");
        Assert.True(EmitsInt(ir, 1));
    }

    [Fact]
    public void SysPlatform_AsAValue_SubstitutesTheTable_NotTheShimPlaceholder()
    {
        // The shim's `platform` global is 'rp2' on every chip; on AVR the table answers
        // the chip name. `p = sys.platform` must reach the same answer a condition does.
        var ir = Gen("import sys\np = sys.platform\n");
        Assert.True(EmitsString(ir, "atmega328p"));
        Assert.False(EmitsString(ir, "rp2"));
    }

    [Fact]
    public void SysImplementationName_AsAValue_SubstitutesTheTable()
    {
        var ir = Gen("import sys\nn = sys.implementation.name\n");
        Assert.True(EmitsString(ir, "micropython"));
    }

    [Fact]
    public void UosUnameField_AsAValue_SubstitutesTheTable()
    {
        var ir = Gen("import uos\ns = uos.uname().sysname\n");
        Assert.True(EmitsString(ir, "atmega328p"));
    }

    [Fact]
    public void OsUnameMachine_AsAValue_SubstitutesTheTable()
    {
        var ir = Gen("import os\nm = os.uname().machine\n");
        Assert.True(EmitsString(ir, "atmega328p"));
    }

    [Fact]
    public void UsysImplementationVersionIndex_AsAValue_SubstitutesTheTable()
    {
        // MicroPython 1.29.0: version[1] answers 29 on this layer.
        var ir = Gen("import usys\nbuf = bytearray(1)\nbuf[0] = usys.implementation.version[1]\n");
        Assert.True(EmitsInt(ir, 29));
    }

    [Fact]
    public void SysImplementationVersion_Bare_IsRefusedAsAnUnanswerableTuple()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            Gen("import sys\nv = sys.implementation.version\n"));
        Assert.Contains("index it", ex.Message);
    }

    [Fact]
    public void UnameField_Unknown_IsRefusedWithTheValidFieldList()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            Gen("import os\nz = os.uname().serial\n"));
        Assert.Contains("sysname, nodename, release, version, machine", ex.Message);
    }

    [Fact]
    public void LinuxNotInUosUname_InAnIfStatement_FoldsTrue()
    {
        // adafruit_dht's guard under the u-spelling: the frontend folds `not in uos.uname()`
        // before IR ever sees the call.
        var ir = Gen(
            "import uos\n" +
            "buf = bytearray(1)\n" +
            "if \"Linux\" not in uos.uname():\n" +
            "    buf[0] = 1\n" +
            "else:\n" +
            "    buf[0] = 0\n");
        Assert.True(EmitsInt(ir, 1));
    }
}
