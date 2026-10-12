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

// The driver probes --help for these before passing them (src/driver/backends/__init__.py).
var stdoutBaudOpt = new Option<int>("--stdout-baud")
{
    Description = "Baud rate of the program's diagnostic output ([tool.pymcu] stdout_baud)",
    DefaultValueFactory = _ => 115200
};

var uartOwnedOpt = new Option<bool>("--uart-owned")
{
    Description = "The program sets the UART up itself, so the unhandled-exception report needs no setup of its own"
};

// RFC 0009 capability flag: the backend moves the Optional/Union member tag (payload in a0,
// tag in a1) whenever the IR carries one, so the flag only lets the driver tell this binary
// from one that would drop the tag.
var returnTagsOpt = new Option<bool>("--return-tags")
{
    Description = "Declare support for RFC 0009 Optional return tags (payload + member byte)"
};

var rootCmd = new RootCommand("pymcuc-riscv — PyMCU RISC-V backend runner");
rootCmd.Arguments.Add(irFileArg);
rootCmd.Options.Add(outputOpt);
rootCmd.Options.Add(targetOpt);
rootCmd.Options.Add(archOpt);
rootCmd.Options.Add(freqOpt);
rootCmd.Options.Add(configOpt);
rootCmd.Options.Add(verboseOpt);
rootCmd.Options.Add(stdoutBaudOpt);
rootCmd.Options.Add(uartOwnedOpt);
rootCmd.Options.Add(returnTagsOpt);

rootCmd.SetAction(pr =>
{
    var irFile  = pr.GetValue(irFileArg) ?? "";
    var output  = pr.GetValue(outputOpt) ?? "";
    var target  = pr.GetValue(targetOpt) ?? "";
    var arch    = pr.GetValue(archOpt) ?? "riscv";
    var freq    = pr.GetValue(freqOpt);
    var configs = pr.GetValue(configOpt) ?? [];
    var verbose = pr.GetValue(verboseOpt);
    var stdoutBaud = pr.GetValue(stdoutBaudOpt);
    var uartOwned = pr.GetValue(uartOwnedOpt);

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
        Chip       = target,
        TargetChip = target,
        Arch       = arch,
        Frequency  = freq,
        StdoutBaud = stdoutBaud,
        UartOwnedByProgram = uartOwned,
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
