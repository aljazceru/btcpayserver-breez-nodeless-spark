#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NBitcoin;

namespace BTCPayServer.Plugins.BreezSpark;

/// <summary>
/// Result of a sweep operation
/// </summary>
public record SweepResult(
    bool Success,
    string? PaymentId,
    string? PaymentHash,
    long FeeSats,
    string? Error
);

/// <summary>
/// Helper utilities for treasury management operations
/// </summary>
public static class TreasuryHelper
{
    /// <summary>
    /// Derives a Bitcoin address from an xpub at the given index using a custom derivation path
    /// Path format: Use {index} as placeholder for the derivation index
    /// Examples: "0/{index}", "0/0/{index}", "84'/0'/0'/0/{index}"
    /// </summary>
    public static string DeriveAddressFromXpub(string xpub, uint index, Network network, string? derivationPath = null)
    {
        var extPubKey = ExtPubKey.Parse(xpub, network);

        // Default to standard receiving path if not specified
        var path = string.IsNullOrWhiteSpace(derivationPath) ? "0/{index}" : derivationPath;

        // Replace the {index} placeholder with actual index
        var resolvedPath = path.Replace("{index}", index.ToString());

        // Parse and apply the derivation path
        var derivedKey = DeriveFromPath(extPubKey, resolvedPath);

        // Generate native segwit (bech32) address by default
        var pubKey = derivedKey.PubKey;
        var address = pubKey.GetAddress(ScriptPubKeyType.Segwit, network);

        return address.ToString();
    }

    /// <summary>
    /// Derives an ExtPubKey from a path string like "0/5" or "0/0/3"
    /// Supports both hardened (') and non-hardened derivation
    /// </summary>
    private static ExtPubKey DeriveFromPath(ExtPubKey key, string path)
    {
        // Remove leading "m/" if present (common in full paths)
        if (path.StartsWith("m/", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Substring(2);
        }

        // Split path into components
        var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        var result = key;
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Check for hardened derivation (ends with ' or h or H)
            var isHardened = trimmed.EndsWith("'") || trimmed.EndsWith("h", StringComparison.OrdinalIgnoreCase);
            var indexStr = isHardened ? trimmed.TrimEnd('\'', 'h', 'H') : trimmed;

            if (!uint.TryParse(indexStr, out var derivationIndex))
            {
                throw new ArgumentException($"Invalid derivation path component: {part}");
            }

            // Note: Hardened derivation from xpub is not possible (requires private key)
            // If hardened path is specified with xpub, we can only derive the non-hardened portion
            if (isHardened)
            {
                throw new ArgumentException($"Cannot derive hardened path '{part}' from extended public key. " +
                    "Hardened derivation requires the master private key. " +
                    "For xpub, use only non-hardened indices (without ' suffix).");
            }

            result = result.Derive(derivationIndex);
        }

        return result;
    }

    /// <summary>
    /// Validates a derivation path string
    /// Returns true if the path is valid for xpub derivation
    /// </summary>
    public static bool ValidateDerivationPath(string? path, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return true; // Empty path means use default
        }

        // Must contain {index} placeholder
        if (!path.Contains("{index}"))
        {
            error = "Derivation path must contain {index} placeholder";
            return false;
        }

        // Replace placeholder to validate the rest
        var testPath = path.Replace("{index}", "0");

        // Remove leading m/ if present
        if (testPath.StartsWith("m/", StringComparison.OrdinalIgnoreCase))
        {
            testPath = testPath.Substring(2);
        }

        var parts = testPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var isHardened = trimmed.EndsWith("'") || trimmed.EndsWith("h", StringComparison.OrdinalIgnoreCase);
            var indexStr = isHardened ? trimmed.TrimEnd('\'', 'h', 'H') : trimmed;

            if (!uint.TryParse(indexStr, out _))
            {
                error = $"Invalid path component: '{part}'. Each component must be a number.";
                return false;
            }

            if (isHardened)
            {
                error = $"Hardened derivation ('{part}') cannot be used with xpub. Remove the ' suffix.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Generates a preview of multiple addresses from an xpub
    /// </summary>
    public static List<(uint Index, string Address)> PreviewAddresses(
        string xpub,
        uint startIndex,
        int count,
        Network network,
        string? derivationPath = null)
    {
        // Guard against negative count causing uint overflow in loop comparison
        if (count <= 0)
            return new List<(uint Index, string Address)>();

        var addresses = new List<(uint Index, string Address)>();

        for (uint i = 0; i < count; i++)
        {
            var index = startIndex + i;
            var address = DeriveAddressFromXpub(xpub, index, network, derivationPath);
            addresses.Add((index, address));
        }

        return addresses;
    }

    /// <summary>
    /// Validates an xpub string
    /// </summary>
    public static bool ValidateXpub(string xpub, Network network)
    {
        if (string.IsNullOrWhiteSpace(xpub))
            return false;

        try
        {
            ExtPubKey.Parse(xpub, network);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates a Lightning address format (user@domain.com)
    /// </summary>
    public static bool ValidateLightningAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var parts = address.Split('@');
        if (parts.Length != 2)
            return false;

        // Basic validation - user part and domain part
        if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return false;

        // Domain should have at least one dot
        if (!parts[1].Contains('.'))
            return false;

        return true;
    }

    /// <summary>
    /// Validates a Bitcoin address
    /// </summary>
    public static bool ValidateBitcoinAddress(string? address, Network network)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        try
        {
            BitcoinAddress.Create(address, network);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a Lightning address (user@domain.com) to a BOLT11 invoice via LNURL-pay
    /// </summary>
    public static async Task<string?> ResolveLightningAddress(
        string lightningAddress,
        long amountSats,
        HttpClient httpClient)
    {
        // Parse lightning address
        var parts = lightningAddress.Split('@');
        if (parts.Length != 2)
            return null;

        var (user, domain) = (parts[0], parts[1]);

        // Fetch LNURL-pay metadata from well-known endpoint
        var wellKnownUrl = $"https://{domain}/.well-known/lnurlp/{user}";

        var response = await httpClient.GetStringAsync(wellKnownUrl);
        var lnurlPayData = JsonSerializer.Deserialize<LnurlPayResponse>(response);

        if (lnurlPayData?.callback is null)
            return null;

        // Validate amount (LNURL uses millisatoshis)
        var amountMsat = amountSats * 1000;
        if (amountMsat < lnurlPayData.minSendable || amountMsat > lnurlPayData.maxSendable)
        {
            throw new InvalidOperationException(
                $"Amount {amountSats} sats outside allowed range " +
                $"[{lnurlPayData.minSendable / 1000}-{lnurlPayData.maxSendable / 1000}] sats");
        }

        // Get invoice from callback
        var callbackUrl = lnurlPayData.callback.Contains('?')
            ? $"{lnurlPayData.callback}&amount={amountMsat}"
            : $"{lnurlPayData.callback}?amount={amountMsat}";

        var invoiceResponse = await httpClient.GetStringAsync(callbackUrl);
        var invoiceData = JsonSerializer.Deserialize<LnurlPayInvoiceResponse>(invoiceResponse);

        return invoiceData?.pr;
    }
}

/// <summary>
/// LNURL-pay response from well-known endpoint
/// </summary>
public class LnurlPayResponse
{
    public string? callback { get; set; }
    public long minSendable { get; set; }
    public long maxSendable { get; set; }
    public string? tag { get; set; }
}

/// <summary>
/// LNURL-pay invoice response from callback
/// </summary>
public class LnurlPayInvoiceResponse
{
    public string? pr { get; set; } // BOLT11 invoice
    public string[]? routes { get; set; }
}
