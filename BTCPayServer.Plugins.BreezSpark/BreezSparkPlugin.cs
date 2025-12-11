#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NBitcoin;
using System;

namespace BTCPayServer.Plugins.BreezSpark
{
    public class BreezSparkPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.2.0" }
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<BreezSparkService>();
            applicationBuilder.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BreezSparkService>());
            applicationBuilder.AddSingleton<BreezSparkLightningConnectionStringHandler>();
            applicationBuilder.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<BreezSparkLightningConnectionStringHandler>());

            // Register the BreezSpark payment method handler
            applicationBuilder.AddSingleton<IPaymentMethodHandler>(provider =>
            {
                var breezService = provider.GetRequiredService<BreezSparkService>();
                var networkProvider = provider.GetRequiredService<BTCPayNetworkProvider>();
                var lightningClientFactory = provider.GetRequiredService<LightningClientFactoryService>();
                var lightningNetworkOptions = provider.GetRequiredService<IOptions<Configuration.LightningNetworkOptions>>();

                return new BreezSparkPaymentMethodHandler(
                    breezService,
                    networkProvider.GetNetwork<BTCPayNetwork>("BTC"),
                    lightningClientFactory,
                    lightningNetworkOptions);
            });

            // Add UI extensions for lightning setup tab (like Boltz does)
            applicationBuilder.AddUIExtension("ln-payment-method-setup-tab", "BreezSpark/LNPaymentMethodSetupTab");
            applicationBuilder.AddUIExtension("ln-payment-method-setup-tabhead", "BreezSpark/LNPaymentMethodSetupTabhead");

            // Surface BreezSpark navigation inside the store integrations nav, matching the plugin template pattern.
            applicationBuilder.AddUIExtension("store-integrations-nav", "BreezSpark/BreezSparkNav");

            base.Execute(applicationBuilder);
        }
    }
}
