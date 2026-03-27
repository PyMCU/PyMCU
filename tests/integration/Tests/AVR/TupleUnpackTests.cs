using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/tuple-unpack.
/// Exercises:
///   F5: multi-item with  (with a as x, b as y:)
///   F6: extended unpacking  (head, *rest, last = tuple)
/// </summary>
[TestFixture]
public class TupleUnpackTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("tuple-unpack");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "TU\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("TU");

    [Test]
    public void ExtendedUnpack_Head_IsFirst()
    {
        // head, *middle, last = (1,2,3,4,5) -> head=1
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("H:01"), maxMs: 300);
        uno.Serial.Text.Should().Contain("H:01",
            "head of (1,2,3,4,5) should be 1 = 0x01");
    }

    [Test]
    public void ExtendedUnpack_Rest_FirstElement()
    {
        // middle[0] = 2 = 0x02
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("R:02"), maxMs: 300);
        uno.Serial.Text.Should().Contain("R:02",
            "middle[0] should be 2 = 0x02");
    }

    [Test]
    public void ExtendedUnpack_Last_IsFifth()
    {
        // last = 5 = 0x05
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("L:05"), maxMs: 300);
        uno.Serial.Text.Should().Contain("L:05",
            "last of (1,2,3,4,5) should be 5 = 0x05");
    }

    [Test]
    public void MultiItemWith_EntersBothContexts()
    {
        // with a as fa, b as fb: fa.entered = 1
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("W:01"), maxMs: 300);
        uno.Serial.Text.Should().Contain("W:01",
            "multi-item with: fa.entered should be 1 = 0x01");
    }
}
