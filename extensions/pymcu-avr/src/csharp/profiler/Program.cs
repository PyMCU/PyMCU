// SPDX-License-Identifier: MIT
// pymcuc-avr-profiler — AVR firmware cycle profiler using AVR8Sharp simulation.
//
// Usage:
//   pymcuc-avr-profiler <hex-file> --symbols <path>
//                       [--cycles N | --ms N]  (default: --ms 100)
//                       [--freq HZ]            (default: 16000000)
//                       [--name "label"]
//                       [-o profile.speedscope.json]

using System.CommandLine;
using PyMCU.AVR.Profiler;

var hexArg = new Argument<string>("hex-file") { Description = "Intel HEX firmware file" };

var symbolsOpt = new Option<string>("--symbols") { Description = "Symbols JSON from --emit-symbols" };
var cyclesOpt  = new Option<ulong?>("--cycles")  { Description = "Number of cycles to simulate" };
var msOpt      = new Option<double?>("--ms")     { Description = "Simulated milliseconds (default: 5000)" };
var freqOpt    = new Option<uint>("--freq")      { Description = "Clock frequency Hz", DefaultValueFactory = _ => 16_000_000U };
var nameOpt    = new Option<string>("--name")    { Description = "Profile label", DefaultValueFactory = _ => "firmware (ATmega328P)" };
var outputOpt  = new Option<string>("-o")        { Description = "Output Speedscope JSON path", DefaultValueFactory = _ => "profile.speedscope.json" };
var debugOpt   = new Option<bool>("--debug")     { Description = "Emit call-stack trace to stderr for diagnostics" };

var root = new RootCommand("pymcuc-avr-profiler — AVR firmware cycle profiler");
root.Arguments.Add(hexArg);
root.Options.Add(symbolsOpt);
root.Options.Add(cyclesOpt);
root.Options.Add(msOpt);
root.Options.Add(freqOpt);
root.Options.Add(nameOpt);
root.Options.Add(outputOpt);
root.Options.Add(debugOpt);

root.SetAction(pr =>
{
    var hexFile     = pr.GetValue(hexArg)!;
    var symbolsPath = pr.GetValue(symbolsOpt);
    var cycles      = pr.GetValue(cyclesOpt);
    var ms          = pr.GetValue(msOpt);
    var freq        = pr.GetValue(freqOpt);
    var name        = pr.GetValue(nameOpt)!;
    var output      = pr.GetValue(outputOpt)!;
    var debug       = pr.GetValue(debugOpt);

    if (string.IsNullOrEmpty(symbolsPath))
    {
        Console.Error.WriteLine("[ERROR] --symbols <path> is required");
        Environment.ExitCode = 1;
        return;
    }

    // Resolve simulation length
    ulong cyclesToRun;
    if (cycles.HasValue)
        cyclesToRun = cycles.Value;
    else
    {
        var simMs = ms ?? 5000.0;
        cyclesToRun = (ulong)(simMs / 1000.0 * freq);
    }

    string hexContent;
    try { hexContent = File.ReadAllText(hexFile); }
    catch (Exception ex) { Console.Error.WriteLine($"[ERROR] Cannot read hex: {ex.Message}"); Environment.ExitCode = 1; return; }

    SymbolMap symbols;
    try { symbols = SymbolMap.Load(symbolsPath); }
    catch (Exception ex) { Console.Error.WriteLine($"[ERROR] Cannot read symbols: {ex.Message}"); Environment.ExitCode = 1; return; }

    Console.WriteLine($"[PROFILER] Simulating {cyclesToRun:N0} cycles @ {freq:N0} Hz...");

    SpeedscopeDocument doc;
    try { doc = ProfilerRunner.Run(hexContent, symbols, cyclesToRun, name, debug); }
    catch (Exception ex) { Console.Error.WriteLine($"[ERROR] Simulation failed: {ex.Message}"); Environment.ExitCode = 1; return; }

    try { File.WriteAllText(output, doc.ToJson(name)); }
    catch (Exception ex) { Console.Error.WriteLine($"[ERROR] Cannot write output: {ex.Message}"); Environment.ExitCode = 1; return; }

    Console.WriteLine($"[DONE] {output}  ({doc.Samples.Count} samples, {doc.Frames.Count} frames)");
    Console.WriteLine($"       Drag {output} to https://speedscope.app to view the flamegraph.");
});

root.Parse(args).Invoke();
