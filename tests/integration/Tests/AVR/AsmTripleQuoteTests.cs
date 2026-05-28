using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/asm-triple-quote.
///
/// Verifies that triple-quoted strings ("""...""" and '''...''') are lexed
/// correctly and that their content — including embedded newlines — is
/// passed verbatim to the inline asm emitter.
///
/// Checkpoint 1: triple-double-quoted asm("LDI r16, 42 / STS 0x3E, r16")
///   GPIOR0 = 0x2A
///
/// Checkpoint 2: triple-single-quoted asm("LDI r16, 0xFF / STS 0x3E, r16")
///   GPIOR0 = 0xFF
///
/// Data-space address (ATmega328P): GPIOR0 = 0x3E
/// </summary>
[TestFixture]
public class AsmTripleQuoteTests
{
    private const int Gpior0Addr = 0x3E;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("asm-triple-quote"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    [Test]
    public void Cp1_TripleDoubleQuote_SetsGpior0To42()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x2A,
            "triple-double-quoted asm must emit LDI r16, 42 / STS and produce GPIOR0 = 0x2A");
    }

    [Test]
    public void Cp2_TripleSingleQuote_SetsGpior0ToFF()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0xFF,
            "triple-single-quoted asm must emit LDI r16, 0xFF / STS and produce GPIOR0 = 0xFF");
    }
}
