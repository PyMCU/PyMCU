// SPDX-License-Identifier: MIT
using System.Text.Json.Serialization;

namespace PyMCU.Backend.Targets.AVR;

/// <summary>Maps a non-inline function name to its word address in flash.</summary>
public record SymbolEntry(string Name, uint WordAddr);

[JsonSerializable(typeof(List<SymbolEntry>))]
internal partial class AvrSymbolsJsonContext : JsonSerializerContext { }
