using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/asm-constraints.
///
/// Verifies that inline ASM with %N register constraint placeholders loads
/// each operand into a scratch register (R16, R17, …), substitutes %N in
/// the template, emits the assembly, and stores the scratch register back
/// into the variable.
///
/// Checkpoint 1: asm("LDI %0, 42", result) → result = 42 = 0x2A
///   GPIOR0 = 0x2A
///
/// Checkpoint 2: asm("MOV %0, %1", dst, src) where src=0xFF → dst = 0xFF
///   GPIOR0 = 0xFF
///
/// Checkpoint 3: asm("INC %0", val) where val=9 → val = 10 = 0x0A
///   GPIOR0 = 0x0A
///
/// Data-space address (ATmega328P): GPIOR0 = 0x3E
/// </summary>
[TestFixture]
public class AsmConstraintsTests
{
    private const int Gpior0Addr = 0x3E;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("asm-constraints");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    [Test]
    public void Cp1_LDI_Constraint_SetsResult42()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x2A,
            "asm(\"LDI %0, 42\", result) must set result = 42 = 0x2A");
    }

    [Test]
    public void Cp2_MOV_Constraint_CopiesFF()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0xFF,
            "asm(\"MOV %0, %1\", dst, src=0xFF) must copy 0xFF into dst");
    }

    [Test]
    public void Cp3_INC_Constraint_Increments9To10()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x0A,
            "asm(\"INC %0\", val=9) must increment to 10 = 0x0A");
    }
}
