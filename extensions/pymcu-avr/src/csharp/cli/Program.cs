// SPDX-License-Identifier: MIT
// pymcuc-avr — AOT-compiled AVR backend runner.
//
// Usage:
//   pymcuc-avr <ir-file.mir> --output <firmware.asm> --target <chip> --freq <hz>
//                             [--config KEY=VALUE]... [--reset-vector N]
//                             [--interrupt-vector N] [--emit-symbols <path>]
//                             [--verbose]
//   pymcuc-avr debug --port <PORT> --hex <HEX_FILE> --linemap <LINEMAP_FILE>
//
// Exit codes:
//   0  — success
//   1  — compilation / IR error
//   2  — license error (should not occur for AVR since it is free)

using System.CommandLine;
using System.Diagnostics;
using PyMCU.Backend.License;
using PyMCU.Backend.Serialization;
using PyMCU.Backend.Targets.AVR;
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
    Description = "Target chip identifier (e.g. atmega328p)",
    DefaultValueFactory = _ => string.Empty
};

var freqOpt = new Option<ulong>("--freq")
{
    Description = "Clock frequency in Hz",
    DefaultValueFactory = _ => 4_000_000UL
};

var configOpt = new Option<List<string>>("--config", "-C")
{
    Description = "Configuration bits KEY=VALUE",
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

var emitSymbolsOpt = new Option<string?>("--emit-symbols")
{
    Description = "Write function symbol map JSON to this path (for profiler use)",
    DefaultValueFactory = _ => null
};

var emitLineMapOpt = new Option<string?>("--emit-linemap")
{
    Description = "Write source line→flash address map JSON to this path (for debugger use)",
    DefaultValueFactory = _ => null
};

var emitVarMapOpt = new Option<string?>("--emit-varmap")
{
    Description = "Write function variable→register map JSON to this path (for debugger variable display)",
    DefaultValueFactory = _ => null
};

var rootCmd = new RootCommand("pymcuc-avr — PyMCU AVR backend runner");
rootCmd.Arguments.Add(irFileArg);
rootCmd.Options.Add(outputOpt);
rootCmd.Options.Add(targetOpt);
rootCmd.Options.Add(freqOpt);
rootCmd.Options.Add(configOpt);
rootCmd.Options.Add(resetVecOpt);
rootCmd.Options.Add(intVecOpt);
rootCmd.Options.Add(verboseOpt);
rootCmd.Options.Add(emitSymbolsOpt);
rootCmd.Options.Add(emitLineMapOpt);
rootCmd.Options.Add(emitVarMapOpt);

rootCmd.SetAction(pr =>
{
    var irFile      = pr.GetValue(irFileArg) ?? "";
    var output      = pr.GetValue(outputOpt) ?? "";
    var target      = pr.GetValue(targetOpt) ?? "";
    var freq        = pr.GetValue(freqOpt);
    var configs     = pr.GetValue(configOpt) ?? [];
    var resetVec    = pr.GetValue(resetVecOpt);
    var intVec      = pr.GetValue(intVecOpt);
    var verbose     = pr.GetValue(verboseOpt);
    var emitSymbols  = pr.GetValue(emitSymbolsOpt);
    var emitLineMap  = pr.GetValue(emitLineMapOpt);
    var emitVarMap   = pr.GetValue(emitVarMapOpt);

    // Derive output path from IR file path when not specified.
    if (string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(irFile))
        output = Path.ChangeExtension(irFile, ".asm");

    // License check (AVR is free — always passes).
    var provider = new AvrBackendProvider();
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
        Console.Error.WriteLine($"[pymcuc-avr] Failed to read IR file '{irFile}': {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    // Build DeviceConfig from CLI flags.
    var cfg = new DeviceConfig
    {
        TargetChip          = target,
        Arch                = "avr",
        Frequency           = freq,
        ResetVector         = resetVec,
        InterruptVector     = intVec,
        // Enable source-line comments in the .asm when a linemap is requested
        // (debug builds) but suppress them in plain release builds.
        EmitDebugComments   = !string.IsNullOrEmpty(emitLineMap),
    };
    foreach (var item in configs)
    {
        var eq = item.IndexOf('=');
        if (eq > 0) cfg.Fuses[item[..eq]] = item[(eq + 1)..];
    }

    // Run codegen.
    try
    {
        var codegen = (AvrCodeGen)provider.Create(cfg);
        codegen.EmitSymbolsPath = emitSymbols;
        codegen.EmitLineMapPath = emitLineMap;
        codegen.EmitVarMapPath  = emitVarMap;
        using var writer = new StreamWriter(output);
        codegen.Compile(ir, writer);
        Console.WriteLine($"[BUILD_OK] {output}");
        if (!string.IsNullOrEmpty(emitSymbols))
            Console.WriteLine($"[SYMBOLS] {emitSymbols}");
        if (!string.IsNullOrEmpty(emitLineMap))
            Console.WriteLine($"[LINEMAP] {emitLineMap}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[pymcuc-avr] Codegen failed: {ex.Message}");
        if (verbose) Console.Error.WriteLine(ex.StackTrace);
        Environment.ExitCode = 1;
    }
});

// ── debug subcommand — delegates to pymcuc-avr-debugserver (non-AOT) ────────

var debugPortOpt = new Option<int>("--port")
{
    Description = "TCP port for the debug server",
    DefaultValueFactory = _ => 57000
};
var debugHexOpt = new Option<string>("--hex")
{
    Description = "Path to the compiled .hex firmware file"
};
var debugLmOpt = new Option<string>("--linemap")
{
    Description = "Path to the linemap.json file produced by pymcuc-avr --emit-linemap"
};

var debugCmd = new Command("debug", "Launch the PyMCU AVR emulator debug server");
debugCmd.Options.Add(debugPortOpt);
debugCmd.Options.Add(debugHexOpt);
debugCmd.Options.Add(debugLmOpt);

debugCmd.SetAction(pr =>
{
    var port    = pr.GetValue(debugPortOpt);
    var hex     = pr.GetValue(debugHexOpt) ?? "";
    var linemap = pr.GetValue(debugLmOpt)  ?? "";

    // pymcuc-avr-debugserver lives next to this binary.
    var selfDir    = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
    var serverBin  = Path.Combine(selfDir, "pymcuc-avr-debugserver");
    if (!File.Exists(serverBin))
    {
        Console.Error.WriteLine($"[pymcuc-avr] Debug server not found: {serverBin}");
        Console.Error.WriteLine("  Build it with: dotnet publish extensions/pymcu-avr/src/csharp/debugserver/");
        Environment.ExitCode = 1;
        return;
    }

    using var proc = new Process();
    proc.StartInfo = new ProcessStartInfo(serverBin)
    {
        Arguments       = $"--port {port}" +
                          (string.IsNullOrEmpty(hex)     ? "" : $" --hex \"{hex}\"") +
                          (string.IsNullOrEmpty(linemap) ? "" : $" --linemap \"{linemap}\""),
        UseShellExecute = false,
    };
    proc.Start();
    proc.WaitForExit();
    Environment.ExitCode = proc.ExitCode;
});

rootCmd.Subcommands.Add(debugCmd);

rootCmd.Parse(args).Invoke();
