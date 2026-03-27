using Avr8Sharp.TestKit.Boards;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

[TestFixture]
public class DiagNew
{
    [Test]
    public void SoftPwm_PopCrashDiag()
    {
        var hex = PymcuCompiler.Build("soft-pwm");
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(hex);

        // Reflection: inspect Timer0's _ovf interrupt config
        var timerType = uno.Timer0.GetType();
        var rfFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var ovfField = timerType.GetField("_ovf", rfFlags);
        var ovf = ovfField?.GetValue(uno.Timer0);
        var addrField = ovf?.GetType().GetField("Address");
        var ovfAddr = addrField?.GetValue(ovf);
        TestContext.WriteLine($"Timer0 _ovf.Address = {ovfAddr} (0x{ovfAddr:X2})");

        // Dump program memory at timer0 OVF vector and ISR
        TestContext.WriteLine("=== ProgramMemory 0x000F..0x0025 ===");
        for (int w = 0x000F; w <= 0x0025; w++)
            TestContext.WriteLine($"  [0x{w:X4}] = 0x{uno.Cpu.ProgramMemory[w]:X4}");

        uno.RunUntilSerial(uno.Serial, "SOFT PWM\n", maxMs: 500);
        TestContext.WriteLine($"Banner done. SP=0x{uno.Cpu.SP:X4} Cycles={uno.Cpu.Cycles}");

        while (uno.Cpu.Cycles < 262100) uno.RunInstructions(1);

        for (int i = 0; i < 1_000_000 && uno.Cpu.Cycles < 262175; i++)
        {
            var spBefore = uno.Cpu.SP;
            var pcBefore = uno.Cpu.PC;
            try { uno.RunInstructions(1); }
            catch (IndexOutOfRangeException)
            {
                TestContext.WriteLine($"CRASH: was at PC=0x{pcBefore:X4} SP=0x{spBefore:X4}");
                Assert.Pass("crash traced");
            }
        }
        Assert.Pass("no crash in window");
    }

    [Test]
    public void Checksum_Diag()
    {
        var hex = PymcuCompiler.Build("checksum");
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
        var hex = PymcuCompiler.BuildFixture("multi-isr");
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(hex);
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
    public void PcintCounter_Diag()
    {
        var hex = PymcuCompiler.Build("pcint-counter");
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(hex);
        uno.PortB.SetPinValue(0, true);  // button released
        uno.RunUntilSerial(uno.Serial, "PCINT COUNTER\n", maxMs: 200);
        TestContext.WriteLine($"Banner: [{uno.Serial.Text.Replace("\n","\\n")}]");

        // Single press
        uno.PortB.SetPinValue(0, false);
        uno.RunMilliseconds(20);
        TestContext.WriteLine($"After press 1 (20ms): [{uno.Serial.Text.Replace("\n","\\n")}]");
        uno.PortB.SetPinValue(0, true);
        uno.RunMilliseconds(20);
        TestContext.WriteLine($"After release 1 (20ms): [{uno.Serial.Text.Replace("\n","\\n")}]");

        // Second press
        uno.PortB.SetPinValue(0, false);
        uno.RunMilliseconds(20);
        TestContext.WriteLine($"After press 2: [{uno.Serial.Text.Replace("\n","\\n")}]");
        uno.PortB.SetPinValue(0, true);
        uno.RunMilliseconds(20);

        // Third press
        uno.PortB.SetPinValue(0, false);
        uno.RunMilliseconds(20);
        TestContext.WriteLine($"After press 3: [{uno.Serial.Text.Replace("\n","\\n")}]");
        uno.PortB.SetPinValue(0, true);
        uno.RunMilliseconds(20);
        TestContext.WriteLine($"Final: [{uno.Serial.Text.Replace("\n","\\n")}]");
        Assert.Pass("ok");
    }

    [Test]
    public void NestedCalls_Diag()
    {
        var hex = PymcuCompiler.BuildFixture("nested-calls");
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
