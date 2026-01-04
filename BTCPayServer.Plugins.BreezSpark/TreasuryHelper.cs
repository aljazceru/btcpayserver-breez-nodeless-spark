#nullable enable
using System;
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
    /// Derives a Bitcoin address from an xpub at the given index using BIP44 standard
    /// Path: m/0/{index} (receiving addresses)
    /// </summary>
    public static string DeriveAddressFromXpub(string xpub, uint index, Network network)
    {
        var extPubKey = ExtPubKey.Parse(xpub, network);

        // Derive m/0/{index} - standard receiving address path
        var derivedKey = extPubKey.Derive(0).Derive(index);

        // Generate native segwit (bech32) address by default
        var pubKey = derivedKey.PubKey;
        var address = pubKey.GetAddress(ScriptPubKeyType.Segwit, network);

        return address.ToString();
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
