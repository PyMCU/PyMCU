using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/tuple-args.
/// Exercises a tuple literal passed to an @inline function and consumed via:
///   - constant subscript: color[0], color[1], color[2]   -> "RGB"
///   - for-in unroll:      for c in seq                    -> "XY"
/// </summary>
[TestFixture]
public class TupleArgsTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("tuple-args"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "TUP\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("TUP");

    [Test]
    public void TupleArg_ConstantSubscript_EmitsRGB()
    {
        // send_indexed(uart, (0x52, 0x47, 0x42)) -> color[0..2] -> 'R' 'G' 'B'
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("RGB"), maxMs: 300);
        uno.Serial.Text.Should().Contain("RGB",
            "tuple-literal arg indexed by constant should yield R, G, B");
    }

    [Test]
    public void TupleArg_ForInUnroll_EmitsXY()
    {
        // send_iter(uart, (0x58, 0x59)) -> for c in seq -> 'X' 'Y'
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("XY"), maxMs: 300);
        uno.Serial.Text.Should().Contain("XY",
            "tuple-literal arg iterated via for-in should yield X, Y");
    }
}
