using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/matrix-math.
/// Boot: "MATRIX\n" (7 bytes). Each frame = 5 bytes (4 row values + 0x0A).
/// Frame 0: 0x01,0x02,0x04,0x08,0x0A  (diagonal at col 0)
/// Frame 1: 0x02,0x04,0x08,0x10,0x0A  (diagonal at col 1)
/// </summary>
[TestFixture]
public class MatrixMathTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("matrix-math");

    [Test]
    public void Boot_SendsMatrixBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "MATRIX");
        uno.Serial.Should().ContainLine("MATRIX");
    }

    [Test]
    public void FirstFrame_IsDiagonalAtCol0()
    {
        var uno = Sim();
        // "MATRIX\n" = 7 bytes, first frame = 5 bytes
        uno.RunUntilSerialBytes(uno.Serial, 12, maxMs: 200);
        uno.Serial.Should().HaveBytesAt(7, [0x01, 0x02, 0x04, 0x08, 0x0A]);
    }

    [Test]
    public void SecondFrame_IsDiagonalAtCol1()
    {
        var uno = Sim();
        // "MATRIX\n" + frame0 + frame1 = 7 + 5 + 5 = 17 bytes
        uno.RunUntilSerialBytes(uno.Serial, 17, maxMs: 300);
        uno.Serial.Should().HaveBytesAt(12, [0x02, 0x04, 0x08, 0x10, 0x0A]);
    }

    [Test]
    public void EightFrames_WrapAround()
    {
        var uno = Sim();
        // After 8 frames the pattern wraps: frame8 == frame0
        // 7 + 8*5 = 47 bytes, then frame8 starts at 47
        uno.RunUntilSerialBytes(uno.Serial, 52, maxMs: 500);
        // Frame 8 (index 0 again): same as frame 0
        uno.Serial.Should().HaveBytesAt(47, [0x01, 0x02, 0x04, 0x08, 0x0A]);
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
