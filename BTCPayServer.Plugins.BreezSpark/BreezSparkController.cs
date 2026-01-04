using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Breez.Sdk.Spark;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Models;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.BreezSpark;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/BreezSpark")]
public class BreezSparkController : Controller
{
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private readonly BreezSparkService _breezService;
    private readonly BTCPayWalletProvider _btcWalletProvider;
    private readonly StoreRepository _storeRepository;
    private readonly ILogger<BreezSparkController> _logger;

    public BreezSparkController(
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        BTCPayNetworkProvider btcPayNetworkProvider,
        BreezSparkService breezService,
        BTCPayWalletProvider btcWalletProvider,
        StoreRepository storeRepository,
        ILogger<BreezSparkController> logger)
    {
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _breezService = breezService;
        _btcWalletProvider = btcWalletProvider;
        _storeRepository = storeRepository;
        _logger = logger;
    }


    [HttpGet("")]
    public async Task<IActionResult> Index(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        return RedirectToAction(client is null ? nameof(Configure) : nameof(Info), new {storeId});
    }

    [HttpGet("swapin")]
    [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SwapIn(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }

    [HttpGet("info")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Info(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }
    [HttpGet("logs")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Logs(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View( client.Events);
    }

    [HttpPost("sweep")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Sweep(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        try
        {
            // In Spark SDK v0.4.1, check for any unclaimed deposits
            var request = new ListUnclaimedDepositsRequest();
            var response = await client.Sdk.ListUnclaimedDeposits(request);

            if (response.deposits.Any())
            {
                TempData[WellKnownTempData.SuccessMessage] = $"Found {response.deposits.Count} unclaimed deposits";
            }
            else
            {
                TempData[WellKnownTempData.SuccessMessage] = "No pending deposits to claim";
            }
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"error claiming deposits: {e.Message}";
        }

        return View((object) storeId);
    }

    [HttpGet("send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }   
    [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [HttpGet("receive")]
    [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Receive(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }

    [HttpPost("receive")]
    [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Receive(string storeId, long? amount, string description)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        try
        {
            description ??= "BTCPay Server Invoice";

            var paymentMethod = new ReceivePaymentMethod.Bolt11Invoice(
                description: description,
                amountSats: amount != null ? (ulong)amount.Value : null
            );

            var request = new ReceivePaymentRequest(paymentMethod: paymentMethod);
            var response = await client.Sdk.ReceivePayment(request: request);

            TempData["bolt11"] = response.paymentRequest;
            TempData[WellKnownTempData.SuccessMessage] = "Invoice created successfully!";

            return RedirectToAction(nameof(Transactions), new {storeId});
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Error creating invoice: {ex.Message}";
            return View((object) storeId);
        }
    }

    [HttpPost("prepare-send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> PrepareSend(string storeId, string address, long? amount)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        try
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                TempData[WellKnownTempData.ErrorMessage] = "Payment destination is required";
                return RedirectToAction(nameof(Send), new {storeId});
            }

            var amountSats = ResolveAmountSats(address, amount);

            var prepareRequest = new PrepareSendPaymentRequest(
                paymentRequest: address,
                amount: amountSats
            );

            var prepareResponse = await client.Sdk.PrepareSendPayment(prepareRequest);

            if (prepareResponse.paymentMethod is SendPaymentMethod.Bolt11Invoice bolt11Method)
            {
                var totalFee = bolt11Method.lightningFeeSats + (bolt11Method.sparkTransferFeeSats ?? 0);
                var amt = amountSats ?? BigInteger.Zero;
                ViewData["PaymentDetails"] = new PaymentDetailsDto(
                    Destination: address,
                    Amount: (long)amt,
                    Fee: (long)totalFee
                );
            }
            else if (prepareResponse.paymentMethod is SendPaymentMethod.BitcoinAddress bitcoinMethod)
            {
                var fees = bitcoinMethod.feeQuote;
                var mediumFee = fees.speedMedium.userFeeSat + fees.speedMedium.l1BroadcastFeeSat;
                ViewData["PaymentDetails"] = new PaymentDetailsDto(
                    Destination: address,
                    Amount: (long)BigInteger.Abs(amountSats ?? BigInteger.Zero),
                    Fee: (long)mediumFee
                );
            }
            else if (prepareResponse.paymentMethod is SendPaymentMethod.SparkAddress sparkMethod)
            {
                ViewData["PaymentDetails"] = new PaymentDetailsDto(
                    Destination: address,
                    Amount: (long)BigInteger.Abs(amountSats ?? BigInteger.Zero),
                    Fee: (long)sparkMethod.fee
                );
            }
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Error preparing payment: {ex.Message}";
        }

        return View(nameof(Send), storeId);
    }

    [HttpPost("confirm-send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ConfirmSend(string storeId, string paymentRequest, long amount)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        try
        {
            // Re-run preparation to avoid polymorphic JSON deserialization issues
            var amountSats = ResolveAmountSats(paymentRequest, amount);
            var prepareResponse = await client.Sdk.PrepareSendPayment(new PrepareSendPaymentRequest(
                paymentRequest: paymentRequest,
                amount: amountSats
            ));

            SendPaymentOptions? options = prepareResponse.paymentMethod switch
            {
                SendPaymentMethod.Bolt11Invoice => new SendPaymentOptions.Bolt11Invoice(
                    preferSpark: false,
                    completionTimeoutSecs: 60
                ),
                SendPaymentMethod.BitcoinAddress => new SendPaymentOptions.BitcoinAddress(
                    confirmationSpeed: OnchainConfirmationSpeed.Medium
                ),
                SendPaymentMethod.SparkAddress => null,
                SendPaymentMethod.SparkInvoice => null,
                _ => null
            };

            var sendRequest = new SendPaymentRequest(
                prepareResponse: prepareResponse,
                options: options
            );

            _logger.LogInformation("BreezSpark sending payment for store {StoreId} to {Destination}", storeId, paymentRequest);
            var sendResponse = await client.Sdk.SendPayment(sendRequest);
            _logger.LogInformation("BreezSpark send complete for store {StoreId}: payment id {PaymentId}, status {Status}",
                storeId, sendResponse.payment?.id, sendResponse.payment?.status);

            TempData[WellKnownTempData.SuccessMessage] = "Payment sent successfully!";
            return RedirectToAction(nameof(Transactions), new {storeId});
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Error sending payment: {ex.Message}";
            _logger.LogError(ex, "BreezSpark send failed for store {StoreId}", storeId);
            return RedirectToAction(nameof(Send), new {storeId});
        }
    }


    [HttpGet("swapout")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SwapOut(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }

    [HttpPost("swapout")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SwapOut(string storeId, string address, ulong amount, uint satPerByte,
        string feesHash)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        try
        {
            // Use current SDK pattern for onchain payments
            var prepareRequest = new PrepareSendPaymentRequest(
                paymentRequest: address,
                amount: new BigInteger(amount)
            );

            var prepareResponse = await client.Sdk.PrepareSendPayment(prepareRequest);

            if (prepareResponse.paymentMethod is SendPaymentMethod.BitcoinAddress bitcoinMethod)
            {
                var options = new SendPaymentOptions.BitcoinAddress(
                    confirmationSpeed: OnchainConfirmationSpeed.Medium
                );

                var sendRequest = new SendPaymentRequest(
                    prepareResponse: prepareResponse,
                    options: options
                );

                var sendResponse = await client.Sdk.SendPayment(sendRequest);

                TempData[WellKnownTempData.SuccessMessage] = "Onchain payment initiated successfully!";
            }
            else
            {
                TempData[WellKnownTempData.ErrorMessage] = "Invalid payment method for onchain swap";
            }
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Error processing swap-out: {ex.Message}";
        }

        return RedirectToAction(nameof(SwapOut), new {storeId});
    }

    [HttpGet("swapin/{address}/refund")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SwapInRefund(string storeId, string address)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }

    [HttpPost("swapin/{address}/refund")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SwapInRefund(string storeId, string txid, uint vout, string refundAddress, uint? satPerByte = null)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        try
        {
            // Parse the txid:vout format from depositId if needed
            var fee = new Fee.Rate((ulong)(satPerByte ?? 5m));
            var request = new RefundDepositRequest(
                txid: txid,
                vout: vout,
                destinationAddress: refundAddress,
                fee: fee
            );

            var resp = await client.Sdk.RefundDeposit(request);
            TempData[WellKnownTempData.SuccessMessage] = $"Refund successful: {resp.txId}";
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Couldnt refund: {e.Message}";
        }

        return RedirectToAction(nameof(SwapIn), new {storeId});
    }

    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [HttpGet("configure")]
    public async Task<IActionResult> Configure(string storeId)
    {
        return View(await _breezService.Get(storeId));
    }
    [HttpPost("configure")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Configure(string storeId, string command, BreezSparkSettings settings)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
        {
            return NotFound();
        }
        var pmi = new PaymentMethodId("BTC-LN");
        // In v2.2.1, payment methods are handled differently
        // TODO: Implement proper v2.2.1 payment method handling
        if (command == "clear")
        {
            await _breezService.Set(storeId, null);
            TempData[WellKnownTempData.SuccessMessage] = "Settings cleared successfully";
            var client = _breezService.GetClient(storeId);
            // In v2.2.1, payment methods are handled differently
            // TODO: Implement proper v2.2.1 payment method handling
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        if (command == "save")
        {
            try
            {
                if (string.IsNullOrEmpty(settings.Mnemonic))
                {
                    ModelState.AddModelError(nameof(settings.Mnemonic), "Mnemonic is required");
                    return View(settings);
                }

                try
                {
                    new Mnemonic(settings.Mnemonic);
                }
                catch (Exception)
                {
                    ModelState.AddModelError(nameof(settings.Mnemonic), "Invalid mnemonic");
                    return View(settings);
                }

                await _breezService.Set(storeId, settings);
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Couldnt use provided settings: {e.Message}";
                return View(settings);
            }

            // In v2.2.1, payment methods are handled differently
            // TODO: Implement proper v2.2.1 payment method handling
            // This will require a complete rewrite of the payment method system

            TempData[WellKnownTempData.SuccessMessage] = "Settings saved successfully";
            return RedirectToAction(nameof(Info), new {storeId});
        }

        return NotFound();
    }

    // Treasury Management Endpoints

    [HttpGet("treasury")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Treasury(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        var settings = await _breezService.GetTreasurySettings(storeId) ?? new TreasurySettings();
        var viewModel = new TreasuryViewModel
        {
            StoreId = storeId,
            Settings = settings
        };

        // Get current balance for display
        try
        {
            var nodeInfo = await client.Sdk.GetInfo(new GetInfoRequest(ensureSynced: false));
            viewModel.CurrentBalanceSats = (long)nodeInfo.balanceSats;
        }
        catch
        {
            viewModel.CurrentBalanceSats = 0;
        }

        return View(viewModel);
    }

    [HttpPost("treasury")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Treasury(string storeId, TreasuryViewModel model)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        var settings = model.Settings;

        // Validation
        if (settings.Enabled)
        {
            if (settings.BalanceThresholdSats <= 0)
            {
                ModelState.AddModelError("Settings.BalanceThresholdSats", "Balance threshold must be greater than 0");
            }

            if (settings.BalanceThresholdSats <= settings.ReserveAmountSats)
            {
                ModelState.AddModelError("Settings.BalanceThresholdSats", "Balance threshold must be greater than reserve amount");
            }

            if (settings.MinSweepAmountSats <= 0)
            {
                ModelState.AddModelError("Settings.MinSweepAmountSats", "Minimum sweep amount must be greater than 0");
            }

            if (settings.CheckIntervalMinutes <= 0)
            {
                ModelState.AddModelError("Settings.CheckIntervalMinutes", "Check interval must be greater than 0");
            }

            // Mode-specific validation
            if (settings.Mode == TreasuryMode.OnChain)
            {
                if (settings.OnChainAddressType == OnChainAddressType.SingleAddress)
                {
                    if (!TreasuryHelper.ValidateBitcoinAddress(settings.OnChainAddress, NBitcoin.Network.Main))
                    {
                        ModelState.AddModelError("Settings.OnChainAddress", "Invalid Bitcoin address");
                    }
                }
                else if (settings.OnChainAddressType == OnChainAddressType.Xpub)
                {
                    if (!TreasuryHelper.ValidateXpub(settings.Xpub ?? "", NBitcoin.Network.Main))
                    {
                        ModelState.AddModelError("Settings.Xpub", "Invalid extended public key");
                    }
                }
            }
            else if (settings.Mode == TreasuryMode.Lightning)
            {
                if (!TreasuryHelper.ValidateLightningAddress(settings.LightningAddress))
                {
                    ModelState.AddModelError("Settings.LightningAddress", "Invalid lightning address format (must be user@domain.com)");
                }
            }
        }

        if (!ModelState.IsValid)
        {
            model.StoreId = storeId;
            try
            {
                var nodeInfo = await client.Sdk.GetInfo(new GetInfoRequest(ensureSynced: false));
                model.CurrentBalanceSats = (long)nodeInfo.balanceSats;
            }
            catch
            {
                model.CurrentBalanceSats = 0;
            }
            return View(model);
        }

        await _breezService.SetTreasurySettings(storeId, settings);
        TempData[WellKnownTempData.SuccessMessage] = "Treasury settings saved successfully";
        return RedirectToAction(nameof(Treasury), new {storeId});
    }

    [HttpGet("treasury/history")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> TreasuryHistory(string storeId, int skip = 0, int count = 20)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        var history = await _breezService.GetTreasuryHistory(storeId);
        var viewModel = new TreasuryHistoryViewModel
        {
            StoreId = storeId,
            Sweeps = history.Sweeps.Skip(skip).Take(count).ToList(),
            Skip = skip,
            Count = count,
            Total = history.Sweeps.Count
        };

        return View(viewModel);
    }

    [HttpPost("treasury/test")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> TreasuryTest(string storeId, long? testAmount)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        var settings = await _breezService.GetTreasurySettings(storeId);
        if (settings is null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Treasury settings not configured";
            return RedirectToAction(nameof(Treasury), new {storeId});
        }

        if (!testAmount.HasValue || testAmount.Value <= 0)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Please specify a valid test amount";
            return RedirectToAction(nameof(Treasury), new {storeId});
        }

        try
        {
            var nodeInfo = await client.Sdk.GetInfo(new GetInfoRequest(ensureSynced: false));
            var currentBalanceSats = (long)nodeInfo.balanceSats;

            if (testAmount.Value > currentBalanceSats)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Test amount ({testAmount.Value} sats) exceeds current balance ({currentBalanceSats} sats)";
                return RedirectToAction(nameof(Treasury), new {storeId});
            }

            var result = await _breezService.ExecuteTreasurySweep(storeId, client, settings, testAmount.Value, currentBalanceSats);

            if (result.Success)
            {
                TempData[WellKnownTempData.SuccessMessage] = $"Test sweep successful! Sent {testAmount.Value} sats, fee: {result.FeeSats} sats";
            }
            else
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Test sweep failed: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Test sweep error: {ex.Message}";
            _logger.LogError(ex, "Treasury test sweep failed for store {StoreId}", storeId);
        }

        return RedirectToAction(nameof(Treasury), new {storeId});
    }

    [Route("transactions")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Transactions(string storeId, PaymentsViewModel viewModel)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        viewModel ??= new PaymentsViewModel();
        viewModel.Balance = await client.GetBalance();
        var req = new ListPaymentsRequest(
            typeFilter: null,
            statusFilter: null,
            assetFilter: new AssetFilter.Bitcoin(),
            fromTimestamp: null,
            toTimestamp: null,
            offset: viewModel.Skip > 0 ? (uint?)viewModel.Skip : null,
            limit: viewModel.Count > 0 ? (uint?)viewModel.Count : null,
            sortAscending: false
        );
        var response = await client.Sdk.ListPayments(req);
        var normalized = new List<NormalizedPayment>();
        foreach (var p in response.payments.Where(p => p != null))
        {
            var norm = client.NormalizePayment(p);
            if (norm is not null)
            {
                normalized.Add(norm);
                continue;
            }

            // Fallback: show raw SDK payment even if we lack invoice context
            long amountSat = 0;
            if (p.details is PaymentDetails.Lightning l && !string.IsNullOrEmpty(l.invoice))
            {
                var nbitcoinNetwork = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC")?.NBitcoinNetwork ?? NBitcoin.Network.Main;
                if (BOLT11PaymentRequest.TryParse(l.invoice, out var pr, nbitcoinNetwork) && pr?.MinimumAmount is not null)
                {
                    amountSat = (long)pr.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
                }
            }

            long feeSat = 0;
            if (p.fees != null)
            {
                feeSat = (long)(p.fees / 1000);
            }
            normalized.Add(new NormalizedPayment
            {
                Id = p.id ?? Guid.NewGuid().ToString("N"),
                PaymentType = p.paymentType,
                Status = p.status,
                Timestamp = p.timestamp,
                Amount = LightMoney.Satoshis(amountSat),
                Fee = LightMoney.Satoshis(feeSat),
                Description = p.details?.ToString() ?? "BreezSpark payment"
            });
        }
        viewModel.Payments = normalized;

        return View("Transactions", viewModel);
    }

    private BigInteger? ResolveAmountSats(string paymentRequest, long? amount)
    {
        if (amount.HasValue && amount.Value > 0)
        {
            return new BigInteger(amount.Value);
        }

        // Try to derive amount from bolt11 invoice if present
        var nbitcoinNetwork = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC")?.NBitcoinNetwork ?? NBitcoin.Network.Main;
        if (BOLT11PaymentRequest.TryParse(paymentRequest, out var pr, nbitcoinNetwork) && pr?.MinimumAmount is not null)
        {
            return new BigInteger((long)pr.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi));
        }

        return null;
    }
}

public class PaymentsViewModel : BasePagingViewModel
{
    public List<NormalizedPayment> Payments { get; set; } = new();
    public LightningNodeBalance? Balance { get; set; }
    public override int CurrentPageCount => Payments.Count;
}

public record PaymentDetailsDto(string Destination, long Amount, long Fee);

// Helper class for swap information display in views
public class SwapInfo
{
    public string? bitcoinAddress { get; set; }
    public ulong minAllowedDeposit { get; set; }
    public ulong maxAllowedDeposit { get; set; }
    public string? status { get; set; }
}

// Helper class for swap limits display in views
public class SwapLimits
{
    public ulong min { get; set; }
    public ulong max { get; set; }
}

// Treasury Management ViewModels
public class TreasuryViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public TreasurySettings Settings { get; set; } = new();
    public long CurrentBalanceSats { get; set; }
}

public class TreasuryHistoryViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public List<TreasurySweepRecord> Sweeps { get; set; } = new();
    public int Skip { get; set; }
    public int Count { get; set; }
    public int Total { get; set; }
}
