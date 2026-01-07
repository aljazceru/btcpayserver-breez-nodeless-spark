#nullable enable
using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.BreezSpark;

/// <summary>
/// Status of a treasury sweep operation
/// </summary>
public enum TreasurySweepStatus
{
    Pending,
    Completed,
    Failed
}

/// <summary>
/// Record of a single treasury sweep operation
/// </summary>
public class TreasurySweepRecord
{
    /// <summary>
    /// Unique identifier for this sweep record
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// When the sweep was initiated
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Amount swept in satoshis
    /// </summary>
    public long AmountSats { get; set; }

    /// <summary>
    /// Fee paid in satoshis
    /// </summary>
    public long FeeSats { get; set; }

    /// <summary>
    /// Destination address or lightning address
    /// </summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// Treasury mode used (OnChain or Lightning)
    /// </summary>
    public TreasuryMode Mode { get; set; }

    /// <summary>
    /// Payment ID from Breez SDK (or txid for on-chain)
    /// </summary>
    public string? PaymentId { get; set; }

    /// <summary>
    /// Payment hash for Lightning payments
    /// </summary>
    public string? PaymentHash { get; set; }

    /// <summary>
    /// Status of the sweep operation
    /// </summary>
    public TreasurySweepStatus Status { get; set; } = TreasurySweepStatus.Pending;

    /// <summary>
    /// Error message if the sweep failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Balance before the sweep in satoshis
    /// </summary>
    public long BalanceBeforeSats { get; set; }

    /// <summary>
    /// Balance after the sweep in satoshis
    /// </summary>
    public long BalanceAfterSats { get; set; }
}

/// <summary>
/// Container for treasury sweep history
/// </summary>
public class TreasuryHistory
{
    /// <summary>
    /// List of sweep records, newest first
    /// </summary>
    public List<TreasurySweepRecord> Sweeps { get; set; } = new();
}
