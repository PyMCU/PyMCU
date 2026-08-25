using System.Text.Json;
using PyMCU.Backend.Serialization;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// device_info(ram_size=..., flash_size=..., eeprom_size=...) has to survive the whole
/// way from the chip file to the .mir a backend reads. Before this existed the numbers
/// stopped at DeviceConfig: the PIC14 backend's `RamSize > 0 && RamSize < 96` was
/// permanently false and the 16F84A put its mul/div scratch inside __BADRAM, while the
/// AVR backend chose LPM over ELPM from a hardcoded list of chip names.
///
/// Each hop is tested separately because each hop lost the value at some point:
/// the prescan reads it, the IR generator has to copy it, the optimizer rebuilds the
/// program from scratch and has to copy it again, and the serializer has to write it.
/// </summary>
public class DeviceGeometryTests
{
    private static ProgramIR Ir(string source, DeviceConfig config)
    {
        var ast = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), config);
    }

    private static DeviceConfig PrescanOf(string deviceInfoCall)
    {
        var cfg = new DeviceConfig();
        var ast = new Parser(new Lexer(deviceInfoCall).Tokenize()).ParseProgram();
        new PreScanVisitor(cfg).Scan(ast, isTargetEstablished: false);
        return cfg;
    }

    // ---------------------------------------------------------------------
    // the prescan
    //
    // Every chip file in the stdlib writes `RAM_SIZE = 2048` on one line and
    // `device_info(ram_size=RAM_SIZE)` on the next. The prescan matched only
    // IntegerLiteral, so the argument being a name meant the size was dropped
    // without a word and DeviceConfig kept its 0 for all twenty AVR parts.
    // ---------------------------------------------------------------------

    [Fact]
    public void ASizeGivenAsAModuleConstant_IsResolved_NotDropped()
    {
        var cfg = PrescanOf(
            "RAM_SIZE = 8192\n"
            + "FLASH_SIZE = 262144\n"
            + "device_info(chip=\"atmega2560\", arch=\"avr\", ram_size=RAM_SIZE, "
            + "flash_size=FLASH_SIZE)\n");

        Assert.Equal(8192, cfg.RamSize);
        Assert.Equal(262144, cfg.FlashSize);
    }

    [Fact]
    public void AConstantDeclaredAfterTheCall_ResolvesToo()
    {
        var cfg = PrescanOf(
            "device_info(chip=\"attiny85\", arch=\"avr\", ram_size=RAM_SIZE)\n"
            + "RAM_SIZE = 512\n");

        Assert.Equal(512, cfg.RamSize);
    }

    [Fact]
    public void ASizeThatCannotBeResolved_IsRefused_NotSilentlyLeftAtZero()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => PrescanOf(
            "device_info(chip=\"atmega328p\", arch=\"avr\", ram_size=SOME_UNKNOWN_NAME)"));

        Assert.Contains("ram_size", ex.Message);
    }

    // ---------------------------------------------------------------------
    // chip file -> IR
    // ---------------------------------------------------------------------

    [Fact]
    public void WhatTheChipFileDeclares_ReachesTheIr()
    {
        var cfg = PrescanOf(
            "device_info(chip=\"atmega2560\", arch=\"avr\", ram_size=8192, flash_size=262144)");
        var ir = Ir("def main():\n    return 0\n", cfg);

        var geo = ir.RequireDevice();
        Assert.Equal("atmega2560", geo.Chip);
        Assert.Equal(8192, geo.RamSize);
        Assert.Equal(262144, geo.FlashSize);
    }

    [Fact]
    public void EepromSize_TravelsToo()
    {
        var cfg = PrescanOf(
            "device_info(chip=\"atmega328p\", arch=\"avr\", ram_size=2048, "
            + "flash_size=32768, eeprom_size=1024)");

        Assert.Equal(1024, Ir("def main():\n    return 0\n", cfg).RequireDevice().EepromSize);
    }

    // ---------------------------------------------------------------------
    // undeclared is null, not zero
    // ---------------------------------------------------------------------

    [Fact]
    public void ASizeTheChipFileDoesNotDeclare_IsUnknown_NotZero()
    {
        var cfg = PrescanOf("device_info(chip=\"attiny85\", arch=\"avr\", ram_size=512)");
        var geo = Ir("def main():\n    return 0\n", cfg).RequireDevice();

        Assert.Equal(512, geo.RamSize);
        Assert.Null(geo.FlashSize);
        Assert.Null(geo.EepromSize);
    }

    [Fact]
    public void AskingForASizeThatWasNeverDeclared_NamesTheChipAndTheField()
    {
        var geo = new DeviceGeometry { Chip = "pic16f84a", RamSize = 68 };

        var ex = Assert.Throws<InvalidOperationException>(
            () => geo.RequireFlashSize("place the reset vector"));

        Assert.Contains("pic16f84a", ex.Message);
        Assert.Contains("flash_size", ex.Message);
        Assert.Contains("place the reset vector", ex.Message);
    }

    // ---------------------------------------------------------------------
    // the optimizer rebuilds the program; geometry must survive it
    // ---------------------------------------------------------------------

    [Fact]
    public void TheOptimizerCarriesTheGeometryThrough()
    {
        var cfg = PrescanOf(
            "device_info(chip=\"atmega2560\", arch=\"avr\", ram_size=8192, flash_size=262144)");
        var optimized = Optimizer.Optimize(Ir("def main():\n    return 0\n", cfg));

        Assert.Equal(262144, optimized.RequireDevice().FlashSize);
    }

    // ---------------------------------------------------------------------
    // the .mir on disk
    // ---------------------------------------------------------------------

    [Fact]
    public void TheGeometryIsWrittenToTheMirFile()
    {
        var cfg = PrescanOf(
            "device_info(chip=\"atmega2560\", arch=\"avr\", ram_size=8192, flash_size=262144)");
        var path = Path.Combine(Path.GetTempPath(), $"pymcu-geo-{Guid.NewGuid():N}.mir");
        try
        {
            IrSerializer.Serialize(Ir("def main():\n    return 0\n", cfg), path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var device = doc.RootElement.GetProperty("device");
            Assert.Equal("atmega2560", device.GetProperty("chip").GetString());
            Assert.Equal(8192, device.GetProperty("ramSize").GetInt32());
            Assert.Equal(262144, device.GetProperty("flashSize").GetInt32());

            Assert.Equal(262144, IrSerializer.Deserialize(path).RequireDevice().FlashSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUndeclaredSizeIsAbsentFromTheMir_NotWrittenAsZero()
    {
        var cfg = PrescanOf("device_info(chip=\"attiny85\", arch=\"avr\", ram_size=512)");
        var path = Path.Combine(Path.GetTempPath(), $"pymcu-geo-{Guid.NewGuid():N}.mir");
        try
        {
            IrSerializer.Serialize(Ir("def main():\n    return 0\n", cfg), path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var device = doc.RootElement.GetProperty("device");
            Assert.False(device.TryGetProperty("flashSize", out _),
                "an undeclared flash_size must be absent, not zero: a backend reading 0 is "
                + "the bug this contract exists to end");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------------
    // a .mir from a compiler that predates the contract
    // ---------------------------------------------------------------------

    [Fact]
    public void AMirWithoutGeometry_IsRefused_NotReadAsZeros()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ProgramIR().RequireDevice());

        Assert.Contains("no device geometry", ex.Message);
        Assert.Contains("Rebuild", ex.Message);
    }
}
