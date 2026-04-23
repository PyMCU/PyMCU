// SPDX-License-Identifier: MIT
// PyMCU AVR Backend — IBackendProvider implementation.

using PyMCU.Backend.License;
using PyMCU.Common.Models;

namespace PyMCU.Backend.Targets.AVR;

/// <summary>
/// Backend provider for the AVR architecture family.
/// This is a free and open-source backend — no license key required.
/// </summary>
public sealed class AvrBackendProvider : IBackendProvider
{
    public string Family => "avr";
    public string Description => "AVR codegen backend (atmega, attiny families)";
    public string Version => "1.0.0-beta1";

    private static readonly string[] SupportedChipPrefixes =
        ["atmega", "attiny", "at90", "atxmega"];

    public bool Supports(string arch)
    {
        var a = arch.ToLowerInvariant();
        foreach (var prefix in SupportedChipPrefixes)
            if (a.StartsWith(prefix)) return true;
        return string.Equals(a, "avr", StringComparison.OrdinalIgnoreCase);
    }

    public CodeGen Create(DeviceConfig config) => new AvrCodeGen(config);

    /// <summary>AVR backend is free — always returns Valid without checking any key.</summary>
    public LicenseResult ValidateLicense(string? licenseKey = null) => LicenseValidator.Free();
}
