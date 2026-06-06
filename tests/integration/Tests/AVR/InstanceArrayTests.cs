using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/instance-array.
/// Verifies a per-instance SRAM array member (self._data: uint8[n*2]) declared
/// in __init__ and accessed by RUNTIME index from ZCA methods -- the mechanism
/// behind a NeoPixel framebuffer (pixels[i] = color).
/// </summary>
[TestFixture]
public class InstanceArrayTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("instance-array"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "IA\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("IA");

    [Test]
    public void InstanceArray_RuntimeWriteThenReadBack_RoundTrips()
    {
        // 8 bytes written by runtime index (buf.set(i, 65+i)) then read back
        // (buf.get(j)) must round-trip to 'A'..'H'.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("ABCDEFGH"), maxMs: 400);
        uno.Serial.Text.Should().Contain("ABCDEFGH",
            "per-instance SRAM array must round-trip runtime-indexed writes and reads");
    }

    [Test]
    public void SetItem_TupleIntoFramebuffer_RoundTrips()
    {
        // strip[i] = (a, b) -> __setitem__ writes the tuple into self._buf at a
        // runtime offset (index*2). Read-back must yield 'P'..'U'.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("PQRSTU"), maxMs: 500);
        uno.Serial.Text.Should().Contain("PQRSTU",
            "pixels[i] = (a, b) must store the tuple into the per-instance framebuffer");
    }
}
