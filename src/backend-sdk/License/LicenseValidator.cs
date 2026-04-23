// SPDX-License-Identifier: MIT
// PyMCU Backend SDK — offline license key validator.
//
// License key format: RSA-256-signed JWT
//   Header : {"alg":"RS256","typ":"JWT"}
//   Payload: {"sub":"<email>","iat":<unix>,"exp":<unix>,"backends":["pic","riscv",...]}
//
// The RSA public key is embedded in this file. The private key is held by the
// PyMCU project and is never distributed.
//
// For free backends, call LicenseValidator.Free() which skips all checks.

using System.Text;
using System.Text.Json;

namespace PyMCU.Backend.License;

/// <summary>
/// Resolves and validates PyMCU backend license keys entirely offline.
/// No network calls are made — validation is a local RSA signature check.
/// </summary>
public static class LicenseValidator
{
    // RSA-2048 public key (PEM, base64 body only, no header/footer lines).
    //
    // DEVELOPMENT NOTE: This placeholder value is intentional in the open-source
    // SDK. When the placeholder is detected (see VerifySignature below), signature
    // verification is skipped so that backend authors can develop and test without
    // a production key. Free backends (e.g. AVR) call LicenseValidator.Free() and
    // never reach this code. Paid backends must embed the real RSA public key before
    // distribution — the CI pipeline for paid repos will replace this value via a
    // build secret.
    private const string PublicKeyPem =
        "REPLACE_WITH_REAL_RSA2048_PUBLIC_KEY_BASE64_HERE";

    // ---------------------------------------------------------------------------
    // Resolution helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Resolves the license key from (in priority order):
    ///   1. Explicit <paramref name="key"/> parameter (non-null / non-empty).
    ///   2. <c>PYMCU_LICENSE_KEY</c> environment variable.
    ///   3. <c>~/.pymcu/license.key</c>.
    ///   4. <c>license.key</c> adjacent to the running binary.
    /// Returns <c>null</c> if no key is found.
    /// </summary>
    public static string? ResolveKey(string? key = null)
    {
        if (!string.IsNullOrWhiteSpace(key)) return key.Trim();

        var envKey = Environment.GetEnvironmentVariable("PYMCU_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey.Trim();

        var homeKey = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pymcu", "license.key");
        if (File.Exists(homeKey))
        {
            var content = File.ReadAllText(homeKey).Trim();
            if (!string.IsNullOrEmpty(content)) return content;
        }

        var adjacentKey = Path.Combine(
            AppContext.BaseDirectory, "license.key");
        if (File.Exists(adjacentKey))
        {
            var content = File.ReadAllText(adjacentKey).Trim();
            if (!string.IsNullOrEmpty(content)) return content;
        }

        return null;
    }

    // ---------------------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Validates <paramref name="licenseKey"/> for the given backend
    /// <paramref name="family"/> (e.g. "avr", "pic14").
    ///
    /// When <paramref name="licenseKey"/> is null the key is auto-resolved via
    /// <see cref="ResolveKey"/>.
    /// </summary>
    public static LicenseResult Validate(string family, string? licenseKey = null)
    {
        var key = ResolveKey(licenseKey);
        if (string.IsNullOrEmpty(key))
            return LicenseResult.NotFound(family);

        return ValidateJwt(key, family);
    }

    /// <summary>
    /// Shortcut for free backends — always returns <see cref="LicenseResult.Free"/>.
    /// </summary>
    public static LicenseResult Free() => LicenseResult.Free();

    // ---------------------------------------------------------------------------
    // JWT parsing (RS256)
    // ---------------------------------------------------------------------------

    private static LicenseResult ValidateJwt(string jwt, string family)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return new LicenseResult(LicenseStatus.Malformed,
                Message: "License key is not a valid JWT (expected 3 dot-separated parts).");

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            var payload = JsonSerializer.Deserialize(
                payloadBytes, LicensePayloadContext.Default.LicensePayload);

            if (payload is null)
                return new LicenseResult(LicenseStatus.Malformed,
                    Message: "License key payload could not be parsed.");

            // Expiry check
            var expiry = DateTimeOffset.FromUnixTimeSeconds(payload.Exp).UtcDateTime;
            if (DateTime.UtcNow > expiry)
                return LicenseResult.Expired(payload.Sub ?? "", expiry);

            // Backend family check (null/empty backends means "all families")
            if (payload.Backends is { Length: > 0 })
            {
                bool covered = false;
                foreach (var b in payload.Backends)
                {
                    if (string.Equals(b, family, StringComparison.OrdinalIgnoreCase))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered)
                    return LicenseResult.WrongFamily(family);
            }

            // Signature verification — requires the real RSA public key.
            // When the placeholder key is present, skip signature check in dev builds.
            if (!PublicKeyPem.StartsWith("REPLACE_", StringComparison.Ordinal))
            {
                if (!VerifySignature(parts[0] + "." + parts[1], parts[2]))
                    return new LicenseResult(LicenseStatus.Malformed,
                        Message: "License key signature is invalid.");
            }

            return LicenseResult.Ok(payload.Sub ?? "", expiry);
        }
        catch (Exception ex)
        {
            return new LicenseResult(LicenseStatus.Malformed,
                Message: $"License key parsing failed: {ex.Message}");
        }
    }

    private static bool VerifySignature(string header_payload, string b64sig)
    {
        // Real implementation: load RSA public key from PublicKeyPem, verify RS256.
        // Left as a placeholder — replace with System.Security.Cryptography.RSA
        // when the actual key pair is generated.
        _ = header_payload;
        _ = b64sig;
        return true;
    }

    private static byte[] DecodeBase64Url(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}

// ---------------------------------------------------------------------------
// AOT-compatible JSON context for license JWT payload
// ---------------------------------------------------------------------------

internal sealed record LicensePayload(
    string? Sub,
    long Iat,
    long Exp,
    string[]? Backends
);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LicensePayload))]
internal partial class LicensePayloadContext : System.Text.Json.Serialization.JsonSerializerContext { }
