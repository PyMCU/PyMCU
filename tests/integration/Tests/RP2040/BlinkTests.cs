using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class BlinkTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("blink");

    private PicoSimulation Sim()
    {
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        return pico;
    }

    [Test]
    public void Led_StartsHighAfterBoot()
    {
        using var pico = Sim();
        // The first thing main() does is configure GP25 as output and drive it high,
        // long before the 500 ms delay elapses.
        pico.RunMilliseconds(5);
        pico.Gpio[25].Should().BeHigh();
    }

    [Test]
    public void Led_TogglesOverTime()
    {
        using var pico = Sim();
        bool sawHigh = false;
        bool sawLow = false;

        // Sample the LED across more than one full blink period (2 x 500 ms).
        for (int i = 0; i < 120 && !(sawHigh && sawLow); i++)
        {
            pico.RunMilliseconds(20);
            if (pico.Gpio[25].OutputValue) sawHigh = true;
            else sawLow = true;
        }

        sawHigh.Should().BeTrue("the LED should be driven high during a blink");
        sawLow.Should().BeTrue("the LED should be driven low during a blink");
    }
}
