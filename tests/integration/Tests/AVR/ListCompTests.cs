using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/list-comp.
/// Exercises compile-time list comprehension unrolling and for-in constant list iteration.
/// </summary>
[TestFixture]
public class ListCompTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("list-comp");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "LC\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("LC");

    [Test]
    public void ListComp_RangeN_SumIsCorrect()
    {
        // [x * 2 for x in range(4)] = [0, 2, 4, 6]; sum = 12 = 0x0C
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("R:0C\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("R:0C",
            "[x*2 for x in range(4)] sums to 0+2+4+6=12=0x0C");
    }

    [Test]
    public void ListComp_ConstList_SumIsCorrect()
    {
        // [v + 1 for v in [10, 20, 30]] = [11, 21, 31]; sum = 63 = 0x3F
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("L:3F\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("L:3F",
            "[v+1 for v in [10,20,30]] sums to 11+21+31=63=0x3F");
    }

    [Test]
    public void ForIn_ConstList_SumIsCorrect()
    {
        // for x in [1, 3, 5, 7]: sum_f += x  → 1+3+5+7 = 16 = 0x10
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("F:10\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("F:10",
            "for x in [1,3,5,7] sums to 16=0x10");
    }
}
