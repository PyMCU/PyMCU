using PyMCU.Frontend;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The observer-mode resolution pass (PYMCU_RESOLVE_OBSERVE=1).
///
/// It decides each simple name's binding once, before lowering, and derives the storage key
/// from that binding plus the expansion the name is read in. Nothing downstream reads its
/// answer: it only says where the ladder's answer differs from its own. These tests pin the
/// rule it applies -- the module level binds the global, a function body binds its own frame,
/// an expansion binds a copy of the callee's frame -- and that it is off unless asked for.
/// </summary>
public class NameResolutionObserverTests
{
    private static FunctionEntry Entry(string prefix, string src)
    {
        var ast = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        var func = ast.Functions[0];
        return new FunctionEntry { Prefix = prefix, Func = func, SourceFile = "main.py" };
    }

    private static NameResolution Built(params FunctionEntry[] entries)
    {
        var r = new NameResolution();
        r.Build(entries);
        return r;
    }

    [Fact]
    public void TheObserverIsOffUnlessAskedFor()
    {
        // A build that does not set the variable pays nothing and prints nothing, which is
        // what keeps the firmware byte-identical.
        Assert.Equal(Environment.GetEnvironmentVariable("PYMCU_RESOLVE_OBSERVE") is "1",
                     NameResolution.Enabled);
    }

    // `main` of the entry file IS the module's top level: the compiler splices the
    // module-level statements into its body, and every read of a name bound there answers the
    // bare global.
    [Fact]
    public void ANameBoundAtModuleLevel_ResolvesToTheModuleGlobal()
    {
        var r = Built(Entry("", "def main():\n    px: uint8 = 99\n"));
        Assert.Equal("px", r.Resolve("", "main", "", "px"));
    }

    [Fact]
    public void ANameBoundAtAModulesTopLevel_CarriesThatModulesPrefix()
    {
        var r = Built(Entry("pymcu_hal_gpio_", "def __module_init():\n    port: uint8 = 3\n"));
        Assert.Equal("pymcu_hal_gpio_port",
                     r.Resolve("pymcu_hal_gpio_", "pymcu_hal_gpio___module_init", "", "port"));
        // Read from another function of the same module, the module global still answers.
        Assert.Equal("pymcu_hal_gpio_port",
                     r.Resolve("pymcu_hal_gpio_", "pymcu_hal_gpio_read", "", "port"));
    }

    [Fact]
    public void ALocalAndAParameterResolveToTheFunctionsOwnFrame()
    {
        var r = Built(Entry("", "def f(a: uint8) -> uint8:\n    b: uint8 = a\n    return b\n"));
        Assert.Equal("f.a", r.Resolve("", "f", "", "a"));
        Assert.Equal("f.b", r.Resolve("", "f", "", "b"));
    }

    // The factorization the pass exists for: the binding is the callee's and does not depend
    // on the expansion, the key is that binding plus THIS expansion instance.
    [Fact]
    public void ALocalOfAnExpandedInline_IsKeyedByTheExpansionInstance()
    {
        var r = Built(Entry("", "def write(data: uint8) -> None:\n    tmp: uint8 = data\n"));
        Assert.Equal("inline1.write.data", r.Resolve("", "main", "inline1.write.", "data"));
        Assert.Equal("inline7.write.tmp", r.Resolve("", "main", "inline7.write.", "tmp"));
    }

    // A name the expansion does not bind is not the expansion's: it belongs to whatever scope
    // the prefix is being expanded into. The ladder's "prefix first, always" is what let an
    // outer name answer a callee's parameter and the other way round.
    [Fact]
    public void ANameTheExpansionDoesNotBind_FallsOutOfTheExpansionFrame()
    {
        var r = Built(Entry("", "def write(data: uint8) -> None:\n    tmp: uint8 = data\n"),
                      Entry("", "def main():\n    total: uint8 = 0\n"));
        Assert.Equal("total", r.Resolve("", "main", "inline1.write.", "total"));
    }

    [Fact]
    public void AGlobalDeclaration_MakesTheNameTheModulesBinding()
    {
        var r = Built(Entry("", "def main():\n    count: uint8 = 0\n"),
                      Entry("", "def bump() -> None:\n    global count\n    count = count + 1\n"));
        Assert.Equal("count", r.Resolve("", "bump", "", "count"));
    }

    // Everything the pass does not claim -- a field, a class, a function, a builtin -- is
    // answered null and never compared, so an unclaimed name can never be reported as a
    // discrepancy.
    [Fact]
    public void ANameNobodyBinds_IsUnclaimed()
    {
        var r = Built(Entry("", "def f(a: uint8) -> uint8:\n    return a\n"));
        Assert.Null(r.Resolve("", "f", "", "nowhere"));
    }

    [Fact]
    public void ASpellingIsNamedByTheScopeItCameFrom()
    {
        Assert.Equal("inline-prefix",
                     NameResolution.SpellingOf("inline1.write.data", "", "main", "inline1.write."));
        Assert.Equal("function-qualified", NameResolution.SpellingOf("f.b", "", "f", ""));
        Assert.Equal("module-qualified",
                     NameResolution.SpellingOf("pymcu_hal_gpio_port", "pymcu_hal_gpio_", "", ""));
        Assert.Equal("bare", NameResolution.SpellingOf("px", "", "main", ""));
    }
}
