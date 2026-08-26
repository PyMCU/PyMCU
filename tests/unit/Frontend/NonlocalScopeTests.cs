using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `nonlocal` means "bind to a name in an ENCLOSING FUNCTION", so with no enclosing function
/// there is nothing it can mean, and CPython rejects it at parse time.
///
/// Accepting it was not harmless, which is the part worth pinning. Inside an `@inline` body the
/// statement aliased the name write-through to the CALLER's variable of the same name, so a
/// local assignment in the inlined function silently overwrote the caller's own: a `bump()`
/// setting its `seed` to 99 left the caller's `seed` reading 99 instead of 5, on a clean build.
///
/// The nesting is what makes it legal. A def inside a def is the shape the aliasing exists for,
/// and refusing that would take away the feature rather than the bug.
/// </summary>
public class NonlocalScopeTests
{
    private static void Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static string Refusal(string src) =>
        Assert.ThrowsAny<Exception>(() => Parse(src)).Message;

    // ── refused ──────────────────────────────────────────────────────────────

    [Fact]
    public void NonlocalInAModuleLevelFunction_IsRefused()
    {
        var msg = Refusal("def main():\n" +
                          "    nonlocal seed\n" +
                          "    seed = 1\n");

        Assert.Contains("nonlocal seed", msg);
        Assert.Contains("no enclosing function", msg);
    }

    [Fact]
    public void TheRefusalNamesTheFunctionItIsTalkingAbout()
    {
        var msg = Refusal("def configure():\n" +
                          "    nonlocal seed\n" +
                          "    seed = 1\n");

        Assert.Contains("'configure'", msg);
    }

    [Fact]
    public void TheRefusalOffersGlobalWithoutAssertingWhatWasMeant()
    {
        // "you meant global" is a claim about intent. "If it is a module-level variable, the
        // declaration is global" is the same help without the claim, and it is the reading the
        // compiler cannot verify from here.
        var msg = Refusal("def main():\n" +
                          "    nonlocal seed\n" +
                          "    seed = 1\n");

        Assert.Contains("global seed", msg);
        Assert.Contains("If 'seed' is a module-level variable", msg);
        Assert.DoesNotContain("you meant", msg);
    }

    [Fact]
    public void TheRefusalSaysWhereNonlocalDoesBelong()
    {
        var msg = Refusal("def main():\n    nonlocal seed\n    seed = 1\n");

        Assert.Contains("nested inside another def", msg);
    }

    [Fact]
    public void NonlocalInAnInlineFunctionAtModuleLevel_IsRefused()
    {
        // The shape that miscompiled: @inline does not supply an enclosing function, it only
        // changes where the body ends up.
        var msg = Refusal("@inline\n" +
                          "def bump() -> uint8:\n" +
                          "    nonlocal seed\n" +
                          "    seed = 99\n" +
                          "    return seed\n");

        Assert.Contains("nonlocal seed", msg);
    }

    [Fact]
    public void NonlocalAtModuleLevel_IsRefused()
    {
        var msg = Refusal("nonlocal seed\n");

        Assert.Contains("no function scope here at all", msg);
    }

    [Fact]
    public void TheFirstNameIsTheOneReported()
    {
        var msg = Refusal("def main():\n    nonlocal a, b\n    a = 1\n");

        Assert.Contains("nonlocal a", msg);
    }

    // ── accepted, and this half is the feature ───────────────────────────────

    [Fact]
    public void NonlocalInsideANestedDef_StillParses()
    {
        Parse("def main():\n" +
              "    count = 0\n" +
              "    @inline\n" +
              "    def increment():\n" +
              "        nonlocal count\n" +
              "        count = count + 1\n" +
              "    increment()\n");
    }

    [Fact]
    public void NonlocalTwoLevelsDown_StillParses()
    {
        Parse("def outer():\n" +
              "    total = 0\n" +
              "    @inline\n" +
              "    def middle():\n" +
              "        @inline\n" +
              "        def inner():\n" +
              "            nonlocal total\n" +
              "            total = total + 1\n" +
              "        inner()\n" +
              "    middle()\n");
    }

    [Fact]
    public void GlobalAtFunctionTopLevel_IsUntouched()
    {
        // `global` is the declaration that IS legal there, and the refusal must not reach it.
        Parse("count = 0\n" +
              "def bump():\n" +
              "    global count\n" +
              "    count = count + 1\n");
    }
}
