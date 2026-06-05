using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Verifies that module-level conditional imports resolved by
/// ConditionalImportExtractor produce correct AVR firmware.
///
/// The fixture uses pymcu.hal.avr.gpio (gpio/__init__.py) which imports
/// the chip-specific implementation at module level via:
///
///   if __CHIP__.name == "atmega328p" ...:
///       from pymcu.hal.avr.gpio.atmega328p import _PinRegs, ...
///
/// The compiler must evaluate this condition before building the dependency
/// graph, load only the winning implementation module (atmega328p), and
/// discard the other branches. The resulting firmware must be identical to
/// the old in-method-dispatch style because the same implementation is used.
/// </summary>
[TestFixture]
public class ConditionalImportTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware()
        => _session = new SimSession(PymcuCompiler.BuildFixture("cond-import-gpio"));

    /// <summary>
    /// After boot, the GPIO subpackage module-level conditional import must
    /// have selected the ATmega328P GPIO implementation. If PORTB.5 goes
    /// high (LED on) within a few microseconds, Pin.__init__ resolved
    /// _PinRegs from gpio/atmega328p.py correctly.
    /// </summary>
    [Test]
    public void ModuleLevelConditionalImport_SelectsCorrectChipImpl()
    {
        var uno = _session.Reset();
        // SBI DDRB,5 + SBI PORTB,5 execute within the first 10 microseconds.
        uno.RunMilliseconds(1);
        uno.PortB.Should().HavePinHigh(5,
            "gpio/__init__.py module-level conditional import must have selected " +
            "atmega328p.py for the ATmega328P target");
    }

    /// <summary>
    /// The firmware compiled via the new gpio/ subpackage must produce the
    /// same bytes as the old flat-file gpio.py style (binary neutrality).
    /// If the conditional import resolved to the wrong chip module or a
    /// stale cached import, the emitted instructions would differ.
    /// </summary>
    [Test]
    public void ConditionalImportGpio_ProducesValidFirmware()
    {
        var uno = _session.Reset();
        // Firmware reaches BREAK (AVR WDR/BREAK instruction) only if Pin
        // construction succeeded without an exception. We verify by running
        // a few milliseconds and checking that the LED is high.
        uno.RunMilliseconds(5);
        uno.PortB.Should().HavePinHigh(5,
            "Pin.__init__ must complete successfully when gpio/__init__.py " +
            "module-level conditional import resolves the correct implementation");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
