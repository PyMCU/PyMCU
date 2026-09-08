using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#270. A class-body attribute is a compile-time constant when it is ALL CAPS and a
/// run-time global when it is not -- and only the constant half ever carried its value. The
/// other half registered the storage and left the initializer unrun, so
///
///     class Dev:
///         LIMIT = 7      # read 7
///         limit = 7      # read 0
///
/// differed by the spelling of the name, with nothing refused and nothing warned. Python has
/// no rule that a class constant be upper case, so the failing spelling is the one a program
/// written anywhere else uses.
///
/// The module-level spelling of the same program has always worked, because a module's
/// statements run and a class body's do not: these assertions are that the class body's
/// initializer now runs too, in the module that declares it.
///
/// Every assertion here is on the VALUE the generated code stores. A test that only checked
/// the program compiles would have passed for the whole life of the bug.
/// </summary>
public class ClassAttributeInitTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static ProgramIR GenWithModule(string mainSrc, string moduleName, string moduleSrc) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>
                { [moduleName] = new Parser(new Lexer(moduleSrc).Tokenize()).ParseProgram() },
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { moduleName });

    /// <summary>Every constant the program copies into the slot <paramref name="slot"/>.</summary>
    private static List<int> ConstantsStoredInto(ProgramIR ir, string slot) =>
        ir.Functions
            .SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name == slot)
            .Select(c => c.Src)
            .OfType<Constant>()
            .Select(c => c.Value)
            .ToList();

    /// <summary>The declared width of the slot, as every reference to it carries it.</summary>
    private static List<DataType> WidthsOf(ProgramIR ir, string slot) =>
        ir.Functions
            .SelectMany(f => f.Body)
            .SelectMany(i => i switch
            {
                Copy c => new[] { c.Src, c.Dst },
                Binary b => new[] { b.Src1, b.Src2, b.Dst },
                Call c => c.Args.Append(c.Dst).ToArray(),
                _ => Array.Empty<Val>(),
            })
            .OfType<Variable>()
            .Where(v => v.Name == slot)
            .Select(v => v.Type)
            .ToList();

    [Fact]
    public void ALowerCaseClassAttribute_StoresItsDeclaredValue()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    limit = 7\n" +
            "def main() -> uint8:\n" +
            "    return Dev.limit\n");

        Assert.Contains(7, ConstantsStoredInto(ir, "Dev_limit"));
    }

    // The half that always worked, kept so the run-time half cannot be fixed by breaking the
    // constant one: an ALL CAPS attribute folds at its reads and needs no slot at all.
    [Fact]
    public void AnUpperCaseClassAttribute_StaysAFoldedConstant()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    LIMIT = 7\n" +
            "def main() -> uint8:\n" +
            "    return Dev.LIMIT\n");

        Assert.DoesNotContain(ir.Globals, g => g.Name == "Dev_LIMIT");
        Assert.Empty(ConstantsStoredInto(ir, "Dev_LIMIT"));
    }

    // Mixed case is not upper case. `Limit` took the run-time path exactly as `limit` does,
    // which is why the discriminator is stated as "not ALL CAPS" and not as "lower case".
    [Fact]
    public void AMixedCaseClassAttribute_StoresItsDeclaredValue()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    Limit = 7\n" +
            "def main() -> uint8:\n" +
            "    return Dev.Limit\n");

        Assert.Contains(7, ConstantsStoredInto(ir, "Dev_Limit"));
    }

    [Fact]
    public void AnAnnotatedLowerCaseClassAttribute_StoresItsDeclaredValue()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    limit: uint8 = 7\n" +
            "def main() -> uint8:\n" +
            "    return Dev.limit\n");

        Assert.Contains(7, ConstantsStoredInto(ir, "Dev_limit"));
    }

    // An UNANNOTATED attribute is typed from its initializer, like an unannotated module
    // global. StringToDataType("") answers uint8 for everything, and a store scheduled into
    // that would truncate the attribute's own declared value: 300 came back as 44.
    [Fact]
    public void AnUnannotatedClassAttribute_IsWideEnoughForItsOwnValue()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    limit = 300\n" +
            "def main() -> uint16:\n" +
            "    return Dev.limit\n");

        Assert.Contains(300, ConstantsStoredInto(ir, "Dev_limit"));
        Assert.NotEmpty(WidthsOf(ir, "Dev_limit"));
        Assert.All(WidthsOf(ir, "Dev_limit"), t => Assert.True(t.SizeOf() >= 2,
            $"'limit = 300' must not be stored through a one-byte slot, saw {t}"));
    }

    // A class declared in an imported module: the store is compiled under THAT module's
    // prefix, so the slot it writes is the one the module's own reads resolve to.
    [Fact]
    public void AClassAttributeInAnImportedModule_StoresItsDeclaredValue()
    {
        var ir = GenWithModule(
            "from cfg import Dev\n" +
            "def main() -> uint8:\n" +
            "    return Dev.limit\n",
            "cfg",
            "class Dev:\n" +
            "    limit = 7\n");

        Assert.Contains(7, ConstantsStoredInto(ir, "cfg_Dev_limit"));
    }

    // A module whose ONLY statement is the class definition has no module-level statement of
    // its own, and the synthesized init used to be skipped for exactly that reason.
    [Fact]
    public void AModuleThatIsNothingButAClass_StillRunsItsAttributeInit()
    {
        var ir = GenWithModule(
            "from cfg import Dev\n" +
            "def main() -> uint8:\n" +
            "    return Dev.limit\n",
            "cfg",
            "class Dev:\n" +
            "    limit = 7\n" +
            "    LIMIT = 9\n");

        Assert.Contains(7, ConstantsStoredInto(ir, "cfg_Dev_limit"));
    }

    // A write still lands. The slot was always real and reachable -- a program that assigned
    // before it read got the right answer, which is part of why the missing initializer went
    // unnoticed -- and the repair must not turn the attribute into a constant that swallows it.
    [Fact]
    public void AWrittenClassAttribute_KeepsItsRunTimeSlot()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    limit = 7\n" +
            "def main() -> uint8:\n" +
            "    Dev.limit = 9\n" +
            "    return Dev.limit\n");

        var stored = ConstantsStoredInto(ir, "Dev_limit");
        Assert.Contains(7, stored);
        Assert.Contains(9, stored);
    }
}
