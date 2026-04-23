// SPDX-License-Identifier: MIT
// PyMCU Backend SDK — AOT-compatible serialization for ProgramIR.
//
// Uses System.Text.Json source generation for full AOT / NativeAOT compatibility.
// The on-disk format is a single UTF-8 JSON file (extension .mir).
//
// Polymorphic instruction/value types are discriminated by a "$t" field.

using System.Text.Json;
using System.Text.Json.Serialization;
using PyMCU.IR;

namespace PyMCU.Backend.Serialization;

/// <summary>
/// Serializes and deserializes <see cref="ProgramIR"/> to/from a .mir JSON file.
/// Both the compiler frontend (<c>--emit-ir</c>) and backend runners use this class.
/// </summary>
public static class IrSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = PymcuIrContext.Default,
        WriteIndented = false,
    };

    /// <summary>Serialize <paramref name="program"/> to the file at <paramref name="path"/>.</summary>
    public static void Serialize(ProgramIR program, string path)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, program, PymcuIrContext.Default.ProgramIR);
    }

    /// <summary>Deserialize a <see cref="ProgramIR"/> from the file at <paramref name="path"/>.</summary>
    public static ProgramIR Deserialize(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, PymcuIrContext.Default.ProgramIR)
               ?? throw new InvalidDataException($"Failed to deserialize IR from '{path}'.");
    }
}

// ---------------------------------------------------------------------------
// AOT source-generation context
// ---------------------------------------------------------------------------

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProgramIR))]
[JsonSerializable(typeof(PyMCU.Common.Models.DeviceConfig))]
internal partial class PymcuIrContext : JsonSerializerContext { }
