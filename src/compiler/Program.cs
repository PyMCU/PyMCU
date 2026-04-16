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

using PyMCU.Common.Models;
using PyMCU.Frontend;

namespace PyMCU;

public static class Program
{
    public static int Main(string[] args)
    {
        var options = ParseArgs(args);

        var moduleLoader = new FileSystemModuleLoader();

        var driver = new Pipeline.CompilerDriver()
            .AddPhase(new Pipeline.Phases.InitializationPhase())
            .AddPhase(new Pipeline.Phases.BootstrapPhase())
            .AddPhase(new Pipeline.Phases.ParsingPhase())
            .AddPhase(new Pipeline.Phases.FrontendResolutionPhase(moduleLoader))
            .AddPhase(new Pipeline.Phases.IrGenerationPhase())
            .AddPhase(new Pipeline.Phases.BackendPhase());

        return driver.Run(options);
    }

    private static CompilerOptions ParseArgs(ReadOnlySpan<string> args)
    {
        string? file = null;
        var output = "";
        var arch = "pic14";
        var chip = "";
        ulong freq = 4000000;
        var configs = new List<string>();
        var includes = new List<string>();
        var resetVector = -1;
        var interruptVector = -1;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-o":
                case "--output":
                {
                    if (++i < args.Length) output = args[i];
                    else throw new ArgumentException($"Missing argument for {arg}");
                    break;
                }
                case "--arch" when ++i < args.Length:
                    arch = args[i];
                    break;
                case "--arch":
                    throw new ArgumentException($"Missing argument for {arg}");
                case "--chip" when ++i < args.Length:
                    chip = args[i];
                    break;
                case "--chip":
                    throw new ArgumentException($"Missing argument for {arg}");
                case "--freq" when ++i < args.Length:
                {
                    if (!ulong.TryParse(args[i], out freq))
                        throw new ArgumentException($"Invalid frequency value: {args[i]}");
                    break;
                }
                case "--freq":
                    throw new ArgumentException($"Missing argument for {arg}");
                case "-C":
                case "--config":
                {
                    if (++i < args.Length) configs.Add(args[i]);
                    else throw new ArgumentException($"Missing argument for {arg}");
                    break;
                }
                case "-I":
                case "--include":
                {
                    if (++i < args.Length) includes.Add(args[i]);
                    else throw new ArgumentException($"Missing argument for {arg}");
                    break;
                }
                case "--reset-vector" when ++i < args.Length:
                {
                    if (!int.TryParse(args[i], out resetVector))
                        throw new ArgumentException($"Invalid reset vector value: {args[i]}");
                    break;
                }
                case "--reset-vector":
                    throw new ArgumentException($"Missing argument for {arg}");
                case "--interrupt-vector" when ++i < args.Length:
                {
                    if (!int.TryParse(args[i], out interruptVector))
                        throw new ArgumentException($"Invalid interrupt vector value: {args[i]}");
                    break;
                }
                case "--interrupt-vector":
                    throw new ArgumentException($"Missing argument for {arg}");
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                {
                    if (!arg.StartsWith("-") && file == null)
                    {
                        file = arg;
                    }
                    else
                    {
                        throw new ArgumentException($"Unknown argument: {arg}");
                    }

                    break;
                }
            }
        }

        if (file == null)
        {
            throw new ArgumentException("Input source file is required.");
        }

        if (string.IsNullOrEmpty(output))
        {
            output = Path.ChangeExtension(file, ".asm");
        }

        return new CompilerOptions(file, output, arch, chip, freq, configs, includes, resetVector, interruptVector);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: pymcuc [options] file");
        Console.WriteLine("Options:");
        Console.WriteLine("  -o, --output <file>         Output ASM file");
        Console.WriteLine("  --arch <arch>               Target architecture (default: pic14)");
        Console.WriteLine(
            "  --chip <chip>               Target chip (e.g., pic16f18877). Locates pymcu/chips/<chip>.py");
        Console.WriteLine("  --freq <freq>               Clock frequency in Hz (default: 4000000)");
        Console.WriteLine("  -C, --config <KEY=VALUE>    Configuration bits (KEY=VALUE)");
        Console.WriteLine("  -I, --include <dir>         Add directory to search path for imports");
        Console.WriteLine("  --reset-vector <addr>       Reset vector address (e.g., 0x2000 for bootloader)");
        Console.WriteLine("  --interrupt-vector <addr>   Interrupt vector address (e.g., 0x2008 for bootloader)");
    }
}