using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A module-level list is program-lifetime storage, but its GC root was scoped
/// to the synthesized `__module_init` that assigns it: the prologue pushed the
/// global's slot onto the shadow stack and the epilogue popped it, so the first
/// collection after init freed or moved the object while the global still
/// pointed at it. The slot then aliased whatever reallocated there -- on the
/// emulator a diagnostic list declared at module level in adafruit_irremote
/// ended up sharing its storage with the live pulse list.
///
/// Global GC_REF slots are permanent roots: the backend seeds them onto the
/// shadow stack once, right after gc_init, before any __module_init runs, and
/// no function ever pushes or pops them. In the IR, a Copy to a global's slot
/// no longer produces a GcRoot/GcUnroot pair.
/// </summary>
public class GlobalGcRootTests
{
    private static ProgramIR GenImported(string moduleSource, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["mod"] = new Parser(new Lexer(moduleSource).Tokenize()).ParseProgram(),
        };
        return new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string>(imported.Keys));
    }

    private static string GlobalSlotName(ProgramIR ir, string suffix) =>
        ir.Globals.Single(g => g.Name.EndsWith(suffix)).Name;

    [Fact]
    public void ModuleInitDoesNotRootOrUnrootAGlobalList()
    {
        var ir = GenImported(
            "from pymcu.types import uint16\n\n" +
            "keep: list[uint16] = list()\n" +
            "keep.append(7)\n",
            "import mod\n" +
            "mod.keep.append(1)\n");
        var init = ir.Functions.Single(f => f.Name.EndsWith("__module_init"));
        string gname = GlobalSlotName(ir, "keep");

        Assert.Equal(DataType.GC_REF, ir.Globals.Single(g => g.Name == gname).Type);
        Assert.DoesNotContain(init.Body, i =>
            i is GcRoot gr && gr.Var is Variable v && v.Name == gname);
        Assert.DoesNotContain(init.Body, i =>
            i is GcUnroot gu && gu.Var is Variable v && v.Name == gname);
    }

    [Fact]
    public void AFunctionAssigningAGlobalListDoesNotRootOrUnrootIt()
    {
        // `global g; g = <list>` inside a function must not take the global's
        // root down on return either -- and must not push a duplicate root on
        // every call, which would grow the shadow stack until it overflowed.
        var ir = GenImported(
            "from pymcu.types import uint16\n\n" +
            "g: list[uint16] = list()\n" +
            "h: list[uint16] = list()\n" +
            "def fill() -> None:\n" +
            "    global g\n" +
            "    g = h\n" +
            "    g.append(3)\n",
            "import mod\n" +
            "mod.fill()\n" +
            "mod.g.append(4)\n");
        var fill = ir.Functions.Single(f => f.Name.EndsWith("fill"));
        string gname = GlobalSlotName(ir, "g");

        Assert.DoesNotContain(fill.Body, i =>
            i is GcRoot gr && gr.Var is Variable v && v.Name == gname);
        Assert.DoesNotContain(fill.Body, i =>
            i is GcUnroot gu && gu.Var is Variable v && v.Name == gname);
    }
}
