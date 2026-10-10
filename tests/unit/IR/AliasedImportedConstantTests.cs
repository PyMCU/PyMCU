using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `from module import X as Y`, where X is either a plain module-level int constant
/// or a mutable global, read inside an `if`/`elif` that has an IMPLICIT third path
/// (no `else`): busio.py's `_i2c_check(rc)` is exactly this shape --
/// `if rc == _I2C_ABRT_NODEV: ... elif rc != _I2C_OK: ... ` (no else, "do nothing"
/// when neither condition holds).
///
/// ResolveBindingLadder's "imported alias" rung (Core.cs) re-keys a MUTABLE global
/// through AliasOriginal(name) correctly, but only ever checked mutableGlobals for
/// it. A bare `X = 0` at module level is a CONSTANT, stored under its own
/// defining-module spelling in constantVariables/globals, never under the
/// importer's alias -- so a renamed import of one found neither key, fell through
/// every rung to "a name this function's frame binds", and was left an
/// uninitialized local: an alloca with no store anywhere in the program, read as
/// undef. The optimizer was then free to treat the whole comparison as dead and
/// collapse the if/elif to an unconditional "do something" -- not a backend bug,
/// not an optimizer bug, a name that resolved to nothing.
///
/// Measured end to end on RP2040 (PyMCU-i2cspi, feat/rp-i2c-spi): the OPTIMIZED .ll
/// for `_i2c_check` was `store exn_code=9; store exn_flag=1; ret`, `rc` never
/// referenced, raising unconditionally regardless of its argument.
///
/// Each shape below runs for both arch "avr" and arch "arm" -- ResolveBindingLadder
/// has no per-architecture branch anywhere near this rung, so the bug (and the fix)
/// is identical on every backend.
/// </summary>
public class AliasedImportedConstantTests
{
    private static ProgramIR Generate(string mainSrc, string arch, string moduleSrc)
    {
        var mod = new Parser(new Lexer(moduleSrc).Tokenize()).ParseProgram();
        var main = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        return new IRGenerator().Generate(main,
            new Dictionary<string, ProgramNode> { ["consts"] = mod },
            new DeviceConfig { Arch = arch });
    }

    private static (JumpIfNotEqual jne, JumpIfEqual jeq) JumpsIn(ProgramIR ir)
    {
        var check = ir.Functions.Single(f => f.Name == "check");
        var jne = check.Body.OfType<JumpIfNotEqual>().Single();
        var jeq = check.Body.OfType<JumpIfEqual>().Single();
        return (jne, jeq);
    }

    // `if rc == OTHER: pass  elif rc != OK: pass` -- no else, so a name that
    // resolves to nothing has nowhere else to be caught except the comparison
    // itself, exactly like _i2c_check's two raises.
    private static string CheckFn(string otherName, string okName) =>
        "from pymcu.types import uint8\n" +
        $"def check(rc: uint8):\n" +
        $"    if rc == {otherName}:\n" +
        "        pass\n" +
        $"    elif rc != {okName}:\n" +
        "        pass\n";

    // --- Shape 1: a plain module-level constant (busio.py's I2C_OK / I2C_ABRT_NODEV) ---

    private const string ConstModule =
        "OK_VAL = 0\n" +
        "OTHER_VAL = 1\n";

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void Constant_Aliased_FoldsToTheDefiningModulesValue(string arch)
    {
        var ir = Generate(
            "from consts import OK_VAL as _OK, OTHER_VAL as _OTHER\n" +
            CheckFn("_OTHER", "_OK"),
            arch, ConstModule);
        var (jne, jeq) = JumpsIn(ir);

        // Before the fix: Src2 on both was a Variable named "check._OTHER" /
        // "check._OK" that no instruction in the whole program ever wrote.
        Assert.Equal(1, Assert.IsType<Constant>(jne.Src2).Value);
        Assert.Equal(0, Assert.IsType<Constant>(jeq.Src2).Value);
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void Constant_Unaliased_AlreadyFoldedBeforeTheFix_StillDoes(string arch)
    {
        // The control: the same module, the same comparison, imported WITHOUT
        // `as`. This always worked (the local spelling happens to equal the
        // defining module's), so a regression here would mean the fix broke
        // the common case.
        var ir = Generate(
            "from consts import OK_VAL, OTHER_VAL\n" +
            CheckFn("OTHER_VAL", "OK_VAL"),
            arch, ConstModule);
        var (jne, jeq) = JumpsIn(ir);

        Assert.Equal(1, Assert.IsType<Constant>(jne.Src2).Value);
        Assert.Equal(0, Assert.IsType<Constant>(jeq.Src2).Value);
    }

    // --- Shape 2: a MUTABLE global (machine.mem8-style; always worked, because
    // this rung's mutableGlobals check predates the fix). Pinned here so one
    // regression suite covers both shapes the ladder has to tell apart. ---

    private const string MutableModule =
        "from pymcu.types import uint8\n" +
        "OK_VAL: uint8 = 0\n" +
        "OTHER_VAL: uint8 = 1\n" +
        "def bump():\n" +
        "    global OK_VAL, OTHER_VAL\n" +
        "    OK_VAL = 2\n" +
        "    OTHER_VAL = 3\n";

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void MutableGlobal_Aliased_ReadsTheDefiningModulesSlot(string arch)
    {
        var ir = Generate(
            "from consts import OK_VAL as _OK, OTHER_VAL as _OTHER, bump\n" +
            "from pymcu.types import uint8\n" +
            "def check(rc: uint8):\n" +
            "    bump()\n" +
            "    if rc == _OTHER:\n" +
            "        pass\n" +
            "    elif rc != _OK:\n" +
            "        pass\n",
            arch, MutableModule);
        var (jne, jeq) = JumpsIn(ir);

        // A mutable global is read from its SLOT (a Variable named after the
        // defining module), never folded to a Constant -- this was already
        // correct before the fix; it is pinned here as the shape the "imported
        // alias" rung's mutableGlobals check always handled.
        var jneVar = Assert.IsType<Variable>(jne.Src2);
        Assert.Contains("OTHER_VAL", jneVar.Name);
        var jeqVar = Assert.IsType<Variable>(jeq.Src2);
        Assert.Contains("OK_VAL", jeqVar.Name);
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void MutableGlobal_Unaliased_ReadsTheDefiningModulesSlot(string arch)
    {
        var ir = Generate(
            "from consts import OK_VAL, OTHER_VAL, bump\n" +
            "from pymcu.types import uint8\n" +
            "def check(rc: uint8):\n" +
            "    bump()\n" +
            "    if rc == OTHER_VAL:\n" +
            "        pass\n" +
            "    elif rc != OK_VAL:\n" +
            "        pass\n",
            arch, MutableModule);
        var (jne, jeq) = JumpsIn(ir);

        var jneVar = Assert.IsType<Variable>(jne.Src2);
        Assert.Contains("OTHER_VAL", jneVar.Name);
        var jeqVar = Assert.IsType<Variable>(jeq.Src2);
        Assert.Contains("OK_VAL", jeqVar.Name);
    }
}
