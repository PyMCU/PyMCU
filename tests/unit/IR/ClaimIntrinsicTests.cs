using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#300. `claim(key, value, owner, hint)` is a compile-time resource claim: the second
/// site that asks the same key for another value is refused where it is written, naming
/// both owners; the same value is shared; the sole owner may retune; a run-time value has
/// nothing to claim. Nothing is emitted.
/// </summary>
public class ClaimIntrinsicTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, uint16, const, inline, claim\n\n" +
        "@inline\n" +
        "def use(pin: const, code: uint8):\n" +
        "    claim(\"Timer0 prescaler\", code, pin, \"share it or move\")\n\n";

    // The same, with a register in scope: the rows that need a value the compiler cannot know
    // read one. Kept separate because the line numbers of Prelude are asserted below.
    private const string PreludeWithRegister =
        "from pymcu.chips.atmega328p import GPIOR0\n" + Prelude;

    [Fact]
    public void ASecondOwnerAskingAnotherValue_IsRefusedNamingBoth()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "def main():\n" +
            "    use(\"PD5\", 2)\n" +
            "    use(\"PD6\", 5)\n"));
        Assert.Contains("Timer0 prescaler", ex.Message);
        Assert.Contains("PD5", ex.Message);
        Assert.Contains("PD6", ex.Message);
        Assert.Contains("already 2", ex.Message);
        Assert.Contains("asks for 5", ex.Message);
        Assert.Contains("share it or move", ex.Message);
        Assert.Equal(9, ex.Line);   // the second call, where the reader can act
    }

    [Fact]
    public void TheSameValue_IsShared()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    use(\"PD5\", 3)\n" +
            "    use(\"PD6\", 3)\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void TheSoleOwner_MayRetune()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    use(\"PD6\", 3)\n" +
            "    use(\"PD6\", 2)\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void AnOwnerWithASibling_MayNotRetune()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "def main():\n" +
            "    use(\"PD5\", 3)\n" +
            "    use(\"PD6\", 3)\n" +
            "    use(\"PD6\", 2)\n"));
        Assert.Contains("PD5", ex.Message);
        Assert.Equal(10, ex.Line);
    }

    [Fact]
    public void ARunTimeValue_HasNothingToClaim()
    {
        // Read from a register: since PyMCU#327 a local that holds a compile-time constant IS
        // passed as one, so `v: uint8 = 3` claims exactly as the literal does, which is the
        // case above. A value nobody knows is what this row is about.
        var ir = Gen(PreludeWithRegister +
            "def main():\n" +
            "    v: uint8 = GPIOR0.value\n" +
            "    use(\"PD5\", v)\n" +
            "    use(\"PD6\", v + 1)\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void AConstantHeldInALocal_ClaimsLikeALiteral()
    {
        // PyMCU#327: the local carries the value into the claim, so the conflict is seen.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "def main():\n" +
            "    v: uint8 = 3\n" +
            "    use(\"PD5\", v)\n" +
            "    use(\"PD6\", v + 1)\n"));
        Assert.Contains("PD5", ex.Message);
        Assert.Contains("PD6", ex.Message);
    }

    [Fact]
    public void AClaim_EmitsNothing()
    {
        string body(string src) => string.Join("|", Gen(src).Functions.Single(f => f.Name == "main").Body
            .Select(i => i.GetType().Name)
            .Where(n => !n.Contains("Marker") && !n.Contains("Label") && !n.Contains("Dbg") && !n.Contains("Debug")));
        var with = body(Prelude + "def main():\n    x: uint8 = 1\n    use(\"PD5\", 3)\n    x = x + 1\n");
        var without = body("from pymcu.types import uint8\n\ndef main():\n    x: uint8 = 1\n    x = x + 1\n");
        Assert.Equal(without, with);
    }

    /// <summary>
    /// PyMCU#303. The quoted site names its FILE, not a bare line number. The message is text,
    /// and the build driver compiles a synthetic entry whose numbering is not the reader's, so
    /// a citation that says only "line 43" cannot be mapped back to the file they wrote -- and
    /// it did point past the end of it. The driver recognises `file:line` and maps it.
    /// </summary>
    [Fact]
    public void TheQuotedSite_NamesTheFileItIsALineOf()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "def main():\n" +
            "    use(\"PD5\", 2)\n" +
            "    use(\"PD6\", 5)\n"));
        Assert.Contains("at main.py:8", ex.Message);
        Assert.DoesNotContain("at line ", ex.Message);
        Assert.Equal(9, ex.Line);
    }

    /// <summary>
    /// A site inside an imported module keeps naming that module, which is what the entry
    /// file's name must not displace.
    /// </summary>
    [Fact]
    public void TheEntryFileName_IsOnlyUsedForTheEntryFile()
    {
        var gen = new IRGenerator { EntryFileName = "app.py" };
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => gen.Generate(
            new Parser(new Lexer(Prelude +
                "def main():\n" +
                "    use(\"PD5\", 2)\n" +
                "    use(\"PD6\", 5)\n").Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" }));
        Assert.Contains("at app.py:8", ex.Message);
    }

    [Fact]
    public void DifferentKeys_NeverMeet()
    {
        var ir = Gen(Prelude +
            "@inline\n" +
            "def use2(pin: const, code: uint8):\n" +
            "    claim(\"Timer2 prescaler\", code, pin)\n\n" +
            "def main():\n" +
            "    use(\"PD5\", 2)\n" +
            "    use2(\"PD3\", 5)\n");
        Assert.NotNull(ir);
    }
}
