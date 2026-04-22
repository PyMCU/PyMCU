// SPDX-License-Identifier: MIT
// PyMCU PIC Backend — IBackendProvider implementation for PIC12, PIC14, PIC18.

using PyMCU.Backend.License;
using PyMCU.Backend.Targets.PIC12;
using PyMCU.Backend.Targets.PIC14;
using PyMCU.Backend.Targets.PIC18;
using PyMCU.Common.Models;

namespace PyMCU.Backend.Targets.PIC;

/// <summary>
/// Backend provider for the PIC architecture family (PIC12, PIC14/16, PIC18).
/// This is a free and open-source backend — no license key required.
/// </summary>
public sealed class PicBackendProvider : IBackendProvider
{
    public string Family => "pic";
    public string Description => "PIC codegen backend (PIC12, PIC14/16F, PIC18F families)";
    public string Version => "1.0.0-beta1";

    public bool Supports(string arch)
    {
        var a = arch.ToLowerInvariant();
        return a == "pic12" || a == "baseline" || a.StartsWith("pic10f") || a.StartsWith("pic12f")
            || a == "pic14" || a == "pic14e" || a == "midrange" || a.StartsWith("pic16f")
            || a == "pic18" || a == "advanced" || a.StartsWith("pic18f");
    }

    public CodeGen Create(DeviceConfig config) => config.Arch.ToLowerInvariant() switch
    {
        var a when a == "pic12" || a == "baseline" || a.StartsWith("pic10f") || a.StartsWith("pic12f")
            => new PIC12CodeGen(config),
        var a when a == "pic14" || a == "pic14e" || a == "midrange" || a.StartsWith("pic16f")
            => new PIC14CodeGen(config),
        _ => new PIC18CodeGen(config)
    };

    /// <summary>PIC backend is free — always returns Valid without checking any key.</summary>
    public LicenseResult ValidateLicense(string? licenseKey = null) => LicenseValidator.Free();
}
