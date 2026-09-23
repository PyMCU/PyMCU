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

    // `import X as Y` -- the folds read the binding importedAliases recorded, so an
    // alias answers the same table the literal module name does.

    [Fact]
    public void UsysAliased_Platform_AsAValue_SubstitutesTheTable()
    {
        var ir = Gen("import usys as s\np = s.platform\n");
        Assert.True(EmitsString(ir, "atmega328p"));
        Assert.False(EmitsString(ir, "rp2"));
    }

    [Fact]
    public void UsysAliased_ImplementationName_AsAValue_SubstitutesTheTable()
    {
        var ir = Gen("import usys as s\nn = s.implementation.name\n");
        Assert.True(EmitsString(ir, "micropython"));
    }

    [Fact]
    public void UosAliased_UnameField_AsAValue_SubstitutesTheTable()
    {
        var ir = Gen("import uos as o\nm = o.uname().machine\n");
        Assert.True(EmitsString(ir, "atmega328p"));
    }

    [Fact]
    public void UsysAliased_Platform_InAnIfStatement_FoldsTrue()
    {
        var ir = Gen(
            "import usys as s\n" +
            "buf = bytearray(1)\n" +
            "if s.platform == \"atmega328p\":\n" +
            "    buf[0] = 1\n" +
            "else:\n" +
            "    buf[0] = 0\n");
        Assert.True(EmitsInt(ir, 1));
    }

    [Fact]
    public void UsysAliased_VersionIndex_AsAValue_SubstitutesTheTable()
    {
        var ir = Gen("import usys as s\nbuf = bytearray(1)\nbuf[0] = s.implementation.version[1]\n");
        Assert.True(EmitsInt(ir, 29));
    }

    // `"x" in uname()` bound as a VALUE -- never a condition the frontend folded,
    // so the same table answers it here.

    [Fact]
    public void Rp2InOsUname_AsAValue_FoldsFalseOnAvr()
    {
        var ir = Gen("import os\nbuf = bytearray(1)\nbuf[0] = 1 if \"rp2\" in os.uname() else 0\n");
        Assert.True(EmitsInt(ir, 0));
    }

    [Fact]
    public void LinuxNotInUosUname_AsAValue_FoldsTrue()
    {
        var ir = Gen("import uos\nbuf = bytearray(1)\nbuf[0] = 1 if \"Linux\" not in uos.uname() else 0\n");
        Assert.True(EmitsInt(ir, 1));
    }

    // `from sys import platform` binds the shim's placeholder global -- 'rp2' on
    // every chip -- so it is refused; the member spelling is the answer.

    [Fact]
    public void FromSysImportPlatform_IsRefusedAsAPlaceholderBinding()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen("from sys import platform\n"));
        Assert.Contains("placeholder", ex.Message);
        Assert.Contains("sys.platform", ex.Message);
    }

    [Fact]
    public void FromUsysImportPlatform_IsRefusedTheSameWay()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen("from usys import platform\n"));
        Assert.Contains("placeholder", ex.Message);
    }

    [Fact]
    public void FromSysImportStar_IsRefusedForTheSameReason()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen("from sys import *\n"));
        Assert.Contains("placeholder", ex.Message);
    }

    [Fact]
    public void FromSysImportImplementation_IsAllowed()
    {
        // `implementation` binds the name-only object -- the honest answer.
        var mods = new Dictionary<string, ProgramNode>
        {
            ["sys"] = new Parser(new Lexer(
                "class _Implementation:\n" +
                "    def __init__(self):\n" +
                "        self.name = \"micropython\"\n" +
                "implementation = _Implementation()\n").Tokenize()).ParseProgram(),
        };
        var config = new DeviceConfig { Arch = "avr", Chip = "atmega328p", Stdlib = "micropython" };
        var program = new Parser(new Lexer("from sys import implementation\n").Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        new IRGenerator().Generate(program, mods, config); // must not throw
    }

    [Fact]
    public void FromSysImportPlatform_NoStdlib_DoesNotRaiseThePlaceholderError()
    {
        // With no compat layer declared, sys is the project's own module: whatever
        // the program does wrong there is not this refusal.
        var act = () => Gen("from sys import platform\n", stdlib: "");
        var ex = Record.Exception(act);
        Assert.True(ex == null || !ex.Message.Contains("placeholder"));
    }

    // `sys.version_info[i]` -- upstream's Python language version, (3, 4, 0) on
    // both flavors. Only the indexed read folds; the bare attribute refuses
    // through member resolution (a tuple global cannot materialize honestly).

    [Fact]
    public void SysVersionInfoIndex_AsAValue_SubstitutesTheLanguageVersion()
    {
        var ir = Gen("import sys\nbuf = bytearray(3)\nbuf[0] = sys.version_info[0]\nbuf[1] = sys.version_info[1]\nbuf[2] = sys.version_info[2]\n");
        Assert.True(EmitsInt(ir, 3));
        Assert.True(EmitsInt(ir, 4));
        Assert.True(EmitsInt(ir, 0));
    }

    [Fact]
    public void UsysVersionInfoIndex_AsAValue_SubstitutesTheLanguageVersion()
    {
        var ir = Gen("import usys\nbuf = bytearray(1)\nbuf[0] = usys.version_info[0]\n");
        Assert.True(EmitsInt(ir, 3));
    }

    [Fact]
    public void SysVersionInfoIndex_Aliased_SubstitutesTheLanguageVersion()
    {
        var ir = Gen("import sys as s\nbuf = bytearray(1)\nbuf[0] = s.version_info[1]\n");
        Assert.True(EmitsInt(ir, 4));
    }

    [Fact]
    public void SysVersionInfoIndex_CircuitPython_AnswersTheSameLanguageVersion()
    {
        // Both flavors report the language version (3, 4, 0), not the
        // distribution version -- that lives in sys.implementation.version.
        var ir = Gen("import sys\nbuf = bytearray(1)\nbuf[0] = sys.version_info[0]\n", stdlib: "circuitpython");
        Assert.True(EmitsInt(ir, 3));
    }

    [Fact]
    public void SysVersionInfoIndex_InAnIfStatement_Folds()
    {
        var ir = Gen(
            "import sys\n" +
            "buf = bytearray(1)\n" +
            "if sys.version_info[0] == 3:\n" +
            "    buf[0] = 1\n" +
            "else:\n" +
            "    buf[0] = 0\n");
        Assert.True(EmitsInt(ir, 1));
    }

    [Fact]
    public void SysVersionInfo_NonLiteralIndex_IsRefusedWithTheIndexingRule()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            Gen("import sys\ni = 0\nbuf = bytearray(1)\nbuf[0] = sys.version_info[i]\n"));
        Assert.Contains("integer literal", ex.Message);
    }

    [Fact]
    public void SysVersionInfoIndex_NoStdlib_IsRefusedAsNoShim()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            Gen("import sys\nbuf = bytearray(1)\nbuf[0] = sys.version_info[0]\n", stdlib: ""));
        Assert.Contains("compat layer", ex.Message);
    }
}
