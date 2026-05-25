// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;
using PyMCU.Backend.Targets.AVR;

namespace PyMCU.AVR.Profiler;

/// <summary>Wraps the symbols JSON produced by --emit-symbols for fast lookup.</summary>
public sealed class SymbolMap
{
    private readonly Dictionary<uint, string> _addrToName = new();
    private readonly Dictionary<string, uint> _nameToAddr = new();

    public SymbolMap(IEnumerable<SymbolEntry> entries)
    {
        foreach (var e in entries)
        {
            _addrToName[e.WordAddr] = e.Name;
            _nameToAddr[e.Name] = e.WordAddr;
        }
    }

    public static SymbolMap Load(string path)
    {
        var entries = JsonSerializer.Deserialize(
            File.ReadAllText(path), AvrSymbolsJsonContextPublic.Default.ListSymbolEntry)
            ?? throw new InvalidDataException($"Could not parse symbols file: {path}");
        return new SymbolMap(entries);
    }

    public string Resolve(uint wordAddr)
        => _addrToName.TryGetValue(wordAddr, out var name) ? name : $"[0x{wordAddr:X4}]";

    public bool TryGetAddress(string name, out uint addr)
        => _nameToAddr.TryGetValue(name, out addr);
}

[JsonSerializable(typeof(List<SymbolEntry>))]
internal partial class AvrSymbolsJsonContextPublic : JsonSerializerContext { }
