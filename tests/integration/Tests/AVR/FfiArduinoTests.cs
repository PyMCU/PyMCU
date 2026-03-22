using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/ffi-arduino.
/// Exercises Arduino-compatible utility functions via @extern C interop:
///   arduino_map(x, in_max, out_max)  -- scales x from [0,in_max] to [0,out_max]
///   arduino_constrain(x, lo, hi)     -- clamps x to [lo, hi]
///   adc_to_pwm(adc_val)              -- maps 10-bit ADC to 8-bit PWM
///
/// Expected UART output:
///   "ARDUINO\n"  -- boot banner
///   "M:7F\n"     -- arduino_map(512, 1023, 255)    = 127 = 0x7F
///   "F:FF\n"     -- arduino_map(1023, 1023, 255)   = 255 = 0xFF
///   "Z:00\n"     -- arduino_map(0, 1023, 255)      =   0 = 0x00
///   "H:C8\n"     -- arduino_constrain(300, 10, 200)= 200 = 0xC8  (clamped hi)
///   "L:0A\n"     -- arduino_constrain(5, 10, 200)  =  10 = 0x0A  (clamped lo)
///   "P:7F\n"     -- adc_to_pwm(512)                = 127 = 0x7F
///   "T:FF\n"     -- adc_to_pwm(1023)               = 255 = 0xFF
///   "OK\n"       -- done
/// </summary>
[TestFixture]
public class FfiArduinoTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("ffi-arduino");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "ARDUINO\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("ARDUINO");

    [Test]
    public void ArduinoMap_512_Of_1023_To_255_Returns127()
    {
        // arduino_map(512, 1023, 255) = 512*255/1023 = 127 = 0x7F
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("M:7F\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("M:7F",
            "arduino_map(512, 1023, 255) should return 127 = 0x7F");
    }

    [Test]
    public void ArduinoMap_1023_Of_1023_To_255_Returns255()
    {
        // arduino_map(1023, 1023, 255) = 255 = 0xFF
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("F:FF\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("F:FF",
            "arduino_map(1023, 1023, 255) should return 255 = 0xFF");
    }

    [Test]
    public void ArduinoMap_0_Of_1023_To_255_Returns0()
    {
        // arduino_map(0, 1023, 255) = 0 = 0x00
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("Z:00\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("Z:00",
            "arduino_map(0, 1023, 255) should return 0 = 0x00");
    }

    [Test]
    public void ArduinoConstrain_300_To_10_200_ClampsHiTo200()
    {
        // arduino_constrain(300, 10, 200) = 200 = 0xC8  (300 > hi)
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("H:C8\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("H:C8",
            "arduino_constrain(300, 10, 200) should clamp to 200 = 0xC8");
    }

    [Test]
    public void ArduinoConstrain_5_To_10_200_ClampsLoTo10()
    {
        // arduino_constrain(5, 10, 200) = 10 = 0x0A  (5 < lo)
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("L:0A\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("L:0A",
            "arduino_constrain(5, 10, 200) should clamp to 10 = 0x0A");
    }

    [Test]
    public void AdcToPwm_512_Returns127()
    {
        // adc_to_pwm(512) = 512*255/1023 = 127 = 0x7F
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("P:7F\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("P:7F",
            "adc_to_pwm(512) should return 127 = 0x7F");
    }

    [Test]
    public void AdcToPwm_1023_Returns255()
    {
        // adc_to_pwm(1023) = 255 = 0xFF
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("T:FF\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("T:FF",
            "adc_to_pwm(1023) should return 255 = 0xFF");
    }

    [Test]
    public void AllResultsPresent_Done()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("OK\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("OK",
            "firmware should print OK after all @extern calls complete");
    }
}
