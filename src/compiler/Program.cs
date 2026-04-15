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

using PyMCU.Backend;
using PyMCU.Common;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);

            var deviceConfig = new DeviceConfig
            {
                Frequency = options.Frequency,
                ResetVector = options.ResetVector,
                InterruptVector = options.InterruptVector
            };

            if (!string.IsNullOrEmpty(options.Arch))
            {
                deviceConfig.TargetChip = options.Arch;
            }

            foreach (var item in options.Configs)
            {
                var eqPos = item.IndexOf('=');
                if (eqPos == -1) continue;
                var key = item[..eqPos];
                var val = item[(eqPos + 1)..];
                deviceConfig.Fuses[key] = val;
            }

            var sourceLines = new List<string>();
            string source;
            try
            {
                source = File.ReadAllText(options.FilePath);
                using var reader = new StringReader(source);
                while (reader.ReadLine() is { } line)
                {
                    sourceLines.Add(line);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal Error: {ex.Message}");
                return 1;
            }

            var includePaths = new List<string>(options.Includes) { "." };
            var parentDir = Path.GetDirectoryName(options.FilePath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                includePaths.Add(parentDir);
            }

            var context = new CompilerContext();

            // =====================================================================
            // Phase 0: Target Bootstrap
            // =====================================================================
            if (!string.IsNullOrEmpty(options.Chip))
            {
                try
                {
                    var target = TargetLoader.Bootstrap(options.Chip, includePaths);

                    deviceConfig.Chip = target.Config.Chip;
                    deviceConfig.DetectedChip = target.Config.DetectedChip;
                    deviceConfig.Arch = target.Config.Arch;
                    if (target.Config.RamSize > 0) deviceConfig.RamSize = target.Config.RamSize;
                    if (target.Config.FlashSize > 0) deviceConfig.FlashSize = target.Config.FlashSize;
                    if (target.Config.EepromSize > 0) deviceConfig.EepromSize = target.Config.EepromSize;

                    if (string.IsNullOrEmpty(deviceConfig.TargetChip))
                    {
                        deviceConfig.TargetChip = options.Chip;
                    }

                    context.ModuleSourceLines[target.ModuleName] = target.SourceLines;
                    context.ModuleCache[target.FilePath] = target.Ast;
                    context.NamedModules[target.ModuleName] = target.Ast;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Warning] Chip bootstrap failed: {ex.Message}");
                }
            }

            try
            {
                var lexer = new Lexer(source);
                var tokens = lexer.Tokenize();

                var parser = new Parser(tokens);
                var ast = parser.ParseProgram();

                // Pass 1: Recursive Import Loading
                LoadImportsRecursively(ast, context, options.FilePath, includePaths);

                // Pass 2: Global Configuration Bootstrap
                var preScanner = new PreScanVisitor(deviceConfig);
                preScanner.Scan(ast);
                foreach (var modAst in context.NamedModules.Values)
                {
                    preScanner.Scan(modAst);
                }

                context.Config = deviceConfig;

                if (string.IsNullOrEmpty(context.Config.Chip)) context.Config.Chip = options.Arch;
                if (string.IsNullOrEmpty(context.Config.Arch)) context.Config.Arch = options.Arch;

                // Pass 3: Global Conditional Compilation
                var conditional = new ConditionalCompilator(context.Config);
                conditional.Process(ast);
                var ccProcessed = new HashSet<ProgramNode> { ast };

                foreach (var modAst in context.NamedModules.Values)
                {
                    if (ccProcessed.Add(modAst))
                    {
                        conditional.Process(modAst);
                    }
                }

                // Pass 4: Final Recursive Load
                LoadImportsRecursively(ast, context, options.FilePath, includePaths);
                foreach (var modAst in context.NamedModules.Values.ToList()) // ToList() to allow modifications
                {
                    LoadImportsRecursively(modAst, context, options.FilePath, includePaths);
                }

                // Pass 4b/5: Iteratively run CC + load until stable
                bool anyNew = true;
                while (anyNew)
                {
                    anyNew = false;
                    foreach (var modAst in context.NamedModules.Values.ToList())
                    {
                        if (ccProcessed.Add(modAst))
                        {
                            conditional.Process(modAst);
                            anyNew = true;
                        }
                    }

                    int before = context.NamedModules.Count;
                    LoadImportsRecursively(ast, context, options.FilePath, includePaths);
                    foreach (var modAst in context.NamedModules.Values.ToList())
                    {
                        LoadImportsRecursively(modAst, context, options.FilePath, includePaths);
                    }

                    if (context.NamedModules.Count > before) anyNew = true;
                }

                // IR Generation
                var irGen = new IRGenerator();
                var ir = irGen.Generate(ast, context.NamedModules, deviceConfig, sourceLines,
                    context.ModuleSourceLines);

                ir = Optimizer.Optimize(ir);

                // Propagate device_info
                if (!string.IsNullOrEmpty(context.Config.Chip)) deviceConfig.Chip = context.Config.Chip;
                if (!string.IsNullOrEmpty(context.Config.Arch)) deviceConfig.Arch = context.Config.Arch;
                if (!string.IsNullOrEmpty(context.Config.TargetChip))
                    deviceConfig.TargetChip = context.Config.TargetChip;
                if (context.Config.RamSize > 0) deviceConfig.RamSize = context.Config.RamSize;

                string targetArch = deviceConfig.Arch;
                if (string.IsNullOrEmpty(targetArch))
                {
                    targetArch = deviceConfig.Chip;
                    if (string.IsNullOrEmpty(targetArch)) targetArch = options.Arch;
                }

                if (string.IsNullOrEmpty(deviceConfig.Chip)) deviceConfig.Chip = options.Arch;
                if (string.IsNullOrEmpty(deviceConfig.Arch)) deviceConfig.Arch = options.Arch;
                if (string.IsNullOrEmpty(deviceConfig.TargetChip)) deviceConfig.TargetChip = deviceConfig.Chip;

                var backend = CodeGenFactory.Create(targetArch, deviceConfig);

                var outputParent = Path.GetDirectoryName(options.OutputPath);
                if (!string.IsNullOrEmpty(outputParent) && !Directory.Exists(outputParent))
                {
                    Directory.CreateDirectory(outputParent);
                }

                Console.WriteLine(
                    $"[pymcuc] Compiling {options.FilePath} -> {options.OutputPath} ({targetArch} @ {deviceConfig.Frequency}Hz)");

                using (var asmFile = new StreamWriter(options.OutputPath))
                {
                    backend.Compile(ir, asmFile);
                }

                Console.WriteLine($"[pymcuc] Success! Output written to {options.OutputPath}");
            }
            catch (CompilerError e)
            {
                Diagnostic.Report(e, source, options.FilePath);
                return 1;
            }
            catch (Exception e)
            {
                Diagnostic.ReportInternal(e.Message, options.FilePath);
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string ResolveModule(string moduleName, List<string> includePaths, string currentFilePath,
        int relativeLevel)
    {
        var pathRel = moduleName.Replace('.', Path.DirectorySeparatorChar);

        if (relativeLevel > 0)
        {
            var searchDir = Path.GetDirectoryName(currentFilePath) ?? string.Empty;

            for (var i = 1; i < relativeLevel; i++)
            {
                searchDir = Path.GetDirectoryName(searchDir) ?? string.Empty;
            }

            var fullPath = Path.Combine(searchDir, pathRel + ".py");
            if (File.Exists(fullPath)) return fullPath;

            fullPath = Path.Combine(searchDir, pathRel, "__init__.py");
            return File.Exists(fullPath) ? fullPath : throw new Exception($"Relative import not found: {fullPath}");
        }

        foreach (var baseDir in includePaths)
        {
            var fullPath = Path.Combine(baseDir, pathRel + ".py");
            if (File.Exists(fullPath)) return fullPath;

            fullPath = Path.Combine(baseDir, pathRel, "__init__.py");
            if (File.Exists(fullPath)) return fullPath;
        }

        throw new Exception($"Module not found: {moduleName}");
    }

    private static void LoadImportsRecursively(ProgramNode ast, CompilerContext ctx, string currentPath,
        List<string> includes)
    {
        foreach (var imp in ast.Imports)
        {
            var path = string.Empty;
            try
            {
                if (imp.ModuleName is "pymcu.types" or "pymcu.chips")
                {
                    continue;
                }

                path = ResolveModule(imp.ModuleName, includes, currentPath, imp.RelativeLevel);

                if (ctx.ModuleCache.TryGetValue(path, out var cachedAst))
                {
                    ctx.NamedModules[imp.ModuleName] = cachedAst;
                    continue;
                }

                if (ctx.LoadingModules.Contains(path)) continue;

                Console.WriteLine($"Loading module: {path}");
                ctx.LoadingModules.Add(path);

                var src = File.ReadAllText(path);
                var lines = new List<string>();
                using (var reader = new StringReader(src))
                {
                    while (reader.ReadLine() is { } line)
                    {
                        lines.Add(line);
                    }
                }

                ctx.ModuleSourceLines[imp.ModuleName] = lines;

                var lexer = new Lexer(src);
                var parser = new Parser(lexer.Tokenize());
                var modAst = parser.ParseProgram();

                ctx.ModuleCache[path] = modAst;
                ctx.NamedModules[imp.ModuleName] = modAst;

                LoadImportsRecursively(modAst, ctx, path, includes);

                ctx.LoadingModules.Remove(path);
            }
            catch (Exception e)
            {
                if (!string.IsNullOrEmpty(path)) ctx.LoadingModules.Remove(path);
                Console.Error.WriteLine($"Error importing '{imp.ModuleName}': {e.Message}");
                throw new CompilerError("ImportError", "Failed to import module", imp.Line, 0);
            }
        }
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