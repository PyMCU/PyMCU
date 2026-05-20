// SPDX-License-Identifier: MIT
// pymcuc-riscv — AOT-compiled RISC-V backend runner.
//
// Usage:
//   pymcuc-riscv <ir-file.mir> --output <firmware.asm> --target <chip> --freq <hz>
//                               [--config KEY=VALUE]... [--verbose]
//
// Exit codes:
//   0  — success
//   1  — compilation / IR error
//   2  — license error

using System.CommandLine;
using PyMCU.Backend.License;
using PyMCU.Backend.Serialization;
using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;

var irFileArg = new Argument<string>("ir-file")
{
    Description = "Path to the .mir IR file produced by pymcuc --emit-ir"
};

var outputOpt = new Option<string>("--output", "-o")
{
    Description = "Output ASM file path"
};

var targetOpt = new Option<string>("--target")
{
    Description = "Target chip identifier (e.g. ch32v003f4p6)",
    DefaultValueFactory = _ => string.Empty
};

var archOpt = new Option<string>("--arch")
{
    Description = "Architecture (riscv, rv32ec)",
    DefaultValueFactory = _ => "riscv"
};

var freqOpt = new Option<ulong>("--freq")
{
    Description = "Clock frequency in Hz",
    DefaultValueFactory = _ => 8_000_000UL
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

var rootCmd = new RootCommand("pymcuc-riscv — PyMCU RISC-V backend runner");
rootCmd.Arguments.Add(irFileArg);
rootCmd.Options.Add(outputOpt);
rootCmd.Options.Add(targetOpt);
rootCmd.Options.Add(archOpt);
rootCmd.Options.Add(freqOpt);
rootCmd.Options.Add(configOpt);
rootCmd.Options.Add(verboseOpt);

rootCmd.SetAction(pr =>
{
    var irFile  = pr.GetValue(irFileArg) ?? "";
    var output  = pr.GetValue(outputOpt) ?? "";
    var target  = pr.GetValue(targetOpt) ?? "";
    var arch    = pr.GetValue(archOpt) ?? "riscv";
    var freq    = pr.GetValue(freqOpt);
    var configs = pr.GetValue(configOpt) ?? [];
    var verbose = pr.GetValue(verboseOpt);

    if (string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(irFile))
        output = Path.ChangeExtension(irFile, ".asm");

    var provider = new RiscVBackendProvider();
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
        Console.Error.WriteLine($"[pymcuc-riscv] Failed to read IR file '{irFile}': {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    var cfg = new DeviceConfig
    {
        TargetChip = target,
        Arch       = arch,
        Frequency  = freq,
    };
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
        Console.Error.WriteLine($"[pymcuc-riscv] Codegen failed: {ex.Message}");
        if (verbose) Console.Error.WriteLine(ex.StackTrace);
        Environment.ExitCode = 1;
    }
});

rootCmd.Parse(args).Invoke();
