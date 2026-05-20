// SPDX-License-Identifier: MIT
// PyMCU RISC-V Backend — IBackendProvider implementation.

using PyMCU.Backend.License;
using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;

namespace PyMCU.Backend.Targets.RiscV;

/// <summary>
/// Backend provider for the RISC-V architecture family (RV32EC, CH32V).
/// This is a free and open-source backend — no license key required.
/// </summary>
public sealed class RiscVBackendProvider : IBackendProvider
{
    public string Family => "riscv";
    public string Description => "RISC-V codegen backend (RV32EC, CH32V003/V103)";
    public string Version => "1.0.0-beta1";

    public bool Supports(string arch)
    {
        var a = arch.ToLowerInvariant();
        return a == "riscv" || a == "rv32ec" || a.StartsWith("ch32v");
    }

    public CodeGen Create(DeviceConfig config) => new RiscvCodeGen(config);

    /// <summary>RISC-V backend is free — always returns Valid without checking any key.</summary>
    public LicenseResult ValidateLicense(string? licenseKey = null) => LicenseValidator.Free();
}
