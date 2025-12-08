using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Breez.Sdk.Spark;
using BTCPayServer.Lightning;
using NBitcoin;
using Network = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Breez;

public class EventLogEntry
{
    public DateTimeOffset timestamp { get; set; }
    public string log { get; set; } = string.Empty;
}

public class BreezLightningClient : ILightningClient, IDisposable
{
    public override string ToString()
    {
        return $"type=breez;key={PaymentKey}";
    }

    private readonly NBitcoin.Network _network;
    public readonly string PaymentKey;

    public ConcurrentQueue<EventLogEntry> Events { get; set; } = new ConcurrentQueue<EventLogEntry>();
    private readonly ConcurrentQueue<Payment> _paymentNotifications = new();
    private readonly ConcurrentDictionary<string, bool> _seenCompletedPayments = new();
    private readonly ConcurrentDictionary<string, bool> _seenPaymentHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InvoiceRecord> _invoicesByHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InvoiceRecord> _invoicesByBolt11 = new(StringComparer.OrdinalIgnoreCase);

    private void DebugLog(string message)
    {
        // Debug logging disabled for release build
    }

    private void DebugLogObject(string label, object obj)
    {
        // Debug logging disabled for release build
    }

    private BreezSdk _sdk;

    public static async Task<BreezLightningClient> Create(string apiKey, string workingDir, NBitcoin.Network network,
        Mnemonic mnemonic, string paymentKey)
    {
        apiKey ??= "99010c6f84541bf582899db6728f6098ba98ca95ea569f4c63f2c2c9205ace57";

        var config = BreezSdkSparkMethods.DefaultConfig(
            network == NBitcoin.Network.Main ? Network.Mainnet :
            network == NBitcoin.Network.RegTest ? Network.Regtest : Network.Mainnet
        ) with
        {
            apiKey = apiKey
        };

        var seed = new Seed.Mnemonic(mnemonic: mnemonic.ToString(), passphrase: null);
        var sdk = await BreezSdkSparkMethods.Connect(new ConnectRequest(config, seed, workingDir));

        return new BreezLightningClient(sdk, network, paymentKey);
    }

    private BreezLightningClient(BreezSdk sdk, NBitcoin.Network network, string paymentKey)
    {
        _sdk = sdk;
        _network = network;
        PaymentKey = paymentKey;

        // Start monitoring payment events
        _ = Task.Run(MonitorPaymentEvents);
    }

    public BreezSdk Sdk => _sdk;

    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        var invoice = await GetInvoiceInternal(invoiceId, cancellation);
        if (invoice is not null)
        {
            return invoice;
        }

        return new LightningInvoice()
        {
            Id = invoiceId,
            PaymentHash = invoiceId,
            Status = LightningInvoiceStatus.Unpaid
        };
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        return await GetInvoice(paymentHash.ToString(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {
        return await ListInvoices((ListInvoicesParams?)null, cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = default)
    {
        var req = new ListPaymentsRequest(
            typeFilter: new List<PaymentType> { PaymentType.Receive },
            statusFilter: request?.PendingOnly == true ? new List<PaymentStatus> { PaymentStatus.Pending } : null,
            assetFilter: new AssetFilter.Bitcoin(),
            fromTimestamp: null,
            toTimestamp: null,
            offset: request?.OffsetIndex != null ? (uint?)request.OffsetIndex : null,
            limit: null,
            sortAscending: false
        );

        var response = await _sdk.ListPayments(req);
        return response.payments.Select(FromPayment).Where(p => p != null).ToArray();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        var payment = await FindPayment(paymentHash, cancellation);
        return payment is not null ? ToLightningPayment(payment) : null;
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        return await ListPayments((ListPaymentsParams?)null, cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = default)
    {
        var req = new ListPaymentsRequest(
            typeFilter: new List<PaymentType> { PaymentType.Send },
            statusFilter: null,
            assetFilter: new AssetFilter.Bitcoin(),
            fromTimestamp: null,
            toTimestamp: null,
            offset: request?.OffsetIndex != null ? (uint?)request.OffsetIndex : null,
            limit: null,
            sortAscending: false
        );

        var response = await _sdk.ListPayments(req);
        return response.payments.Select(ToLightningPayment).Where(p => p != null).ToArray();
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        var descriptionToUse = description ?? "Invoice";
        var amountSats = (ulong)amount.ToUnit(LightMoneyUnit.Satoshi);
        var paymentMethod = new ReceivePaymentMethod.Bolt11Invoice(descriptionToUse, amountSats);
        var response = await _sdk.ReceivePayment(new ReceivePaymentRequest(paymentMethod));
        DebugLogObject("ReceivePaymentResponse(CreateInvoice)", response);
        return FromReceivePaymentResponse(response, amount);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = default)
    {
        var description = createInvoiceRequest.Description ?? createInvoiceRequest.DescriptionHash?.ToString() ?? "Invoice";
        var amountSats = (ulong)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        var paymentMethod = new ReceivePaymentMethod.Bolt11Invoice(description, amountSats);
        var response = await _sdk.ReceivePayment(new ReceivePaymentRequest(paymentMethod));
        DebugLogObject("ReceivePaymentResponse(CreateInvoiceParams)", response);
        return FromReceivePaymentResponse(response, createInvoiceRequest.Amount);
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        return new BreezInvoiceListener(this, cancellation);
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        try
        {
            var response = await _sdk.GetInfo(new GetInfoRequest(ensureSynced: false));

            return new LightningNodeInformation()
            {
                Alias = "Breez Spark (nodeless)",
                BlockHeight = 0, // Spark SDK doesn't expose block height
                Version = "0.4.1" // SDK version hardcoded since property not found
            };
        }
        catch
        {
            return new LightningNodeInformation()
            {
                Alias = "Breez Spark (nodeless)",
                BlockHeight = 0
            };
        }
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        try
        {
            var response = await _sdk.GetInfo(new GetInfoRequest(ensureSynced: false));

            return new LightningNodeBalance()
            {
                OnchainBalance = new OnchainBalance()
                {
                    Confirmed = Money.Satoshis((long)response.balanceSats)
                },
                OffchainBalance = new OffchainBalance()
                {
                    Local = LightMoney.Satoshis((long)response.balanceSats),
                    Remote = LightMoney.Zero
                }
            };
        }
        catch
        {
            return new LightningNodeBalance()
            {
                OnchainBalance = new OnchainBalance()
                {
                    Confirmed = Money.Zero
                },
                OffchainBalance = new OffchainBalance()
                {
                    Local = LightMoney.Zero,
                    Remote = LightMoney.Zero
                }
            };
        }
    }

    public async Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        return await Pay(null, payParams, cancellation);
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = default)
    {
        try
        {
            if (string.IsNullOrEmpty(bolt11))
            {
                return new PayResponse(PayResult.Error, "BOLT11 invoice required");
            }

            BigInteger? amountSats = null;
            if (payParams.Amount > 0)
            {
                amountSats = new BigInteger(payParams.Amount);
            }

            var prepareRequest = new PrepareSendPaymentRequest(
                paymentRequest: bolt11,
                amount: amountSats
            );
            var prepareResponse = await _sdk.PrepareSendPayment(prepareRequest);

            if (prepareResponse.paymentMethod is SendPaymentMethod.Bolt11Invoice bolt11Method)
            {
                var options = new SendPaymentOptions.Bolt11Invoice(
                    preferSpark: false,
                    completionTimeoutSecs: 60
                );

                var sendRequest = new SendPaymentRequest(
                    prepareResponse: prepareResponse,
                    options: options
                );
                var sendResponse = await _sdk.SendPayment(sendRequest);

                return new PayResponse()
                {
                    Result = sendResponse.payment.status switch
                    {
                        PaymentStatus.Failed => PayResult.Error,
                        PaymentStatus.Completed => PayResult.Ok,
                        PaymentStatus.Pending => PayResult.Unknown,
                        _ => PayResult.Error
                    },
                    Details = new PayDetails()
                    {
                        Status = sendResponse.payment.status switch
                        {
                            PaymentStatus.Failed => LightningPaymentStatus.Failed,
                            PaymentStatus.Completed => LightningPaymentStatus.Complete,
                            PaymentStatus.Pending => LightningPaymentStatus.Pending,
                            _ => LightningPaymentStatus.Unknown
                        },
                        TotalAmount = LightMoney.Satoshis((long)(sendResponse.payment.amount / 1000)),
                        FeeAmount = (long)(bolt11Method.lightningFeeSats + (bolt11Method.sparkTransferFeeSats ?? 0))
                    }
                };
            }
            else
            {
                return new PayResponse(PayResult.Error, "Invalid payment method");
            }
        }
        catch (Exception e)
        {
            return new PayResponse(PayResult.Error, e.Message);
        }
    }

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        return await Pay(bolt11, null, cancellation);
    }

    public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    private LightningInvoice FromReceivePaymentResponse(ReceivePaymentResponse response, LightMoney requestedAmount)
    {
        string? paymentHash = null;
        try
        {
            if (BOLT11PaymentRequest.TryParse(response.paymentRequest, out var pr, _network))
            {
                paymentHash = pr.PaymentHash?.ToString();
            }
        }
        catch
        {
            // Ignore parse errors and fall back to raw request
        }

        DebugLogObject("FromReceivePaymentResponse", response);
        RecordInvoiceAmount(response.paymentRequest, paymentHash, requestedAmount);

        return new LightningInvoice()
        {
            Id = paymentHash ?? response.paymentRequest,
            PaymentHash = paymentHash,
            BOLT11 = response.paymentRequest,
            Status = LightningInvoiceStatus.Unpaid,
            Amount = requestedAmount
        };
    }

    private LightningInvoice FromPayment(Payment payment)
    {
        if (payment == null) return null;

        string paymentHash = ExtractPaymentHash(payment);
        string bolt11 = null;
        LightMoney? boltAmount = null;
        LightMoney? recordedAmount = null;

        if (payment.details is PaymentDetails.Lightning lightningDetails)
        {
            bolt11 = lightningDetails.invoice;
            if (!string.IsNullOrEmpty(lightningDetails.invoice) &&
                BOLT11PaymentRequest.TryParse(lightningDetails.invoice, out var pr, _network))
            {
                boltAmount = pr.MinimumAmount;
            }

            var rec = LookupInvoice(lightningDetails.invoice, paymentHash);
            recordedAmount = rec?.Amount;
        }

        // Reject if hash is missing or not one we issued
        if (string.IsNullOrEmpty(paymentHash))
        {
            DebugLog($"FromPayment: missing payment hash for payment.id={payment.id}");
            return null;
        }

        var record = LookupInvoice(null, paymentHash);
        if (record is null || record.PaymentHash != paymentHash)
        {
            DebugLog($"FromPayment: unknown payment hash={paymentHash} payment.id={payment.id}");
            return null;
        }

        recordedAmount ??= record.Amount;

        // Always use the invoice amount (BOLT11 truth). Never fall back to what Breez reports.
        var resolvedAmount = recordedAmount ?? boltAmount;
        if (boltAmount is not null && recordedAmount is not null && boltAmount != recordedAmount)
        {
            DebugLog($"FromPayment: bolt amount {boltAmount.ToUnit(LightMoneyUnit.Satoshi)} != recorded {recordedAmount.ToUnit(LightMoneyUnit.Satoshi)} for hash={paymentHash}");
        }

        var invoiceId = paymentHash;
        if (resolvedAmount is null)
        {
            DebugLog($"FromPayment: missing amount for hash={paymentHash} bolt11={Shorten(bolt11)}");
            return null;
        }

        DebugLog($"FromPayment: returning invoice id={invoiceId} hash={paymentHash} bolt11={Shorten(bolt11)} boltSat={boltAmount?.ToUnit(LightMoneyUnit.Satoshi)} recSat={recordedAmount?.ToUnit(LightMoneyUnit.Satoshi)} raw_msat={payment.amount} fee_msat={payment.fees} chosenSat={resolvedAmount.ToUnit(LightMoneyUnit.Satoshi)}");

        return new LightningInvoice()
        {
            Id = invoiceId,
            PaymentHash = paymentHash ?? invoiceId,
            BOLT11 = bolt11 ?? payment.id,
            Amount = resolvedAmount,
            AmountReceived = resolvedAmount,
            Status = payment.status switch
            {
                PaymentStatus.Pending => LightningInvoiceStatus.Unpaid,
                PaymentStatus.Failed => LightningInvoiceStatus.Expired,
                PaymentStatus.Completed => LightningInvoiceStatus.Paid,
                _ => LightningInvoiceStatus.Unpaid
            },
            PaidAt = DateTimeOffset.FromUnixTimeSeconds((long)payment.timestamp)
        };
    }

    private LightningPayment ToLightningPayment(Payment payment)
    {
        if (payment == null) return null;

        string paymentHash = ExtractPaymentHash(payment);
        string preimage = null;
        string bolt11 = null;
        LightMoney? boltAmount = null;
        LightMoney? recordedAmount = null;
        var feeAmount = GetFeeFromPayment(payment);

        if (payment.details is PaymentDetails.Lightning lightningDetails)
        {
            preimage = lightningDetails.preimage;
            bolt11 = lightningDetails.invoice;
            if (!string.IsNullOrEmpty(lightningDetails.invoice) &&
                BOLT11PaymentRequest.TryParse(lightningDetails.invoice, out var pr, _network))
            {
                boltAmount = pr.MinimumAmount;
            }

            var rec = LookupInvoice(lightningDetails.invoice, paymentHash);
            recordedAmount = rec?.Amount;
        }

        if (string.IsNullOrEmpty(paymentHash))
        {
            DebugLog($"ToLightningPayment: missing payment hash for payment.id={payment.id}");
            return null;
        }

        var record = LookupInvoice(null, paymentHash);
        if (record is null || record.PaymentHash != paymentHash)
        {
            DebugLog($"ToLightningPayment: unknown payment hash={paymentHash} payment.id={payment.id}");
            return null;
        }

        recordedAmount ??= record.Amount;

        var resolvedAmount = recordedAmount ?? boltAmount;
        if (boltAmount is not null && recordedAmount is not null && boltAmount != recordedAmount)
        {
            DebugLog($"ToLightningPayment: bolt amount {boltAmount.ToUnit(LightMoneyUnit.Satoshi)} != recorded {recordedAmount.ToUnit(LightMoneyUnit.Satoshi)} for hash={paymentHash}");
        }

        var paymentId = paymentHash;
        if (resolvedAmount is null)
        {
            DebugLog($"ToLightningPayment: missing amount for hash={paymentHash} bolt11={Shorten(bolt11)}");
            return null;
        }

        DebugLog($"ToLightningPayment: returning payment id={paymentId} hash={paymentHash} bolt11={Shorten(bolt11)} boltSat={boltAmount?.ToUnit(LightMoneyUnit.Satoshi)} recSat={recordedAmount?.ToUnit(LightMoneyUnit.Satoshi)} raw_msat={payment.amount} fee_msat={payment.fees} chosenSat={resolvedAmount.ToUnit(LightMoneyUnit.Satoshi)}");

        return new LightningPayment()
        {
            Id = paymentId,
            PaymentHash = paymentHash ?? paymentId,
            Preimage = preimage,
            BOLT11 = bolt11,
            Amount = resolvedAmount,
            Status = payment.status switch
            {
                PaymentStatus.Failed => LightningPaymentStatus.Failed,
                PaymentStatus.Completed => LightningPaymentStatus.Complete,
                PaymentStatus.Pending => LightningPaymentStatus.Pending,
                _ => LightningPaymentStatus.Unknown
            },
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds((long)payment.timestamp),
            Fee = feeAmount,
            AmountSent = resolvedAmount
        };
    }

    private void RecordInvoiceAmount(string bolt11, string? paymentHash, LightMoney requestedAmount)
    {
        // Prefer the amount encoded in the BOLT11 (ground truth), fall back to the requested amount.
        LightMoney amount = requestedAmount;
        try
        {
            if (BOLT11PaymentRequest.TryParse(bolt11, out var pr, _network))
            {
                if (pr.MinimumAmount is not null)
                    amount = pr.MinimumAmount;
                if (string.IsNullOrEmpty(paymentHash) && pr.PaymentHash is not null)
                    paymentHash = pr.PaymentHash.ToString();
            }
        }
        catch { }

        if (string.IsNullOrEmpty(paymentHash))
            return;

        var record = new InvoiceRecord
        {
            PaymentHash = paymentHash,
            Bolt11 = bolt11,
            Amount = amount
        };

        _invoicesByHash[paymentHash] = record;
        _invoicesByBolt11[bolt11] = record;
    }

    private InvoiceRecord? LookupInvoice(string? bolt11, string? paymentHash)
    {
        if (!string.IsNullOrEmpty(paymentHash) && _invoicesByHash.TryGetValue(paymentHash, out var recByHash))
        {
            DebugLog($"LookupInvoice: hit by hash={paymentHash} amount_sat={recByHash.Amount.ToUnit(LightMoneyUnit.Satoshi)} bolt11={Shorten(recByHash.Bolt11)}");
            return recByHash;
        }

        if (!string.IsNullOrEmpty(bolt11) && _invoicesByBolt11.TryGetValue(bolt11, out var recByBolt))
        {
            DebugLog($"LookupInvoice: hit by bolt11={Shorten(bolt11)} amount_sat={recByBolt.Amount.ToUnit(LightMoneyUnit.Satoshi)}");
            return recByBolt;
        }

        DebugLog($"LookupInvoice: miss for hash={paymentHash} bolt11={Shorten(bolt11)}");
        return null;
    }

    private bool IsKnownPayment(Payment payment)
    {
        var paymentHash = ExtractPaymentHash(payment);
        if (string.IsNullOrEmpty(paymentHash))
            return false;

        return LookupInvoice(null, paymentHash) is not null;
    }

    private LightMoney InferAmountFromPayment(Payment payment)
    {
        var rawAmount = payment.amount;

        if (rawAmount == 0)
        {
            return LightMoney.Zero;
        }

        // Breez SDK surfaces amounts in millisats for lightning payments; fall back to sats otherwise.
        if (rawAmount % 1000 == 0)
        {
            return LightMoney.Satoshis((long)(rawAmount / 1000));
        }

        return LightMoney.Satoshis((long)rawAmount);
    }

    private string? ExtractPaymentHash(Payment payment)
    {
        if (payment?.details is not PaymentDetails.Lightning ln)
            return null;

        if (!string.IsNullOrEmpty(ln.paymentHash))
            return ln.paymentHash;

        if (!string.IsNullOrEmpty(ln.invoice) &&
            BOLT11PaymentRequest.TryParse(ln.invoice, out var pr, _network) &&
            pr.PaymentHash is not null)
        {
            return pr.PaymentHash.ToString();
        }

        return null;
    }

    private LightMoney GetFeeFromPayment(Payment payment)
    {
        return payment.fees % 1000 == 0
            ? LightMoney.Satoshis((long)(payment.fees / 1000))
            : LightMoney.Satoshis((long)payment.fees);
    }

    private bool TryMarkPaymentSeen(Payment payment)
    {
        var paymentHash = ExtractPaymentHash(payment);
        var seenByHash = !string.IsNullOrEmpty(paymentHash) && _seenPaymentHashes.ContainsKey(paymentHash);
        var seenById = _seenCompletedPayments.ContainsKey(payment.id);
        if (seenByHash || seenById)
        {
            DebugLog($"TryMarkPaymentSeen: already seen payment.id={payment.id} hash={paymentHash}");
            return false;
        }

        _seenCompletedPayments.TryAdd(payment.id, true);
        if (!string.IsNullOrEmpty(paymentHash))
        {
            _seenPaymentHashes.TryAdd(paymentHash, true);
        }

        return true;
    }

    public NormalizedPayment NormalizePayment(Payment payment)
    {
        if (payment == null) throw new ArgumentNullException(nameof(payment));

        string paymentHash = null;
        string bolt11 = null;
        string description = null;
        LightMoney? boltAmount = null;
        LightMoney? recordedAmount = null;
        var feeAmount = GetFeeFromPayment(payment);

        if (payment.details is PaymentDetails.Lightning lightningDetails)
        {
            paymentHash = ExtractPaymentHash(payment);
            bolt11 = lightningDetails.invoice;
            description = lightningDetails.description;
            if (!string.IsNullOrEmpty(lightningDetails.invoice) &&
                BOLT11PaymentRequest.TryParse(lightningDetails.invoice, out var pr, _network))
            {
                boltAmount = pr.MinimumAmount;
            }

            var rec = LookupInvoice(lightningDetails.invoice, lightningDetails.paymentHash);
            recordedAmount = rec?.Amount;
        }

        if (string.IsNullOrEmpty(paymentHash))
        {
            DebugLog($"NormalizePayment: missing payment hash for payment.id={payment.id}");
            return null;
        }

        var record = LookupInvoice(null, paymentHash);
        if (record is null || record.PaymentHash != paymentHash)
        {
            DebugLog($"NormalizePayment: unknown payment hash={paymentHash} payment.id={payment.id}");
            return null;
        }

        recordedAmount ??= record.Amount;
        var amount = recordedAmount ?? boltAmount;
        if (boltAmount is not null && recordedAmount is not null && boltAmount != recordedAmount)
        {
            DebugLog($"NormalizePayment: bolt amount {boltAmount.ToUnit(LightMoneyUnit.Satoshi)} != recorded {recordedAmount.ToUnit(LightMoneyUnit.Satoshi)} for hash={paymentHash}");
        }

        if (amount is null)
        {
            // If we can't prove the amount from the BOLT11 or stored record, reject the payment.
            DebugLog($"NormalizePayment: missing amount for hash={paymentHash} bolt11={Shorten(bolt11)}");
            return null;
        }
        var fee = feeAmount;

        return new NormalizedPayment
        {
            Id = paymentHash ?? bolt11 ?? payment.id,
            PaymentType = payment.paymentType,
            Status = payment.status,
            Timestamp = payment.timestamp,
            Amount = amount,
            Fee = fee,
            Description = description ?? bolt11
        };
    }

    public void Dispose()
    {
        _sdk?.Dispose();
    }

    public class BreezInvoiceListener : ILightningInvoiceListener
    {
        private readonly BreezLightningClient _breezLightningClient;
        private readonly CancellationToken _cancellationToken;
        private readonly ConcurrentQueue<Payment> _invoices = new();

        public BreezInvoiceListener(BreezLightningClient breezLightningClient, CancellationToken cancellationToken)
        {
            _breezLightningClient = breezLightningClient;
            _cancellationToken = cancellationToken;
        }

        public void Dispose()
        {
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellation);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                // Check the client's payment notification queue
                if (_breezLightningClient._paymentNotifications.TryDequeue(out var payment))
                {
                    var invoice = _breezLightningClient.FromPayment(payment);
                    if (invoice is not null)
                    {
                        _breezLightningClient.DebugLog($"WaitInvoice: dequeued payment.id={payment.id} hash={invoice.PaymentHash} bolt11={_breezLightningClient.Shorten(invoice.BOLT11)} status={payment.status} raw_msat={payment.amount} fee_msat={payment.fees}");
                        // Force amount to the recorded invoice amount (BOLT11 truth) before handing to BTCPay
                        var rec = _breezLightningClient.LookupInvoice(invoice.BOLT11, invoice.PaymentHash);
                        if (rec is not null)
                        {
                            invoice.Amount = rec.Amount;
                            invoice.AmountReceived = rec.Amount;
                            _breezLightningClient.DebugLog($"WaitInvoice: normalized invoice amount to recorded {rec.Amount.ToUnit(LightMoneyUnit.Satoshi)} sats for hash={invoice.PaymentHash}");
                        }
                        return invoice;
                    }
                }

                        // Also check the local queue for backwards compatibility
                if (_invoices.TryDequeue(out var payment2))
                {
                    var invoice = _breezLightningClient.FromPayment(payment2);
                    if (invoice is not null)
                    {
                        _breezLightningClient.DebugLog($"WaitInvoice(local): dequeued payment.id={payment2.id} hash={invoice.PaymentHash} bolt11={_breezLightningClient.Shorten(invoice.BOLT11)} status={payment2.status} raw_msat={payment2.amount} fee_msat={payment2.fees}");
                        var rec = _breezLightningClient.LookupInvoice(invoice.BOLT11, invoice.PaymentHash);
                        if (rec is not null)
                        {
                            invoice.Amount = rec.Amount;
                            invoice.AmountReceived = rec.Amount;
                            _breezLightningClient.DebugLog($"WaitInvoice: normalized (local queue) invoice amount to recorded {rec.Amount.ToUnit(LightMoneyUnit.Satoshi)} sats for hash={invoice.PaymentHash}");
                        }
                        return invoice;
                    }
                }

                await Task.Delay(1000, linkedCts.Token); // Check every second
            }

            linkedCts.Token.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task MonitorPaymentEvents()
    {
        try
        {
            while (true)
            {
                try
                {
                    // Get all payments and check for new paid ones
                    var payments = await _sdk.ListPayments(new ListPaymentsRequest(
                        typeFilter: new List<PaymentType> { PaymentType.Receive }
                    ));

                    foreach (var payment in payments.payments)
                    {
                        // If payment is complete, add it to the notification queue
                        if (payment.status == PaymentStatus.Completed &&
                            TryMarkPaymentSeen(payment) &&
                            IsKnownPayment(payment))
                        {
                            DebugLogObject("MonitorPaymentEvents:payment", payment);
                            LogCompletedPayment(payment);
                            _paymentNotifications.Enqueue(payment);
                        }
                    }

                    await Task.Delay(5000); // Poll every 5 seconds
                }
                catch (Exception ex)
                {
                    // Log error but continue monitoring
                    Console.WriteLine($"Error monitoring Breez payments: {ex.Message}");
                    await Task.Delay(10000); // Wait longer on error
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Breez payment monitoring stopped: {ex.Message}");
        }
    }

    public void AddPaymentNotification(Payment payment)
    {
        if (TryMarkPaymentSeen(payment) &&
            IsKnownPayment(payment))
        {
            DebugLogObject("AddPaymentNotification:payment", payment);
            LogCompletedPayment(payment);
            _paymentNotifications.Enqueue(payment);
        }
    }

    public async Task<(LightningInvoice Invoice, long FeeSats)> CreateInvoiceWithFee(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
    {
        var description = createInvoiceRequest.Description ?? createInvoiceRequest.DescriptionHash?.ToString() ?? "Invoice";
        var amountSats = (ulong)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        var paymentMethod = new ReceivePaymentMethod.Bolt11Invoice(description, amountSats);
        var response = await _sdk.ReceivePayment(new ReceivePaymentRequest(paymentMethod));
        var feeSats = (long)response.fee;
        var invoice = FromReceivePaymentResponse(response, createInvoiceRequest.Amount);
        return (invoice, feeSats);
    }

    private async Task<LightningInvoice?> GetInvoiceInternal(string identifier, CancellationToken cancellation)
    {
        var payment = await FindPayment(identifier, cancellation);
        if (payment is null)
            return null;

        // Deduplicate completed payments so LightningListener doesn't try to add the same payment twice.
        if (payment.status == PaymentStatus.Completed && !TryMarkPaymentSeen(payment))
        {
            return null;
        }

        return FromPayment(payment);
    }

    private async Task<Payment?> FindPayment(string identifier, CancellationToken cancellation)
    {
        try
        {
            var byId = await _sdk.GetPayment(new GetPaymentRequest(identifier));
            DebugLogObject("FindPayment:GetPayment", byId);
            if (byId?.payment != null && IsKnownPayment(byId.payment))
            {
                return byId.payment;
            }
        }
        catch
        {
            // Ignore and fallback to listing payments
        }

        try
        {
            var list = await _sdk.ListPayments(new ListPaymentsRequest(
                typeFilter: new List<PaymentType> { PaymentType.Receive },
                assetFilter: new AssetFilter.Bitcoin()
            ));
            DebugLogObject("FindPayment:ListPayments", list);

            return list.payments.FirstOrDefault(p =>
            {
                if (p.details is PaymentDetails.Lightning lightning)
                {
                    if (!IsKnownPayment(p))
                        return false;

                    return lightning.paymentHash == identifier ||
                           lightning.invoice == identifier;
                }

                return p.id == identifier;
            });
        }
        catch
        {
            return null;
        }
    }

    private void LogCompletedPayment(Payment payment)
    {
        try
        {
            string paymentHash = ExtractPaymentHash(payment);
            string bolt11 = null;
            LightMoney? boltAmount = null;
            if (payment.details is PaymentDetails.Lightning ln)
            {
                bolt11 = ln.invoice;
                if (!string.IsNullOrEmpty(bolt11) &&
                    BOLT11PaymentRequest.TryParse(bolt11, out var pr, _network))
                {
                    boltAmount = pr.MinimumAmount;
                }
            }

            var record = LookupInvoice(bolt11, paymentHash);
            var recAmount = record?.Amount.ToUnit(LightMoneyUnit.Satoshi);
            var rawAmount = payment.amount;
            var fee = payment.fees;
            var grossSat = InferAmountFromPayment(payment).ToUnit(LightMoneyUnit.Satoshi) +
                           GetFeeFromPayment(payment).ToUnit(LightMoneyUnit.Satoshi);
            var boltSat = boltAmount?.ToUnit(LightMoneyUnit.Satoshi);
        }
        catch
        {
            // best-effort logging
        }
    }

    private string Shorten(string? s, int head = 6, int tail = 6)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        if (s.Length <= head + tail + 3)
            return s;
        return $"{s.Substring(0, head)}...{s.Substring(s.Length - tail)}";
    }
}

public class NormalizedPayment
{
    public string Id { get; set; } = string.Empty;
    public PaymentType PaymentType { get; set; }
    public PaymentStatus Status { get; set; }
    public ulong Timestamp { get; set; }
    public LightMoney Amount { get; set; } = LightMoney.Zero;
    public LightMoney Fee { get; set; } = LightMoney.Zero;
    public string? Description { get; set; }
}
