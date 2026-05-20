using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Verifies that the stdlib style refactoring (if/elif -> match, reg[n]==1 -> reg[n])
/// generates byte-for-byte identical AVR firmware.
///
/// Each pair of fixtures implements the same logic using the old and new styles.
/// If both compile to the same Intel HEX string the refactoring is proven neutral.
/// </summary>
[TestFixture]
public class StdlibStyleRefactorTests
{
    /// <summary>
    /// match/case and if/elif produce identical code when dispatching on a runtime integer.
    /// Exercises the same code path used in _ws2812_b, _ws2812_d, ws2812_write_byte,
    /// ws2812_init, and ws2812_reset after the neopixel refactor.
    /// Both fixtures read a runtime value from PINB and dispatch on it, ensuring
    /// the compiler cannot constant-fold away the branches.
    /// </summary>
    [Test]
    public void MatchDispatch_ProducesIdenticalBinaryToIfElif()
    {
        var hexMatch  = PymcuCompiler.BuildFixture("style-dispatch-match");
        var hexIfelif = PymcuCompiler.BuildFixture("style-dispatch-ifelif");
        hexMatch.Should().Be(hexIfelif,
            "match/case and if/elif on a runtime integer should emit identical AVR instructions");
    }

    /// <summary>
    /// Bare reg[n] and reg[n]==0 / reg[n]==1 conditions produce identical SBIC/SBIS code.
    /// Exercises the same code path used in EECR, ADCSRA, TWCR polling after the refactor.
    /// </summary>
    [Test]
    public void BitRegExplicit_ProducesIdenticalBinaryToImplicit()
    {
        var hexOld = PymcuCompiler.BuildFixture("style-bitreg-old");
        var hexNew = PymcuCompiler.BuildFixture("style-bitreg-new");
        hexNew.Should().Be(hexOld,
            "reg[n], reg[n]==1, and not reg[n] / reg[n]==0 should emit identical SBIS/SBIC instructions");
    }
}
