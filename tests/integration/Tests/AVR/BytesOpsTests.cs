using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/bytes-ops.
/// Exercises bytes literal, int.from_bytes, enumerate on runtime arrays.
/// </summary>
[TestFixture]
public class BytesOpsTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("bytes-ops");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "BYTES\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("BYTES");

    [Test]
    public void ForIn_BytesLiteral_SumIsCorrect()
    {
        // for x in b"\xDE\xAD": sum_f += x
        // 0xDE + 0xAD = 0x18B; low byte = 0x8B
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("F:8B\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("F:8B",
            "sum of 0xDE+0xAD low byte should be 0x8B");
    }

    [Test]
    public void ArrayInit_BytesLiteral_ElementIsCorrect()
    {
        // buf: uint8[3] = b"\x0A\x1F\x2B"; buf[1] == 0x1F
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:1F\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("A:1F",
            "buf[1] should be 0x1F after bytes-literal init");
    }

    [Test]
    public void ArrayInit_BytesRepeat_ZeroFill()
    {
        // zeros: uint8[4] = b"\x00" * 4; zeros[0] == 0x00
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("Z:00\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("Z:00",
            "zeros[0] should be 0x00 after b\"\\x00\"*4 init");
    }

    [Test]
    public void FromBytes_LittleEndian_Runtime_LowByteIsCorrect()
    {
        // int.from_bytes([1, 2], 'little') -> 0x0201; low byte = 0x01
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("L:01\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("L:01",
            "int.from_bytes([1,2],'little') low byte should be 0x01");
    }

    [Test]
    public void FromBytes_BigEndian_CompileTime_LowByteIsCorrect()
    {
        // int.from_bytes(b"\x01\x02", 'big') -> 0x0102; low byte = 0x02
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:02\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("B:02",
            "int.from_bytes(b\"\\x01\\x02\",'big') low byte should be 0x02");
    }

    [Test]
    public void Enumerate_RuntimeArray_IndexSumIsCorrect()
    {
        // for i, x in enumerate(data): idx_sum += i
        // data = [10,20,30]; idx_sum = 0+1+2 = 3 = 0x03
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("I:03\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("I:03",
            "enumerate index sum 0+1+2 should be 3 = 0x03");
    }

    [Test]
    public void Enumerate_RuntimeArray_ValueSumIsCorrect()
    {
        // for i, x in enumerate(data): val_sum += x
        // data = [10,20,30]; val_sum = 10+20+30 = 60 = 0x3C
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("V:3C\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("V:3C",
            "enumerate value sum 10+20+30 should be 60 = 0x3C");
    }
}
