using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class UartEchoTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("uart-echo");

    private PicoSimulation Sim()
    {
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        return pico;
    }

    [Test]
    public void Boot_SendsEchoBanner()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "ECHO", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("ECHO");
    }

    [Test]
    public void Echo_SingleByte()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "ECHO", timeoutMs: 20_000);
        var before = pico.Uart0.ByteCount;

        pico.Uart0.InjectByte(0x41); // 'A'
        pico.RunUntilOutput(pico.Uart0, _ => pico.Uart0.ByteCount > before, timeoutMs: 5_000)
            .Should().BeTrue("the firmware should echo the injected byte");

        pico.Uart0.Bytes[^1].Should().Be(0x41);
    }
}
