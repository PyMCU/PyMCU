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
    // Sorted ascending for binary-search in ResolveContaining.
    private (uint Addr, string Name)[] _sortedByAddr = [];

    public SymbolMap(IEnumerable<SymbolEntry> entries)
    {
        foreach (var e in entries)
        {
            _addrToName[e.WordAddr] = e.Name;
            _nameToAddr[e.Name] = e.WordAddr;
        }
        _sortedByAddr = _addrToName
            .Select(kv => (Addr: kv.Key, Name: kv.Value))
            .OrderBy(x => x.Addr)
            .ToArray();
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

    /// <summary>
    /// Returns the name of the function that CONTAINS <paramref name="wordAddr"/>
    /// (i.e. the last known symbol whose entry address is ≤ wordAddr).
    /// Used to identify which task is being resumed at a RETI site that falls
    /// in the middle of a function rather than at its entry point.
    /// Returns <c>"[0xXXXX]"</c> if no symbol precedes the address.
    /// </summary>
    public string ResolveContaining(uint wordAddr)
    {
        // Binary search: find last entry with Addr <= wordAddr.
        int lo = 0, hi = _sortedByAddr.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_sortedByAddr[mid].Addr <= wordAddr) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best >= 0 ? _sortedByAddr[best].Name : $"[0x{wordAddr:X4}]";
    }

    public bool TryGetAddress(string name, out uint addr)
        => _nameToAddr.TryGetValue(name, out addr);
}

[JsonSerializable(typeof(List<SymbolEntry>))]
internal partial class AvrSymbolsJsonContextPublic : JsonSerializerContext { }
