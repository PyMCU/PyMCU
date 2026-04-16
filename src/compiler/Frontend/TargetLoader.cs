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

using PyMCU.Common;
using PyMCU.Common.Models;

namespace PyMCU.Frontend;

public class TargetLoaderResult
{
    public DeviceConfig Config { get; } = new();
    public string ModuleName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public ProgramNode Ast { get; set; } = null!;
    public List<string> SourceLines { get; } = [];
}

public static class TargetLoader
{
    private static string ResolveChipModule(string chipName, List<string> includePaths)
    {
        var relPath = Path.Combine("pymcu", "chips", $"{chipName}.py");

        foreach (var candidate in includePaths.Select(@base => Path.Combine(@base, relPath)).Where(File.Exists))
        {
            return Path.GetFullPath(candidate);
        }

        var dirs = string.Join(", ", includePaths);
        throw new Exception($"Chip definition not found: {chipName}\nSearched for: {relPath}\nIn directories: {dirs}");
    }

    public static TargetLoaderResult Bootstrap(string chipName, List<string> includePaths)
    {
        var result = new TargetLoaderResult
        {
            ModuleName = $"pymcu.chips.{chipName}",
            FilePath = ResolveChipModule(chipName, includePaths)
        };

        Console.WriteLine($"[TargetLoader] Loading chip: {chipName} from {result.FilePath}");

        // Step 2: Read and tokenize
        string source = Utils.ReadSource(result.FilePath);

        using (var reader = new StringReader(source))
        {
            while (reader.ReadLine() is { } line)
            {
                result.SourceLines.Add(line);
            }
        }

        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();

        // Step 3: Parse into AST
        var parser = new Parser(tokens);
        result.Ast = parser.ParseProgram();

        // Step 4: Extract device_info() metadata via PreScanVisitor
        var scanner = new PreScanVisitor(result.Config);
        scanner.Scan(result.Ast);

        // Step 5: Validate
        if (string.IsNullOrEmpty(result.Config.Arch))
        {
            throw new Exception(
                $"Chip definition '{chipName}' does not contain a valid device_info() call. Expected: device_info(chip=\"{chipName}\", arch=\"...\", ram_size=...)");
        }

        Console.WriteLine(
            $"[TargetLoader] Target: {result.Config.Chip} (arch={result.Config.Arch}, RAM={result.Config.RamSize})");

        return result;
    }
}