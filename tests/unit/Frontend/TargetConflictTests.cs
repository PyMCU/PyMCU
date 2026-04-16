using Xunit;
using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

// Tests for PreScanVisitor arch-family validation when IsTargetEstablished = true.
public class TargetConflictTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ProgramNode ParseSource(string src)
    {
        var lexer  = new Lexer(src);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        return parser.ParseProgram();
    }

    private static DeviceConfig AvrConfig() =>
        new() { Chip = "atmega328p", TargetChip = "atmega328p", Arch = "avr" };

    private static DeviceConfig BlankConfig() => new();

    // -------------------------------------------------------------------------
    // ArchFamilyResolver
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("avr",       "avr")]
    [InlineData("avr8",      "avr")]
    [InlineData("atmega328p","avr")]
    [InlineData("atmega2560","avr")]
    [InlineData("attiny85",  "avr")]
    [InlineData("pic12",     "pic12")]
    [InlineData("pic10f200", "pic12")]
    [InlineData("pic14",     "pic14")]
    [InlineData("pic14e",    "pic14")]
    [InlineData("pic16f84a", "pic14")]
    [InlineData("pic18",     "pic18")]
    [InlineData("pic18f45k50","pic18")]
    [InlineData("riscv",     "riscv")]
    [InlineData("ch32v003",  "riscv")]
    [InlineData("pio",       "pio")]
    public void ArchFamilyResolver_KnownArch_ReturnsCorrectFamily(string input, string expected)
        => ArchFamilyResolver.Resolve(input).Should().Be(expected);

    [Fact]
    public void ArchFamilyResolver_SameFamily_AvrVariants()
        => ArchFamilyResolver.SameFamily("avr", "atmega328p").Should().BeTrue();

    [Fact]
    public void ArchFamilyResolver_DifferentFamily_AvrVsPic18()
        => ArchFamilyResolver.SameFamily("avr", "pic18f45k50").Should().BeFalse();

    // -------------------------------------------------------------------------
    // PreScanVisitor — bootstrap mode (IsTargetEstablished = false)
    // -------------------------------------------------------------------------

    [Fact]
    public void Bootstrap_DeviceInfo_SetsArchAndChip()
    {
        var cfg = BlankConfig();
        var visitor = new PreScanVisitor(cfg);
        var ast = ParseSource("device_info(chip=\"atmega328p\", arch=\"avr\", ram_size=2048)");

        visitor.Scan(ast, isTargetEstablished: false);

        cfg.Chip.Should().Be("atmega328p");
        cfg.Arch.Should().Be("avr");
        cfg.RamSize.Should().Be(2048);
    }

    // -------------------------------------------------------------------------
    // PreScanVisitor — validation mode (IsTargetEstablished = true)
    // -------------------------------------------------------------------------

    [Fact]
    public void Established_SameChip_NoException()
    {
        var cfg = AvrConfig();
        var visitor = new PreScanVisitor(cfg);
        var ast = ParseSource("device_info(chip=\"atmega328p\", arch=\"avr\", ram_size=2048)");

        // Re-scanning bootstrap chip after target is established: must be silent.
        var act = () => visitor.Scan(ast, isTargetEstablished: true);
        act.Should().NotThrow();
        cfg.Arch.Should().Be("avr");
        cfg.Chip.Should().Be("atmega328p");
    }

    [Fact]
    public void Established_SameFamily_DifferentChip_NoException_ChipUnchanged()
    {
        var cfg = AvrConfig();
        var visitor = new PreScanVisitor(cfg);
        // atmega328 (without 'p') is in the same avr family — warning, not error.
        var ast = ParseSource("device_info(chip=\"atmega328\", arch=\"avr\", ram_size=2048)");

        var act = () => visitor.Scan(ast, isTargetEstablished: true);
        act.Should().NotThrow();
        // TargetChip and Chip must remain the original bootstrap values.
        cfg.TargetChip.Should().Be("atmega328p");
        cfg.Chip.Should().Be("atmega328p");
        cfg.Arch.Should().Be("avr");
    }

    [Fact]
    public void Established_CrossArch_Pic18InAvrProject_ThrowsCompilerError()
    {
        var cfg = AvrConfig();
        var visitor = new PreScanVisitor(cfg);
        var ast = ParseSource("device_info(chip=\"pic18f45k50\", arch=\"pic18\", ram_size=2048)");

        var act = () => visitor.Scan(ast, isTargetEstablished: true);
        act.Should().Throw<CompilerError>()
            .WithMessage("*Cross-architecture import*");
    }

    [Fact]
    public void Established_CrossArch_Pic14InAvrProject_ThrowsCompilerError()
    {
        var cfg = AvrConfig();
        var visitor = new PreScanVisitor(cfg);
        var ast = ParseSource("device_info(chip=\"pic16f84a\", arch=\"pic14\", ram_size=36)");

        var act = () => visitor.Scan(ast, isTargetEstablished: true);
        act.Should().Throw<CompilerError>()
            .WithMessage("*Cross-architecture import*");
    }

    [Fact]
    public void Established_MemoryFields_NotOverwritten()
    {
        var cfg = AvrConfig();
        cfg.RamSize   = 2048;
        cfg.FlashSize = 32768;

        var visitor = new PreScanVisitor(cfg);
        // Another chip file in the same family with different memory sizes.
        var ast = ParseSource("device_info(chip=\"atmega168\", arch=\"avr\", ram_size=1024, flash_size=16384)");

        visitor.Scan(ast, isTargetEstablished: true);

        // Memory fields must NOT be overwritten when target is established.
        cfg.RamSize.Should().Be(2048);
        cfg.FlashSize.Should().Be(32768);
    }
}


