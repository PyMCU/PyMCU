using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;
using System.IO;

namespace PyMCU.IntegrationTests.Tests;

[TestFixture]
public class DiagNew
{
    [Test]
    public void Checksum_Diag()
    {
        var hex = File.ReadAllText("/Users/begeistert/Repos/pymcu/examples/avr/checksum/dist/firmware.hex");
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(hex);
        uno.RunUntilSerial(uno.Serial, "CHECKSUM\n", maxMs: 200);
        TestContext.WriteLine($"Banner: [{uno.Serial.Text.Replace("\n","\\n")}]");
        var before = uno.Serial.ByteCount;
        // Inject 4 bytes: 0xAA, 0x55, 0xF0, 0x0F -> XOR = AA^55^F0^0F = 00
        uno.Serial.InjectByte(0xAA);
        uno.RunMilliseconds(5);
        uno.Serial.InjectByte(0x55);
        uno.RunMilliseconds(5);
        uno.Serial.InjectByte(0xF0);
        uno.RunMilliseconds(5);
        uno.Serial.InjectByte(0x0F);
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 500);
        TestContext.WriteLine($"Output: [{uno.Serial.Text.Replace("\n","\\n")}]");
        TestContext.WriteLine($"Checksum byte: 0x{uno.Serial.Bytes[before]:X2}");
        Assert.Pass("ok");
    }

    [Test]
    public void MultiIsr_Diag()
    {
        var hex = File.ReadAllText("/Users/begeistert/Repos/pymcu/examples/avr/multi-isr/dist/firmware.hex");
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(hex);
        uno.AddTimer(AvrTimer.Timer0Config);
        uno.RunUntilSerial(uno.Serial, "MULTI ISR\n", maxMs: 500);
        TestContext.WriteLine($"Banner: [{uno.Serial.Text.Replace("\n","\\n")}]");
        var before = uno.Serial.ByteCount;
        // Trigger INT0: falling edge on PD2
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunMilliseconds(100);
        TestContext.WriteLine($"After INT0: [{uno.Serial.Text.Replace("\n","\\n")}]");
        TestContext.WriteLine($"Bytes added: {uno.Serial.ByteCount - before}");
        if (uno.Serial.ByteCount > before)
            TestContext.WriteLine($"First new byte: 0x{uno.Serial.Bytes[before]:X2}");
        Assert.Pass("ok");
    }

    [Test]
    public void NestedCalls_Diag()
    {
        var hex = File.ReadAllText("/Users/begeistert/Repos/pymcu/examples/avr/nested-calls/dist/firmware.hex");
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(hex);
        uno.RunUntilSerial(uno.Serial, "HEX ENCODE\n", maxMs: 500);
        var before = uno.Serial.ByteCount;
        // Each output line: hi, lo, chk, '\n' = 4 bytes; run for first 4 lines
        uno.RunUntilSerialBytes(uno.Serial, before + 16, maxMs: 500);
        var bytes = uno.Serial.Bytes.Skip(before).Take(16).ToArray();
        TestContext.WriteLine($"First 4 lines raw bytes: {string.Join(",", bytes.Select(b => $"0x{b:X2}"))}");
        // Line 0 (val=0): hi='0'=0x30, lo='0'=0x30, chk=0x30^0x30=0x00, '\n'=0x0A
        TestContext.WriteLine($"Line0: hi=0x{bytes[0]:X2} lo=0x{bytes[1]:X2} chk=0x{bytes[2]:X2} nl=0x{bytes[3]:X2}");
        if (bytes.Length > 7)
            TestContext.WriteLine($"Line1: hi=0x{bytes[4]:X2} lo=0x{bytes[5]:X2} chk=0x{bytes[6]:X2} nl=0x{bytes[7]:X2}");
        Assert.Pass("ok");
    }
}
