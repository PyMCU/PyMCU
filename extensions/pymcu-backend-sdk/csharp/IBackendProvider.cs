// SPDX-License-Identifier: MIT
// PyMCU Backend SDK — contract interface for all codegen backend providers.

using PyMCU.Backend.License;
using PyMCU.Common.Models;

namespace PyMCU.Backend;

/// <summary>
/// Entry-point contract that every PyMCU backend package must implement.
///
/// Each backend binary discovers its provider by convention: the runner binary
/// (e.g. pymcuc-avr) directly instantiates the concrete class.  There is no
/// runtime plugin loading — backends are AOT-compiled standalone executables.
/// </summary>
public interface IBackendProvider
{
    /// <summary>Canonical family name, e.g. "avr", "pic14", "riscv".</summary>
    string Family { get; }

    /// <summary>Human-readable description shown in <c>pymcu backend list</c>.</summary>
    string Description { get; }

    /// <summary>Version string of this backend package.</summary>
    string Version { get; }

    /// <summary>Returns true when this provider can handle the given arch/chip string.</summary>
    bool Supports(string arch);

    /// <summary>Construct a <see cref="CodeGen"/> instance for the given config.</summary>
    CodeGen Create(DeviceConfig config);

    /// <summary>
    /// Validate the license for this backend.
    ///
    /// Resolution order for <paramref name="licenseKey"/>:
    ///   1. Explicitly provided key (e.g. from CLI or Python driver).
    ///   2. <c>PYMCU_LICENSE_KEY</c> environment variable.
    ///   3. <c>~/.pymcu/license.key</c> file.
    ///   4. <c>license.key</c> adjacent to the backend binary.
    /// </summary>
    LicenseResult ValidateLicense(string? licenseKey = null);
}
