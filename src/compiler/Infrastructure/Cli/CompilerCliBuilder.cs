/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using System.CommandLine;
using PyMCU.Common.Models;

namespace PyMCU.Infrastructure.Cli;

public static class CompilerCliBuilder
{
    public static RootCommand BuildRootCommand(Func<CompilerOptions, int> compilerRunner)
    {
        Argument<string> fileArgument = new("file")
        {
            Description = "Input source file (.py)"
        };

        Option<string> outputOption = new("--output", "-o")
        {
            Description = "Output ASM file"
        };

        Option<string> archOption = new("--arch")
        {
            Description = "Target architecture",
            DefaultValueFactory = parseResult => "avr"
        };

        Option<string> chipOption = new("--chip")
        {
            Description = "Target chip (e.g., pic16f18877). Locates pymcu/chips/<chip>.py",
            DefaultValueFactory = parseResult => string.Empty
        };

        Option<ulong> freqOption = new("--freq")
        {
            Description = "Clock frequency in Hz",
            DefaultValueFactory = parseResult => 4000000UL
        };

        Option<List<string>> configOption = new("--config", "-C")
        {
            Description = "Configuration bits (KEY=VALUE)",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = parseResult => []
        };

        Option<List<string>> includeOption = new("--include", "-I")
        {
            Description = "Add directory to search path for imports",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = parseResult => []
        };

        Option<int> resetVectorOption = new("--reset-vector")
        {
            Description = "Reset vector address (e.g., 0x2000)",
            DefaultValueFactory = parseResult => -1
        };

        Option<int> interruptVectorOption = new("--interrupt-vector")
        {
            Description = "Interrupt vector address (e.g., 0x2008)",
            DefaultValueFactory = parseResult => -1
        };

        RootCommand rootCommand = new("PyMCU Compiler (pymcuc)");

        rootCommand.Arguments.Add(fileArgument);
        rootCommand.Options.Add(outputOption);
        rootCommand.Options.Add(archOption);
        rootCommand.Options.Add(chipOption);
        rootCommand.Options.Add(freqOption);
        rootCommand.Options.Add(configOption);
        rootCommand.Options.Add(includeOption);
        rootCommand.Options.Add(resetVectorOption);
        rootCommand.Options.Add(interruptVectorOption);

        rootCommand.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArgument) ?? string.Empty;
            var output = parseResult.GetValue(outputOption) ?? string.Empty;

            if (string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(file))
            {
                output = Path.ChangeExtension(file, ".asm");
            }

            CompilerOptions options = new(
                FilePath: file,
                OutputPath: output,
                Arch: parseResult.GetValue(archOption) ?? string.Empty,
                Chip: parseResult.GetValue(chipOption) ?? string.Empty,
                Frequency: parseResult.GetValue(freqOption),
                Configs: parseResult.GetValue(configOption) ?? [],
                Includes: parseResult.GetValue(includeOption) ?? [],
                ResetVector: parseResult.GetValue(resetVectorOption),
                InterruptVector: parseResult.GetValue(interruptVectorOption)
            );

            var exitCode = compilerRunner(options);

            Environment.ExitCode = exitCode;
        });

        return rootCommand;
    }
}