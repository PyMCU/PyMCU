using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Verifies that the MicroPython compat layer (machine.Pin + machine.UART) compiles
/// to functionally correct RP2040 firmware.  The source at examples/rp2040/mp-uart-echo
/// is identical to what runs under MicroPython on a real Pico -- same file, two targets.
/// </summary>
[TestFixture]
public class MpUartEchoTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("mp-uart-echo");

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
    public void Echo_SingleByte()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "READY", timeoutMs: 20_000);
        var before = pico.Uart0.ByteCount;

        pico.Uart0.InjectByte(0x42); // 'B'
        pico.RunUntilOutput(pico.Uart0, _ => pico.Uart0.ByteCount > before, timeoutMs: 5_000)
            .Should().BeTrue("the machine.UART firmware should echo the injected byte");

        pico.Uart0.Bytes[^1].Should().Be(0x42);
    }

    [Test]
    public void Echo_SequentialBytes()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "READY", timeoutMs: 20_000);

        byte[] payload = [0x50, 0x59, 0x4D, 0x43, 0x55]; // "PYMCU"
        foreach (var b in payload)
        {
            var before = pico.Uart0.ByteCount;
            pico.Uart0.InjectByte(b);
            pico.RunUntilOutput(pico.Uart0, _ => pico.Uart0.ByteCount > before, timeoutMs: 5_000)
                .Should().BeTrue($"byte 0x{b:X2} should be echoed");
            pico.Uart0.Bytes[^1].Should().Be(b);
        }
    }
}
