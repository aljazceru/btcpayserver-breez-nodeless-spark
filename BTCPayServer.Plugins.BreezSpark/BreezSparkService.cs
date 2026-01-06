#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Breez.Sdk.Spark;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.BreezSpark;

public class BreezSparkService:EventHostedServiceBase
{
    private readonly StoreRepository _storeRepository;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary => _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
    private readonly ILogger _logger;
    private Dictionary<string, BreezSparkSettings> _settings = new();
    private Dictionary<string, BreezSparkLightningClient> _clients = new();

    // Treasury management
    private readonly ConcurrentDictionary<string, TreasurySettings> _treasurySettings = new();
    private readonly ConcurrentDictionary<string, TreasuryHistory> _treasuryHistory = new();
    private CancellationTokenSource? _treasuryCts;
    private Task? _treasuryLoopTask;
    private readonly SemaphoreSlim _treasurySweepLock = new(1, 1);

    public BreezSparkService(
        EventAggregator eventAggregator,
        StoreRepository storeRepository,
        IOptions<DataDirectories> dataDirectories,
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<BreezSparkService> logger) : base(eventAggregator, logger)
    {
        _storeRepository = storeRepository;
        _dataDirectories = dataDirectories;
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override void SubscribeToEvents()
    {
        base.SubscribeToEvents();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        await base.ProcessEvent(evt, cancellationToken);
    }

    public  string GetWorkDir(string storeId)
    {
        ArgumentNullException.ThrowIfNull(storeId);
        var dir =  _dataDirectories.Value.DataDir;
        return Path.Combine(dir, "Plugins", "BreezSpark",storeId);
    }

    TaskCompletionSource tcs = new();
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _settings = (await _storeRepository.GetSettingsAsync<BreezSparkSettings>("BreezSpark")).Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!);
        foreach (var keyValuePair in _settings)
        {
            try
            {
                _logger.LogInformation("Initializing BreezSpark client for store {StoreId}", keyValuePair.Key);
                await Handle(keyValuePair.Key, keyValuePair.Value);
                _logger.LogInformation("Successfully initialized BreezSpark client for store {StoreId}", keyValuePair.Key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize BreezSpark client for store {StoreId}: {Message}",
                    keyValuePair.Key, ex.Message);
            }
        }

        // Load treasury settings into concurrent dictionaries
        var treasurySettingsData = await _storeRepository.GetSettingsAsync<TreasurySettings>("BreezSparkTreasury");
        foreach (var kvp in treasurySettingsData.Where(pair => pair.Value is not null))
        {
            _treasurySettings[kvp.Key] = kvp.Value!;
        }

        var treasuryHistoryData = await _storeRepository.GetSettingsAsync<TreasuryHistory>("BreezSparkTreasuryHistory");
        foreach (var kvp in treasuryHistoryData.Where(pair => pair.Value is not null))
        {
            _treasuryHistory[kvp.Key] = kvp.Value!;
        }

        // Start treasury check loop with PeriodicTimer (runs every minute, checks individual store intervals)
        _treasuryCts = new CancellationTokenSource();
        _treasuryLoopTask = TreasuryCheckLoopAsync(_treasuryCts.Token);

        tcs.TrySetResult();
        await base.StartAsync(cancellationToken);
    }

    public async Task<BreezSparkSettings?> Get(string storeId)
    {
        await tcs.Task;
        _settings.TryGetValue(storeId, out var settings);
        
        return settings;
    }

    public async Task<BreezSparkLightningClient?> Handle(string? storeId, BreezSparkSettings? settings)
    {
        if (string.IsNullOrEmpty(storeId))
        {
            return null;
        }
        if (settings is null)
        {
            if (storeId is not null && _clients.Remove(storeId, out var client))
            {
                client.Dispose();
            }
        }
        else
        {
            try
            {
                var network = NBitcoin.Network.Main;
                var dir = GetWorkDir(storeId);
                Directory.CreateDirectory(dir);
                settings.PaymentKey ??= Guid.NewGuid().ToString();

                var client = await BreezSparkLightningClient.Create(
                    settings.ApiKey ?? string.Empty,
                    dir,
                    network,
                    new Mnemonic(settings.Mnemonic),
                    settings.PaymentKey
                );

                if (storeId is not null)
                {
                    _clients.AddOrReplace(storeId, client);
                }

                return client;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not create BreezSpark client");
                throw;
            }
        }

        return null;
    }

    public async Task Set(string storeId, BreezSparkSettings? settings)
    {
            
        var result = await Handle(storeId, settings);
        await _storeRepository.UpdateSetting(storeId, "BreezSpark", settings!);
        if (settings is null)
        {
            _settings.Remove(storeId, out var oldSettings );
            var data = await _storeRepository.FindStore(storeId);
            if (data != null)
            {
                var pmi = new PaymentMethodId("BTC-LN");
                // In v2.2.1, the payment methods are handled differently
                // We'll skip this for now as it needs to be refactored completely
                // TODO: Implement proper v2.2.1 payment method handling
            }
            Directory.Delete(GetWorkDir(storeId), true);

        }
        else if(result is not null )
        {
            _settings.AddOrReplace(storeId, settings);
        }


    }

    public BreezSparkLightningClient? GetClient(string? storeId)
    {
        
        tcs.Task.GetAwaiter().GetResult();
        if(storeId is null)
            return null;
        _clients.TryGetValue(storeId, out var client);
        return client;
    }  
    public BreezSparkLightningClient? GetClientByPaymentKey(string? paymentKey)
    {
        tcs.Task.GetAwaiter().GetResult();
        if(paymentKey is null)
        {
            _logger.LogWarning("GetClientByPaymentKey called with null paymentKey");
            return null;
        }
        var match = _settings.FirstOrDefault(pair => pair.Value.PaymentKey == paymentKey).Key;
        if (match is null)
        {
            _logger.LogWarning("No settings found for paymentKey {PaymentKey}. Available keys: [{Keys}]",
                paymentKey, string.Join(", ", _settings.Values.Select(s => s.PaymentKey ?? "null")));
            return null;
        }
        var client = GetClient(match);
        if (client is null)
        {
            _logger.LogWarning("Client not found for store {StoreId} with paymentKey {PaymentKey}. Available client stores: [{Stores}]",
                match, paymentKey, string.Join(", ", _clients.Keys));
        }
        return client;
    }

    // Treasury Management Methods

    public async Task<TreasurySettings?> GetTreasurySettings(string storeId)
    {
        await tcs.Task;
        _treasurySettings.TryGetValue(storeId, out var settings);
        return settings;
    }

    public async Task SetTreasurySettings(string storeId, TreasurySettings? settings)
    {
        await tcs.Task;
        await _storeRepository.UpdateSetting(storeId, "BreezSparkTreasury", settings!);
        if (settings is null)
        {
            _treasurySettings.TryRemove(storeId, out _);
        }
        else
        {
            _treasurySettings[storeId] = settings;
        }
    }

    public async Task<TreasuryHistory> GetTreasuryHistory(string storeId)
    {
        await tcs.Task;
        if (_treasuryHistory.TryGetValue(storeId, out var history))
        {
            return history;
        }
        return new TreasuryHistory();
    }

    public async Task AddTreasurySweepRecord(string storeId, TreasurySweepRecord record)
    {
        await tcs.Task;
        if (!_treasuryHistory.TryGetValue(storeId, out var history))
        {
            history = new TreasuryHistory();
            _treasuryHistory[storeId] = history;
        }

        // Insert at beginning (newest first)
        history.Sweeps.Insert(0, record);

        // Keep only last 100 records
        if (history.Sweeps.Count > 100)
        {
            history.Sweeps = history.Sweeps.Take(100).ToList();
        }

        await _storeRepository.UpdateSetting(storeId, "BreezSparkTreasuryHistory", history);
    }

    private async Task TreasuryCheckLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Use semaphore to prevent overlapping sweeps
                if (!await _treasurySweepLock.WaitAsync(0, cancellationToken))
                {
                    _logger.LogDebug("Treasury sweep still in progress, skipping this tick");
                    continue;
                }

                try
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    // Take a snapshot of settings for thread-safe iteration
                    var settingsSnapshot = _treasurySettings.ToArray();

                    foreach (var kvp in settingsSnapshot)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        var storeId = kvp.Key;
                        var settings = kvp.Value;

                        if (!settings.Enabled)
                            continue;

                        // Check if enough time has passed since last check
                        var intervalSeconds = settings.CheckIntervalMinutes * 60;
                        if (now - settings.LastCheckTimestamp < intervalSeconds)
                            continue;

                        try
                        {
                            await ProcessTreasurySweep(storeId, settings);

                            // Update last check timestamp
                            settings.LastCheckTimestamp = now;
                            await SetTreasurySettings(storeId, settings);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Treasury sweep check failed for store {StoreId}", storeId);
                        }
                    }
                }
                finally
                {
                    _treasurySweepLock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown
            _logger.LogDebug("Treasury check loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Treasury check loop failed unexpectedly");
        }
    }

    private async Task ProcessTreasurySweep(string storeId, TreasurySettings settings)
    {
        var client = GetClient(storeId);
        if (client?.Sdk is null)
        {
            _logger.LogWarning("No Breez client available for store {StoreId}", storeId);
            return;
        }

        // Get current balance
        var nodeInfo = await client.Sdk.GetInfo(new GetInfoRequest(ensureSynced: false));
        var currentBalanceSats = (long)nodeInfo.balanceSats;

        _logger.LogDebug("Treasury check for store {StoreId}: balance={Balance} sats, threshold={Threshold} sats",
            storeId, currentBalanceSats, settings.BalanceThresholdSats);

        // Check if balance exceeds threshold
        if (currentBalanceSats <= settings.BalanceThresholdSats)
            return;

        // Calculate sweep amount
        var sweepAmount = currentBalanceSats - settings.ReserveAmountSats;

        // Check minimum sweep amount
        if (sweepAmount < settings.MinSweepAmountSats)
        {
            _logger.LogDebug("Sweep amount {Amount} is below minimum {Min} for store {StoreId}",
                sweepAmount, settings.MinSweepAmountSats, storeId);
            return;
        }

        _logger.LogInformation("Initiating treasury sweep for store {StoreId}: amount={Amount} sats",
            storeId, sweepAmount);

        await ExecuteTreasurySweep(storeId, client, settings, sweepAmount, currentBalanceSats);
    }

    public async Task<SweepResult> ExecuteTreasurySweep(
        string storeId,
        BreezSparkLightningClient client,
        TreasurySettings settings,
        long amountSats,
        long currentBalanceSats)
    {
        var record = new TreasurySweepRecord
        {
            AmountSats = amountSats,
            BalanceBeforeSats = currentBalanceSats,
            Mode = settings.Mode,
            Status = TreasurySweepStatus.Pending
        };

        try
        {
            string destination;
            long feeSats = 0;

            if (settings.Mode == TreasuryMode.OnChain)
            {
                // Determine on-chain destination
                if (settings.OnChainAddressType == OnChainAddressType.Xpub && !string.IsNullOrEmpty(settings.Xpub))
                {
                    destination = TreasuryHelper.DeriveAddressFromXpub(
                        settings.Xpub,
                        settings.XpubDerivationIndex,
                        NBitcoin.Network.Main,
                        settings.XpubDerivationPath);
                }
                else if (!string.IsNullOrEmpty(settings.OnChainAddress))
                {
                    destination = settings.OnChainAddress;
                }
                else
                {
                    throw new InvalidOperationException("No on-chain destination configured");
                }

                record.Destination = destination;

                // Map fee speed to Breez SDK confirmation speed
                var confirmationSpeed = settings.OnchainFeeSpeed switch
                {
                    OnchainFeeSpeed.Slow => OnchainConfirmationSpeed.Slow,
                    OnchainFeeSpeed.Medium => OnchainConfirmationSpeed.Medium,
                    OnchainFeeSpeed.Fast => OnchainConfirmationSpeed.Fast,
                    _ => OnchainConfirmationSpeed.Medium
                };

                // Prepare on-chain payment
                var prepareRequest = new PrepareSendPaymentRequest(
                    paymentRequest: destination,
                    amount: new BigInteger(amountSats));

                var prepareResponse = await client.Sdk.PrepareSendPayment(prepareRequest);

                if (prepareResponse.paymentMethod is SendPaymentMethod.BitcoinAddress bitcoinMethod)
                {
                    // Extract fee based on selected speed
                    var feeQuote = confirmationSpeed switch
                    {
                        OnchainConfirmationSpeed.Slow => bitcoinMethod.feeQuote.speedSlow,
                        OnchainConfirmationSpeed.Fast => bitcoinMethod.feeQuote.speedFast,
                        _ => bitcoinMethod.feeQuote.speedMedium
                    };
                    feeSats = (long)(feeQuote.userFeeSat + feeQuote.l1BroadcastFeeSat);
                    record.FeeSats = feeSats;

                    var options = new SendPaymentOptions.BitcoinAddress(confirmationSpeed);
                    var sendRequest = new SendPaymentRequest(prepareResponse: prepareResponse, options: options);
                    var sendResponse = await client.Sdk.SendPayment(sendRequest);

                    // Update record with success
                    record.Status = TreasurySweepStatus.Completed;
                    record.PaymentId = sendResponse.payment?.id;
                    record.PaymentHash = sendResponse.payment?.id;
                }
                else
                {
                    throw new InvalidOperationException("Unexpected payment method for on-chain destination");
                }
            }
            else // Lightning mode
            {
                if (string.IsNullOrEmpty(settings.LightningAddress))
                {
                    throw new InvalidOperationException("No lightning address configured");
                }

                destination = settings.LightningAddress;
                record.Destination = destination;

                // Resolve lightning address to BOLT11
                var httpClient = _httpClientFactory.CreateClient("TreasuryLnurl");
                var bolt11 = await TreasuryHelper.ResolveLightningAddress(
                    settings.LightningAddress,
                    amountSats,
                    httpClient);

                if (string.IsNullOrEmpty(bolt11))
                {
                    throw new InvalidOperationException($"Failed to resolve lightning address: {settings.LightningAddress}");
                }

                // Prepare lightning payment
                var prepareRequest = new PrepareSendPaymentRequest(paymentRequest: bolt11);
                var prepareResponse = await client.Sdk.PrepareSendPayment(prepareRequest);

                if (prepareResponse.paymentMethod is SendPaymentMethod.Bolt11Invoice bolt11Method)
                {
                    feeSats = (long)(bolt11Method.lightningFeeSats + (bolt11Method.sparkTransferFeeSats ?? 0));
                    record.FeeSats = feeSats;

                    var sendRequest = new SendPaymentRequest(prepareResponse: prepareResponse);
                    var sendResponse = await client.Sdk.SendPayment(sendRequest);

                    // Update record with success
                    record.Status = TreasurySweepStatus.Completed;
                    record.PaymentId = sendResponse.payment?.id;
                    record.PaymentHash = sendResponse.payment?.id;
                }
                else
                {
                    throw new InvalidOperationException("Unexpected payment method for lightning address");
                }
            }

            record.BalanceAfterSats = currentBalanceSats - amountSats - feeSats;

            // Increment xpub derivation index on success
            if (settings.Mode == TreasuryMode.OnChain &&
                settings.OnChainAddressType == OnChainAddressType.Xpub)
            {
                settings.XpubDerivationIndex++;
                await SetTreasurySettings(storeId, settings);
            }

            // Update last sweep timestamp
            settings.LastSweepTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await SetTreasurySettings(storeId, settings);

            _logger.LogInformation(
                "Treasury sweep completed for store {StoreId}: amount={Amount}, fee={Fee}, destination={Dest}",
                storeId, amountSats, feeSats, destination);

            await AddTreasurySweepRecord(storeId, record);

            return new SweepResult(true, record.PaymentId, record.PaymentHash, feeSats, null);
        }
        catch (Exception ex)
        {
            record.Status = TreasurySweepStatus.Failed;
            record.ErrorMessage = ex.Message;
            record.BalanceAfterSats = currentBalanceSats;

            _logger.LogError(ex, "Treasury sweep failed for store {StoreId}", storeId);

            await AddTreasurySweepRecord(storeId, record);

            return new SweepResult(false, null, null, 0, ex.Message);
        }
    }

    public new async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel and await the treasury loop
        if (_treasuryCts is not null)
        {
            await _treasuryCts.CancelAsync();
            if (_treasuryLoopTask is not null)
            {
                try
                {
                    await _treasuryLoopTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
            _treasuryCts.Dispose();
        }

        _clients.Values.ToList().ForEach(c => c.Dispose());
        _treasurySweepLock.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
