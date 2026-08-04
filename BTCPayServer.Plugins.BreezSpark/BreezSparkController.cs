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
    public Task<IActionResult> Index(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        return Task.FromResult<IActionResult>(RedirectToAction(client is null ? nameof(Configure) : nameof(Info), new {storeId}));
    }

    [HttpGet("swapin")]
    [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public Task<IActionResult> SwapIn(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Configure), new {storeId}));
        }

        return Task.FromResult<IActionResult>(View((object) storeId));
    }

    [HttpGet("info")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public Task<IActionResult> Info(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Configure), new {storeId}));
        }

        return Task.FromResult<IActionResult>(View((object) storeId));
    }
    [HttpGet("logs")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public Task<IActionResult> Logs(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Configure), new {storeId}));
        }

        return Task.FromResult<IActionResult>(View(client.Events));
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
                TempData[WellKnownTempData.SuccessMessage] = $"Found {response.deposits.Length} unclaimed deposits";
            }
            else
            {
                TempData[WellKnownTempData.SuccessMessage] = "No pending deposits to claim";
            }
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = FriendlyError("Claiming deposits", e);
        }

        return View((object) storeId);
    }

    [HttpGet("send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public Task<IActionResult> Send(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Configure), new {storeId}));
        }

        return Task.FromResult<IActionResult>(View((object) storeId));
    }   
    [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [HttpGet("receive")]
    [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public Task<IActionResult> Receive(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Configure), new {storeId}));
        }

        return Task.FromResult<IActionResult>(View((object) storeId));
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
                amountSats: amount != null ? (ulong)amount.Value : null,
                expirySecs: 3600,  // 1 hour default expiry
                paymentHash: null
            );

            var request = new ReceivePaymentRequest(paymentMethod: paymentMethod);
            var response = await client.Sdk.ReceivePayment(request: request);

            TempData["bolt11"] = response.paymentRequest;
            TempData[WellKnownTempData.SuccessMessage] = "Invoice created successfully!";

            return RedirectToAction(nameof(Transactions), new {storeId});
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = FriendlyError("Creating invoice", ex);
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
                paymentRequest: new Breez.Sdk.Spark.PaymentRequest.Input(input: address),
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
            TempData[WellKnownTempData.ErrorMessage] = FriendlyError("Preparing payment", ex);
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
                paymentRequest: new Breez.Sdk.Spark.PaymentRequest.Input(input: paymentRequest),
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
            TempData[WellKnownTempData.ErrorMessage] = FriendlyError("Sending payment", ex);
            _logger.LogError(ex, "BreezSpark send failed for store {StoreId}", storeId);
            return RedirectToAction(nameof(Send), new {storeId});
        }
    }


    [HttpGet("swapout")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public Task<IActionResult> SwapOut(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Configure), new {storeId}));
        }

        return Task.FromResult<IActionResult>(View((object) storeId));
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
                paymentRequest: new Breez.Sdk.Spark.PaymentRequest.Input(input: address),
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
                TempData[WellKnownTempData.ErrorMessage] = "This address could not be processed as an on-chain payment. Verify it is a valid Bitcoin address.";
            }
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = FriendlyError("Processing swap-out", ex);
        }

        return RedirectToAction(nameof(SwapOut), new {storeId});
    }

    [HttpGet("swapin/{address}/refund")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public Task<IActionResult> SwapInRefund(string storeId, string address)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Configure), new {storeId}));
        }

        return Task.FromResult<IActionResult>(View((object) storeId));
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
            TempData[WellKnownTempData.ErrorMessage] = FriendlyError("Processing refund", e);
        }

        return RedirectToAction(nameof(SwapIn), new {storeId});
    }

    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [HttpGet("configure")]
    public async Task<IActionResult> Configure(string storeId)
    {
        return View(await _breezService.Get(storeId) ?? new BreezSparkSettings());
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
                    settings.Mnemonic = string.Empty;
                    return View(settings);
                }

                // Normalize whitespace: trim and collapse multiple spaces
                settings.Mnemonic = System.Text.RegularExpressions.Regex.Replace(settings.Mnemonic.Trim(), @"\s+", " ");

                var words = settings.Mnemonic.Split(' ');
                if (words.Length != 12 && words.Length != 24)
                {
                    ModelState.AddModelError(nameof(settings.Mnemonic),
                        $"Mnemonic must be 12 or 24 words (you entered {words.Length})");
                    settings.Mnemonic = string.Empty;
                    return View(settings);
                }

                try
                {
                    new Mnemonic(settings.Mnemonic);
                }
                catch (Exception)
                {
                    // Try to identify the specific invalid word
                    var wordlist = NBitcoin.Wordlist.English;
                    var invalidWord = words.FirstOrDefault(w => !wordlist.WordExists(w, out _));
                    if (invalidWord != null)
                    {
                        ModelState.AddModelError(nameof(settings.Mnemonic),
                            $"Word '{invalidWord}' is not a valid BIP39 word");
                    }
                    else
                    {
                        ModelState.AddModelError(nameof(settings.Mnemonic),
                            "Invalid mnemonic: the word combination is not valid (bad checksum)");
                    }
                    settings.Mnemonic = string.Empty;
                    return View(settings);
                }

                await _breezService.Set(storeId, settings);
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = FriendlyError("Applying settings", e);
                settings.Mnemonic = string.Empty;
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

            // Use minimum of 1000 sats or 10% of test amount as floor for retry logic
            var minAmount = Math.Max(1000, testAmount.Value / 10);

            // Use retry logic to handle insufficient funds errors
            var result = await _breezService.ExecuteTreasurySweepWithRetry(
                storeId, client, settings, testAmount.Value, currentBalanceSats, minAmount);

            if (result.Success)
            {
                var actualAmount = result.AmountSats > 0 ? result.AmountSats : testAmount.Value;
                TempData[WellKnownTempData.SuccessMessage] = $"Test sweep successful! Sent {actualAmount} sats, fee: {result.FeeSats} sats";
            }
            else
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Test sweep failed: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = FriendlyError("Test sweep", ex);
            _logger.LogError(ex, "Treasury test sweep failed for store {StoreId}", storeId);
        }

        return RedirectToAction(nameof(Treasury), new {storeId});
    }

    [HttpPost("treasury/preview-addresses")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public Task<IActionResult> TreasuryPreviewAddresses(string storeId, string? xpub, uint? startIndex, int? count)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return Task.FromResult<IActionResult>(Json(new { success = false, error = "Breez client not configured" }));
        }

        if (string.IsNullOrWhiteSpace(xpub))
        {
            return Task.FromResult<IActionResult>(Json(new { success = false, error = "Extended public key is required" }));
        }

        if (!TreasuryHelper.ValidateXpub(xpub, NBitcoin.Network.Main))
        {
            return Task.FromResult<IActionResult>(Json(new { success = false, error = "Invalid extended public key" }));
        }

        try
        {
            var start = startIndex ?? 0;
            var addressCount = Math.Max(1, Math.Min(count ?? 5, 20)); // Clamp to [1, 20] addresses

            var addresses = TreasuryHelper.PreviewAddresses(
                xpub,
                start,
                addressCount,
                NBitcoin.Network.Main,
                "0/{index}"); // Hardcoded standard receiving path

            return Task.FromResult<IActionResult>(Json(new
            {
                success = true,
                addresses = addresses.Select(a => new
                {
                    index = a.Index,
                    address = a.Address,
                    path = $"0/{a.Index}"
                })
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating preview addresses for store {StoreId}", storeId);
            return Task.FromResult<IActionResult>(Json(new { success = false, error = FriendlyError("Generating addresses", ex) }));
        }
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
            paymentDetailsFilter: null,
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
            string description = "";
            if (p.details is PaymentDetails.Lightning l && !string.IsNullOrEmpty(l.invoice))
            {
                var nbitcoinNetwork = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC")?.NBitcoinNetwork ?? NBitcoin.Network.Main;
                if (BOLT11PaymentRequest.TryParse(l.invoice, out var pr, nbitcoinNetwork) && pr is not null)
                {
                    if (pr.MinimumAmount is not null)
                    {
                        amountSat = (long)pr.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
                    }
                    description = pr.ShortDescription ?? l.description ?? "";
                }
                else
                {
                    description = l.description ?? "";
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
                Description = description
            });
        }
        viewModel.Payments = normalized;

        return View("Transactions", viewModel);
    }

    private string FriendlyError(string context, Exception ex)
    {
        _logger.LogError(ex, "BreezSpark error during: {Context}", context);

        var msg = ex.Message ?? string.Empty;

        if (msg.Contains("Invalid certificate", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Breez API key", StringComparison.OrdinalIgnoreCase))
            return "Invalid or expired Breez API key. Check your key or leave blank for the default.";

        if (msg.Contains("insufficient funds", StringComparison.OrdinalIgnoreCase))
            return "Insufficient funds for this operation.";

        if (msg.Contains("invoice expired", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("invoice is expired", StringComparison.OrdinalIgnoreCase))
            return "The invoice has expired. Please create a new one.";

        if (msg.Contains("payment timeout", StringComparison.OrdinalIgnoreCase))
            return "Payment timed out. The recipient may be offline.";

        if (msg.Contains("route not found", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("no route", StringComparison.OrdinalIgnoreCase))
            return "No payment route found. The recipient may not have enough inbound capacity.";

        if (msg.Contains("amount too low", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("below minimum", StringComparison.OrdinalIgnoreCase))
            return "Amount is too low for this payment type.";

        if (msg.Contains("amount too high", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("above maximum", StringComparison.OrdinalIgnoreCase))
            return "Amount exceeds the maximum for this payment type.";

        return $"{context} failed. Please try again or check the logs for details.";
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
