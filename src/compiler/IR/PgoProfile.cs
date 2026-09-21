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

// The PGO profile produced by `pymcu profile --pgo` (see
// docs/rfcs/0009-profile-guided-optimisation.md). Blocks are keyed by the MIR
// label names the backend emitted in its block map; counts/cycles are
// aggregated across every scenario the workload declared.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PyMCU.IR;

public sealed class PgoProfile
{
    [JsonPropertyName("format")] public int Format { get; set; }
    [JsonPropertyName("chip")] public string Chip { get; set; } = "";
    [JsonPropertyName("freq")] public ulong Freq { get; set; }
    [JsonPropertyName("scenarios")] public List<PgoScenario> Scenarios { get; set; } = new();
    [JsonPropertyName("functions")] public Dictionary<string, PgoFunctionStat> Functions { get; set; } = new();
    [JsonPropertyName("blocks")] public Dictionary<string, PgoBlockStat> Blocks { get; set; } = new();
    [JsonPropertyName("edges")] public Dictionary<string, long> Edges { get; set; } = new();
    [JsonPropertyName("loops")] public Dictionary<string, PgoLoopStat> Loops { get; set; } = new();

    public ulong TotalCycles =>
        Scenarios.Aggregate(0UL, (a, s) => a + s.Cycles);

    public ulong BlockCount(string label) =>
        Blocks.TryGetValue(label, out var b) ? b.Count : 0;

    public ulong BlockCycles(string label) =>
        Blocks.TryGetValue(label, out var b) ? b.Cycles : 0;

    /// <summary>
    /// Load a profile JSON file, or return null when it cannot be read/parsed.
    /// The caller decides whether that is a warning (a path the user named) --
    /// never a hard error: a bad profile must degrade to an ordinary build.
    /// </summary>
    public static PgoProfile? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var p = JsonSerializer.Deserialize(stream, PgoProfileJsonContext.Default.PgoProfile);
            return p is { Format: 1 } ? p : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether any profiled block key names a label/function this program
    /// actually contains. A profile captured from another program (or from a
    /// build whose labels all shifted) is worse than no profile -- the spec is
    /// to warn and ignore it.
    /// </summary>
    public bool MatchesProgram(ProgramIR program)
    {
        if (Blocks.Count == 0) return false;
        var names = new HashSet<string>(program.Functions.Select(f => f.Name));
        foreach (var f in program.Functions)
            foreach (var ins in f.Body)
                if (ins is Label l) names.Add(l.Name);
        return Blocks.Keys.Any(names.Contains);
    }
}

public sealed class PgoScenario
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("cycles")] public ulong Cycles { get; set; }
    [JsonPropertyName("instructions")] public ulong Instructions { get; set; }
}

public sealed class PgoFunctionStat
{
    [JsonPropertyName("cycles")] public ulong Cycles { get; set; }
    [JsonPropertyName("entries")] public ulong Entries { get; set; }
}

public sealed class PgoBlockStat
{
    [JsonPropertyName("count")] public ulong Count { get; set; }
    [JsonPropertyName("cycles")] public ulong Cycles { get; set; }
}

public sealed class PgoLoopStat
{
    [JsonPropertyName("iterations")] public ulong Iterations { get; set; }
    [JsonPropertyName("entries")] public ulong Entries { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(PgoProfile))]
internal partial class PgoProfileJsonContext : JsonSerializerContext { }
