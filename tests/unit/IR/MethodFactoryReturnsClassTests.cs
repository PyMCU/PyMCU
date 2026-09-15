using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#421. A method call that returns a class instance -- `led = pcf.get_pin(7)`, an
/// ORDINARY method, not a constructor, whose body builds and returns a `DigitalInOut` --
/// never tagged the assignment target with a class, so the next method call on it
/// (`led.switch_to_output(...)`) mangled to an undefined `led_switch_to_output`. Confirmed
/// this is about the receiver's missing class, not the keyword argument: the same call fails
/// identically written positionally, and even fails chained with no assignment at all.
///
/// A second, narrower bug found while reducing this: the analogous single-field "factory
/// handle" tracking (a bare, non-@inline free function returning a single-field class)
/// qualified the assignment target as `currentFunction + "." + name` instead of
/// `SlotInstanceKey(name)` -- the same mismatch #390 fixed for `with`-bound names.
///
/// A third bug, found only once the fix above was measured against the real library:
/// adafruit_pcf8574's PCF8574 and DigitalInOut are both defined in a DIFFERENT module from
/// the program that calls get_pin(). The class name inside get_pin()'s own `return
/// DigitalInOut(...)` is written unqualified, from that module's own point of view, but was
/// read (ResolveCallee) against the CALLING module's prefix -- the same class of cross-module
/// resolution gap #420 fixed for inheritance, here for a factory method's return statement.
/// </summary>
public class MethodFactoryReturnsClassTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static ProgramIR GenWithModule(string mainSrc, string moduleName, string moduleSrc)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            [moduleName] = new Parser(new Lexer(moduleSrc).Tokenize()).ParseProgram(),
        };
        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.ProjectModules.Add(moduleName);
        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
    }

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n\n";

    [Fact]
    public void AModuleLevelSingleFieldFreeFunctionFactoryTargetDispatches()
    {
        // The narrower, single-field bug: a non-@inline free function factory, called at
        // module level (so its target is a topLevelInstanceTarget under a bare name, not
        // "main."-qualified). It used to raise UserError("call to undefined function
        // 'led_value' ...").
        var ir = Gen(Preamble +
            "class Pin:\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self._n: uint8 = n\n\n" +
            "    def value(self, v: uint8) -> None:\n" +
            "        GPIOR1.value = self._n + v\n\n" +
            "def get_pin(n: uint8) -> Pin:\n" +
            "    return Pin(n)\n\n" +
            "led = get_pin(7)\n" +
            "led.value(1)\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AMethodThatReturnsAConstructedInstanceTagsItsAssignmentTarget()
    {
        // The reported shape: adafruit_pcf8574's `pcf.get_pin(pin)` reduced -- an ORDINARY
        // method (not a constructor) whose body builds and returns a multi-field instance
        // of a DIFFERENT class. It used to raise UserError("call to undefined function
        // 'led_switch_to_output' ...").
        var ir = Gen(Preamble +
            "class Pin:\n" +
            "    def __init__(self, n: uint8, owner: \"Owner\") -> None:\n" +
            "        self._n: uint8 = n\n" +
            "        self._owner: Owner = owner\n\n" +
            "    def switch_to_output(self, value: bool = False) -> None:\n" +
            "        self._owner.written = value\n\n" +
            "class Owner:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.written: bool = False\n\n" +
            "    def get_pin(self, n: uint8) -> Pin:\n" +
            "        return Pin(n, self)\n\n" +
            "o = Owner()\n" +
            "led = o.get_pin(7)\n" +
            "led.switch_to_output(True)\n" +
            "GPIOR1.value = o.written\n");

        // Not just "it builds" -- the read has to reach the SAME storage the write went to
        // (o_written), not an untagged, never-written name.
        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(stores, s => s.Src is Variable sv && sv.Name == "o_written");
    }

    [Fact]
    public void TheKeywordArgumentIsNotWhatMakesItWork()
    {
        // Same as the reported shape, but with the exact keyword-argument spelling the
        // issue is filed against (value=True, plus **kwargs on the signature): confirms the
        // fix is not specific to the positional form used in the test above.
        var ir = Gen(Preamble +
            "class Pin:\n" +
            "    def __init__(self, n: uint8, owner: \"Owner\") -> None:\n" +
            "        self._n: uint8 = n\n" +
            "        self._owner: Owner = owner\n\n" +
            "    def switch_to_output(self, value: bool = False, **kwargs) -> None:\n" +
            "        self._owner.written = value\n\n" +
            "class Owner:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.written: bool = False\n\n" +
            "    def get_pin(self, n: uint8) -> Pin:\n" +
            "        return Pin(n, self)\n\n" +
            "o = Owner()\n" +
            "led = o.get_pin(7)\n" +
            "led.switch_to_output(value=True)\n" +
            "GPIOR1.value = o.written\n");

        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(stores, s => s.Src is Variable sv && sv.Name == "o_written");
    }

    [Fact]
    public void ACrossModuleFactoryMethodsClassResolvesInItsOwnModule()
    {
        // adafruit_pcf8574's real shape: PCF8574.get_pin() and its DigitalInOut return type
        // are both defined in a module DIFFERENT from the one that calls get_pin(). It built
        // fine when everything lived in one file (the test above) and failed again, the
        // same "led_switch_to_output" way, once split across two -- the class name inside
        // get_pin()'s own return statement was resolved against the CALLER's module prefix
        // instead of its own.
        const string pinlibMod =
            "from pymcu.types import uint8\n\n" +
            "class Pin:\n" +
            "    def __init__(self, n: uint8, owner: \"Owner\") -> None:\n" +
            "        self._n: uint8 = n\n" +
            "        self._owner: Owner = owner\n\n" +
            "    def switch_to_output(self, value: bool = False) -> None:\n" +
            "        self._owner.written = value\n\n" +
            "class Owner:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.written: bool = False\n\n" +
            "    def get_pin(self, n: uint8) -> Pin:\n" +
            "        return Pin(n, self)\n";

        var ir = GenWithModule(
            "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
            "from pymcu.types import uint8\n" +
            "import pinlib\n\n" +
            "o = pinlib.Owner()\n" +
            "led = o.get_pin(7)\n" +
            "led.switch_to_output(True)\n" +
            "GPIOR1.value = o.written\n",
            "pinlib", pinlibMod);

        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(stores, s => s.Src is Variable sv && sv.Name == "o_written");
    }

    [Fact]
    public void ASingleFieldFactoryWithAnUnannotatedFieldThreadsTheHandleIntoTheFlattenedFieldName()
    {
        // PyMCU#429. Sensor's field is a bare parameter passthrough with NO annotation
        // (`self.base = base`), unlike every other factory test in this file, which types
        // the field explicitly (`self._n: uint8 = n`). DeriveFieldLayout derived an empty
        // type string for such a field, and IsOutlineSafe's scalar check reads "" as "not a
        // plain value", so read() was never outlined; its call site fell back to the
        // ordinary Model A flattened `<inst>_<field>` name -- one this factory-handle
        // assignment (RFC 0001 Model B) never wrote -- and read the field as zero. A
        // GPIOR0-seeded argument, not a literal, so the assertion measures the runtime
        // value thread rather than a compile-time fold ([[medir-no-es-compilar]]).
        var ir = Gen(Preamble +
            "class Sensor:\n" +
            "    def __init__(self, base):\n" +
            "        self.base = base\n\n" +
            "    def read(self):\n" +
            "        return self.base + 1\n\n" +
            "def make_sensor(base: uint8) -> Sensor:\n" +
            "    return Sensor(base)\n\n" +
            "s = make_sensor(GPIOR0.value)\n" +
            "GPIOR1.value = s.read()\n");

        // The handle assignment has to ALSO write the flattened field name: whichever
        // dispatch a later method call on `s` takes (an outlined call, or the ordinary
        // force-inline expansion) reads the value from there.
        var flattenedStores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name == "s_base")
            .ToList();
        Assert.NotEmpty(flattenedStores);
    }
}
