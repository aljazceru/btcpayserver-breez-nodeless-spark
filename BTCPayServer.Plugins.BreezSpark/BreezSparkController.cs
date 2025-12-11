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

    public BreezSparkController(
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        BTCPayNetworkProvider btcPayNetworkProvider,
        BreezSparkService breezService,
        BTCPayWalletProvider btcWalletProvider, StoreRepository storeRepository)
    {
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _breezService = breezService;
        _btcWalletProvider = btcWalletProvider;
        _storeRepository = storeRepository;
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

            return RedirectToAction(nameof(Payments), new {storeId});
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

            BigInteger? amountSats = null;
            if (amount > 0)
            {
                amountSats = new BigInteger(amount.Value);
            }

            var prepareRequest = new PrepareSendPaymentRequest(
                paymentRequest: address,
                amount: amountSats
            );

            var prepareResponse = await client.Sdk.PrepareSendPayment(prepareRequest);

            if (prepareResponse.paymentMethod is SendPaymentMethod.Bolt11Invoice bolt11Method)
            {
                var totalFee = bolt11Method.lightningFeeSats + (bolt11Method.sparkTransferFeeSats ?? 0);
                var viewModel = new
                {
                    Destination = address,
                    Amount = amountSats ?? 0,
                    Fee = totalFee,
                    PrepareResponseJson = JsonSerializer.Serialize(prepareResponse)
                };
                ViewData["PaymentDetails"] = viewModel;
            }
            else if (prepareResponse.paymentMethod is SendPaymentMethod.BitcoinAddress bitcoinMethod)
            {
                var fees = bitcoinMethod.feeQuote;
                var mediumFee = fees.speedMedium.userFeeSat + fees.speedMedium.l1BroadcastFeeSat;
                var viewModel = new
                {
                    Destination = address,
                    Amount = amountSats ?? 0,
                    Fee = mediumFee,
                    PrepareResponseJson = JsonSerializer.Serialize(prepareResponse)
                };
                ViewData["PaymentDetails"] = viewModel;
            }
            else if (prepareResponse.paymentMethod is SendPaymentMethod.SparkAddress sparkMethod)
            {
                var viewModel = new
                {
                    Destination = address,
                    Amount = amountSats ?? 0,
                    Fee = sparkMethod.fee,
                    PrepareResponseJson = JsonSerializer.Serialize(prepareResponse)
                };
                ViewData["PaymentDetails"] = viewModel;
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
    public async Task<IActionResult> ConfirmSend(string storeId, string paymentRequest, long amount, string prepareResponseJson)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        try
        {
            var prepareResponse = JsonSerializer.Deserialize<PrepareSendPaymentResponse>(prepareResponseJson);
            if (prepareResponse == null)
            {
                throw new InvalidOperationException("Invalid payment preparation data");
            }

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
                _ => throw new NotSupportedException("Unsupported payment method")
            };

            var sendRequest = new SendPaymentRequest(
                prepareResponse: prepareResponse,
                options: options
            );

            var sendResponse = await client.Sdk.SendPayment(sendRequest);

            TempData[WellKnownTempData.SuccessMessage] = "Payment sent successfully!";
            return RedirectToAction(nameof(Payments), new {storeId});
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Error sending payment: {ex.Message}";
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

    [Route("payments")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Payments(string storeId, PaymentsViewModel viewModel)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        viewModel ??= new PaymentsViewModel();
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
        viewModel.Payments = response.payments.Select(client.NormalizePayment).ToList();

        return View(viewModel);
    }
}

public class PaymentsViewModel : BasePagingViewModel
{
    public List<NormalizedPayment> Payments { get; set; } = new();
    public override int CurrentPageCount => Payments.Count;
}

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
