using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/match-advanced.
/// Exercises PEP 634 extensions:
///   F2: match guard  case x if x > 50:
///   F3: sequence pattern  case [0xFF, cmd, data]:
///   F4: capture pattern   case v:  /  case 1|2 as n:
/// </summary>
[TestFixture]
public class MatchAdvancedTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("match-advanced");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "MA\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("MA");

    [Test]
    public void Guard_HighValue_MatchesGuardCase()
    {
        // val=80; case x if x > 50: -> "G:HI"
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("G:HI"), maxMs: 300);
        uno.Serial.Text.Should().Contain("G:HI",
            "val=80 > 50 should match the guard case");
    }

    [Test]
    public void Guard_LowValue_FallsThrough()
    {
        // val=20; case x if x > 50: fails guard -> case _: -> "G:LO"
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("G:LO"), maxMs: 300);
        uno.Serial.Text.Should().Contain("G:LO",
            "val=20 fails guard (not > 50), should fall to default -> G:LO");
    }

    [Test]
    public void SequencePattern_PacketMatch_ExtractsCmd()
    {
        // packet=[0xFF,42,0]; case [0xFF,cmd,data]: cmd=42=0x2A -> "S:2A"
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("S:2A"), maxMs: 300);
        uno.Serial.Text.Should().Contain("S:2A",
            "cmd=42=0x2A extracted from sequence pattern [0xFF,cmd,data]");
    }

    [Test]
    public void CapturePattern_BareIdentifier_BindsValue()
    {
        // probe=7; case v: v bound to 7 -> "C:07"
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:07"), maxMs: 300);
        uno.Serial.Text.Should().Contain("C:07",
            "bare capture 'case v' should bind v=7=0x07");
    }

    [Test]
    public void OrCapture_AsBinding_BindsValue()
    {
        // code=2; case 1|2 as n: n=2 -> "O:02"
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("O:02"), maxMs: 300);
        uno.Serial.Text.Should().Contain("O:02",
            "case 1|2 as n should bind n=2=0x02");
    }
}
