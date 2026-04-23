// SPDX-License-Identifier: MIT
// PyMCU ARM Backend — IBackendProvider implementation.

using PyMCU.Backend.License;
using PyMCU.Backend.Targets.ARM;
using PyMCU.Common.Models;

namespace PyMCU.Backend.Targets.ARM;

/// <summary>
/// Backend provider for the ARM Cortex-M architecture family (RP2040, RP2350).
/// Emits LLVM IR text (.ll) which clang/lld compiles to an ARM ELF binary.
/// This is a free and open-source backend — no license key required.
/// </summary>
public sealed class ArmBackendProvider : IBackendProvider
{
    public string Family => "arm";
    public string Description => "ARM LLVM IR codegen backend (RP2040 Cortex-M0+, RP2350 Cortex-M33)";
    public string Version => "0.1.0-alpha.1";

    public bool Supports(string arch)
    {
        var a = arch.ToLowerInvariant();
        return a == "arm"
            || a.StartsWith("rp2")
            || a.StartsWith("thumbv6m")
            || a.StartsWith("thumbv8m");
    }

    public CodeGen Create(DeviceConfig config) => new ArmLlvmCodeGen(config);

    /// <summary>ARM backend is free — always returns Valid without checking any key.</summary>
    public LicenseResult ValidateLicense(string? licenseKey = null) => LicenseValidator.Free();
}
