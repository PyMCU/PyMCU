using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/dunder-ops.
/// Exercises F7 dunder method operator overloading:
///   __add__, __sub__, __len__, __contains__, __getitem__
/// </summary>
[TestFixture]
public class DunderOpsTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("dunder-ops");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "DO\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("DO");

    [Test]
    public void Add_VecPlusVec_YComponentCorrect()
    {
        // Vec(3,4).__add__(Vec(1,3)) -> y = 4+3 = 7 = 0x07
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:07"), maxMs: 300);
        uno.Serial.Text.Should().Contain("A:07",
            "__add__: (3+1,4+3).y = 7 = 0x07");
    }

    [Test]
    public void Sub_VecMinusVec_XComponentCorrect()
    {
        // Vec(5,4).__sub__(Vec(3,2)) -> x = 5-3 = 2 = 0x02
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("S:02"), maxMs: 300);
        uno.Serial.Text.Should().Contain("S:02",
            "__sub__: (5-3,4-2).x = 2 = 0x02");
    }

    [Test]
    public void Len_Vec_IsTwo()
    {
        // len(Vec(3,4)) -> __len__ returns 2
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("L:02"), maxMs: 300);
        uno.Serial.Text.Should().Contain("L:02",
            "len(Vec) via __len__ should return 2");
    }

    [Test]
    public void Contains_ElementPresent_ReturnsOne()
    {
        // 3 in Vec(3,4) -> __contains__ -> True = 1
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:01"), maxMs: 300);
        uno.Serial.Text.Should().Contain("C:01",
            "3 in Vec(3,4) via __contains__ should be True = 1");
    }

    [Test]
    public void GetItem_IndexOne_ReturnsY()
    {
        // Vec(3,4)[1] -> __getitem__(1) -> y = 4 = 0x04
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("G:04"), maxMs: 300);
        uno.Serial.Text.Should().Contain("G:04",
            "Vec(3,4)[1] via __getitem__ should return y=4=0x04");
    }
}
