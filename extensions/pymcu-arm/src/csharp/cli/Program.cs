// SPDX-License-Identifier: MIT
// pymcuc-arm — ARM LLVM IR backend runner (RP2040 / RP2350).
//
// Usage:
//   pymcuc-arm <ir-file.mir> --output <firmware.ll> --target <chip> --freq <hz>
//                             [--arch <arch>] [--config KEY=VALUE]... [--verbose]
//
// Exit codes:
//   0  — success
//   1  — compilation / IR error
//   2  — license error

using System.CommandLine;
using PyMCU.Backend.License;
using PyMCU.Backend.Serialization;
using PyMCU.Backend.Targets.ARM;
using PyMCU.Common.Models;
using PyMCU.IR;

var irFileArg = new Argument<string>("ir-file")
{
    Description = "Path to the .mir IR file produced by pymcuc --emit-ir"
};

var outputOpt = new Option<string>("--output", "-o")
{
    Description = "Output LLVM IR file path (.ll)"
};

var targetOpt = new Option<string>("--target")
{
    Description = "Target chip identifier (e.g. rp2040, rp2350)",
    DefaultValueFactory = _ => "rp2040"
};

var archOpt = new Option<string>("--arch")
{
    Description = "Architecture override (e.g. thumbv6m, thumbv8m)",
    DefaultValueFactory = _ => string.Empty
};

var freqOpt = new Option<ulong>("--freq")
{
    Description = "System clock frequency in Hz",
    DefaultValueFactory = _ => 125_000_000UL
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

var rootCmd = new RootCommand("pymcuc-arm — PyMCU ARM LLVM IR backend runner (RP2040 / RP2350)");
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
    var target  = pr.GetValue(targetOpt) ?? "rp2040";
    var arch    = pr.GetValue(archOpt)   ?? string.Empty;
    var freq    = pr.GetValue(freqOpt);
    var configs = pr.GetValue(configOpt) ?? [];
    var verbose = pr.GetValue(verboseOpt);

    if (string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(irFile))
        output = Path.ChangeExtension(irFile, ".ll");

    var provider = new ArmBackendProvider();
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
        Console.Error.WriteLine($"[pymcuc-arm] Failed to read IR file '{irFile}': {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    // Derive arch from target chip when not explicitly provided.
    if (string.IsNullOrEmpty(arch))
    {
        arch = target.ToLowerInvariant() switch
        {
            var t when t.StartsWith("rp2350") => "rp2350",
            _ => "rp2040"
        };
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
        Console.Error.WriteLine($"[pymcuc-arm] Codegen failed: {ex.Message}");
        if (verbose) Console.Error.WriteLine(ex.StackTrace);
        Environment.ExitCode = 1;
    }
});

rootCmd.Parse(args).Invoke();
