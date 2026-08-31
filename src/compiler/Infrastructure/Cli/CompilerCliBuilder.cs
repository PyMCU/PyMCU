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
            Description = "Target architecture (internal/advanced use only)",
            DefaultValueFactory = parseResult => string.Empty
        };

        Option<string> targetOption = new("--target")
        {
            Description = "Target chip (e.g., atmega328p, pic16f18877). Locates pymcu/chips/<target>.py",
            DefaultValueFactory = parseResult => string.Empty
        };

        Option<string> boardOption = new("--board")
        {
            Description = "Board name (e.g., pico_w, pico2_w). Optional: a board carries "
                          + "facts the chip does not, such as which radio is soldered next to it",
            DefaultValueFactory = parseResult => string.Empty
        };

        Option<ulong> freqOption = new("--freq")
        {
            Description = "Clock frequency in Hz",
            DefaultValueFactory = parseResult => 4000000UL
        };

        // NOT AllowMultipleArgumentsPerToken. It makes a list option keep consuming tokens
        // until the next one it recognises, and a token starting with `--` is NOT a stopping
        // condition, so everything after this flag is swallowed as another value:
        //
        //     pymcuc w.py --target rp2040 -I lib --totally-invented   ->  rc=0, silent
        //     pymcuc w.py --totally-invented --target rp2040 -I lib   ->  refused, rc=1
        //
        // The same argument, refused or accepted depending on which side of `-I` it fell on,
        // and every real command line puts the include paths last. Worse than ignored: the
        // token becomes an entry in the module search path, so a misspelled flag silently
        // widens where imports resolve from. Measured: with a bare `extra` after `-I lib`,
        // `from mymod import ...` resolved out of `extra/`.
        //
        // `TreatUnmatchedTokensAsErrors` is already true and the parser already refuses an
        // unrecognised argument by itself; this option was the only thing stopping it from
        // seeing one. Nothing needs the `-I a b` spelling: every caller in this repo and in
        // the backend repos passes the flag once per path. Issue #237.
        Option<List<string>> configOption = new("--config", "-C")
        {
            Description = "Configuration bits (KEY=VALUE)",
            DefaultValueFactory = parseResult => []
        };

        // Same reason as --config above. Issue #237.
        Option<List<string>> includeOption = new("--include", "-I")
        {
            Description = "Add directory to search path for imports",
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

        Option<bool> verboseOption = new("--verbose", "-v")
        {
            Description = "Enable verbose logging output",
            DefaultValueFactory = parseResult => false
        };

        Option<string?> emitIrOption = new("--emit-ir")
        {
            Description = "Emit the IR to a .mir file instead of invoking a codegen backend",
            DefaultValueFactory = parseResult => null
        };

        Option<string?> projectRootOption = new("--project-root")
        {
            Description = "The project's own source directory; modules loaded from inside it "
                        + "have their module level executed on import",
            DefaultValueFactory = parseResult => null
        };

        RootCommand rootCommand = new("PyMCU Compiler (pymcuc)");

        rootCommand.Arguments.Add(fileArgument);
        rootCommand.Options.Add(outputOption);
        rootCommand.Options.Add(archOption);
        rootCommand.Options.Add(targetOption);
        rootCommand.Options.Add(boardOption);
        rootCommand.Options.Add(freqOption);
        rootCommand.Options.Add(configOption);
        rootCommand.Options.Add(includeOption);
        rootCommand.Options.Add(resetVectorOption);
        rootCommand.Options.Add(interruptVectorOption);
        rootCommand.Options.Add(verboseOption);
        rootCommand.Options.Add(emitIrOption);
        rootCommand.Options.Add(projectRootOption);

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
                Target: parseResult.GetValue(targetOption) ?? string.Empty,
                Board: parseResult.GetValue(boardOption) ?? string.Empty,
                Frequency: parseResult.GetValue(freqOption),
                Configs: parseResult.GetValue(configOption) ?? [],
                Includes: parseResult.GetValue(includeOption) ?? [],
                ResetVector: parseResult.GetValue(resetVectorOption),
                InterruptVector: parseResult.GetValue(interruptVectorOption),
                Verbose: parseResult.GetValue(verboseOption),
                EmitIrPath: parseResult.GetValue(emitIrOption),
                ProjectRoot: parseResult.GetValue(projectRootOption)
            );

            // Return the exit code so Invoke() (and thus the process) actually fails
            // on a compile error. Setting Environment.ExitCode alone was ignored
            // because Main returns Invoke()'s own result — which is why a frontend
            // diagnostic still exited 0, letting the driver run the backend on a
            // missing .mir and pile on "Failed to read IR file" cascade errors.
            return compilerRunner(options);
        });

        return rootCommand;
    }
}