using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/fp-mul-debug.
///
/// Diagnostic tests for the AVR soft-float __fp_mul routine.
///
/// After each __fp_mul call the fixture captures the float result (in R22:R25)
/// into four MMIO registers via inline OUT instructions — the AVR equivalent
/// of C's per-byte reinterpret-cast (*((uint8_t*)&f)):
///   OUT 0x1E, R22  ->  GPIOR0 (0x3E) = byte3 / LSB
///   OUT 0x2A, R23  ->  GPIOR1 (0x4A) = byte2
///   OUT 0x2B, R24  ->  GPIOR2 (0x4B) = byte1
///   OUT 0x27, R25  ->  OCR0A  (0x47) = byte0 / MSB
/// The test asserts those four locations for the expected IEEE 754 pattern.
/// R22:R25 are also printed from data-space (Rn = Data[n]) for cross-check.
///
/// Checkpoints and expected IEEE 754 bit patterns:
///   CP1  10 * 0.1 =   1.0   0x3F800000   MSB=0x3F B1=0x80 B2=0x00 LSB=0x00
///   CP2 550 * 0.1 =  55.0   0x425C0000   MSB=0x42 B1=0x5C B2=0x00 LSB=0x00
///   CP3  r2 * 10  = 550.0   0x44098000   MSB=0x44 B1=0x09 B2=0x80 LSB=0x00
///   CP4 235 * 0.1 =  23.5   0x41BC0000   MSB=0x41 B1=0xBC B2=0x00 LSB=0x00
///   CP5  r4 * 10  = 235.0   0x436B0000   MSB=0x43 B1=0x6B B2=0x00 LSB=0x00
/// </summary>
[TestFixture]
public class FpMulDebugTests
{
    // ---------------------------------------------------------------------------
    // MMIO addresses written by _capture_float_bits() via OUT instructions.
    // OUT port -> data-space:  0x1E->0x3E(GPIOR0)  0x2A->0x4A(GPIOR1)
    //                          0x2B->0x4B(GPIOR2)  0x27->0x47(OCR0A)
    // ---------------------------------------------------------------------------
    private const int Gpior0 = 0x3E;  // byte3 / LSB  (R22)
    private const int Gpior1 = 0x4A;  // byte2        (R23)
    private const int Gpior2 = 0x4B;  // byte1        (R24)
    private const int Ocr0A  = 0x47;  // byte0 / MSB  (R25)

    // Data-space addresses of AVR general-purpose registers (Rn = Data[n]).
    // Used for cross-check only.
    private const int Rn22 = 0x16;
    private const int Rn23 = 0x17;
    private const int Rn24 = 0x18;
    private const int Rn25 = 0x19;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("fp-mul-debug"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    /// <summary>Advances through N BREAK checkpoints.</summary>
    private static void SkipBreaks(ArduinoUnoSimulation uno, int count)
    {
        for (var i = 0; i < count; i++)
        {
            uno.RunToBreak();
            uno.RunInstructions(1);
        }
    }

    /// <summary>
    /// Reads the float bits captured by _capture_float_bits() from MMIO,
    /// and also reads R22:R25 from data-space for cross-check.
    /// Format: MSB(OCR0A) : B1(GPIOR2) : B2(GPIOR1) : LSB(GPIOR0)
    /// </summary>
    private static string FloatDump(ArduinoUnoSimulation uno, string label)
    {
        byte lsb  = uno.Data[Gpior0];  // R22 captured via OUT
        byte b2   = uno.Data[Gpior1];  // R23 captured via OUT
        byte b1   = uno.Data[Gpior2];  // R24 captured via OUT
        byte msb  = uno.Data[Ocr0A];   // R25 captured via OUT
        byte r22  = uno.Data[Rn22];    // R22 live register (cross-check)
        byte r25  = uno.Data[Rn25];    // R25 live register (cross-check)
        return $"{label}: captured={msb:X2}:{b1:X2}:{b2:X2}:{lsb:X2}" +
               $"  live-R25:R22={r25:X2}:{r22:X2}";
    }

    // -------------------------------------------------------------------------
    // Checkpoint 1: (uint16)10 * 0.1 = 1.0
    // Exponents: 130 (float(10)) + 123 (float(0.1)) - 127 = 126,
    // but 1.25 * 1.6 = 2.0 causes a mantissa carry, so result exp = 127.
    // float(1.0) = 0x3F800000  MSB=0x3F B1=0x80 B2=0x00 LSB=0x00
    // -------------------------------------------------------------------------

    [Test]
    public void Cp1_10_times_0p1_MSB_is_0x3F()
    {
        var uno = Boot();
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP1 10*0.1->1.0"));
        uno.Data[Ocr0A].Should().Be(0x3F, "float(1.0) MSB (OCR0A) must be 0x3F");
    }

    [Test]
    public void Cp1_10_times_0p1_B1_is_0x80()
    {
        var uno = Boot();
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP1 10*0.1->1.0"));
        uno.Data[Gpior2].Should().Be(0x80, "float(1.0) byte1 (GPIOR2) must be 0x80");
    }

    [Test]
    public void Cp1_10_times_0p1_B2_and_LSB_are_zero()
    {
        var uno = Boot();
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP1 10*0.1->1.0"));
        uno.Data[Gpior1].Should().Be(0x00, "float(1.0) byte2 (GPIOR1) must be zero");
        uno.Data[Gpior0].Should().Be(0x00, "float(1.0) LSB (GPIOR0) must be zero");
    }

    // -------------------------------------------------------------------------
    // Checkpoint 2: (uint16)550 * 0.1 = 55.0   (DHT22 humidity path)
    // float(550) exp=136, float(0.1) exp=123. result exp=136+123-127=132.
    // float(55.0) = 0x425C0000  MSB=0x42 B1=0x5C B2=0x00 LSB=0x00
    // -------------------------------------------------------------------------

    [Test]
    public void Cp2_550_times_0p1_MSB_is_0x42()
    {
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP2 550*0.1->55.0"));
        uno.Data[Ocr0A].Should().Be(0x42, "float(55.0) MSB (OCR0A) must be 0x42");
    }

    [Test]
    public void Cp2_550_times_0p1_B1_is_0x5C()
    {
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP2 550*0.1->55.0"));
        uno.Data[Gpior2].Should().Be(0x5C, "float(55.0) byte1 (GPIOR2) must be 0x5C");
    }

    [Test]
    public void Cp2_550_times_0p1_B2_and_LSB_are_zero()
    {
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP2 550*0.1->55.0"));
        uno.Data[Gpior1].Should().Be(0x00, "float(55.0) byte2 (GPIOR1) must be zero");
        uno.Data[Gpior0].Should().Be(0x00, "float(55.0) LSB (GPIOR0) must be zero");
    }

    // -------------------------------------------------------------------------
    // Checkpoint 3: r2 * 10.0 = 550.0   (float*float, uart_write_float path)
    // float(55.0) exp=132, float(10.0) exp=130. result exp=132+130-127=135.
    // 1.71875 * 1.25 = 2.148... -> carry -> exp=136.
    // float(550.0) = 0x44098000  MSB=0x44 B1=0x09 B2=0x80 LSB=0x00
    // -------------------------------------------------------------------------

    [Test]
    public void Cp3_r2_times_10_MSB_is_0x44()
    {
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP3 r2*10->550.0"));
        uno.Data[Ocr0A].Should().Be(0x44, "float(550.0) MSB (OCR0A) must be 0x44");
    }

    [Test]
    public void Cp3_r2_times_10_B1_is_0x09()
    {
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP3 r2*10->550.0"));
        uno.Data[Gpior2].Should().Be(0x09, "float(550.0) byte1 (GPIOR2) must be 0x09");
    }

    [Test]
    public void Cp3_r2_times_10_B2_is_0x80()
    {
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP3 r2*10->550.0"));
        uno.Data[Gpior1].Should().Be(0x80, "float(550.0) byte2 (GPIOR1) must be 0x80 (non-zero mantissa)");
    }

    [Test]
    public void Cp3_r2_times_10_LSB_is_zero()
    {
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP3 r2*10->550.0"));
        uno.Data[Gpior0].Should().Be(0x00, "float(550.0) LSB (GPIOR0) must be zero");
    }

    // -------------------------------------------------------------------------
    // Checkpoint 4: (uint16)235 * 0.1 = 23.5   (DHT22 temperature path)
    // float(235) exp=134, float(0.1) exp=123. result exp=134+123-127=130.
    // float(23.5) = 0x41BC0000  MSB=0x41 B1=0xBC B2=0x00 LSB=0x00
    // -------------------------------------------------------------------------

    [Test]
    public void Cp4_235_times_0p1_MSB_is_0x41()
    {
        var uno = Boot();
        SkipBreaks(uno, 3);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP4 235*0.1->23.5"));
        uno.Data[Ocr0A].Should().Be(0x41, "float(23.5) MSB (OCR0A) must be 0x41");
    }

    [Test]
    public void Cp4_235_times_0p1_B1_is_0xBC()
    {
        var uno = Boot();
        SkipBreaks(uno, 3);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP4 235*0.1->23.5"));
        uno.Data[Gpior2].Should().Be(0xBC, "float(23.5) byte1 (GPIOR2) must be 0xBC");
    }

    [Test]
    public void Cp4_235_times_0p1_B2_and_LSB_are_zero()
    {
        var uno = Boot();
        SkipBreaks(uno, 3);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP4 235*0.1->23.5"));
        uno.Data[Gpior1].Should().Be(0x00, "float(23.5) byte2 (GPIOR1) must be zero");
        uno.Data[Gpior0].Should().Be(0x00, "float(23.5) LSB (GPIOR0) must be zero");
    }

    // -------------------------------------------------------------------------
    // Checkpoint 5: r4 * 10.0 = 235.0   (float*float, uart_write_float path)
    // float(23.5) exp=131, float(10.0) exp=130. result exp=131+130-127=134.
    // 1.46875 * 1.25 = 1.835... -> no carry -> exp stays at 134.
    // float(235.0) = 0x436B0000  MSB=0x43 B1=0x6B B2=0x00 LSB=0x00
    // -------------------------------------------------------------------------

    [Test]
    public void Cp5_r4_times_10_MSB_is_0x43()
    {
        var uno = Boot();
        SkipBreaks(uno, 4);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP5 r4*10->235.0"));
        uno.Data[Ocr0A].Should().Be(0x43, "float(235.0) MSB (OCR0A) must be 0x43");
    }

    [Test]
    public void Cp5_r4_times_10_B1_is_0x6B()
    {
        var uno = Boot();
        SkipBreaks(uno, 4);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP5 r4*10->235.0"));
        uno.Data[Gpior2].Should().Be(0x6B, "float(235.0) byte1 (GPIOR2) must be 0x6B");
    }

    [Test]
    public void Cp5_r4_times_10_B2_and_LSB_are_zero()
    {
        var uno = Boot();
        SkipBreaks(uno, 4);
        uno.RunToBreak();
        TestContext.Out.WriteLine(FloatDump(uno, "CP5 r4*10->235.0"));
        uno.Data[Gpior1].Should().Be(0x00, "float(235.0) byte2 (GPIOR1) must be zero");
        uno.Data[Gpior0].Should().Be(0x00, "float(235.0) LSB (GPIOR0) must be zero");
    }
}
