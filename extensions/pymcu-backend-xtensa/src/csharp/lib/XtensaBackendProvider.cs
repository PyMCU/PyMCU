// SPDX-License-Identifier: MIT
// PyMCU Xtensa Backend — IBackendProvider implementation.

using PyMCU.Backend.License;
using PyMCU.Backend.Targets.Xtensa;
using PyMCU.Common.Models;

namespace PyMCU.Backend.Targets.Xtensa;

/// <summary>
/// Backend provider for the Xtensa architecture family.
/// Supports: ESP8266 (LX106), ESP32 (LX6), ESP32-S2 (LX7), ESP32-S3 (LX7).
/// This is a free and open-source backend — no license key required.
/// </summary>
public sealed class XtensaBackendProvider : IBackendProvider
{
    public string Family => "xtensa";
    public string Description => "Xtensa codegen backend (ESP8266, ESP32, ESP32-S2, ESP32-S3)";
    public string Version => "1.0.0-beta1";

    public bool Supports(string arch)
    {
        var a = arch.ToLowerInvariant();
        return a == "xtensa"
            || a.StartsWith("esp8266")
            || a.StartsWith("esp32")
            || a == "lx106"
            || a == "lx6"
            || a == "lx7";
    }

    public CodeGen Create(DeviceConfig config) => new XtensaCodeGen(config);

    /// <summary>Xtensa backend is free — always returns Valid without checking any key.</summary>
    public LicenseResult ValidateLicense(string? licenseKey = null) => LicenseValidator.Free();
}
