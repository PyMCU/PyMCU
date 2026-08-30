/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using Xunit;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#229. A decorator whose entire purpose is to change the code generated for a function
/// either reaches the backend or is refused. It is never quietly dropped.
///
/// The two that were dropped are not cosmetic. `@naked` suppresses the prologue and epilogue,
/// so a `@naked` that does not arrive emits the very code the author wrote it to remove, and an
/// RTOS context switch built on it corrupts the frame it was saving. `@interrupt` installs a
/// handler in a vector, so an `@interrupt` that does not arrive is a handler that is never
/// installed and an interrupt that is never serviced. Both compiled clean.
///
/// The AST was never wrong: the parser puts both flags on a method's FunctionDef. Everything
/// here is about what the scan then does with the node.
/// </summary>
public class CodegenDecoratorTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static CompilerError Refused(string src) =>
        Assert.ThrowsAny<CompilerError>(() => Gen(src));

    private const string Head =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n";

    private static string Tail(string body) =>
        "\ndef main() -> None:\n" + body + "\n    while True:\n        pass\n";

    // One field is RFC 0001 Model A (one parameter per field); three is Model B (an SRAM slot
    // and a `self` pointer). Both are built by RegisterOutlinedMethod, which is the point: the
    // `self` in Model B's parameter list is synthesised there too, so a parameter named `self`
    // does not tell the two models apart from the outside.
    private const string OneField =
        "class Box:\n" +
        "    def __init__(self, n: uint8) -> None:\n" +
        "        self.n: uint8 = n\n\n";
    private const string ThreeFields =
        "class Box:\n" +
        "    def __init__(self, n: uint8) -> None:\n" +
        "        self.a: uint8 = n\n" +
        "        self.b: uint8 = n\n" +
        "        self.c: uint8 = n\n\n";

    // --- @naked reaches the backend through every path that compiles a subroutine ---------

    [Theory]
    [InlineData("Model A, one field", OneField, "        return self.n + 1\n")]
    [InlineData("Model B, three fields", ThreeFields, "        return self.a + self.b + self.c\n")]
    public void ANakedMethodIsStillNakedAfterOutlining(string _, string cls, string body)
    {
        // The discriminator. Before the fix both of these lowered with isNaked false, so the
        // prologue was emitted into a function whose author had asked for none.
        var ir = Gen(Head + cls +
                     "    @naked\n" +
                     "    def bump(self) -> uint8:\n" + body +
                     Tail("    b = Box(GPIOR0.value)\n    GPIOR1.value = b.bump()"));

        Assert.True(Assert.Single(ir.Functions, f => f.Name == "Box_bump").IsNaked);
    }

    [Fact]
    public void ANakedModuleFunctionIsUnaffected()
    {
        // The control. It has always worked, and it is the reason carrying the flag onto the
        // stand-in is the right answer rather than refusing: an outlined method is a subroutine
        // taking leading parameters, which is exactly the shape this already supports.
        var ir = Gen(Head +
                     "@naked\n" +
                     "def bump(n: uint8) -> uint8:\n" +
                     "    return n + 1\n" +
                     Tail("    GPIOR1.value = bump(GPIOR0.value)"));

        Assert.True(Assert.Single(ir.Functions, f => f.Name == "bump").IsNaked);
    }

    [Theory]
    [InlineData("@naked", "IsNaked")]
    [InlineData("@interrupt(1)", "IsInterrupt")]
    public void AMethodWithNoSelfKeepsBothDecorators(string decorator, string flag)
    {
        // The invariant, and the path the issue originally blamed. A method with no `self` is a
        // plain function written in a class body: the scan registers the node the user wrote,
        // not a stand-in, so it never had the defect. A fix applied one branch too wide would
        // break it, and nothing else here would notice.
        var ir = Gen(Head +
                     "class Box:\n" +
                     $"    {decorator}\n" +
                     "    def bump(n: uint8) -> uint8:\n" +
                     "        return n + 1\n" +
                     Tail("    GPIOR1.value = Box.bump(GPIOR0.value)"));

        var fn = Assert.Single(ir.Functions, f => f.Name == "Box_bump");
        Assert.True(flag == "IsNaked" ? fn.IsNaked : fn.IsInterrupt);
    }

    // --- @interrupt on an instance method is refused, not carried -------------------------

    [Theory]
    [InlineData(OneField, "        return self.n + 1\n")]
    [InlineData(ThreeFields, "        return self.a + self.b + self.c\n")]
    public void AnInterruptOnAnInstanceMethodIsRefused(string cls, string body)
    {
        // Carrying it would have been the easy symmetry with @naked and it would have been
        // worse than the bug: an ISR is entered by the hardware, so nothing passes the leading
        // parameters an outlined method is compiled with -- the instance's own fields. The
        // vector would have pointed at a body reading storage no caller ever wrote, which
        // trades a silent no-op for silent wrong code.
        var ex = Refused(Head + cls +
                         "    @interrupt(1)\n" +
                         "    def bump(self) -> uint8:\n" + body +
                         Tail("    b = Box(GPIOR0.value)\n    GPIOR1.value = b.bump()"));

        Assert.Contains("@interrupt", ex.Message);
        Assert.Contains("no caller to pass `self`", ex.Message);
    }

    // --- a function that is EXPANDED cannot carry either ----------------------------------

    [Theory]
    // An explicit @inline, at module level. Not a class defect at all, which is why the check
    // is written against the result and not against the class paths.
    [InlineData("@inline\n@naked\ndef bump(n: uint8) -> uint8:\n    return n + 1\n",
                "    GPIOR1.value = bump(GPIOR0.value)")]
    [InlineData("@inline\n@interrupt(1)\ndef bump(n: uint8) -> uint8:\n    return n + 1\n",
                "    GPIOR1.value = bump(GPIOR0.value)")]
    // A ZCA instance parameter forces expansion with no @inline written anywhere: the fields
    // live in the caller's frame, so the body only has meaning at the call site.
    [InlineData("class Box:\n    def __init__(self, n: uint8) -> None:\n        self.n: uint8 = n\n\n" +
                "@naked\ndef handle(b: Box) -> uint8:\n    return b.n + 1\n",
                "    box = Box(GPIOR0.value)\n    GPIOR1.value = handle(box)")]
    // A single-field method that BOTH mutates its field and returns a value is force-inlined so
    // the mutation is not lost through the one return slot. Also no @inline in the source.
    [InlineData("class Box:\n    def __init__(self, n: uint8) -> None:\n        self.n: uint8 = n\n\n" +
                "    @naked\n    def bump(self) -> uint8:\n        self.n = self.n + 1\n        return self.n\n",
                "    b = Box(GPIOR0.value)\n    GPIOR1.value = b.bump()")]
    public void ACodegenDecoratorOnAnExpandedFunctionIsRefused(string decl, string body)
    {
        var ex = Refused(Head + decl + Tail(body));

        Assert.Contains("expanded into its callers", ex.Message);
    }

    [Fact]
    public void APropertyAccessorStillCompiles()
    {
        // The invariant beside the refusal above. A property accessor is forced inline, so it is
        // expanded like everything else in that list -- and it carries no codegen decorator, so
        // the check must leave it alone. A guard written on "is expanded" rather than on "is
        // expanded AND is decorated" would refuse every property in the stdlib.
        var ir = Gen(Head + OneField +
                     "    @property\n" +
                     "    def doubled(self) -> uint8:\n" +
                     "        return self.n + self.n\n" +
                     Tail("    b = Box(GPIOR0.value)\n    GPIOR1.value = b.doubled"));

        Assert.DoesNotContain(ir.Functions, f => f.Name == "Box_doubled");
    }
}
