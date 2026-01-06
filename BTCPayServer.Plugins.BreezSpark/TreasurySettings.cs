#nullable enable
using System;

namespace BTCPayServer.Plugins.BreezSpark;

/// <summary>
/// Step 1: Choose the treasury mode - where to send funds
/// </summary>
public enum TreasuryMode
{
    OnChain,    // Send to a Bitcoin address
    Lightning   // Send to a Lightning address (user@domain.com)
}

/// <summary>
/// Step 2a: For OnChain mode, choose address type
/// </summary>
public enum OnChainAddressType
{
    SingleAddress,  // Use a fixed Bitcoin address
    Xpub           // Derive new addresses from an extended public key
}

/// <summary>
/// Fee speed for on-chain transactions
/// </summary>
public enum OnchainFeeSpeed
{
    Slow,
    Medium,
    Fast
}

/// <summary>
/// Treasury management settings for automatic fund sweeping
/// </summary>
public class TreasurySettings
{
    /// <summary>
    /// Enable or disable automatic treasury management
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Balance threshold in satoshis - triggers sweep when exceeded
    /// </summary>
    public long BalanceThresholdSats { get; set; } = 0;

    /// <summary>
    /// Reserve amount in satoshis - keep this amount in wallet after sweep
    /// </summary>
    public long ReserveAmountSats { get; set; } = 0;

    /// <summary>
    /// Minimum sweep amount in satoshis - don't sweep if amount is too small
    /// </summary>
    public long MinSweepAmountSats { get; set; } = 0;

    /// <summary>
    /// Step 1: Choose between On-chain or Lightning destination
    /// </summary>
    public TreasuryMode Mode { get; set; } = TreasuryMode.OnChain;

    /// <summary>
    /// Step 2a: For OnChain mode - choose single address or xpub
    /// </summary>
    public OnChainAddressType OnChainAddressType { get; set; } = OnChainAddressType.SingleAddress;

    /// <summary>
    /// Single Bitcoin address for on-chain sweeps (used when OnChainAddressType = SingleAddress)
    /// </summary>
    public string? OnChainAddress { get; set; }

    /// <summary>
    /// Extended public key for address derivation (used when OnChainAddressType = Xpub)
    /// </summary>
    public string? Xpub { get; set; }

    /// <summary>
    /// Current derivation index for xpub - auto-increments after each successful sweep
    /// </summary>
    public uint XpubDerivationIndex { get; set; } = 0;

    /// <summary>
    /// Custom derivation path for xpub address generation.
    /// Use {index} as placeholder for the derivation index.
    /// Example: "m/84'/0'/0'/0/{index}" or "0/{index}" (relative to xpub)
    /// Default: "0/{index}" (standard receiving address path)
    /// </summary>
    public string XpubDerivationPath { get; set; } = "0/{index}";

    /// <summary>
    /// Fee speed for on-chain transactions
    /// </summary>
    public OnchainFeeSpeed OnchainFeeSpeed { get; set; } = OnchainFeeSpeed.Medium;

    /// <summary>
    /// Step 2b: Lightning address for Lightning mode (user@domain.com format)
    /// </summary>
    public string? LightningAddress { get; set; }

    /// <summary>
    /// How often to check balance in minutes
    /// </summary>
    public int CheckIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Unix timestamp of last balance check
    /// </summary>
    public long LastCheckTimestamp { get; set; } = 0;

    /// <summary>
    /// Unix timestamp of last successful sweep
    /// </summary>
    public long LastSweepTimestamp { get; set; } = 0;
}
