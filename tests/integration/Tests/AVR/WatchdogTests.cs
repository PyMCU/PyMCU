using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/watchdog.
/// Tests Watchdog.enable(), Watchdog.feed(), Watchdog.disable() compilation and execution.
/// Expected UART sequence: "WDT INIT\n", ten "FEED\n" lines, "DONE\n".
/// </summary>
[TestFixture]
public class WatchdogTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("watchdog");

    [Test]
    public void Boot_PrintsWdtInit()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "WDT INIT", maxMs: 200);
        uno.Serial.Should().Contain("WDT INIT");
    }

    [Test]
    public void Boot_PrintsTenFeeds()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE", maxMs: 500);
        uno.Serial.Text.Split('\n')
            .Count(line => line.Contains("FEED"))
            .Should().Be(10, "exactly 10 feed calls are expected");
    }

    [Test]
    public void Boot_PrintsDoneAfterDisable()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE", maxMs: 500);
        uno.Serial.Should().Contain("DONE");
    }

    [Test]
    public void Boot_OutputOrderIsCorrect()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE", maxMs: 500);
        var text = uno.Serial.Text;
        var initIdx = text.IndexOf("WDT INIT", StringComparison.Ordinal);
        var feedIdx = text.IndexOf("FEED",     StringComparison.Ordinal);
        var doneIdx = text.IndexOf("DONE",     StringComparison.Ordinal);
        initIdx.Should().BeLessThan(feedIdx, "WDT INIT precedes first FEED");
        feedIdx.Should().BeLessThan(doneIdx, "FEED precedes DONE");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
