using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Verifies that the CircuitPython compat layer (digitalio.DigitalInOut +
/// busio.UART) compiles to correct RP2040 firmware.  The source at
/// examples/rp2040/cp-digitalio-uart is identical to what runs under
/// CircuitPython on a real Pico -- same file, two targets.
/// </summary>
[TestFixture]
public class CpDigitalioUartTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("cp-digitalio-uart");

    private PicoSimulation Sim()
    {
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        return pico;
    }

    [Test]
    public void Boot_SendsReadyBanner()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "READY", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("READY");
    }

    [Test]
    public void Echo_OddByte_LedHigh()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "READY", timeoutMs: 20_000);
        var before = pico.Uart0.ByteCount;

        pico.Uart0.InjectByte(0x41); // 'A' -- LSB=1, LED should go high
        pico.RunUntilOutput(pico.Uart0, _ => pico.Uart0.ByteCount > before, timeoutMs: 5_000)
            .Should().BeTrue("firmware should echo the byte");

        pico.Uart0.Bytes[^1].Should().Be(0x41);
        pico.Gpio[25].Should().BeHigh("LED mirrors the LSB of the received byte");
    }

    [Test]
    public void Echo_EvenByte_LedLow()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "READY", timeoutMs: 20_000);
        var before = pico.Uart0.ByteCount;

        pico.Uart0.InjectByte(0x42); // 'B' -- LSB=0, LED should go low
        pico.RunUntilOutput(pico.Uart0, _ => pico.Uart0.ByteCount > before, timeoutMs: 5_000)
            .Should().BeTrue("firmware should echo the byte");

        pico.Uart0.Bytes[^1].Should().Be(0x42);
        pico.Gpio[25].Should().BeLow("LED mirrors the LSB of the received byte");
    }
}
