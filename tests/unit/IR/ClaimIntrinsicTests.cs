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
        var ir = Gen(Prelude +
            "def main():\n" +
            "    v: uint8 = 3\n" +
            "    use(\"PD5\", v)\n" +
            "    use(\"PD6\", v + 1)\n");
        Assert.NotNull(ir);
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
