// SPDX-License-Identifier: MIT
// pymcuc-rp2040 - AOT-compiled RP2040 backend runner.
//
// Usage:
//   pymcuc-rp2040 <ir-file.mir> --output <firmware.ll> --target <chip> --freq <hz>
//                               [--config KEY=VALUE]... [--reset-vector N]
//                               [--interrupt-vector N] [--verbose]
//
// Reads a .mir IR file produced by `pymcuc --emit-ir` and emits LLVM IR text
// (.ll). The rp2040 toolchain plugin then drives LLVM (opt/llc/lld) to produce
// a flashable image.
//
// Exit codes:
//   0  - success
//   1  - compilation / IR error
//   2  - license error (should not occur for RP2040 since it is free)

using System.CommandLine;
using PyMCU.Backend.License;
using PyMCU.Backend.Serialization;
using PyMCU.Backend.Targets.RP2040;
using PyMCU.Common.Models;
using PyMCU.IR;

var irFileArg = new Argument<string>("ir-file")
{
    Description = "Path to the .mir IR file produced by pymcuc --emit-ir"
};

var outputOpt = new Option<string>("--output", "-o")
{
    Description = "Output LLVM IR (.ll) file path"
};

var targetOpt = new Option<string>("--target")
{
    Description = "Target chip identifier (e.g. rp2040)",
    DefaultValueFactory = _ => string.Empty
};

var freqOpt = new Option<ulong>("--freq")
{
    Description = "Clock frequency in Hz",
    DefaultValueFactory = _ => 125_000_000UL
};

var configOpt = new Option<List<string>>("--config", "-C")
{
    Description = "Configuration values KEY=VALUE",
    AllowMultipleArgumentsPerToken = true,
    DefaultValueFactory = _ => []
};

var resetVecOpt = new Option<int>("--reset-vector")
{
    Description = "Reset vector address",
    DefaultValueFactory = _ => -1
};

var intVecOpt = new Option<int>("--interrupt-vector")
{
    Description = "Interrupt vector address",
    DefaultValueFactory = _ => -1
};

var verboseOpt = new Option<bool>("--verbose", "-v")
{
    Description = "Verbose output",
    DefaultValueFactory = _ => false
};

var rootCmd = new RootCommand("pymcuc-rp2040 - PyMCU RP2040 (LLVM IR) backend runner");
rootCmd.Arguments.Add(irFileArg);
rootCmd.Options.Add(outputOpt);
rootCmd.Options.Add(targetOpt);
rootCmd.Options.Add(freqOpt);
rootCmd.Options.Add(configOpt);
rootCmd.Options.Add(resetVecOpt);
rootCmd.Options.Add(intVecOpt);
rootCmd.Options.Add(verboseOpt);

rootCmd.SetAction(pr =>
{
    var irFile   = pr.GetValue(irFileArg) ?? "";
    var output   = pr.GetValue(outputOpt) ?? "";
    var target   = pr.GetValue(targetOpt) ?? "";
    var freq     = pr.GetValue(freqOpt);
    var configs  = pr.GetValue(configOpt) ?? [];
    var resetVec = pr.GetValue(resetVecOpt);
    var intVec   = pr.GetValue(intVecOpt);
    var verbose  = pr.GetValue(verboseOpt);

    // Derive output path from IR file path when not specified.
    if (string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(irFile))
        output = Path.ChangeExtension(irFile, ".ll");

    // License check (RP2040 is free - always passes).
    var provider = new Rp2040BackendProvider();
    var license  = provider.ValidateLicense();
    if (license.Status != LicenseStatus.Valid)
    {
        Console.Error.WriteLine($"[LICENSE] {license.Message}");
        Environment.ExitCode = 2;
        return;
    }

    // Deserialize IR.
    ProgramIR ir;
    try
    {
        ir = IrSerializer.Deserialize(irFile);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[pymcuc-rp2040] Failed to read IR file '{irFile}': {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    // Build DeviceConfig from CLI flags.
    var cfg = new DeviceConfig
    {
        TargetChip      = target,
        Arch            = "rp2040",
        Frequency       = freq,
        ResetVector     = resetVec,
        InterruptVector = intVec,
    };
    foreach (var item in configs)
    {
        var eq = item.IndexOf('=');
        if (eq > 0) cfg.Fuses[item[..eq]] = item[(eq + 1)..];
    }

    // Run codegen.
    try
    {
        var codegen = provider.Create(cfg);
        using var writer = new StreamWriter(output);
        codegen.Compile(ir, writer);
        Console.WriteLine($"[BUILD_OK] {output}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[pymcuc-rp2040] Codegen failed: {ex.Message}");
        if (verbose) Console.Error.WriteLine(ex.StackTrace);
        Environment.ExitCode = 1;
    }
});

rootCmd.Parse(args).Invoke();
