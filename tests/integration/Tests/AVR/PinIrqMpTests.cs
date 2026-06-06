using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/pin-irq-mp.
///
/// Verifies machine.Pin.irq() with the standard MicroPython handler(pin) API:
///   def on_press(pin: Pin): if pin.value() == 0: flag = 1
///
/// The compiler synthesizes a parameterless ISR (_irq_synth_on_press_btn) that
/// inline-expands on_press with btn's ZCA constants bound to the pin parameter.
/// pin.value() compiles to SBIS PIND,2 -- reading the actual PD2 state.
///
/// Hardware mapping:
///   btn = Pin(2, Pin.IN)  -> PD2 = INT0, active-low button
///   led = Pin(13, Pin.OUT) -> PB5 = built-in LED
///   UART 9600 baud
///
/// Firmware behaviour:
///   Boot:    sends "PIN IRQ MP\n"
///   On INT0: if PD2 == 0 -> flag=1 -> main loop toggles PB5 + sends "PRESSED\n"
/// </summary>
[TestFixture]
public class PinIrqMpTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("pin-irq-mp"));

    // ── Boot ─────────────────────────────────────────────────────────────────

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ MP\n", maxMs: 200);
        uno.Serial.Should().ContainLine("PIN IRQ MP");
    }

    // ── Single interrupt ─────────────────────────────────────────────────────

    [Test]
    public void Interrupt_SendsPressed()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ MP\n", maxMs: 200);

        // Falling edge: PD2 high -> low (button pressed and held)
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false); // INT0 fires here; pin remains low
        uno.RunMilliseconds(20);

        uno.Serial.Should().ContainLine("PRESSED",
            "on_press(pin) should set flag when pin.value() == 0");
    }

    [Test]
    public void Interrupt_Toggles_Led()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ MP\n", maxMs: 200);
        var ledBefore = uno.PortB.GetPinState(5);

        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunMilliseconds(20);

        var ledAfter = uno.PortB.GetPinState(5);
        ledAfter.Should().NotBe(ledBefore,
            "main loop toggles PB5 each time ISR sets the flag");
    }

    // ── ZCA synthesis correctness ─────────────────────────────────────────────
    // These tests confirm that pin.value() inside on_press(pin) reads PD2 (not
    // a wrong register), i.e., the ZCA binding in the synthesized ISR is correct.

    [Test]
    public void PinValueZero_WhenPinIsLow_SetsFlag()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ MP\n", maxMs: 200);

        // Hold PD2 low so pin.value() == 0 during ISR -> flag set
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false); // falling edge + stays low
        uno.RunMilliseconds(20);

        uno.Serial.Lines.Should().Contain("PRESSED",
            "pin.value() == 0 when PD2 is low -> ISR sets flag");
    }

    [Test]
    public void PinValueOne_WhenPinGoesHighBeforeIsr_NoPress()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ MP\n", maxMs: 200);
        var linesBefore = uno.Serial.Lines.Count;

        // Pulse so short the ISR fires but PD2 is back high before ISR body reads it.
        // This tests that pin.value() checks the real-time PD2 state, not a cached value.
        uno.PortD.SetPinValue(2, true);
        uno.RunCycles(10);                // a few cycles high
        uno.PortD.SetPinValue(2, false);  // falling edge triggers INT0
        uno.RunCycles(10);
        uno.PortD.SetPinValue(2, true);   // back high before ISR reads PIND
        uno.RunMilliseconds(20);

        // pin.value() == 1 (PD2 high) -> condition false -> flag NOT set -> no "PRESSED"
        var newLines = uno.Serial.Lines.Skip(linesBefore).ToList();
        newLines.Should().NotContain("PRESSED",
            "pin.value() == 1 when PD2 is high -> ISR should not set flag");
    }

    // ── Multiple interrupts ───────────────────────────────────────────────────

    [Test]
    public void MultipleInterrupts_SendPressedEachTime()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ MP\n", maxMs: 200);

        // Issue all 3 presses, waiting for each "PRESSED\n" before the next
        var pressCount = 0;
        void Press()
        {
            pressCount++;
            var target = pressCount;
            uno.PortD.SetPinValue(2, true);
            uno.RunMilliseconds(5);
            uno.PortD.SetPinValue(2, false);
            uno.RunUntilSerial(uno.Serial,
                text => text.Split("PRESSED\n").Length - 1 >= target,
                maxMs: 500);
        }

        Press(); Press(); Press();

        // Count total "PRESSED" lines — firmware only emits them on interrupt
        var pressed = uno.Serial.Lines.Count(l => l == "PRESSED");
        pressed.Should().Be(3, "each falling edge should produce exactly one PRESSED line");
    }

    [Test]
    public void MultipleInterrupts_Toggle_LedEachTime()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ MP\n", maxMs: 200);

        var states = new AVR8Sharp.Core.Peripherals.PinState[4];
        states[0] = uno.PortB.GetPinState(5);

        for (var i = 0; i < 3; i++)
        {
            uno.PortD.SetPinValue(2, true);
            uno.RunMilliseconds(2);
            uno.PortD.SetPinValue(2, false);
            uno.RunMilliseconds(30);
            states[i + 1] = uno.PortB.GetPinState(5);
        }

        for (var i = 0; i < 3; i++)
            states[i + 1].Should().NotBe(states[i],
                $"LED should toggle on press {i + 1}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim()
    {
        var uno = _session.Reset();
        uno.PortD.SetPinValue(2, true); // button released initially (PD2 high)
        return uno;
    }
}
