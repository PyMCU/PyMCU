// SPDX-License-Identifier: MIT
// pymcuc-xtensa — AOT-compiled Xtensa backend runner.
//
// Usage:
//   pymcuc-xtensa <ir-file.mir> --output <firmware.asm> --target <chip> --freq <hz>
//                               [--emit-llvm] [--config KEY=VALUE]... [--verbose]
//
// Exit codes:
//   0  — success
//   1  — compilation / IR error
//   2  — license error

using System.CommandLine;
using PyMCU.Backend.License;
using PyMCU.Backend.Serialization;
using PyMCU.Backend.Targets.Xtensa;
using PyMCU.Common.Models;
using PyMCU.IR;

var irFileArg = new Argument<string>("ir-file")
{
    Description = "Path to the .mir IR file produced by pymcuc --emit-ir"
};

var outputOpt = new Option<string>("--output", "-o")
{
    Description = "Output file path (.asm for GAS, .ll for LLVM IR)"
};

var targetOpt = new Option<string>("--target")
{
    Description = "Target chip identifier (e.g. esp32, esp8266, esp32s3)",
    DefaultValueFactory = _ => string.Empty
};

var archOpt = new Option<string>("--arch")
{
    Description = "Architecture (xtensa, esp8266, esp32, esp32s2, esp32s3)",
    DefaultValueFactory = _ => "xtensa"
};

var freqOpt = new Option<ulong>("--freq")
{
    Description = "Clock frequency in Hz",
    DefaultValueFactory = _ => 240_000_000UL
};

var emitLlvmOpt = new Option<bool>("--emit-llvm")
{
    Description = "Emit LLVM IR (.ll) instead of GAS assembly — requires xtensa-esp-elf-clang to compile",
    DefaultValueFactory = _ => false
};

var configOpt = new Option<List<string>>("--config", "-C")
{
    Description = "Configuration bits KEY=VALUE",
    AllowMultipleArgumentsPerToken = true,
    DefaultValueFactory = _ => []
};

var verboseOpt = new Option<bool>("--verbose", "-v")
{
    Description = "Verbose output",
    DefaultValueFactory = _ => false
};

var rootCmd = new RootCommand(
    "pymcuc-xtensa — PyMCU Xtensa backend runner (ESP8266, ESP32, ESP32-S2, ESP32-S3)");
rootCmd.Arguments.Add(irFileArg);
rootCmd.Options.Add(outputOpt);
rootCmd.Options.Add(targetOpt);
rootCmd.Options.Add(archOpt);
rootCmd.Options.Add(freqOpt);
rootCmd.Options.Add(emitLlvmOpt);
rootCmd.Options.Add(configOpt);
rootCmd.Options.Add(verboseOpt);

rootCmd.SetAction(pr =>
{
    var irFile   = pr.GetValue(irFileArg) ?? "";
    var output   = pr.GetValue(outputOpt) ?? "";
    var target   = pr.GetValue(targetOpt) ?? "";
    var arch     = pr.GetValue(archOpt) ?? "xtensa";
    var freq     = pr.GetValue(freqOpt);
    var emitLlvm = pr.GetValue(emitLlvmOpt);
    var configs  = pr.GetValue(configOpt) ?? [];
    var verbose  = pr.GetValue(verboseOpt);

    // Default output extension depends on the selected codegen mode.
    if (string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(irFile))
        output = Path.ChangeExtension(irFile, emitLlvm ? ".ll" : ".asm");

    var provider = new XtensaBackendProvider();
    var license  = provider.ValidateLicense();
    if (license.Status != LicenseStatus.Valid)
    {
        Console.Error.WriteLine($"[LICENSE] {license.Message}");
        Environment.ExitCode = 2;
        return;
    }

    ProgramIR ir;
    try
    {
        ir = IrSerializer.Deserialize(irFile);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[pymcuc-xtensa] Failed to read IR file '{irFile}': {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    var cfg = new DeviceConfig
    {
        TargetChip = target,
        Chip       = string.IsNullOrEmpty(target) ? arch : target,
        Arch       = arch,
        Frequency  = freq,
    };

    if (emitLlvm)
        cfg.Fuses["llvm_backend"] = "true";

    foreach (var item in configs)
    {
        var eq = item.IndexOf('=');
        if (eq > 0) cfg.Fuses[item[..eq]] = item[(eq + 1)..];
    }

    try
    {
        var codegen = provider.Create(cfg);
        using var writer = new StreamWriter(output);
        codegen.Compile(ir, writer);
        Console.WriteLine($"[BUILD_OK] {output}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[pymcuc-xtensa] Codegen failed: {ex.Message}");
        if (verbose) Console.Error.WriteLine(ex.StackTrace);
        Environment.ExitCode = 1;
    }
});

rootCmd.Parse(args).Invoke();
