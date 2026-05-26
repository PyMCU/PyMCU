// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PyMCU.AVR.DebugServer;

public sealed record LineMapEntry(
    [property: JsonPropertyName("File")]     string File,
    [property: JsonPropertyName("Line")]     int    Line,
    [property: JsonPropertyName("WordAddr")] uint   WordAddr);

[JsonSerializable(typeof(List<LineMapEntry>))]
internal sealed partial class LineMapJsonContext : JsonSerializerContext { }

/// <summary>
/// Bidirectional map between source positions and AVR word addresses.
/// Built from the linemap.json file emitted by pymcuc-avr --emit-linemap.
/// </summary>
public sealed class LineMap
{
    private readonly Dictionary<(string file, int line), uint> _toAddr   = new();
    private readonly Dictionary<uint, (string file, int line)> _fromAddr = new();

    private LineMap() { }

    public static LineMap Load(string path)
    {
        var json    = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize(json, LineMapJsonContext.Default.ListLineMapEntry)
                      ?? throw new InvalidDataException($"Empty or invalid linemap: {path}");

        var map = new LineMap();
        foreach (var e in entries)
        {
            map._toAddr.TryAdd((e.File, e.Line), e.WordAddr);
            // For reverse lookup: keep the highest line number for a given word address.
            // When a loop header ("while True:") and its first body statement share a word,
            // the body statement's line number is higher and is more useful for display/stepping.
            if (!map._fromAddr.TryGetValue(e.WordAddr, out var existing) || e.Line > existing.line)
                map._fromAddr[e.WordAddr] = (e.File, e.Line);
        }
        return map;
    }

    public uint? GetWordAddr(string file, int line)
        => _toAddr.TryGetValue((file, line), out var addr) ? addr : null;

    public (string file, int line)? GetSourcePos(uint wordAddr)
        => _fromAddr.TryGetValue(wordAddr, out var pos) ? pos : null;

    public bool IsEmpty => _toAddr.Count == 0;
    public int  EntryCount => _toAddr.Count;

    public string DebugSummary()
    {
        var samples = _toAddr.Take(5)
            .Select(kv => $"{kv.Key.file}:{kv.Key.line}→0x{kv.Value:X}");
        return string.Join(", ", samples);
    }
}
