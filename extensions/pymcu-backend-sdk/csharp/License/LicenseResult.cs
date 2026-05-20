// SPDX-License-Identifier: MIT
// PyMCU Backend SDK — license validation result types.

namespace PyMCU.Backend.License;

/// <summary>Outcome of validating a PyMCU backend license key.</summary>
public enum LicenseStatus
{
    /// <summary>License is valid and covers the requested backend family.</summary>
    Valid,

    /// <summary>No license key was found (env var, file, or explicit).</summary>
    Missing,

    /// <summary>The license key is present but has passed its expiry date.</summary>
    Expired,

    /// <summary>The license does not cover the requested backend family.</summary>
    InvalidTarget,

    /// <summary>The license key could not be parsed or the signature is invalid.</summary>
    Malformed,
}

/// <summary>Result of a license validation check.</summary>
public sealed record LicenseResult(
    LicenseStatus Status,
    string? Email = null,
    DateTime? ExpiryDate = null,
    string? Message = null
)
{
    /// <summary>Convenience factory for a valid result.</summary>
    public static LicenseResult Ok(string email, DateTime expiry) =>
        new(LicenseStatus.Valid, Email: email, ExpiryDate: expiry);

    /// <summary>Convenience factory — free backend (always valid, no expiry).</summary>
    public static LicenseResult Free() =>
        new(LicenseStatus.Valid, Message: "free");

    /// <summary>Convenience factory for a missing license.</summary>
    public static LicenseResult NotFound(string family) =>
        new(LicenseStatus.Missing,
            Message: $"PyMCU Backend for {family} requires a license. " +
                     $"Purchase at https://pymcu.dev/pricing. " +
                     $"Set PYMCU_LICENSE_KEY or place your key at ~/.pymcu/license.key.");

    /// <summary>Convenience factory for an expired license.</summary>
    public static LicenseResult Expired(string email, DateTime expiry) =>
        new(LicenseStatus.Expired, Email: email, ExpiryDate: expiry,
            Message: $"Your PyMCU backend license expired on {expiry:yyyy-MM-dd}. " +
                     $"Renew at https://pymcu.dev/renew.");

    /// <summary>Convenience factory for a key that does not cover the requested family.</summary>
    public static LicenseResult WrongFamily(string family) =>
        new(LicenseStatus.InvalidTarget,
            Message: $"Your license does not include the {family} backend. " +
                     $"Upgrade at https://pymcu.dev/pricing.");
}
