#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
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
    private PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary => _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
    private readonly ILogger _logger;
    private Dictionary<string, BreezSparkSettings> _settings = new();
    private Dictionary<string, BreezSparkLightningClient> _clients = new();

    public BreezSparkService(
        EventAggregator eventAggregator,
        StoreRepository storeRepository,
        IOptions<DataDirectories> dataDirectories, 
        IServiceProvider serviceProvider,
        ILogger<BreezSparkService> logger) : base(eventAggregator, logger)
    {
        _storeRepository = storeRepository;
        _dataDirectories = dataDirectories;
        _serviceProvider = serviceProvider;
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

                await Handle(keyValuePair.Key, keyValuePair.Value);
            }
            catch
            {
            }
        }
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
                var network = Network.Main;
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
        
    public new async Task StopAsync(CancellationToken cancellationToken)
    {
        _clients.Values.ToList().ForEach(c => c.Dispose());
        await base.StopAsync(cancellationToken);
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
            return null;
        var match = _settings.FirstOrDefault(pair => pair.Value.PaymentKey == paymentKey).Key;
        return GetClient(match);
    }
}
