using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/nested-listcomp.
/// Exercises nested list comprehension, if-filter comprehension, and bytearray.
/// </summary>
[TestFixture]
public class NestedListCompTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("nested-listcomp");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "NLC\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("NLC");

    [Test]
    public void NestedListComp_SumIsCorrect()
    {
        // [x+y for x in [1,2,3] for y in [10,20,30]]
        // = [11,21,31, 12,22,32, 13,23,33]  sum = 198 = 0xC6
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("N:C6\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("N:C6",
            "nested [x+y for x in [1,2,3] for y in [10,20,30]] sums to 198=0xC6");
    }

    [Test]
    public void FilteredListComp_SumIsCorrect()
    {
        // [x for x in [1,2,3,4,5,6] if x > 3] = [4,5,6]  sum = 15 = 0x0F
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("F:0F\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("F:0F",
            "[x for x in [1..6] if x>3] = [4,5,6] sums to 15=0x0F");
    }

    [Test]
    public void Bytearray_WriteAndRead()
    {
        // buf=bytearray(4); buf[0]=0xAA; buf[3]=0xBB; result=buf[0]+buf[3]
        // 0xAA(170) + 0xBB(187) = 357 = 0x165, low byte = 0x65
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:65\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("B:65",
            "bytearray write/read: 0xAA+0xBB=0x165, low byte 0x65");
    }
}
