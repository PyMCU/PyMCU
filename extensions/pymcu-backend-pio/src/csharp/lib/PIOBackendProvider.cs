// SPDX-License-Identifier: MIT
// PyMCU PIO Backend — IBackendProvider implementation.

using PyMCU.Backend.License;
using PyMCU.Backend.Targets.PIO;
using PyMCU.Common.Models;

namespace PyMCU.Backend.Targets.PIO;

/// <summary>
/// Backend provider for the RP2040 PIO state-machine architecture.
/// This is a free and open-source backend — no license key required.
/// </summary>
public sealed class PIOBackendProvider : IBackendProvider
{
    public string Family => "pio";
    public string Description => "RP2040 PIO state-machine codegen backend";
    public string Version => "1.0.0-beta1";

    public bool Supports(string arch)
    {
        var a = arch.ToLowerInvariant();
        return a == "pio" || a == "rp2040-pio";
    }

    public CodeGen Create(DeviceConfig config) => new PIOCodeGen(config);

    /// <summary>PIO backend is free — always returns Valid without checking any key.</summary>
    public LicenseResult ValidateLicense(string? licenseKey = null) => LicenseValidator.Free();
}
