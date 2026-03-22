using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/builtin-ops.
/// Exercises the new language features:
///   - in / not in  membership operators on constant lists
///   - is / is not  identity operators (maps to == / != on MCU)
///   - sum(list)    compile-time summation built-in
///   - any(list)    compile-time / runtime OR-chain built-in
///   - all(list)    compile-time / runtime AND-chain built-in
///   - divmod(a,b)  quotient + remainder built-in
/// </summary>
[TestFixture]
public class BuiltinOpsTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("builtin-ops");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "BO\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("BO");

    [Test]
    public void InOperator_MemberFound_ReturnsOne()
    {
        // x=3, x in [1,2,3] => result=1 => I:01
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("I:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("I:01", "3 in [1,2,3] should be 1");
    }

    [Test]
    public void NotInOperator_MemberMissing_ReturnsOne()
    {
        // y=5, y not in [1,2,3] => result=1 => N:01
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("N:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("N:01", "5 not in [1,2,3] should be 1");
    }

    [Test]
    public void IsOperator_EqualValue_ReturnsOne()
    {
        // z=7, z is 7 => result=1 => S:01
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("S:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("S:01", "z is 7 (z=7) should be 1");
    }

    [Test]
    public void IsNotOperator_DifferentValue_ReturnsOne()
    {
        // z=7, z is not 3 => result=1 => T:01
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("T:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("T:01", "z is not 3 (z=7) should be 1");
    }

    [Test]
    public void SumBuiltin_ConstantList_ReturnsCorrectSum()
    {
        // sum([1,2,3]) = 6 => U:06
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("U:06\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("U:06", "sum([1,2,3]) should be 6");
    }

    [Test]
    public void AnyBuiltin_ListWithTruthy_ReturnsOne()
    {
        // any([0,0,1]) = 1 => A:01
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("A:01", "any([0,0,1]) should be 1");
    }

    [Test]
    public void AllBuiltin_AllNonZero_ReturnsOne()
    {
        // all([1,1,1]) = 1 => L:01
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("L:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("L:01", "all([1,1,1]) should be 1");
    }

    [Test]
    public void DivmodBuiltin_Quotient_IsCorrect()
    {
        // divmod(10, 3) quotient=3 => Q:03
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("Q:03\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("Q:03", "divmod(10,3) quotient should be 3");
    }

    [Test]
    public void DivmodBuiltin_Remainder_IsCorrect()
    {
        // divmod(10, 3) remainder=1 => R:01
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("R:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("R:01", "divmod(10,3) remainder should be 1");
    }

    [Test]
    public void HexBuiltin_CompileTimeConversion_IsCorrect()
    {
        // hex(255) => "0xff" => H:0xff
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("H:0xff\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("H:0xff", "hex(255) should produce '0xff'");
    }

    [Test]
    public void BinBuiltin_CompileTimeConversion_IsCorrect()
    {
        // bin(5) => "0b101" => B:0b101
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:0b101\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("B:0b101", "bin(5) should produce '0b101'");
    }

    [Test]
    public void UartAvailable_NoDataWaiting_ReturnsZero()
    {
        // uart.available() with no pending RX data => 0 => V:00
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("V:00\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("V:00", "uart.available() with no RX data should return 0");
    }
}
