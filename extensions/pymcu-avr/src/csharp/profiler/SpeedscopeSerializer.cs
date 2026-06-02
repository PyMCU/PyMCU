// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PyMCU.AVR.Profiler;

public record SpeedscopeFrame(string Name);
public record TaskProfile(string Name, List<int[]> Samples, List<long> Weights);

// ── Serialization model (sampled format) ─────────────────────────────────────

internal record SpeedscopeFrameDto(
    [property: JsonPropertyName("name")] string Name);

internal record SpeedscopeSharedDto(
    [property: JsonPropertyName("frames")] List<SpeedscopeFrameDto> Frames);

internal record SpeedscopeProfileDto(
    [property: JsonPropertyName("type")]       string Type,
    [property: JsonPropertyName("name")]       string Name,
    [property: JsonPropertyName("unit")]       string Unit,
    [property: JsonPropertyName("startValue")] long StartValue,
    [property: JsonPropertyName("endValue")]   long EndValue,
    [property: JsonPropertyName("samples")]    List<int[]> Samples,
    [property: JsonPropertyName("weights")]    List<long> Weights);

internal record SpeedscopeRootDto(
    [property: JsonPropertyName("$schema")]  string Schema,
    [property: JsonPropertyName("profiles")] List<SpeedscopeProfileDto> Profiles,
    [property: JsonPropertyName("shared")]   SpeedscopeSharedDto Shared);

[JsonSerializable(typeof(SpeedscopeRootDto))]
internal partial class SpeedscopeJsonContext : JsonSerializerContext { }

// ── Document ──────────────────────────────────────────────────────────────────

public sealed class SpeedscopeDocument
{
    public List<SpeedscopeFrame> Frames { get; }
    public List<int[]> Samples { get; }
    // Weights are stored in CPU cycles; ToJson converts them to nanoseconds.
    public List<long> Weights { get; }
    public long EndCycles { get; }
    public List<TaskProfile> TaskProfiles { get; }

    public SpeedscopeDocument(
        List<SpeedscopeFrame> frames,
        List<int[]> samples,
        List<long> weights,
        long endCycles,
        List<TaskProfile>? taskProfiles = null)
    {
        Frames = frames;
        Samples = samples;
        Weights = weights;
        EndCycles = endCycles;
        TaskProfiles = taskProfiles ?? [];
    }

    public string ToJson(string profileName = "firmware (ATmega328P @ 16MHz)", uint freq = 16_000_000)
    {
        // Convert cycle-based weights and endValue to nanoseconds so speedscope
        // displays a real-time axis in the Time Order / Left Heavy views.
        static long CyclesToNs(long cycles, uint hz) => cycles * 1_000_000_000L / hz;

        var nsEndValue = CyclesToNs(EndCycles, freq);

        // Per-task profiles: each task gets its own tab in speedscope.
        // endValue = sum of that task's active weights so the timeline represents
        // only the CPU time this task actually consumed.
        var profiles = new List<SpeedscopeProfileDto>();
        foreach (var tp in TaskProfiles)
        {
            var nsWeights  = tp.Weights.Select(w => CyclesToNs(w, freq)).ToList();
            var taskEndNs  = nsWeights.Sum();
            profiles.Add(new SpeedscopeProfileDto(
                Type: "sampled",
                Name: tp.Name,
                Unit: "nanoseconds",
                StartValue: 0L,
                EndValue: taskEndNs,
                Samples: tp.Samples,
                Weights: nsWeights));
        }

        // Merged profile last (shows combined CPU usage across all tasks).
        if (Samples.Count > 0)
        {
            var mergedWeights = Weights.Select(w => CyclesToNs(w, freq)).ToList();
            profiles.Add(new SpeedscopeProfileDto(
                Type: "sampled",
                Name: profileName,
                Unit: "nanoseconds",
                StartValue: 0L,
                EndValue: nsEndValue,
                Samples: Samples,
                Weights: mergedWeights));
        }

        var dto = new SpeedscopeRootDto(
            Schema: "https://www.speedscope.app/file-format-schema.json",
            Profiles: profiles,
            Shared: new SpeedscopeSharedDto(
                Frames: Frames.Select(f => new SpeedscopeFrameDto(f.Name)).ToList()));

        return JsonSerializer.Serialize(dto, SpeedscopeJsonContext.Default.SpeedscopeRootDto);
    }
}
