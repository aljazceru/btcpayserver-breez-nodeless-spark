#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BreezSpark
{
    public class BreezSparkPaymentMethodConfig : LightningPaymentMethodConfig
    {
        public string PaymentKey { get; set; } = string.Empty;
        public string StoreId { get; set; } = string.Empty;
    }

    public class BreezSparkPaymentMethodHandler : IPaymentMethodHandler, ILightningPaymentHandler
    {
        private readonly BreezSparkService _breezService;
        private readonly PaymentMethodId _paymentMethodId;
        private readonly BTCPayNetwork _network;
        private readonly LightningClientFactoryService _lightningClientFactory;
        private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;
        public JsonSerializer Serializer { get; }

        public BreezSparkPaymentMethodHandler(
            BreezSparkService breezService,
            BTCPayNetwork network,
            LightningClientFactoryService lightningClientFactory,
            IOptions<LightningNetworkOptions> lightningNetworkOptions)
        {
            _breezService = breezService;
            _network = network;
            _lightningClientFactory = lightningClientFactory;
            _lightningNetworkOptions = lightningNetworkOptions;
            _paymentMethodId = PaymentMethodId.Parse("BTC-BreezSpark");
            Serializer = BlobSerializer.CreateSerializer(network.NBitcoinNetwork).Serializer;
        }

        public PaymentMethodId PaymentMethodId => _paymentMethodId;

        public BTCPayNetwork Network => _network;

        public Task BeforeFetchingRates(PaymentMethodContext context)
        {
            context.Prompt.Currency = _network.CryptoCode;
            context.Prompt.PaymentMethodFee = 0m;
            context.Prompt.Divisibility = 11;
            context.Prompt.RateDivisibility = 8;
            return Task.CompletedTask;
        }

        public async Task ConfigurePrompt(PaymentMethodContext context)
        {
            if (context.InvoiceEntity.Type == InvoiceType.TopUp)
            {
                throw new PaymentMethodUnavailableException("BreezSpark Lightning Network payment method is not available for top-up invoices");
            }

            var paymentPrompt = context.Prompt;
            var storeBlob = context.StoreBlob;
            var store = context.Store;

            // Parse BreezSpark-specific config
            var breezConfig = ParsePaymentMethodConfig(context.PaymentMethodConfig);
            if (breezConfig == null || string.IsNullOrEmpty(breezConfig.PaymentKey))
            {
                throw new PaymentMethodUnavailableException("BreezSpark payment key is not configured");
            }

            // Get BreezSpark client
            var breezClient = _breezService.GetClient(breezConfig.StoreId);
            if (breezClient == null)
            {
                throw new PaymentMethodUnavailableException("BreezSpark client is not available for this store");
            }

            var invoice = context.InvoiceEntity;
            decimal due = paymentPrompt.Calculate().Due;
            var expiry = invoice.ExpirationTime - DateTimeOffset.UtcNow;
            if (expiry < TimeSpan.Zero)
                expiry = TimeSpan.FromSeconds(1);

            LightningInvoice lightningInvoice;
            string description = storeBlob.LightningDescriptionTemplate;
            description = description.Replace("{StoreName}", store.StoreName ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{ItemDescription}", invoice.Metadata.ItemDesc ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{OrderId}", invoice.Metadata.OrderId ?? "", StringComparison.OrdinalIgnoreCase);

            try
            {
                var request = new CreateInvoiceParams(
                    new LightMoney(due, LightMoneyUnit.BTC),
                    description,
                    expiry);
                request.PrivateRouteHints = storeBlob.LightningPrivateRouteHints;

                lightningInvoice = await breezClient.CreateInvoice(request, CancellationToken.None);
            }
            catch (Exception ex)
            {
                throw new PaymentMethodUnavailableException($"Impossible to create BreezSpark lightning invoice ({ex.Message})", ex);
            }

            paymentPrompt.Destination = lightningInvoice.BOLT11;
            var details = new LigthningPaymentPromptDetails
            {
                PaymentHash = lightningInvoice.GetPaymentHash(_network.NBitcoinNetwork),
                Preimage = string.IsNullOrEmpty(lightningInvoice.Preimage) ? null : uint256.Parse(lightningInvoice.Preimage),
                InvoiceId = lightningInvoice.Id,
                NodeInfo = "BreezSpark Lightning Wallet"
            };
            paymentPrompt.Details = JObject.FromObject(details, Serializer);
        }

        public BreezSparkPaymentMethodConfig ParsePaymentMethodConfig(JToken config)
        {
            return config.ToObject<BreezSparkPaymentMethodConfig>(Serializer) ?? new BreezSparkPaymentMethodConfig();
        }

        object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config)
        {
            return ParsePaymentMethodConfig(config);
        }

        public Task<ILightningClient?> CreateLightningClient(LightningPaymentMethodConfig config)
        {
            var breezConfig = config as BreezSparkPaymentMethodConfig;
            if (breezConfig == null || string.IsNullOrEmpty(breezConfig.StoreId))
                return Task.FromResult<ILightningClient?>(null);

            return Task.FromResult<ILightningClient?>(_breezService.GetClient(breezConfig.StoreId));
        }

        public object ParsePaymentPromptDetails(JToken details)
        {
            return details.ToObject<LigthningPaymentPromptDetails>(Serializer) ?? new LigthningPaymentPromptDetails();
        }

        public LightningPaymentData ParsePaymentDetails(JToken details)
        {
            return details.ToObject<LightningPaymentData>(Serializer) ?? new LightningPaymentData();
        }

        object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
        {
            return ParsePaymentDetails(details);
        }
    }
}
