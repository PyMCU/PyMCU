// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PyMCU.AVR.Profiler;

public record SpeedscopeEvent(string Type, int Frame, long At);
public record SpeedscopeFrame(string Name);

// ── Serialization model ───────────────────────────────────────────────────────

internal record SpeedscopeEventDto(
    [property: JsonPropertyName("type")]  string Type,
    [property: JsonPropertyName("frame")] int Frame,
    [property: JsonPropertyName("at")]    long At);

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
    [property: JsonPropertyName("events")]     List<SpeedscopeEventDto> Events);

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
    public List<SpeedscopeEvent> Events { get; }
    public long EndValue { get; }

    public SpeedscopeDocument(List<SpeedscopeFrame> frames, List<SpeedscopeEvent> events, long endValue)
    {
        Frames = frames;
        Events = events;
        EndValue = endValue;
    }

    public string ToJson(string profileName = "firmware (ATmega328P @ 16MHz)")
    {
        var dto = new SpeedscopeRootDto(
            Schema: "https://www.speedscope.app/file-format-schema.json",
            Profiles:
            [
                new SpeedscopeProfileDto(
                    Type: "evented",
                    Name: profileName,
                    Unit: "none",
                    StartValue: 0L,
                    EndValue: EndValue,
                    Events: Events.Select(e => new SpeedscopeEventDto(e.Type, e.Frame, e.At)).ToList())
            ],
            Shared: new SpeedscopeSharedDto(
                Frames: Frames.Select(f => new SpeedscopeFrameDto(f.Name)).ToList()));

        return JsonSerializer.Serialize(dto, SpeedscopeJsonContext.Default.SpeedscopeRootDto);
    }
}
