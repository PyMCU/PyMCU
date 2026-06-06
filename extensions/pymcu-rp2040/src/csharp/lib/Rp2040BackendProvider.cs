// SPDX-License-Identifier: MIT
// PyMCU RP2040 Backend - IBackendProvider implementation.

using PyMCU.Backend.License;
using PyMCU.Common.Models;

namespace PyMCU.Backend.Targets.RP2040;

/// <summary>
/// Backend provider for the RP2040 (ARM Cortex-M0+) target.
/// Free and open-source - no license key required.
/// Produces LLVM IR text rather than assembly.
/// </summary>
public sealed class Rp2040BackendProvider : IBackendProvider
{
    public string Family => "rp2040";
    public string Description => "RP2040 codegen backend (Cortex-M0+, LLVM IR output)";
    public string Version => "0.1.0-alpha.1";

    private static readonly string[] SupportedArches =
        ["rp2040", "cortex-m0plus", "cortex-m0+", "cortex-m0", "arm"];

    public bool Supports(string arch)
    {
        var a = arch.ToLowerInvariant();
        foreach (var s in SupportedArches)
            if (a == s) return true;
        return false;
    }

    public CodeGen Create(DeviceConfig config) => new Rp2040LlvmCodeGen(config);

    /// <summary>RP2040 backend is free - always returns Valid.</summary>
    public LicenseResult ValidateLicense(string? licenseKey = null) => LicenseValidator.Free();
}
